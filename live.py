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

from pynput import keyboard
import uiautomation as auto

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QIcon, QMouseEvent, QPainter, QPixmap
from PySide6.QtSvg import QSvgRenderer
from PySide6.QtWidgets import (
    QApplication,
    QFrame,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from bridge_server import (
    PromptBridgeServer,
    PromptStore,
    apply_selected_client_context_to_prompt_store,
    get_selected_client_id,
)
from pipeline import build_chunk_prompts, process_caption_chunk
from prep_wizard import PrepWizardWindow
from prompt_templates import build_initial_session_prompt
from ws_bridge import InterviewWSServer


LIVE_CAPTIONS_WINDOW_NAME = "Live Captions"


def _default_windows_live_captions_exe() -> Path:
    windir = os.environ.get("SystemRoot") or os.environ.get("WINDIR") or r"C:\Windows"
    return Path(windir) / "System32" / "LiveCaptions.exe"


def restart_live_caption_exe() -> None:
    """Kill Windows Live Captions (LiveCaptions.exe) and relaunch for a clean session."""
    if os.name != "nt":
        print("[live.py] LiveCaptions restart is only supported on Windows.")
        return
    exe_path = Path(os.environ.get("LIVE_CAPTION_EXE", str(_default_windows_live_captions_exe()))).resolve()
    if not exe_path.is_file():
        print(
            f"[live.py] LiveCaptions not found at {exe_path}. "
            "Install Windows 11 Live Captions or set LIVE_CAPTION_EXE to the full path."
        )
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
        print(f"[live.py] Started {exe_path}")
    except OSError as exc:
        print(f"[live.py] Failed to start {exe_path}: {exc}")
    time.sleep(1.0)


RESUME_TEXT = "Paste your resume content here."
JOB_DESCRIPTION_TEXT = "Paste job description here."
ADDITIONAL_CONTEXT_TEXT = (
    "Answer naturally in first person. Keep concise. "
    "Do not add explanations or meta commentary."
)

BUBBLE_WIDTH_PERCENT = 0.85
TEXT_COLOR = "#111111"
DIVIDER_COLOR = "#444444"

capture_lock = threading.Lock()
# Full normalized caption from the Live Captions window (listener; never cleared on End).
refined_full_caption = ""
# Character index in refined_full_caption where the next End slice starts (inclusive).
next_chunk_start_index = 0
last_end_key_at = 0.0
last_delete_key_at = 0.0
last_page_down_at = 0.0
END_KEY_COOLDOWN_SECONDS = 0.8

_interview_ws = None  # InterviewWSServer instance after prep completes

ui_queue: queue.Queue = queue.Queue()
app_running = True
pending_request_id = ""
last_answer_request_id = ""

prompt_store = PromptStore()
bridge = PromptBridgeServer(prompt_store)


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


def handle_ws_extension_payload(payload: dict) -> None:
    global pending_request_id, last_answer_request_id
    typ = str(payload.get("type", "")).strip()
    if typ == "LIVE_ANSWER":
        rid = str(payload.get("request_id", "")).strip()
        text = str(payload.get("text", ""))
        if rid and pending_request_id and rid == pending_request_id and text:
            queue_status_replace("gpt", f"Gpt (live):\n{text}")
        return
    if typ == "FINAL_ANSWER":
        rid = str(payload.get("request_id", "")).strip()
        answer = str(payload.get("answer", "")).strip()
        if not rid or not answer or rid != pending_request_id:
            return
        last_answer_request_id = rid
        pending_request_id = ""
        prompt_store.set_answer(rid, answer)
        queue_status_replace("gpt", f"Gpt answer:\n{answer}")
        return
    if typ == "STATUS":
        msg = str(payload.get("message", "")).strip()
        if msg:
            queue_ui_message(f"Extension: {msg}", "left")


class StatusRow:
    def __init__(self, label: QLabel, base: str):
        self.label = label
        self.base = base
        self.step = 0
        self.running = True


class InterviewWindow(QWidget):
    def __init__(self, active_user_label: str = ""):
        super().__init__()
        self.drag_offset = None
        self.resize_margin = 8
        self.resize_edges = None
        self.resize_start_geom = None
        self.resize_start_pos = None
        self.min_w = 520
        self.min_h = 320
        self.active_draft_label: QLabel | None = None
        self.active_draft_row: QWidget | None = None
        self.status_rows: dict[str, list[StatusRow]] = {"llama": [], "gpt": []}
        self.message_panels: list[QWidget] = []

        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setMouseTracking(True)
        self.resize(820, 620)

        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)

        self.shell = QFrame()
        self.shell.setStyleSheet(
            """
            QFrame {
                background-color: rgba(255, 255, 255, 217);
                border: none;
                border-radius: 20px;
            }
            """
        )
        root.addWidget(self.shell)

        shell_layout = QVBoxLayout(self.shell)
        shell_layout.setContentsMargins(14, 14, 14, 14)
        shell_layout.setSpacing(0)

        topbar = QHBoxLayout()
        if active_user_label:
            self.account_label = QLabel(f"Using: {active_user_label}")
            self.account_label.setStyleSheet("font-size: 12px; font-weight: 600; color: #222;")
            topbar.addWidget(self.account_label)
        topbar.addStretch(1)
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

        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(True)
        self.scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.scroll.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.scroll.setFrameShape(QFrame.NoFrame)
        self.scroll.setStyleSheet("QScrollArea { background: transparent; border: none; }")

        self.content = QWidget()
        self.content.setStyleSheet("QWidget { background: transparent; border: none; }")
        self.content_layout = QVBoxLayout(self.content)
        self.content_layout.setContentsMargins(0, 0, 0, 0)
        self.content_layout.setSpacing(10)
        self.content_layout.addStretch(1)
        self.scroll.setWidget(self.content)
        shell_layout.addWidget(self.scroll)

        self.queue_timer = QTimer(self)
        self.queue_timer.timeout.connect(self.process_ui_events)
        self.queue_timer.start(100)

        self.anim_timer = QTimer(self)
        self.anim_timer.timeout.connect(self.tick_status_animation)
        self.anim_timer.start(350)

        self.resize_timer = QTimer(self)
        self.resize_timer.setSingleShot(True)
        self.resize_timer.timeout.connect(self.refresh_panel_widths)

        # Hover cursor updates should work before clicking.
        self._enable_hover_tracking(self)

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
        if event.button() == Qt.LeftButton and not self.close_btn.geometry().contains(event.position().toPoint()):
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
        if event.type() == event.Type.MouseMove:
            # Map child hover position into window coordinates.
            try:
                local = self.mapFromGlobal(watched.mapToGlobal(event.position().toPoint()))
                self._update_cursor(local)
            except Exception:
                pass
        return super().eventFilter(watched, event)

    def mouseReleaseEvent(self, event: QMouseEvent):
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

    def resizeEvent(self, event):
        self.resize_timer.start(20)
        super().resizeEvent(event)

    def bubble_width(self) -> int:
        inner = self.shell.width() - 28
        return max(320, int(inner * BUBBLE_WIDTH_PERCENT))

    def create_row(self, side: str) -> tuple[QWidget, QFrame, QLabel]:
        row = QWidget()
        h = QHBoxLayout(row)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(0)

        panel = QFrame()
        panel.setMinimumHeight(44)
        panel.setMaximumHeight(2000)
        panel.setStyleSheet("QFrame { background: transparent; border: none; }")
        panel_layout = QVBoxLayout(panel)
        panel_layout.setContentsMargins(0, 0, 0, 0)
        panel_layout.setSpacing(8)

        label = QLabel()
        label.setWordWrap(True)
        label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        label.setStyleSheet(f"QLabel {{ color: {TEXT_COLOR}; background: transparent; border: none; font-size: 16px; }}")
        panel_layout.addWidget(label)

        divider = QFrame()
        divider.setFixedHeight(1)
        divider.setStyleSheet(f"QFrame {{ background: {DIVIDER_COLOR}; border: none; }}")
        panel_layout.addWidget(divider)

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

    def add_text_row(self, text: str, side: str):
        row, panel, label = self.create_row(side)
        label.setText(text or " ")
        self.insert_before_draft(row)
        panel.setFixedWidth(self.bubble_width())
        panel.adjustSize()
        self.scroll_to_bottom()

    def show_or_update_draft(self, text: str):
        if self.active_draft_label is None:
            row, panel, label = self.create_row("right")
            label.setText(text or " ")
            stretch_index = self.content_layout.count() - 1
            self.content_layout.insertWidget(stretch_index, row)
            panel.setFixedWidth(self.bubble_width())
            self.active_draft_row = row
            self.active_draft_label = label
        else:
            self.active_draft_label.setText(text or " ")
        self.scroll_to_bottom()

    def finalize_draft(self, text: str):
        if self.active_draft_label is not None:
            self.active_draft_label.setText(text or " ")
            self.active_draft_label = None
            self.active_draft_row = None
            self.scroll_to_bottom()
            return
        self.add_text_row(text, "right")

    def create_new_empty_draft(self):
        if self.active_draft_label is None:
            self.show_or_update_draft("")

    def start_status_animation(self, kind: str, base_text: str):
        row, panel, label = self.create_row("left")
        panel.setFixedWidth(self.bubble_width())
        status = StatusRow(label, base_text)
        label.setText(self.format_status(base_text, status.step))
        self.status_rows[kind].append(status)
        self.insert_before_draft(row)
        self.scroll_to_bottom()

    def replace_status_text(self, kind: str, text: str):
        if not self.status_rows[kind]:
            return
        status = self.status_rows[kind][-1]
        status.running = False
        status.label.setText(text or " ")
        self.scroll_to_bottom()

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

    def scroll_to_bottom(self):
        bar = self.scroll.verticalScrollBar()
        bar.setValue(bar.maximum())

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
                self.finalize_draft(event.get("text", ""))
            elif t == "status_start":
                self.start_status_animation(event.get("kind", "llama"), event.get("text", "Processing"))
            elif t == "status_replace":
                self.replace_status_text(event.get("kind", "llama"), event.get("text", ""))
            elif t == "new_empty_draft":
                self.create_new_empty_draft()
            elif t == "message":
                self.add_text_row(event.get("text", ""), event.get("side", "left"))
            elif t == "ws_ext":
                handle_ws_extension_payload(event.get("payload") or {})


def queue_ui_message(text, side):
    ui_queue.put({"type": "message", "text": text, "side": side})


def queue_draft_input(text):
    ui_queue.put({"type": "draft", "text": text})


def queue_finalize_input(text):
    ui_queue.put({"type": "finalize", "text": text})


def queue_status_start(kind, base_text):
    ui_queue.put({"type": "status_start", "kind": kind, "text": base_text})


def queue_status_replace(kind, text):
    ui_queue.put({"type": "status_replace", "kind": kind, "text": text})


def queue_new_empty_draft():
    ui_queue.put({"type": "new_empty_draft"})


def snapshot_chunk_since_last_end() -> str:
    """Return caption text since the previous End, then advance the slice cursor to the end."""
    global refined_full_caption, next_chunk_start_index
    with capture_lock:
        full = refined_full_caption
        start = next_chunk_start_index
        if len(full) < start:
            # Buffer shrank (reset or heavy re-edit); treat as fresh tail from the start.
            start = 0
        chunk = full[start:].strip()
        next_chunk_start_index = len(full)
    return chunk


def skip_pending_captions_without_gpt() -> None:
    """Advance slice cursor to end of current caption; drop pending text (no GPT)."""
    global refined_full_caption, next_chunk_start_index
    with capture_lock:
        full = refined_full_caption
        if len(full) < next_chunk_start_index:
            next_chunk_start_index = 0
        next_chunk_start_index = len(full)


def process_chunk(raw_chunk):
    global pending_request_id, _interview_ws
    ctx = prompt_store.get_context()
    resume = (ctx.get("resume") or "").strip() or RESUME_TEXT
    jd = (ctx.get("job_description") or "").strip() or JOB_DESCRIPTION_TEXT
    _cleaned, final_prompt = build_chunk_prompts(
        raw_chunk, resume, jd, ADDITIONAL_CONTEXT_TEXT
    )
    request_id = str(uuid.uuid4())
    client_id = get_selected_client_id()
    if client_id and _interview_ws and _interview_ws.send_action(
        client_id,
        {"type": "INTERVIEWER_CHUNK", "prompt": final_prompt, "request_id": request_id},
    ):
        pending_request_id = request_id
        queue_status_start("gpt", "Gpt processing (WebSocket)")
        return
    if client_id:
        print("[live.py] Selected Chrome client is not connected. Falling back to HTTP.")
        queue_ui_message(
            "Selected Chrome client is not connected. Using HTTP polling for this prompt.",
            "left",
        )
    queue_status_start("gpt", "Gpt processing")
    result = process_caption_chunk(
        raw_chunk=raw_chunk,
        prompt_store=prompt_store,
        resume_text=resume,
        job_description_text=jd,
        additional_context_text=ADDITIONAL_CONTEXT_TEXT,
        log_fn=lambda _t, _m: None,
    )
    pending_request_id = result.get("request_id", "").strip()


def send_initial_interview_prompt() -> None:
    global pending_request_id, _interview_ws
    ctx = prompt_store.get_context()
    resume = (ctx.get("resume") or "").strip() or RESUME_TEXT
    jd = (ctx.get("job_description") or "").strip() or JOB_DESCRIPTION_TEXT
    prompt = build_initial_session_prompt(
        resume_text=resume,
        job_description_text=jd,
        additional_context_text=ADDITIONAL_CONTEXT_TEXT,
    )
    request_id = str(uuid.uuid4())
    client_id = get_selected_client_id()
    if client_id and _interview_ws and _interview_ws.send_action(
        client_id,
        {"type": "INITIAL_PROMPT", "prompt": prompt, "request_id": request_id},
    ):
        pending_request_id = request_id
        queue_status_start("gpt", "Session start (WebSocket)")
        return
    if client_id:
        print("[live.py] Selected Chrome client is not connected. Falling back to HTTP.")
        queue_ui_message(
            "Selected Chrome client is not connected. Using HTTP polling for initial prompt.",
            "left",
        )
    queue_status_start("gpt", "Session start (HTTP)")
    metadata = prompt_store.set_prompt(prompt)
    pending_request_id = str(metadata.get("request_id", "")).strip()


def on_key_event(key):
    global last_end_key_at, last_delete_key_at, last_page_down_at
    try:
        if key == keyboard.Key.page_down:
            now = time.time()
            if now - last_page_down_at < END_KEY_COOLDOWN_SECONDS:
                return
            last_page_down_at = now
            threading.Thread(target=send_initial_interview_prompt, daemon=True).start()
            return
        if key == keyboard.Key.delete:
            now = time.time()
            if now - last_delete_key_at < END_KEY_COOLDOWN_SECONDS:
                return
            last_delete_key_at = now
            skip_pending_captions_without_gpt()
            queue_draft_input("")
            return
        if key == keyboard.Key.end:
            now = time.time()
            if now - last_end_key_at < END_KEY_COOLDOWN_SECONDS:
                return
            last_end_key_at = now
            text = snapshot_chunk_since_last_end()
            if text:
                queue_finalize_input(text)
                queue_new_empty_draft()
                threading.Thread(target=process_chunk, args=(text,), daemon=True).start()
            else:
                queue_ui_message("No captured text yet.", "left")
    except AttributeError:
        pass


def start_listener():
    with keyboard.Listener(on_press=on_key_event) as listener:
        listener.join()


def run_capture_loop():
    global refined_full_caption, next_chunk_start_index
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
                    if len(refined_text) < next_chunk_start_index:
                        next_chunk_start_index = 0
                    if refined_full_caption == refined_text:
                        time.sleep(0.2)
                        continue
                    refined_full_caption = refined_text
                    start = next_chunk_start_index
                    draft_tail = refined_text[start:]

                queue_draft_input(draft_tail)
                with open("live.txt", "w", encoding="utf-8") as file:
                    file.write(refined_text)
            except Exception:
                time.sleep(0.3)
            time.sleep(0.2)


def poll_latest_answer_loop():
    global pending_request_id, last_answer_request_id
    while app_running:
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
                queue_status_replace("gpt", f"Gpt answer:\n{answer}")
        except Exception:
            pass
        time.sleep(1.0)


def main():
    with open("live.txt", "w", encoding="utf-8") as file:
        file.write("")

    bridge.start()

    app = QApplication([])
    wizard = PrepWizardWindow(bridge_base="http://127.0.0.1:8765")

    def launch_interview_session(display_email: str, _client_id: str) -> None:
        global _interview_ws
        wizard.hide()
        apply_selected_client_context_to_prompt_store(prompt_store)
        _interview_ws = InterviewWSServer(ui_queue)
        _interview_ws.start()
        restart_live_caption_exe()
        threading.Thread(target=start_listener, daemon=True).start()
        threading.Thread(target=run_capture_loop, daemon=True).start()
        threading.Thread(target=poll_latest_answer_loop, daemon=True).start()
        interview = InterviewWindow(active_user_label=display_email)
        interview.show()
        wizard.deleteLater()

    wizard.finished.connect(launch_interview_session)
    wizard.show()
    app.exec()


if __name__ == "__main__":
    main()
