import json
import os
import queue
import re
import uuid
import subprocess
import threading
import time
import urllib.request
from pathlib import Path
from typing import Tuple
from urllib.parse import urlencode

from pynput import keyboard
import uiautomation as auto

from PySide6.QtCore import QEvent, QSize, Qt, QTimer
from PySide6.QtGui import QIcon, QMouseEvent, QPainter, QPainterPath, QPixmap, QRegion
from PySide6.QtSvg import QSvgRenderer
from PySide6.QtWidgets import (
    QApplication,
    QFrame,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSplitter,
    QScrollArea,
    QSizePolicy,
    QSlider,
    QStackedWidget,
    QTextEdit,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from bridge_server import (
    PromptBridgeServer,
    PromptStore,
    _clear_interview_live_text,
    apply_selected_client_context_to_prompt_store,
    get_selected_client_id,
    set_selected_client_id,
)
from pipeline import build_chunk_prompts, process_caption_chunk
from prep_wizard import PrepWizardWidget
from ws_bridge import InterviewWSServer


LIVE_CAPTIONS_WINDOW_NAME = "Live Captions"


def _default_windows_live_captions_exe() -> Path:
    windir = os.environ.get("SystemRoot") or os.environ.get("WINDIR") or r"C:\Windows"
    return Path(windir) / "System32" / "LiveCaptions.exe"


def restart_live_caption_exe() -> None:
    """Kill Windows Live Captions (LiveCaptions.exe) and relaunch for a clean session."""
    if os.name != "nt":
        # print("[live.py] LiveCaptions restart is only supported on Windows.")
        return
    exe_path = Path(os.environ.get("LIVE_CAPTION_EXE", str(_default_windows_live_captions_exe()))).resolve()
    if not exe_path.is_file():
        # print(
        #     f"[live.py] LiveCaptions not found at {exe_path}. "
        #     "Install Windows 11 Live Captions or set LIVE_CAPTION_EXE to the full path."
        # )
        return
    exe_name = exe_path.name
    try:
        subprocess.run(
            ["taskkill", "/F", "/IM", exe_name],
            capture_output=True,
            timeout=15,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    time.sleep(0.4)
    try:
        subprocess.Popen([str(exe_path)], cwd=str(exe_path.parent))
        # print(f"[live.py] Started {exe_path}")
    except OSError as exc:
        # print(f"[live.py] Failed to start {exe_path}: {exc}")
        pass
    time.sleep(1.0)


RESUME_TEXT = "Paste your resume content here."
JOB_DESCRIPTION_TEXT = "Paste job description here."
ADDITIONAL_CONTEXT_TEXT = (
    "Answer naturally in first person. Keep concise. "
    "Do not add explanations or meta commentary."
)

BUBBLE_WIDTH_PERCENT = 0.85
TEXT_COLOR = "#111111"
# QSS border-radius on the shell must match the top-level window mask (see _sync_round_window_mask).
APP_SHELL_CORNER_RADIUS_PX = 0

capture_lock = threading.Lock()
# Rolling normalized caption from Live Captions (not trimmed while listening).
refined_full_caption = ""
# Character index in refined_full_caption where the next End slice starts (inclusive).
next_chunk_start_index = 0
# After End (or Delete skip), full caption text at that moment — used to realign the index when
# Live Captions retroactively edits earlier words without changing total length much.
_caption_chunk_anchor_prefix = ""

_CHUNK_ANCHOR_TAIL_CHARS = 200
_MIN_STRONG_OVERLAP_CHARS = 24
_BOUNDARY_VISIBLE_TAIL_FALLBACK_CHARS = 96
_BOUNDARY_SHIFT_CONFIRM_CHARS = 180
_BOUNDARY_SHIFT_CONFIRM_FRAMES = 2
_boundary_shift_candidate_idx = -1
_boundary_shift_candidate_hits = 0
last_end_key_at = 0.0
last_delete_key_at = 0.0
END_KEY_COOLDOWN_SECONDS = 0.8
HOME_KEY_COOLDOWN_SECONDS = 0.45
last_home_key_at = 0.0

# Last finalized GPT answer for global paste hotkeys (F9 = paste, Shift+F9 = select-all + delete + paste).
last_gpt_answer_for_paste = ""
last_f9_key_at = 0.0
F9_COOLDOWN_SECONDS = 0.45
_shift_physically_down = False

_interview_ws = None  # InterviewWSServer instance after prep completes
_main_interview_window = None  # InterviewWindow — strong ref after handoff
_active_interview_client_id = ""  # Client chosen in prep wizard for this live session only.

ui_queue: queue.Queue = queue.Queue()
app_running = True
pending_request_id = ""
last_answer_request_id = ""
# Templates pinned at interview start (PageDown). Once set, every chunk uses these
# even if the extension's stored value is later cleared or edited mid-interview.
# None = not yet pinned (use the live extension value); "" = pinned to "no template".
_session_initial_template: str | None = None
_session_chunk_template: str | None = None
# One-shot UI notice when HTTP fallback is used (avoid spamming every chunk).
_http_fallback_notified = False
# Dedupe live partial updates from /interview-live polling.
_last_live_poll_for_rid: Tuple[str, str] = ("", "")

prompt_store = PromptStore()
bridge = PromptBridgeServer(prompt_store)


def _remember_last_gpt_answer(text: str) -> None:
    global last_gpt_answer_for_paste
    t = (text or "").strip()
    if t:
        last_gpt_answer_for_paste = t


def _win32_set_clipboard_unicode(text: str) -> None:
    """Set the system clipboard on Windows (call from the Qt GUI thread when possible)."""
    import ctypes

    CF_UNICODETEXT = 13
    GMEM_MOVEABLE = 0x0002
    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32
    opened = False
    for _ in range(12):
        if user32.OpenClipboard(0):
            opened = True
            break
        time.sleep(0.04)
    if not opened:
        raise OSError("OpenClipboard failed")
    try:
        if not user32.EmptyClipboard():
            raise OSError("EmptyClipboard failed")
        raw = (text or "").replace("\r\n", "\n").replace("\r", "\n")
        data = raw.encode("utf-16-le") + b"\x00\x00"
        size = len(data)
        h = kernel32.GlobalAlloc(GMEM_MOVEABLE, size)
        if not h:
            raise OSError("GlobalAlloc failed")
        ptr = kernel32.GlobalLock(h)
        if not ptr:
            kernel32.GlobalFree(h)
            raise OSError("GlobalLock failed")
        try:
            ctypes.memmove(ptr, data, size)
        finally:
            kernel32.GlobalUnlock(h)
        if not user32.SetClipboardData(CF_UNICODETEXT, h):
            kernel32.GlobalFree(h)
            raise OSError("SetClipboardData failed")
        # On success the system owns the global handle; do not free.
    finally:
        user32.CloseClipboard()


def _set_clipboard_for_paste(text: str) -> None:
    """Prefer Qt clipboard on the GUI thread; Win32 fallback on Windows."""
    app = QApplication.instance()
    if app is not None:
        app.clipboard().setText(text or "")
        return
    if os.name == "nt":
        _win32_set_clipboard_unicode(text)


def _do_keyboard_paste_only(replace_all: bool) -> None:
    """Send Ctrl+V (and optional select-all + delete) after clipboard is already set."""
    time.sleep(0.1)
    kb = keyboard.Controller()
    ctrl = keyboard.Key.ctrl
    try:
        if replace_all:
            with kb.pressed(ctrl):
                kb.press("a")
                kb.release("a")
            time.sleep(0.05)
            kb.press(keyboard.Key.delete)
            kb.release(keyboard.Key.delete)
            time.sleep(0.05)
        with kb.pressed(ctrl):
            kb.press("v")
            kb.release("v")
    except Exception:
        pass


def _inject_last_gpt_paste(replace_all: bool) -> None:
    """Clipboard must be set on the Qt main thread; keyboard sim stays on a worker thread."""
    text = (last_gpt_answer_for_paste or "").strip()
    if not text:
        ui_queue.put({"type": "message", "text": "No GPT answer to paste yet.", "side": "left"})
        return
    ui_queue.put({"type": "f9_paste", "text": text, "replace_all": bool(replace_all)})


def get_all_text_controls(control):
    children = control.GetChildren()
    for child in children:
        if child.ControlTypeName == "TextControl" and len(child.Name) > 10:
            return child.Name
        nested_text = get_all_text_controls(child)
        if nested_text:
            return nested_text
    return ""


def normalize_caption_text(text):
    cleaned = " ".join(line.strip() for line in text.splitlines() if line.strip())
    cleaned = re.sub(r"\s+", " ", cleaned).strip()
    cleaned = re.sub(r"\s+([,.;:!?])", r"\1", cleaned)
    return cleaned


def strip_prefix_casefold(longer: str, prefix: str) -> str:
    if not longer or not prefix:
        return longer
    lo, lp = longer.casefold(), prefix.casefold()
    if lo.startswith(lp):
        return longer[len(prefix):].lstrip(" ,.;:-")
    return longer


def _alnum_only(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "", value or "")


def strip_soft_prefix(longer: str, prefix: str) -> str:
    if not longer or not prefix:
        return longer
    hard = strip_prefix_casefold(longer, prefix)
    if hard != longer:
        return hard

    lo, lp = _alnum_only(longer), _alnum_only(prefix)
    if not lp or not lo.startswith(lp):
        return longer

    need = len(lp)
    seen = 0
    idx = 0
    while idx < len(longer) and seen < need:
        if longer[idx].isalnum():
            seen += 1
        idx += 1
    return longer[idx:].lstrip(" ,.;:-")


def strip_llama_meta(text: str) -> str:
    if not text:
        return ""
    t = text.strip()
    if (t.startswith('"') and t.endswith('"')) or (t.startswith("'") and t.endswith("'")):
        t = t[1:-1].strip()
    patterns = [
        r"(?is)^\s*here(?:'s| is)\s+(?:the\s+)?(?:cleaned|clean)\s+transcript\s*:?\s*",
        r"(?is)^\s*below\s+is\s+(?:the\s+)?(?:cleaned|clean)\s+transcript\s*:?\s*",
        r"(?is)^\s*cleaned\s+transcript\s*:?\s*",
        r"(?is)^\s*summary\s*:?\s*",
    ]
    for pat in patterns:
        t = re.sub(pat, "", t).strip()
    return t


def format_extracted_for_ui(cleaned: str, raw_chunk: str) -> str:
    body = strip_llama_meta(cleaned or raw_chunk)
    body = strip_soft_prefix(body, raw_chunk.strip())
    return body.strip()


def _fetch_interview_live_http(request_id: str) -> str:
    if not request_id:
        return ""
    try:
        qs = urlencode({"request_id": request_id})
        with urllib.request.urlopen(
            f"http://127.0.0.1:8765/interview-live?{qs}", timeout=2
        ) as response:
            data = json.loads(response.read().decode("utf-8"))
        return str(data.get("text", "") or "")
    except Exception:
        return ""


def handle_ws_extension_payload(payload: dict) -> None:
    global pending_request_id, last_answer_request_id, _last_live_poll_for_rid
    typ = str(payload.get("type", "")).strip()
    if typ == "LIVE_ANSWER":
        rid = str(payload.get("request_id", "")).strip()
        text = str(payload.get("text", ""))
        if rid and pending_request_id and rid == pending_request_id and text:
            _last_live_poll_for_rid = (rid, text)
            queue_gpt_stream_partial(text)
        return
    if typ == "FINAL_ANSWER":
        rid = str(payload.get("request_id", "")).strip()
        answer = str(payload.get("answer", "")).strip()
        if not rid or not answer or rid != pending_request_id:
            return
        last_answer_request_id = rid
        pending_request_id = ""
        _last_live_poll_for_rid = ("", "")
        prompt_store.set_answer(rid, answer)
        queue_gpt_final(answer)
        return
    if typ == "STATUS":
        msg = str(payload.get("message", "")).strip()
        if msg:
            queue_ui_message(f"Extension: {msg}", "left")


class StatusRow:
    def __init__(self, label: QLabel, base: str, row: QWidget | None = None, panel: QFrame | None = None):
        self.label = label
        self.base = base
        self.row = row
        self.panel = panel
        self.step = 0
        self.running = True


class InterviewWindow(QWidget):
    def __init__(
        self,
        active_user_label: str = "",
        bridge_base: str = "http://127.0.0.1:8765",
        start_with_prep: bool = True,
    ):
        super().__init__()
        self.drag_offset = None
        self.resize_margin = 8
        self.resize_edges = None
        self.resize_start_geom = None
        self.resize_start_pos = None
        self.min_w = 520
        self.min_h = 320
        self._bridge_base = bridge_base.rstrip("/")
        self._start_with_prep = start_with_prep
        self.prep_widget: PrepWizardWidget | None = None
        self.main_stack: QStackedWidget | None = None
        self.active_draft_label: QLabel | None = None
        self.active_draft_row: QWidget | None = None
        self.active_draft_panel: QFrame | None = None
        self._home_edit_active = False
        self._frozen_draft_text = ""
        self._caption_edit_text_edit: QTextEdit | None = None
        self._caption_edit_panel: QFrame | None = None
        self._caption_edit_cap_id: str | None = None
        self._caption_edit_sent_once = False
        self._allow_draft_updates_during_edit = False
        self._splitter_initialized = False
        self.gpt_result_history: list[str] = []
        self.gpt_history_index = -1
        self.gpt_stream_row: QWidget | None = None
        self.gpt_stream_panel: QFrame | None = None
        self.gpt_stream_label: QLabel | None = None
        self.gpt_fallback_notice_row: QWidget | None = None
        self.gpt_fallback_notice_panel: QFrame | None = None
        self.gpt_fallback_notice_label: QLabel | None = None
        self.status_rows: dict[str, list[StatusRow]] = {"llama": [], "gpt": []}
        self.message_panels: list[QWidget] = []

        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.WindowType.NoDropShadowWindowHint
        )
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setObjectName("InterviewWindow")
        self.setStyleSheet(
            "#InterviewWindow { background-color: transparent; border: none; outline: none; }"
        )
        self.setMouseTracking(True)
        self.resize(820, 620)

        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)

        self.shell = QFrame()
        self.shell.setObjectName("InterviewShell")
        self.shell.setFrameShape(QFrame.Shape.NoFrame)
        self.shell.setFrameShadow(QFrame.Shadow.Plain)
        # Rounded QSS + partial borders + translucent fill often leaves 1px corner gaps (desktop bleeds through).
        self.shell.setAttribute(Qt.WidgetAttribute.WA_StyledBackground, True)
        self._shell_corner_r = float(APP_SHELL_CORNER_RADIUS_PX)
        self._shell_bg_opacity = 0.8
        self._shell_phase_prep = False
        root.addWidget(self.shell)

        shell_layout = QVBoxLayout(self.shell)
        shell_layout.setContentsMargins(14, 14, 14, 14)
        shell_layout.setSpacing(0)

        topbar = QHBoxLayout()
        self.account_label = QLabel()
        self.account_label.setStyleSheet("font-size: 12px; font-weight: 700; color: #222;")
        if active_user_label:
            self.account_label.setText(f"Using: {active_user_label}")
            self.account_label.show()
        else:
            self.account_label.hide()
        topbar.addWidget(self.account_label)
        self.top_drag_zone = QWidget()
        self.top_drag_zone.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Preferred)
        self.top_drag_zone.setMinimumHeight(26)
        self.top_drag_zone.setCursor(Qt.OpenHandCursor)
        self.top_drag_zone.installEventFilter(self)
        topbar.addWidget(self.top_drag_zone, stretch=1)
        opacity_lbl = QLabel("Bg")
        opacity_lbl.setStyleSheet("font-size: 11px; font-weight: 700; color: #222;")
        self.opacity_slider = QSlider(Qt.Orientation.Horizontal)
        self.opacity_slider.setRange(0, 100)
        self.opacity_slider.setValue(80)
        self.opacity_slider.setFixedWidth(110)
        self.opacity_slider.setToolTip("Shell background opacity only (text stays solid)")
        self.opacity_slider.valueChanged.connect(self._on_opacity_slider_changed)
        topbar.addWidget(opacity_lbl)
        topbar.addWidget(self.opacity_slider)
        self.close_btn = QPushButton()
        self.close_btn.setFixedSize(28, 28)
        self.close_btn.setCursor(Qt.PointingHandCursor)
        self.close_btn.setIcon(self._build_close_svg_icon(16, "#111111"))
        self.close_btn.setIconSize(self.close_btn.size())
        self.close_btn.setStyleSheet(
            """
            QPushButton {
                border: none;
                border-radius: 14px;
                background: rgba(255,255,255,170);
            }
            QPushButton:hover {
                background: rgba(255,80,80,220);
            }
            """
        )
        self.close_btn.clicked.connect(self.close)
        topbar.addWidget(self.close_btn)
        shell_layout.addLayout(topbar)

        self.caption_scroll = QScrollArea()
        self.caption_scroll.setWidgetResizable(True)
        self.caption_scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.caption_scroll.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self.caption_scroll.setFrameShape(QFrame.NoFrame)
        self.caption_scroll.setStyleSheet(
            "QScrollArea { background: transparent; border: none; }"
            "QScrollBar:vertical { width: 10px; background: rgba(0,0,0,24); border-radius: 5px; margin: 2px; }"
            "QScrollBar::handle:vertical { min-height: 24px; background: rgba(57,73,171,160); border-radius: 5px; }"
            "QScrollBar::handle:vertical:hover { background: rgba(57,73,171,220); }"
            "QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }"
        )
        # Pause auto-scroll while the user is interacting (wheel / drag / arrows).
        self._user_scroll_cooldown_until = 0.0
        self._user_scroll_cooldown_seconds = 1.4
        self._suppress_user_scroll_signal = False
        # Auto-scroll is gated on "interviewer is actively saying" (live caption draft updates).
        self._interviewer_speaking_until = 0.0
        self._interviewer_speaking_window_seconds = 2.0
        self.caption_scroll.viewport().installEventFilter(self)
        sb = self.caption_scroll.verticalScrollBar()
        sb.installEventFilter(self)
        sb.actionTriggered.connect(self._on_scrollbar_user_action)
        sb.sliderPressed.connect(self._mark_user_scroll_activity)
        sb.sliderReleased.connect(self._mark_user_scroll_activity)

        self.content = QWidget()
        self.content.setStyleSheet("QWidget { background: transparent; border: none; }")
        self.content_layout = QVBoxLayout(self.content)
        self.content_layout.setContentsMargins(0, 0, 0, 0)
        self.content_layout.setSpacing(10)
        self.content_layout.addStretch(1)
        self.caption_scroll.setWidget(self.content)

        self.gpt_result_panel = QFrame()
        self.gpt_result_panel.setObjectName("GptResultPanel")
        self.gpt_result_panel.setStyleSheet(
            "QFrame#GptResultPanel { background: rgba(255,255,255,0.55); border: 1px solid rgba(17,17,17,70); }"
        )
        gpt_layout = QVBoxLayout(self.gpt_result_panel)
        gpt_layout.setContentsMargins(10, 10, 10, 10)
        gpt_layout.setSpacing(6)
        self.gpt_result_title = QLabel("0/0")
        self.gpt_result_title.setStyleSheet("font-size: 12px; font-weight: 700; color: #333;")
        self.gpt_result_body = QTextEdit()
        self.gpt_result_body.setReadOnly(True)
        self.gpt_result_body.setAcceptRichText(False)
        self.gpt_result_body.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self.gpt_result_body.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.gpt_result_body.setStyleSheet(
            f"QTextEdit {{ color: {TEXT_COLOR}; background: transparent; border: none; font-size: 16px; font-weight: 700; }}"
        )
        controls_row = QHBoxLayout()
        controls_row.setContentsMargins(0, 0, 0, 0)
        controls_row.setSpacing(6)
        self.gpt_copy_btn = QToolButton()
        self.gpt_prev_btn = QToolButton()
        self.gpt_next_btn = QToolButton()
        self.gpt_copy_btn.setIcon(self._build_copy_glyph_svg_icon(14, "#455a64"))
        self.gpt_prev_btn.setIcon(self._build_prev_chevron_svg_icon(14, "#455a64"))
        self.gpt_next_btn.setIcon(self._build_next_chevron_svg_icon(14, "#455a64"))
        self.gpt_prev_btn.setIconSize(QSize(14, 14))
        self.gpt_next_btn.setIconSize(QSize(14, 14))
        for _btn, _tip in (
            (self.gpt_copy_btn, "Copy result"),
            (self.gpt_prev_btn, "Previous result"),
            (self.gpt_next_btn, "Next result"),
        ):
            _btn.setCursor(Qt.PointingHandCursor)
            _btn.setToolTip(_tip)
            _btn.setFixedSize(24, 24)
            _btn.setStyleSheet(
                "QToolButton { border: none; border-radius: 12px; background: transparent; font-weight: 700; }"
                "QToolButton:hover { background: rgba(0, 0, 0, 0.10); }"
            )
        self.gpt_copy_btn.clicked.connect(self._copy_current_gpt_result)
        self.gpt_prev_btn.clicked.connect(self._show_prev_gpt_result)
        self.gpt_next_btn.clicked.connect(self._show_next_gpt_result)
        controls_row.addWidget(self.gpt_copy_btn)
        controls_row.addWidget(self.gpt_prev_btn)
        controls_row.addWidget(self.gpt_next_btn)
        controls_row.addStretch(1)
        gpt_layout.addWidget(self.gpt_result_title)
        gpt_layout.addWidget(self.gpt_result_body, 1)
        gpt_layout.addLayout(controls_row)
        self._refresh_gpt_history_nav()

        self.interview_splitter = QSplitter(Qt.Orientation.Vertical)
        self.interview_splitter.addWidget(self.caption_scroll)
        self.interview_splitter.addWidget(self.gpt_result_panel)
        self.interview_splitter.setChildrenCollapsible(False)

        self.interview_page = QWidget()
        interview_layout = QVBoxLayout(self.interview_page)
        interview_layout.setContentsMargins(0, 0, 0, 0)
        interview_layout.setSpacing(0)
        interview_layout.addWidget(self.interview_splitter)
        self.scroll = self.caption_scroll

        self.main_stack = QStackedWidget()
        self.main_stack.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        if self._start_with_prep:
            self.prep_widget = PrepWizardWidget(self._bridge_base, parent=self.main_stack)
            self.main_stack.addWidget(self.prep_widget)
            self.main_stack.addWidget(self.interview_page)
            self.prep_widget.finished.connect(self._on_prep_finished)
            self.main_stack.currentChanged.connect(self._sync_shell_for_main_stack)
            self._sync_shell_for_main_stack(self.main_stack.currentIndex())
        else:
            self.main_stack.addWidget(self.interview_page)
            self._shell_phase_prep = False
            self._apply_shell_visual()
        shell_layout.addWidget(self.main_stack, stretch=1)

        self.queue_timer = QTimer(self)
        self.queue_timer.timeout.connect(self.process_ui_events)
        self.queue_timer.start(100)

        self.anim_timer = QTimer(self)
        self.anim_timer.timeout.connect(self.tick_status_animation)
        self.anim_timer.start(350)

        self.caption_autoscroll_timer = QTimer(self)
        self.caption_autoscroll_timer.timeout.connect(self._tick_caption_autoscroll)
        self.caption_autoscroll_timer.start(500)

        self.resize_timer = QTimer(self)
        self.resize_timer.setSingleShot(True)
        self.resize_timer.timeout.connect(self.refresh_panel_widths)

        # Hover cursor updates should work before clicking.
        self._enable_hover_tracking(self)

    def _on_opacity_slider_changed(self, value: int) -> None:
        self._shell_bg_opacity = max(0.0, min(1.0, value / 100.0))
        self._apply_shell_visual()

    def _apply_shell_visual(self) -> None:
        """Only the shell panel background fades; labels/buttons stay opaque."""
        a = max(0.0, min(1.0, float(self._shell_bg_opacity)))
        ab = int(round(a * 255))
        r = int(self._shell_corner_r)
        if self._shell_phase_prep:
            bg = f"rgba(228, 231, 238, {ab})"
            border_decl = (
                "border: none;"
                if ab == 0
                else f"border: 1px solid rgba(174, 182, 200, {min(255, ab + 50)});"
            )
        else:
            bg = f"rgba(252, 252, 255, {ab})"
            border_decl = "border: none;" if ab == 0 else "border: 1px solid rgba(17, 17, 17, 140);"
        self.shell.setStyleSheet(
            f"""
            QFrame#InterviewShell {{
                background-color: {bg};
                {border_decl}
                border-radius: {r}px;
            }}
            QFrame#InterviewShell QLabel {{
                font-weight: 700;
            }}
            QFrame#InterviewShell QPushButton {{
                font-weight: 700;
            }}
            """
        )

    def _sync_shell_for_main_stack(self, index: int) -> None:
        if self._start_with_prep and self.main_stack is not None:
            self._shell_phase_prep = index == 0
        self._apply_shell_visual()

    def _on_prep_finished(self, display_email: str, client_id: str) -> None:
        global _interview_ws, _main_interview_window, _active_interview_client_id
        global _session_initial_template, _session_chunk_template
        chosen = str(client_id or "").strip()
        if chosen:
            set_selected_client_id(chosen)
            _active_interview_client_id = chosen
        else:
            _active_interview_client_id = str(get_selected_client_id() or "").strip()
        apply_selected_client_context_to_prompt_store(prompt_store)
        # Pin the templates that step 4 just shipped to ChatGPT — every caption from now
        # on uses these snapshots, so editing the extension mid-interview cannot change behavior.
        _session_initial_template = (prompt_store.get_template("initial_interview") or "").strip()
        _session_chunk_template = (prompt_store.get_template("chunk_interview") or "").strip()
        _interview_ws = InterviewWSServer(ui_queue)
        _interview_ws.start()
        threading.Thread(target=start_listener, daemon=True).start()
        threading.Thread(target=run_capture_loop, daemon=True).start()
        threading.Thread(target=poll_latest_answer_loop, daemon=True).start()

        label = (display_email or "").strip() or (
            _active_interview_client_id[:8] if _active_interview_client_id else "session"
        )
        self.account_label.setText(f"Using: {label}")
        self.account_label.show()

        if self.prep_widget is not None:
            self.prep_widget.shutdown_for_handoff()
        if self.main_stack is not None:
            self.main_stack.setCurrentIndex(1)
            QTimer.singleShot(0, self._apply_initial_splitter_sizes)
        restart_live_caption_exe()

        def _focus_session_window() -> None:
            try:
                if self.isVisible():
                    self.raise_()
                    self.activateWindow()
                app_inst = QApplication.instance()
                if app_inst is not None:
                    try:
                        app_inst.alert(self)
                    except Exception:
                        pass
            except Exception:
                pass

        QTimer.singleShot(150, _focus_session_window)
        QTimer.singleShot(600, _focus_session_window)
        _main_interview_window = self

    def _enable_hover_tracking(self, widget):
        widget.setMouseTracking(True)
        widget.installEventFilter(self)
        for child in widget.findChildren(QWidget):
            child.setMouseTracking(True)
            child.installEventFilter(self)

    def _build_close_svg_icon(self, size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 16 16">
<path fill="{color}" d="M3.72 3.72a.75.75 0 0 1 1.06 0L8 6.94l3.22-3.22a.75.75 0 1 1 1.06 1.06L9.06 8l3.22 3.22a.75.75 0 1 1-1.06 1.06L8 9.06l-3.22 3.22a.75.75 0 0 1-1.06-1.06L6.94 8L3.72 4.78a.75.75 0 0 1 0-1.06z"/>
</svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    @staticmethod
    def _build_tick_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 16 16">
<path fill="{color}" d="M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.75.75 0 1 1 1.06-1.06L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0z"/>
</svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    @staticmethod
    def _build_copy_glyph_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="{color}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
<rect x="9" y="9" width="13" height="13" rx="2" ry="2"/>
<path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>
</svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    @staticmethod
    def _build_prev_chevron_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="{color}" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round">
<polyline points="15 18 9 12 15 6"/></svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    @staticmethod
    def _build_next_chevron_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="{color}" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round">
<polyline points="9 18 15 12 9 6"/></svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    @staticmethod
    def _build_x_cancel_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="{color}" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
<path d="M18 6L6 18M6 6l12 12"/></svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    @staticmethod
    def _build_send_plane_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24" fill="{color}">
<path d="M2 21l23-9L2 3v7l15 2-15 2v7z"/></svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    def _hit_test_edges(self, local_pos):
        rect = self.rect()
        x, y = local_pos.x(), local_pos.y()
        left = x <= self.resize_margin
        right = x >= rect.width() - self.resize_margin
        top = y <= self.resize_margin
        bottom = y >= rect.height() - self.resize_margin
        return left, right, top, bottom

    def _update_cursor(self, local_pos):
        left, right, top, bottom = self._hit_test_edges(local_pos)
        if (left and top) or (right and bottom):
            self.setCursor(Qt.SizeFDiagCursor)
        elif (right and top) or (left and bottom):
            self.setCursor(Qt.SizeBDiagCursor)
        elif left or right:
            self.setCursor(Qt.SizeHorCursor)
        elif top or bottom:
            self.setCursor(Qt.SizeVerCursor)
        else:
            # Interior is draggable.
            self.setCursor(Qt.OpenHandCursor)

    def mousePressEvent(self, event: QMouseEvent):
        if event.button() == Qt.LeftButton and not self.close_btn.geometry().contains(
            event.position().toPoint()
        ):
            left, right, top, bottom = self._hit_test_edges(event.position().toPoint())
            if left or right or top or bottom:
                self.resize_edges = (left, right, top, bottom)
                self.resize_start_geom = self.geometry()
                self.resize_start_pos = event.globalPosition().toPoint()
                self.drag_offset = None
            else:
                self.resize_edges = None
                self.drag_offset = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
                self.setCursor(Qt.ClosedHandCursor)
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent):
        if self.resize_edges is not None and event.buttons() & Qt.LeftButton:
            left, right, top, bottom = self.resize_edges
            start = self.resize_start_geom
            delta = event.globalPosition().toPoint() - self.resize_start_pos

            x, y = start.x(), start.y()
            w, h = start.width(), start.height()

            if left:
                x = start.x() + delta.x()
                w = start.width() - delta.x()
                if w < self.min_w:
                    x = start.right() - self.min_w + 1
                    w = self.min_w
            elif right:
                w = max(self.min_w, start.width() + delta.x())

            if top:
                y = start.y() + delta.y()
                h = start.height() - delta.y()
                if h < self.min_h:
                    y = start.bottom() - self.min_h + 1
                    h = self.min_h
            elif bottom:
                h = max(self.min_h, start.height() + delta.y())

            self.setGeometry(x, y, w, h)
        elif self.drag_offset is not None and event.buttons() & Qt.LeftButton:
            self.move(event.globalPosition().toPoint() - self.drag_offset)
        else:
            self._update_cursor(event.position().toPoint())
        super().mouseMoveEvent(event)

    def eventFilter(self, watched, event):
        drag_zone = getattr(self, "top_drag_zone", None)
        if drag_zone is not None and watched is drag_zone and event.type() == QEvent.Type.MouseButtonPress:
            me = event
            if me.button() == Qt.LeftButton:
                self.drag_offset = me.globalPosition().toPoint() - self.frameGeometry().topLeft()
                self.resize_edges = None
                self.grabMouse()
                self.setCursor(Qt.ClosedHandCursor)
                return True
        et = event.type()
        scroll = getattr(self, "scroll", None)
        if isinstance(scroll, QWidget):
            sb = scroll.verticalScrollBar()
            if watched is scroll.viewport():
                if et in (QEvent.Type.Wheel, QEvent.Type.MouseButtonPress, QEvent.Type.TouchBegin):
                    self._mark_user_scroll_activity()
            if watched is sb and et in (
                QEvent.Type.MouseButtonPress,
                QEvent.Type.MouseMove,
                QEvent.Type.Wheel,
                QEvent.Type.KeyPress,
            ):
                self._mark_user_scroll_activity()
        if event.type() == event.Type.MouseMove:
            try:
                local = self.mapFromGlobal(watched.mapToGlobal(event.position().toPoint()))
                self._update_cursor(local)
            except Exception:
                pass
        return super().eventFilter(watched, event)

    def mouseReleaseEvent(self, event: QMouseEvent):
        try:
            self.releaseMouse()
        except Exception:
            pass
        self.drag_offset = None
        self.resize_edges = None
        self.resize_start_geom = None
        self.resize_start_pos = None
        self._update_cursor(event.position().toPoint())
        super().mouseReleaseEvent(event)

    def closeEvent(self, event):
        global app_running
        app_running = False
        super().closeEvent(event)

    def _sync_round_window_mask(self) -> None:
        """Clip the HWND to the same rounded rect as the shell QSS so the frosted bg edge matches the window."""
        w = max(1, int(self.width()))
        h = max(1, int(self.height()))
        r = float(APP_SHELL_CORNER_RADIUS_PX)
        path = QPainterPath()
        path.addRoundedRect(0.0, 0.0, float(w), float(h), r, r)
        self.setMask(QRegion(path.toFillPolygon().toPolygon()))

    def showEvent(self, event):
        super().showEvent(event)
        self._sync_round_window_mask()
        self._apply_initial_splitter_sizes()

    def resizeEvent(self, event):
        self._sync_round_window_mask()
        self.resize_timer.start(20)
        if not self._splitter_initialized:
            QTimer.singleShot(0, self._apply_initial_splitter_sizes)
        super().resizeEvent(event)

    def _apply_initial_splitter_sizes(self) -> None:
        if not hasattr(self, "interview_splitter") or self.interview_splitter is None:
            return
        total = max(1, self.interview_splitter.height())
        top = max(120, int(total * 0.30))
        bottom = max(180, total - top)
        self.interview_splitter.setSizes([top, bottom])
        self._splitter_initialized = True

    def _tick_caption_autoscroll(self) -> None:
        if self._home_edit_active:
            return
        bar = self.caption_scroll.verticalScrollBar()
        bar.setValue(bar.maximum())

    def _set_gpt_result_text(self, text: str) -> None:
        self.gpt_result_body.setPlainText((text or "").strip() or " ")

    def _refresh_gpt_history_nav(self) -> None:
        if not hasattr(self, "gpt_prev_btn") or not hasattr(self, "gpt_next_btn"):
            return
        n = len(self.gpt_result_history)
        idx = int(self.gpt_history_index)
        self.gpt_prev_btn.setEnabled(n > 0 and idx > 0)
        self.gpt_next_btn.setEnabled(n > 0 and idx >= 0 and idx < (n - 1))
        if n <= 0 or idx < 0:
            self.gpt_result_title.setText("0/0")
        else:
            self.gpt_result_title.setText(f"{idx + 1}/{n}")

    def _push_gpt_result_history(self, text: str) -> None:
        body = (text or "").strip()
        if not body:
            return
        if self.gpt_result_history and self.gpt_result_history[-1] == body:
            self.gpt_history_index = len(self.gpt_result_history) - 1
            self._refresh_gpt_history_nav()
            return
        self.gpt_result_history.append(body)
        self.gpt_history_index = len(self.gpt_result_history) - 1
        self._refresh_gpt_history_nav()

    def _copy_current_gpt_result(self) -> None:
        txt = (self.gpt_result_body.toPlainText() or "").strip()
        QApplication.clipboard().setText(txt)
        self.gpt_copy_btn.setIcon(self._build_tick_svg_icon(14, "#2e7d32"))

        def _restore_copy_icon() -> None:
            try:
                self.gpt_copy_btn.setIcon(self._build_copy_glyph_svg_icon(14, "#455a64"))
            except Exception:
                pass

        QTimer.singleShot(900, _restore_copy_icon)

    def _show_prev_gpt_result(self) -> None:
        if self.gpt_history_index <= 0:
            return
        self.gpt_history_index -= 1
        self._set_gpt_result_text(self.gpt_result_history[self.gpt_history_index])
        self._refresh_gpt_history_nav()

    def _show_next_gpt_result(self) -> None:
        if self.gpt_history_index < 0 or self.gpt_history_index >= len(self.gpt_result_history) - 1:
            return
        self.gpt_history_index += 1
        self._set_gpt_result_text(self.gpt_result_history[self.gpt_history_index])
        self._refresh_gpt_history_nav()

    def bubble_width(self) -> int:
        inner = self.shell.width() - 28
        return max(320, int(inner * BUBBLE_WIDTH_PERCENT))

    @staticmethod
    def _on_copy_bubble_clicked(copy_btn: QToolButton, label: QLabel) -> None:
        prop = label.property("copy_text")
        if isinstance(prop, str) and prop.strip():
            to_copy = prop
        else:
            to_copy = (label.text() or "").strip()
        QApplication.clipboard().setText(to_copy)
        sz = copy_btn.iconSize().width() or 14
        copy_btn.setIcon(InterviewWindow._build_tick_svg_icon(sz, "#2e7d32"))

        def restore() -> None:
            try:
                copy_btn.setIcon(InterviewWindow._build_copy_glyph_svg_icon(sz, "#455a64"))
            except RuntimeError:
                pass

        QTimer.singleShot(1500, restore)

    def create_row(
        self, side: str, with_copy_button: bool = False, copy_tooltip: str = "Copy"
    ) -> tuple[QWidget, QFrame, QLabel]:
        row = QWidget()
        h = QHBoxLayout(row)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(0)

        panel = QFrame()
        panel.setMinimumHeight(44)
        panel.setMaximumHeight(2000)
        panel.setStyleSheet(
            "QFrame { background: transparent; border: none; border-bottom: 1px solid rgba(17,17,17,80); }"
        )
        panel_layout = QVBoxLayout(panel)
        panel_layout.setContentsMargins(0, 0, 0, 0)
        panel_layout.setSpacing(8)

        label = QLabel()
        label.setWordWrap(True)
        label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        label.setStyleSheet(
            f"QLabel {{ color: {TEXT_COLOR}; background: transparent; border: none; "
            f"font-size: 16px; font-weight: 700; }}"
        )
        panel_layout.addWidget(label)
        if with_copy_button:
            actions = QHBoxLayout()
            actions.setContentsMargins(0, 0, 0, 2)
            actions.setSpacing(4)
            actions.addStretch(1)
            copy_btn = QToolButton(panel)
            _isz = 14
            copy_btn.setIcon(InterviewWindow._build_copy_glyph_svg_icon(_isz, "#455a64"))
            copy_btn.setIconSize(QSize(_isz, _isz))
            copy_btn.setCursor(Qt.PointingHandCursor)
            copy_btn.setToolTip(copy_tooltip)
            copy_btn.setFixedSize(20, 20)
            copy_btn.setStyleSheet(
                "QToolButton { border: none; border-radius: 10px; background: transparent; }"
                "QToolButton:hover { background: rgba(0, 0, 0, 0.10); }"
            )
            copy_btn.clicked.connect(
                lambda _checked=False, btn=copy_btn, lbl=label: InterviewWindow._on_copy_bubble_clicked(btn, lbl)
            )
            actions.addWidget(copy_btn)
            panel_layout.addLayout(actions)

        if side == "right":
            h.addStretch(1)
            h.addWidget(panel)
        else:
            h.addWidget(panel)
            h.addStretch(1)

        self.message_panels.append(panel)
        return row, panel, label

    def insert_before_draft(self, row: QWidget):
        stretch_index = self.content_layout.count() - 1
        draft_index = self.content_layout.indexOf(self.active_draft_row) if self.active_draft_row else -1
        if draft_index >= 0:
            self.content_layout.insertWidget(draft_index, row)
        else:
            self.content_layout.insertWidget(stretch_index, row)

    def add_text_row(self, text: str, side: str, clipboard_prompt: str | None = None):
        tip = "Copy GPT prompt" if side == "right" else "Copy"
        row, panel, label = self.create_row(side, with_copy_button=True, copy_tooltip=tip)
        label.setText(text or " ")
        if side == "right":
            _, default_p = build_chunk_prompts(
                text or "", prompt_store, template_override=_session_chunk_template
            )
            label.setProperty("copy_text", clipboard_prompt if clipboard_prompt is not None else default_p)
        self.insert_before_draft(row)
        panel.setFixedWidth(self.bubble_width())
        panel.adjustSize()
        self.scroll_to_bottom()

    def show_or_update_draft(self, text: str):
        if self._home_edit_active and not self._allow_draft_updates_during_edit:
            self._frozen_draft_text = text or ""
            return
        if self.active_draft_label is None:
            row, panel, label = self.create_row(
                "right", with_copy_button=True, copy_tooltip="Copy GPT prompt"
            )
            label.setText(text or " ")
            stretch_index = self.content_layout.count() - 1
            self.content_layout.insertWidget(stretch_index, row)
            panel.setFixedWidth(self.bubble_width())
            self.active_draft_row = row
            self.active_draft_panel = panel
            self.active_draft_label = label
        else:
            self.active_draft_label.setText(text or " ")
        _, prompt = build_chunk_prompts(
            text or "", prompt_store, template_override=_session_chunk_template
        )
        if self.active_draft_label is not None:
            self.active_draft_label.setProperty("copy_text", prompt)
        if (text or "").strip():
            self._interviewer_speaking_until = time.time() + float(
                getattr(self, "_interviewer_speaking_window_seconds", 2.0)
            )

    def finalize_draft(self, text: str, clipboard_prompt: str | None = None):
        if self.active_draft_label is not None:
            self.active_draft_label.setText(text or " ")
            _, default_p = build_chunk_prompts(
                text or "", prompt_store, template_override=_session_chunk_template
            )
            self.active_draft_label.setProperty(
                "copy_text", clipboard_prompt if clipboard_prompt is not None else default_p
            )
            self.active_draft_label = None
            self.active_draft_row = None
            self.active_draft_panel = None
            self._tick_caption_autoscroll()
            return
        self.add_text_row(text, "right", clipboard_prompt)

    def create_new_empty_draft(self):
        self._frozen_draft_text = ""
        if self.active_draft_label is None:
            self.show_or_update_draft("")

    def _try_enter_caption_edit_hotkey(self) -> None:
        if self._home_edit_active:
            if self._caption_edit_text_edit is not None:
                self._caption_edit_text_edit.setFocus()
                self._caption_edit_text_edit.selectAll()
            return
        if self.active_draft_label is None or self.active_draft_panel is None:
            return
        label_text = (self.active_draft_label.text() or "").strip()
        # Consume current pending chunk so new live draft does not repeat the caption being edited.
        consumed_text = (snapshot_chunk_since_last_end() or "").strip()
        text = consumed_text or label_text
        if not text:
            return
        self._home_edit_active = True
        self._caption_edit_cap_id = str(uuid.uuid4())
        self._caption_edit_sent_once = False
        self._frozen_draft_text = ""
        self._allow_draft_updates_during_edit = False
        self.active_draft_label = None

        panel = self.active_draft_panel
        lay = panel.layout()
        if lay is None:
            return
        while lay.count():
            it = lay.takeAt(0)
            w = it.widget()
            if w is not None:
                w.setParent(None)
                w.deleteLater()

        te = QTextEdit(panel)
        te.setPlainText(text)
        te.setAcceptRichText(False)
        panel_h = int(panel.height() or panel.sizeHint().height() or 72)
        edit_h = max(88, (panel_h - 38) * 2)
        edit_h = min(edit_h, 320)
        te.setFixedHeight(edit_h)
        te.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        te.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        te.setStyleSheet(
            f"QTextEdit {{ color: {TEXT_COLOR}; background: rgba(255,255,255,0.92); border: 1px solid rgba(17,17,17,100); padding: 6px; font-size: 15px; font-weight: 600; }}"
        )
        lay.addWidget(te)
        btn_row = QHBoxLayout()
        btn_row.addStretch(1)
        reject_btn = QToolButton(panel)
        send_btn = QToolButton(panel)
        reject_btn.setIcon(self._build_x_cancel_svg_icon(18, "#c62828"))
        send_btn.setIcon(self._build_send_plane_svg_icon(18, "#1565c0"))
        reject_btn.setToolTip("Reject")
        send_btn.setToolTip("Send")
        reject_btn.setCursor(Qt.PointingHandCursor)
        send_btn.setCursor(Qt.PointingHandCursor)
        reject_btn.setIconSize(QSize(14, 14))
        send_btn.setIconSize(QSize(14, 14))
        reject_btn.setFixedSize(20, 20)
        send_btn.setFixedSize(20, 20)
        reject_btn.setStyleSheet(
            "QToolButton { border: none; border-radius: 10px; background: transparent; }"
            "QToolButton:hover { background: rgba(0, 0, 0, 0.08); }"
        )
        send_btn.setStyleSheet(
            "QToolButton { border: none; border-radius: 10px; background: transparent; }"
            "QToolButton:hover { background: rgba(0, 0, 0, 0.08); }"
        )
        reject_btn.clicked.connect(lambda _c=False, cid=self._caption_edit_cap_id: self._finish_caption_edit(True, cid))
        send_btn.clicked.connect(lambda _c=False, cid=self._caption_edit_cap_id: self._finish_caption_edit(False, cid))
        btn_row.addWidget(reject_btn)
        btn_row.addWidget(send_btn)
        lay.addLayout(btn_row)
        self._caption_edit_panel = panel
        self._caption_edit_text_edit = te
        panel.adjustSize()
        te.setFocus()
        te.selectAll()
        # Immediately create a fresh draft row so new interviewer speech continues there
        # while this row stays dedicated to edit-only controls.
        self._allow_draft_updates_during_edit = True
        self.create_new_empty_draft()

    def _restore_active_draft_panel_as_final(self, text: str, clipboard_prompt: str | None = None) -> None:
        panel = self._caption_edit_panel
        if panel is None:
            self.add_text_row(text, "right", clipboard_prompt)
            return
        lay = panel.layout()
        if lay is None:
            lay = QVBoxLayout(panel)
            lay.setContentsMargins(0, 0, 0, 0)
            lay.setSpacing(8)
        while lay.count():
            it = lay.takeAt(0)
            w = it.widget()
            if w is not None:
                w.setParent(None)
                w.deleteLater()
        label = QLabel()
        label.setWordWrap(True)
        label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        label.setStyleSheet(
            f"QLabel {{ color: {TEXT_COLOR}; background: transparent; border: none; font-size: 16px; font-weight: 700; }}"
        )
        label.setText(text or " ")
        _, default_p = build_chunk_prompts(text or "", prompt_store, template_override=_session_chunk_template)
        label.setProperty("copy_text", clipboard_prompt if clipboard_prompt is not None else default_p)
        lay.addWidget(label)
        actions = QHBoxLayout()
        actions.setContentsMargins(0, 0, 0, 2)
        actions.setSpacing(4)
        actions.addStretch(1)
        copy_btn = QToolButton(panel)
        copy_btn.setIcon(self._build_copy_glyph_svg_icon(14, "#455a64"))
        copy_btn.setIconSize(QSize(14, 14))
        copy_btn.setCursor(Qt.PointingHandCursor)
        copy_btn.setToolTip("Copy GPT prompt")
        copy_btn.setFixedSize(20, 20)
        copy_btn.setStyleSheet(
            "QToolButton { border: none; border-radius: 10px; background: transparent; }"
            "QToolButton:hover { background: rgba(0, 0, 0, 0.10); }"
        )
        copy_btn.clicked.connect(
            lambda _checked=False, btn=copy_btn, lbl=label: InterviewWindow._on_copy_bubble_clicked(btn, lbl)
        )
        actions.addWidget(copy_btn)
        lay.addLayout(actions)
        panel.setFixedWidth(self.bubble_width())
        panel.adjustSize()

    def _finish_caption_edit(self, reject: bool, cap_id: str | None) -> None:
        if not self._home_edit_active:
            return
        if cap_id is None or cap_id != self._caption_edit_cap_id:
            return
        if self._caption_edit_sent_once:
            return
        self._caption_edit_sent_once = True
        te = self._caption_edit_text_edit
        panel = self._caption_edit_panel
        if te is None or panel is None:
            self._home_edit_active = False
            return
        edited = te.toPlainText().strip()
        if reject:
            self._restore_active_draft_panel_as_final(edited, None)
        else:
            _, clip_prompt = build_chunk_prompts(
                edited, prompt_store, template_override=_session_chunk_template
            )
            self._restore_active_draft_panel_as_final(edited, clip_prompt)
            if edited:
                threading.Thread(target=process_chunk, args=(edited,), daemon=True).start()
        # Keep current active draft row (created after entering edit) for ongoing interviewer speech.
        self._tick_caption_autoscroll()
        self._caption_edit_text_edit = None
        self._caption_edit_panel = None
        self._caption_edit_cap_id = None
        self._caption_edit_sent_once = False
        self._home_edit_active = False
        self._allow_draft_updates_during_edit = False
        if self._frozen_draft_text:
            self.show_or_update_draft(self._frozen_draft_text)
            self._frozen_draft_text = ""

    def _spawn_new_draft_while_editing(self) -> None:
        if not self._home_edit_active:
            return
        self._allow_draft_updates_during_edit = True
        self.create_new_empty_draft()

    def start_status_animation(self, kind: str, base_text: str):
        if kind == "gpt":
            self._set_gpt_result_text(base_text or "Generating...")
            return
        row, panel, label = self.create_row("left")
        panel.setFixedWidth(self.bubble_width())
        status = StatusRow(label, base_text, row, panel)
        label.setText(self.format_status(base_text, status.step))
        self.status_rows[kind].append(status)
        self.insert_before_draft(row)

    def replace_status_text(self, kind: str, text: str):
        if kind == "gpt":
            self._set_gpt_result_text(text or " ")
            return
        if not self.status_rows[kind]:
            return
        status = self.status_rows[kind][-1]
        status.running = False
        status.label.setText(text or " ")

    def _remove_all_gpt_status_rows(self) -> None:
        self.status_rows["gpt"] = []

    def _remove_gpt_fallback_notice_row(self) -> None:
        self.gpt_fallback_notice_row = None
        self.gpt_fallback_notice_panel = None
        self.gpt_fallback_notice_label = None

    def show_gpt_fallback_notice(self, text: str) -> None:
        self._set_gpt_result_text((text or "").strip() or " ")

    def begin_gpt_thinking_phase(self, thinking_text: str) -> None:
        self._set_gpt_result_text(thinking_text or "Generating...")

    def apply_gpt_session_start(self, http_notice: str, thinking_text: str) -> None:
        self._remove_gpt_fallback_notice_row()
        self._remove_all_gpt_status_rows()
        self._remove_gpt_stream_row()
        notice = (http_notice or "").strip()
        if notice:
            self._set_gpt_result_text(notice)
        else:
            _ = thinking_text
            self._set_gpt_result_text("Generating...")
        self._refresh_gpt_history_nav()

    def _remove_gpt_stream_row(self) -> None:
        self.gpt_stream_row = None
        self.gpt_stream_panel = None
        self.gpt_stream_label = None

    def show_or_update_gpt_stream_partial(self, text: str) -> None:
        body = (text or "").strip()
        if not body:
            return
        self._set_gpt_result_text(body)

    def finalize_gpt_stream_to_answer_row(self, answer: str) -> None:
        self._remove_gpt_fallback_notice_row()
        self._remove_all_gpt_status_rows()
        ans = (answer or "").strip()
        self._remove_gpt_stream_row()
        shown = ans or (self.gpt_result_body.toPlainText() or "").strip()
        self._set_gpt_result_text(shown)
        if shown:
            self._push_gpt_result_history(shown)
            _remember_last_gpt_answer(shown)

    def format_status(self, base: str, step: int) -> str:
        dot_count = 1 + (step % 3)
        return f"{base}{'.' * dot_count}"

    def tick_status_animation(self):
        for kind in ("llama", "gpt"):
            for status in self.status_rows[kind]:
                if not status.running:
                    continue
                status.step += 1
                status.label.setText(self.format_status(status.base, status.step))

    def refresh_panel_widths(self):
        w = self.bubble_width()
        for panel in self.message_panels:
            if panel and panel.isVisible():
                panel.setFixedWidth(w)

    def _mark_user_scroll_activity(self) -> None:
        if getattr(self, "_suppress_user_scroll_signal", False):
            return
        self._user_scroll_cooldown_until = time.time() + float(
            getattr(self, "_user_scroll_cooldown_seconds", 1.4)
        )

    def _on_scrollbar_user_action(self, _action: int) -> None:
        # Any user-driven scrollbar action (arrows, page, slider) extends the cooldown.
        self._mark_user_scroll_activity()

    def scroll_to_bottom(self):
        """Auto-stick to bottom only while the interviewer is actively speaking.

        Skip auto-scroll when:
          - no recent live-caption draft update (interviewer not currently saying),
          - the scrollbar slider is being dragged,
          - a recent wheel/keyboard scroll just happened (short cooldown),
          - the scrollbar is not near the bottom (user scrolled up).
        """
        now = time.time()
        try:
            speaking_until = float(getattr(self, "_interviewer_speaking_until", 0.0))
        except (TypeError, ValueError):
            speaking_until = 0.0
        if now > speaking_until:
            return
        bar = self.scroll.verticalScrollBar()
        if bar.isSliderDown():
            return
        try:
            cooldown_until = float(getattr(self, "_user_scroll_cooldown_until", 0.0))
        except (TypeError, ValueError):
            cooldown_until = 0.0
        if now < cooldown_until:
            return
        if (bar.maximum() - bar.value()) > 6:
            return
        self._suppress_user_scroll_signal = True
        try:
            bar.setValue(bar.maximum())
        finally:
            self._suppress_user_scroll_signal = False

    def process_ui_events(self):
        while True:
            try:
                event = ui_queue.get_nowait()
            except queue.Empty:
                break

            t = event.get("type")
            if t == "draft":
                self.show_or_update_draft(event.get("text", ""))
            elif t == "finalize":
                self.finalize_draft(
                    event.get("text", ""),
                    event.get("clipboard_prompt"),
                )
            elif t == "status_start":
                self.start_status_animation(event.get("kind", "llama"), event.get("text", "Processing"))
            elif t == "status_replace":
                self.replace_status_text(event.get("kind", "llama"), event.get("text", ""))
            elif t == "gpt_session_start":
                self.apply_gpt_session_start(
                    str(event.get("http_notice", "") or ""),
                    str(event.get("thinking_text", "Gpt processing") or "Gpt processing"),
                )
            elif t == "gpt_stream":
                self.show_or_update_gpt_stream_partial(event.get("text", ""))
            elif t == "gpt_final":
                self.finalize_gpt_stream_to_answer_row(event.get("text", ""))
            elif t == "new_empty_draft":
                self.create_new_empty_draft()
            elif t == "caption_edit_hotkey":
                self._try_enter_caption_edit_hotkey()
            elif t == "caption_edit_reject_hotkey":
                self._finish_caption_edit(True, self._caption_edit_cap_id)
            elif t == "caption_edit_spawn_new_draft":
                self._spawn_new_draft_while_editing()
            elif t == "message":
                self.add_text_row(event.get("text", ""), event.get("side", "left"))
            elif t == "f9_paste":
                text = str(event.get("text", "") or "")
                replace_all = bool(event.get("replace_all", False))
                app_inst = QApplication.instance()
                if app_inst is None:
                    self.add_text_row("Could not set clipboard.", "left")
                    continue
                try:
                    app_inst.clipboard().setText(text)
                except Exception:
                    if os.name == "nt":
                        try:
                            _win32_set_clipboard_unicode(text)
                        except OSError:
                            self.add_text_row("Could not set clipboard.", "left")
                            continue
                    else:
                        self.add_text_row("Could not set clipboard.", "left")
                        continue
                threading.Thread(
                    target=_do_keyboard_paste_only, args=(replace_all,), daemon=True
                ).start()
            elif t == "ws_ext":
                handle_ws_extension_payload(event.get("payload") or {})


def queue_ui_message(text, side):
    ui_queue.put({"type": "message", "text": text, "side": side})


def queue_draft_input(text):
    ui_queue.put({"type": "draft", "text": text})


def queue_finalize_input(text: str, clipboard_prompt: str | None = None) -> None:
    payload: dict = {"type": "finalize", "text": text}
    if clipboard_prompt is not None:
        payload["clipboard_prompt"] = clipboard_prompt
    ui_queue.put(payload)


def queue_status_start(kind, base_text):
    ui_queue.put({"type": "status_start", "kind": kind, "text": base_text})


def queue_status_replace(kind, text):
    ui_queue.put({"type": "status_replace", "kind": kind, "text": text})


def queue_gpt_stream_partial(text: str) -> None:
    ui_queue.put({"type": "gpt_stream", "text": text})


def queue_gpt_final(answer: str) -> None:
    _remember_last_gpt_answer(answer)
    ui_queue.put({"type": "gpt_final", "text": answer})


def queue_gpt_session_start(http_notice: str, thinking_text: str) -> None:
    ui_queue.put(
        {
            "type": "gpt_session_start",
            "http_notice": http_notice or "",
            "thinking_text": thinking_text or "Gpt processing",
        }
    )


def queue_new_empty_draft():
    ui_queue.put({"type": "new_empty_draft"})


def queue_caption_edit_hotkey():
    ui_queue.put({"type": "caption_edit_hotkey"})


def queue_caption_edit_reject_hotkey():
    ui_queue.put({"type": "caption_edit_reject_hotkey"})


def queue_caption_edit_spawn_new_draft():
    ui_queue.put({"type": "caption_edit_spawn_new_draft"})


def _suffix_prefix_overlap_len(a: str, b: str, max_chars: int) -> int:
    """Largest k where a[-k:] == b[:k]."""
    max_len = min(len(a), len(b), max(0, int(max_chars)))
    for k in range(max_len, 0, -1):
        if a[-k:] == b[:k]:
            return k
    return 0


def _realign_chunk_boundary(new_text: str, anchor_prefix: str, fallback_idx: int) -> tuple[int, bool]:
    """Map chunk start index into new_text.

    Returns (index, is_confident). Low-confidence mappings are handled conservatively by caller.
    """
    n = len(new_text)
    fb = max(0, min(int(fallback_idx), n))
    anchor = str(anchor_prefix or "")
    if not anchor:
        return fb, False
    if new_text.startswith(anchor):
        return min(len(anchor), n), True
    tail_len = min(_CHUNK_ANCHOR_TAIL_CHARS, len(anchor))
    tail = anchor[-tail_len:] if tail_len else anchor
    pos = new_text.find(tail)
    if pos >= 0:
        return min(pos + len(tail), n), True
    overlap = _suffix_prefix_overlap_len(anchor, new_text, tail_len)
    if overlap >= _MIN_STRONG_OVERLAP_CHARS:
        return min(overlap, n), True
    if n > _BOUNDARY_VISIBLE_TAIL_FALLBACK_CHARS and fb >= n:
        return max(0, n - _BOUNDARY_VISIBLE_TAIL_FALLBACK_CHARS), False
    return fb, False


def snapshot_chunk_since_last_end() -> str:
    """Return caption text since the previous End, then advance the slice cursor to the end."""
    global refined_full_caption, next_chunk_start_index, _caption_chunk_anchor_prefix
    global _boundary_shift_candidate_idx, _boundary_shift_candidate_hits
    with capture_lock:
        full = refined_full_caption
        start = min(next_chunk_start_index, len(full))
        chunk = full[start:].strip()
        next_chunk_start_index = len(full)
        _caption_chunk_anchor_prefix = full
        _boundary_shift_candidate_idx = -1
        _boundary_shift_candidate_hits = 0
    return chunk


def _target_client_id() -> str:
    """Lock sends to the prep-selected client for this interview session."""
    pinned = str(_active_interview_client_id or "").strip()
    if pinned:
        return pinned
    return str(get_selected_client_id() or "").strip()


def _try_ws_send_action(payload: dict, max_wait_s: float = 1.2) -> bool:
    """Extension may reconnect a few hundred ms after prep; retry before HTTP fallback."""
    global _interview_ws
    client_id = _target_client_id()
    if not client_id or not _interview_ws:
        return False
    deadline = time.time() + max_wait_s
    interval = 0.05
    while time.time() < deadline:
        try:
            if _interview_ws.send_action(client_id, payload):
                return True
        except Exception:
            pass
        time.sleep(interval)
    return False


def skip_pending_captions_without_gpt() -> None:
    """Advance slice cursor to end of current caption; drop pending text (no GPT)."""
    global refined_full_caption, next_chunk_start_index, _caption_chunk_anchor_prefix
    global _boundary_shift_candidate_idx, _boundary_shift_candidate_hits
    with capture_lock:
        next_chunk_start_index = len(refined_full_caption)
        _caption_chunk_anchor_prefix = refined_full_caption
        _boundary_shift_candidate_idx = -1
        _boundary_shift_candidate_hits = 0


def process_chunk(raw_chunk):
    global pending_request_id, _interview_ws, _http_fallback_notified, _last_live_poll_for_rid
    prev_rid = (pending_request_id or "").strip()
    _cleaned, final_prompt = build_chunk_prompts(
        raw_chunk, prompt_store, template_override=_session_chunk_template
    )
    request_id = str(uuid.uuid4())
    if prev_rid:
        _clear_interview_live_text(prev_rid)
        _last_live_poll_for_rid = ("", "")
    client_id = _target_client_id()
    if _try_ws_send_action(
        {"type": "INTERVIEWER_CHUNK", "prompt": final_prompt, "request_id": request_id},
    ):
        _http_fallback_notified = False
        pending_request_id = request_id
        queue_gpt_session_start("", "Gpt processing (WebSocket)")
        return
    http_notice = ""
    if client_id and not _http_fallback_notified:
        _http_fallback_notified = True
        http_notice = (
            "Selected Chrome client is not connected. Using HTTP polling for this prompt "
            "(live updates via bridge)."
        )
    queue_gpt_session_start(http_notice, "Gpt processing")
    result = process_caption_chunk(
        raw_chunk=raw_chunk,
        prompt_store=prompt_store,
        log_fn=lambda _t, _m: None,
        template_override=_session_chunk_template,
    )
    pending_request_id = result.get("request_id", "").strip()


def on_press(key):
    global last_end_key_at, last_delete_key_at, last_home_key_at
    global _shift_physically_down, last_f9_key_at
    try:
        if key in (keyboard.Key.shift, keyboard.Key.shift_l, keyboard.Key.shift_r):
            _shift_physically_down = True
            return
        if key == keyboard.Key.delete:
            now = time.time()
            if now - last_delete_key_at < END_KEY_COOLDOWN_SECONDS:
                return
            last_delete_key_at = now
            skip_pending_captions_without_gpt()
            queue_draft_input("")
            return
        if key == keyboard.Key.f9:
            now = time.time()
            if now - last_f9_key_at < F9_COOLDOWN_SECONDS:
                return
            last_f9_key_at = now
            replace_all = _shift_physically_down
            threading.Thread(target=_inject_last_gpt_paste, args=(replace_all,), daemon=True).start()
            return
        if key == keyboard.Key.home:
            now = time.time()
            if now - last_home_key_at < HOME_KEY_COOLDOWN_SECONDS:
                return
            last_home_key_at = now
            queue_caption_edit_hotkey()
            return
        if key == keyboard.Key.end:
            now = time.time()
            if now - last_end_key_at < END_KEY_COOLDOWN_SECONDS:
                return
            last_end_key_at = now
            if _main_interview_window is not None and bool(
                getattr(_main_interview_window, "_home_edit_active", False)
            ):
                queue_caption_edit_spawn_new_draft()
                return
            text = snapshot_chunk_since_last_end()
            if text:
                _, clip_prompt = build_chunk_prompts(
                    text, prompt_store, template_override=_session_chunk_template
                )
                queue_finalize_input(text, clip_prompt)
                queue_new_empty_draft()
                threading.Thread(target=process_chunk, args=(text,), daemon=True).start()
            else:
                queue_ui_message("No captured text yet.", "left")
    except AttributeError:
        pass


def on_release(key):
    global _shift_physically_down
    try:
        if key in (keyboard.Key.shift, keyboard.Key.shift_l, keyboard.Key.shift_r):
            _shift_physically_down = False
    except AttributeError:
        pass


def start_listener():
    with keyboard.Listener(on_press=on_press, on_release=on_release) as listener:
        listener.join()


def run_capture_loop():
    global refined_full_caption, next_chunk_start_index, _caption_chunk_anchor_prefix
    global _boundary_shift_candidate_idx, _boundary_shift_candidate_hits
    with auto.UIAutomationInitializerInThread():
        window = auto.WindowControl(Name=LIVE_CAPTIONS_WINDOW_NAME)
        while app_running:
            try:
                if not window.Exists(maxSearchSeconds=0.2):
                    time.sleep(0.5)
                    continue

                text = get_all_text_controls(window)
                if not text:
                    time.sleep(0.2)
                    continue

                refined_text = normalize_caption_text(text)
                if not refined_text:
                    time.sleep(0.2)
                    continue

                with capture_lock:
                    old_full = refined_full_caption
                    if old_full and refined_text:
                        # If source still includes already-trimmed head text, align to existing buffer start
                        # so the trimmed head does not come back while listening.
                        pos = refined_text.find(old_full)
                        if pos > 0:
                            refined_text = refined_text[pos:]
                    if refined_full_caption == refined_text:
                        time.sleep(0.2)
                        continue
                    prev_start = next_chunk_start_index
                    candidate_start, confident = _realign_chunk_boundary(
                        refined_text, _caption_chunk_anchor_prefix, prev_start
                    )
                    if (
                        not confident
                        and abs(candidate_start - prev_start) >= _BOUNDARY_SHIFT_CONFIRM_CHARS
                    ):
                        if _boundary_shift_candidate_idx == candidate_start:
                            _boundary_shift_candidate_hits += 1
                        else:
                            _boundary_shift_candidate_idx = candidate_start
                            _boundary_shift_candidate_hits = 1
                        if _boundary_shift_candidate_hits < _BOUNDARY_SHIFT_CONFIRM_FRAMES:
                            candidate_start = prev_start
                    else:
                        _boundary_shift_candidate_idx = -1
                        _boundary_shift_candidate_hits = 0
                    next_chunk_start_index = candidate_start
                    if len(refined_text) < next_chunk_start_index:
                        next_chunk_start_index = len(refined_text)
                    refined_full_caption = refined_text
                    start = next_chunk_start_index
                    draft_tail = refined_full_caption[start:]
                    cap_for_file = refined_full_caption

                queue_draft_input(draft_tail)
                with open("live.txt", "w", encoding="utf-8") as file:
                    file.write(cap_for_file)
            except Exception:
                time.sleep(0.3)
            time.sleep(0.2)


def poll_latest_answer_loop():
    global pending_request_id, last_answer_request_id, _last_live_poll_for_rid
    while app_running:
        sleep_s = 1.0
        try:
            with urllib.request.urlopen("http://127.0.0.1:8765/latest-answer", timeout=2) as response:
                payload = json.loads(response.read().decode("utf-8"))
            request_id = str(payload.get("request_id", "")).strip()
            answer = str(payload.get("answer", "")).strip()
            if (
                pending_request_id
                and request_id
                and answer
                and request_id == pending_request_id
                and request_id != last_answer_request_id
            ):
                last_answer_request_id = request_id
                pending_request_id = ""
                _last_live_poll_for_rid = ("", "")
                queue_gpt_final(answer)

            pend = (pending_request_id or "").strip()
            if pend:
                sleep_s = 0.1
                live_txt = _fetch_interview_live_http(pend)
                if live_txt and (pend, live_txt) != _last_live_poll_for_rid:
                    _last_live_poll_for_rid = (pend, live_txt)
                    queue_gpt_stream_partial(live_txt)
        except Exception:
            pass
        time.sleep(sleep_s)


def main():
    global _main_interview_window
    with open("live.txt", "w", encoding="utf-8") as file:
        file.write("")

    bridge.start()

    app = QApplication([])
    session = InterviewWindow(bridge_base="http://127.0.0.1:8765", start_with_prep=True)
    _main_interview_window = session
    session.show()
    app.exec()


if __name__ == "__main__":
    main()
