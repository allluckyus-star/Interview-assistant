import json
import os
import queue
import sys
import re
import uuid
import subprocess
import threading
import time
from datetime import datetime
import urllib.error
import urllib.request
from pathlib import Path
from typing import Tuple
from urllib.parse import urlencode

from pynput import keyboard
import uiautomation as auto

from PySide6.QtCore import (
    QAbstractAnimation,
    QEasingCurve,
    QEvent,
    QParallelAnimationGroup,
    QPoint,
    QPropertyAnimation,
    QSize,
    Qt,
    QTimer,
)
from PySide6.QtGui import QAction, QGuiApplication, QIcon, QMouseEvent, QPainter, QPainterPath, QPixmap, QRegion
from PySide6.QtSvg import QSvgRenderer
from PySide6.QtWidgets import (
    QApplication,
    QFileDialog,
    QFrame,
    QGraphicsOpacityEffect,
    QHBoxLayout,
    QLabel,
    QMenu,
    QMessageBox,
    QPlainTextEdit,
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

import app_prompt_files

from bridge_server import (
    PromptBridgeServer,
    PromptStore,
    _clear_interview_live_text,
    apply_selected_client_context_to_prompt_store,
    get_selected_client_id,
    set_selected_client_id,
)
from pipeline import build_chunk_prompts, process_caption_chunk
from deploy_extension import ensure_extension_deployed
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


# Windows 10 2004+: SetWindowDisplayAffinity — see _desired_display_affinity_for_window for policy.
_WDA_NONE = 0
_WDA_EXCLUDEFROMCAPTURE = 0x00000011
# Legacy: exclude only on primary, WDA_NONE on other displays (weaker stealth; for bad USB-HDMI paths).
_ENV_PRIMARY_ONLY_CAPTURE_EXCLUDE = "INTERVIEW_ASSISTANT_PRIMARY_ONLY_CAPTURE_EXCLUDE"
_ENV_DEBUG_AFFINITY = "INTERVIEW_ASSISTANT_DEBUG_AFFINITY"


def _env_primary_only_capture_exclude() -> bool:
    v = (os.environ.get(_ENV_PRIMARY_ONLY_CAPTURE_EXCLUDE) or "").strip().lower()
    return v in ("1", "true", "yes", "on")


def _env_debug_affinity() -> bool:
    v = (os.environ.get(_ENV_DEBUG_AFFINITY) or "").strip().lower()
    return v in ("1", "true", "yes", "on")


def _affinity_constant_name(affinity: int) -> str:
    if affinity == _WDA_NONE:
        return "WDA_NONE"
    if affinity == _WDA_EXCLUDEFROMCAPTURE:
        return "WDA_EXCLUDEFROMCAPTURE"
    return f"WDA_0x{int(affinity):x}"


def _screen_key_at_window_center(w: QWidget) -> tuple:
    scr = QGuiApplication.screenAt(w.geometry().center())
    if scr is None:
        return ("", None)
    g = scr.geometry()
    return (scr.name() or "", (g.x(), g.y(), g.width(), g.height()))


def _primary_vs_screen_at_debug_suffix(w: QWidget) -> str:
    pri = QGuiApplication.primaryScreen()
    scr = QGuiApplication.screenAt(w.geometry().center())
    if pri is None:
        ppart = "primary=? "
    else:
        pg = pri.geometry()
        ppart = (
            f"primary={pri.name() or '(unnamed)'} "
            f"geom={pg.x()},{pg.y()} {pg.width()}x{pg.height()} "
        )
    if scr is None:
        spart = "screenAt=? "
    else:
        sg = scr.geometry()
        spart = (
            f"screenAt={scr.name() or '(unnamed)'} "
            f"geom={sg.x()},{sg.y()} {sg.width()}x{sg.height()}"
        )
    return ppart + "| " + spart


def _desired_display_affinity_for_window(w: QWidget) -> int:
    """Return affinity for ``SetWindowDisplayAffinity`` from the window's current geometry.

    Default is ``WDA_EXCLUDEFROMCAPTURE`` on every display so stealth matches common overlays: the
    flag follows whichever monitor contains the window center (updated on move/resize/show), not
    "non-primary == pretend we are being shared."

    If a secondary display fails to composite exclude-from-capture (rare USB-HDMI / hybrid cases),
    set ``INTERVIEW_ASSISTANT_PRIMARY_ONLY_CAPTURE_EXCLUDE=1`` to use exclude only while the center
    lies on the primary screen and ``WDA_NONE`` elsewhere (capture may include the window there).
    """
    if os.name != "nt":
        return _WDA_NONE
    if not _env_primary_only_capture_exclude():
        return _WDA_EXCLUDEFROMCAPTURE
    scr = QGuiApplication.screenAt(w.geometry().center())
    pri = QGuiApplication.primaryScreen()
    if scr is None or pri is None:
        return _WDA_EXCLUDEFROMCAPTURE
    if scr is not pri:
        return _WDA_NONE
    return _WDA_EXCLUDEFROMCAPTURE


def _win32_set_window_display_affinity(hwnd: int, affinity: int) -> int:
    """Return Win32 BOOL as int: non-zero success, zero failure."""
    if os.name != "nt" or not hwnd:
        return 0
    import ctypes

    user32 = ctypes.windll.user32
    user32.SetWindowDisplayAffinity.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    user32.SetWindowDisplayAffinity.restype = ctypes.c_int
    return int(
        user32.SetWindowDisplayAffinity(ctypes.c_void_p(hwnd), ctypes.c_uint32(int(affinity)))
    )


_AFFINITY_DBG_PREV_UNSET = object()


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

_TOAST_MARGIN_TOP = 12
_TOAST_GAP_PX = 6
_TOAST_MIN_OUTER = 88
_TOAST_MAX_OUTER = 288
_TOAST_LABEL_MAX_WIDTH = 248
_TOAST_FONT_PX = 11
_TOAST_SHOW_MS = 220
_TOAST_HIDE_MS = 200
_TOAST_SLIDE_PX = 12
_TOAST_LINGER_SUCCESS_MS = 2600
_TOAST_LINGER_WARNING_MS = 3600
_TOAST_LINGER_ERROR_MS = 4800
_TOAST_TEXT_MAX_CHARS = 200
_TOPBAR_CYAN_BG = "#b2ebf2"
_TOPBAR_CYAN_HOVER = "#80deea"
_TOPBAR_CLOSE_HOVER_BG = "#ff8a80"

# Top bar / settings header: light cyan pill buttons; shared hover (except close uses red hover).

# Caption bubble row bottom border — reuse for settings horizontal splitter handle.
CAPTION_ROW_BOTTOM_BORDER_COLOR = "rgba(17,17,17,80)"


def _toast_trim_message(text: str) -> str:
    t = (text or "").strip()
    if len(t) <= _TOAST_TEXT_MAX_CHARS:
        return t
    return f"{t[: _TOAST_TEXT_MAX_CHARS - 1]}…"


capture_lock = threading.Lock()
# Rolling normalized caption from Live Captions (not trimmed while listening).
refined_full_caption = ""
previous_refined_text = ""
fixed_caption = ""
# Character index in refined_full_caption where the next End slice starts (inclusive).
next_chunk_start_index = 0
next_chunk_start_text = ""
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

# Text F9 / Shift+F9 paste: finalized answers plus current streaming partial while a reply is in progress.
last_gpt_answer_for_paste = ""
last_f9_key_at = 0.0
F9_COOLDOWN_SECONDS = 0.45
_shift_physically_down = False

_interview_ws = None  # InterviewWSServer instance after prep completes
_main_interview_window = None  # InterviewWindow — strong ref after handoff
_active_interview_client_id = ""  # Client chosen in prep wizard for this live session only.
_keyboard_listener = None  # pynput.Listener — stopped on window close so the process can exit

ui_queue: queue.Queue = queue.Queue()
app_running = True

# Append-only session transcript: every interviewer capture (End, Delete skip, Home buffer, reject,
# sent-to-GPT) plus GPT finals / honest placeholders. Cleared when the live interview starts.
_interview_history_lock = threading.Lock()
_interview_history_events: list[dict[str, str]] = []

pending_request_id = ""
last_answer_request_id = ""
# Initial interview template snapshot pinned at prep handoff (see _session_initial_template).
# Live chunk prompts use mode-specific templates via _active_chunk_template_override().
_session_initial_template: str | None = None
# Guidance mode toggled from the top-bar cycle button. "read" uses
# READ_MODE_CHUNK_TEMPLATE; "type" overrides with TYPE_MODE_CHUNK_TEMPLATE
# ([READ-n]/[TYPE-n]); "behavioral" overrides with BEHAVIORAL_MODE_CHUNK_TEMPLATE
# for STAR-style guidance.
_session_mode: str = "read"
TYPE_MODE_CHUNK_TEMPLATE = """You are producing TYPE MODE live interview guidance.

Use this only when the interviewer asks me to type, code, draw, use Notepad, or show something on screen.

If there is no clear typing/coding/drawing task, output exactly:
WAITING

OUTPUT FORMAT:
- Output everything inside one fenced code block.
- Use the correct code fence language when known: ```python, ```sql, ```text, ```javascript.
- Do not output anything outside the code block.
- This prevents Markdown from treating # comments as headings.

CONTENT TYPES:
Use these markers:

[SAY-n]
Natural words I say out loud to the interviewer. This is not typed into the editor.

[READ-n]
Short words I say while typing the next line.

[TYPE-n]
Marker comment before the exact line I type next.

COMMENT STYLE:
Inside the code block, READ and TYPE must use the target language comment syntax.

Python / shell / YAML / diagram / plain text:
# [READ-n]
# [TYPE-n]

JavaScript / TypeScript / Java / C# / C++:
// [READ-n]
// [TYPE-n]

SQL:
-- [READ-n]
-- [TYPE-n]

HTML / XML:
<!-- [READ-n] -->
<!-- [TYPE-n] -->

RULES:
- [SAY-n] must sound natural in a real interview.
- Do not say “the interviewer can see,” “for the interviewer,” or “I am going to impress the interviewer.”
- Speak like a real engineer explaining work while coding.
- [SAY-n] should be 1–3 short sentences.
- [READ-n] should be one short sentence.
- Every [TYPE-n] must be followed by exactly ONE typed line.
- Preserve exact indentation for code.
- Keep code valid if all [SAY-n] lines are removed.
- Never output raw [READ-n] or [TYPE-n] outside comments.
- No fake experience, fake metrics, or fake company details.
- Use candidate briefing and job briefing already provided.

FOR CODE:
- Start with [SAY-1] for the approach.
- Then use commented [READ-n] and [TYPE-n] before every typed code line.
- After each logical block, add [SAY-n] for trade-off, edge case, or design reasoning.

FOR DIAGRAM:
- Use ```text.
- Use # [READ-n] and # [TYPE-n].
- Keep diagram spacing clean.
- Add [SAY-n] before and after the diagram for natural explanation.

GOOD SPOKEN STYLE:
[SAY-1]
I’ll keep the design small but realistic. I’ll separate ingestion, retrieval, and generation so each part can be replaced later without changing the whole API.

BAD SPOKEN STYLE:
[SAY-1]
I’ll separate the RAG project into ingestion, retrieval, and generation so the interviewer can see the full flow clearly.

Interviewer said:
\"\"\"
{cleaned_interviewer_intent}
\"\"\"
"""

BEHAVIORAL_MODE_CHUNK_TEMPLATE = """Behavioral Mode prompt:
Use this when the interviewer asks about teamwork, conflict, leadership, failure, ambiguity, ownership, communication, deadlines, prioritization, business partners, or past experience.

You are producing behavioral interview speaking guidance.

If there is no clear behavioral question, output exactly:
WAITING

Otherwise output exactly this structure:

SHORT ANSWER:
[1–2 natural sentences I can say immediately. Max 45 words.]

STORY ANSWER:
[A realistic first-person story using situation → action → result → reflection. 4–7 sentences. Max 160 words.]

FOLLOW-UP DEPTH:
[1–2 concise points I can add if the interviewer asks for more detail.]

Rules:
- First person only.
- Natural spoken English.
- No generic leadership slogans.
- No fake experience, fake metrics, or fake company details.
- Use candidate briefing and job briefing already provided.
- Prefer practical engineering behavior: communication, trade-offs, ownership, alignment, risk handling.
- Make it sound like a real engineer speaking under interview pressure.
- Do not over-polish; keep it human and believable.

Interviewer said:
\"\"\"
{cleaned_interviewer_intent}
\"\"\"
"""

READ_MODE_CHUNK_TEMPLATE = """You are producing live interview speaking guidance.

If there is no clear question, output exactly:
WAITING

Otherwise output exactly this structure:

SHORT ANSWER:
[1–2 natural sentences I can say immediately. Max 45 words.]

DETAILED ANSWER:
[3–5 additional sentences that extend the answer naturally. Max 120 words.]

FOLLOW-UP DEPTH:
[1–2 technical depth points, concise.]

Rules:
- First person only.
- No analysis.
- No generic textbook answer.
- No fake experience, fake metrics, or fake company details.
- Use the candidate briefing and job briefing already provided.
- Make it sound like a real engineer speaking under interview pressure.
- Prefer clear trade-offs and practical implementation details.

Specific-info rule:
- If the interviewer asks for a specific fact, version, tool, library, API, command, number, timeline, cloud service, framework, model, or name, answer directly in the first sentence.
- Do not give a broad explanation before answering the specific point.
- If the exact fact is known from the candidate briefing or job briefing, state it clearly.
- If the exact fact is not known but can be reasonably inferred from my work period, tech stack, or project context, give it as an estimate, not as a confirmed fact.
- Use wording like:
  "I don't remember the exact version, but based on that project period, it was likely around [estimated range]. My work was mainly with [tool/API/service] for [specific purpose]."
- If there is no safe estimate, say the closest truthful answer:
  "I don't remember the exact version, but I used [tool/API/service] mainly for [specific purpose]."
- Do not invent exact versions, dates, metrics, commands, or tools.
- Keep the answer confident and practical, not apologetic.
- After the direct answer, add one practical detail about how I used it.

Interviewer said:
\"\"\"
{cleaned_interviewer_intent}
\"\"\"
"""


def _http_post_json_local(url: str, payload: dict) -> tuple[dict, int]:
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        method="POST",
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=45) as resp:
            return json.loads(resp.read().decode("utf-8")), int(resp.status)
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            return json.loads(raw), int(exc.code)
        except json.JSONDecodeError:
            return {"ok": False, "error": raw or str(exc)}, int(exc.code)
    except OSError as exc:
        return {"ok": False, "error": str(exc)}, 0


def _load_mode_prompt_overrides_from_json() -> None:
    """Merge ~/.interview_assistant/mode_prompts.json keys read/type/behavioral into live chunk templates."""
    global READ_MODE_CHUNK_TEMPLATE, TYPE_MODE_CHUNK_TEMPLATE, BEHAVIORAL_MODE_CHUNK_TEMPLATE
    path = Path.home() / ".interview_assistant" / "mode_prompts.json"
    try:
        raw = path.read_text(encoding="utf-8")
        data = json.loads(raw)
    except (OSError, json.JSONDecodeError):
        return
    if not isinstance(data, dict):
        return
    r = data.get("read")
    if isinstance(r, str) and r.strip():
        READ_MODE_CHUNK_TEMPLATE = r
    t = data.get("type")
    if isinstance(t, str) and t.strip():
        TYPE_MODE_CHUNK_TEMPLATE = t
    b = data.get("behavioral")
    if isinstance(b, str) and b.strip():
        BEHAVIORAL_MODE_CHUNK_TEMPLATE = b


def _persist_mode_prompts_to_json() -> None:
    global READ_MODE_CHUNK_TEMPLATE, TYPE_MODE_CHUNK_TEMPLATE, BEHAVIORAL_MODE_CHUNK_TEMPLATE
    root = Path.home() / ".interview_assistant"
    root.mkdir(parents=True, exist_ok=True)
    path = root / "mode_prompts.json"
    path.write_text(
        json.dumps(
            {
                "read": READ_MODE_CHUNK_TEMPLATE,
                "type": TYPE_MODE_CHUNK_TEMPLATE,
                "behavioral": BEHAVIORAL_MODE_CHUNK_TEMPLATE,
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
        newline="\n",
    )


_load_mode_prompt_overrides_from_json()


def _active_chunk_template_override() -> str | None:
    """Resolve which chunk-prompt template should be used for the next outgoing prompt.

    Read mode uses READ_MODE_CHUNK_TEMPLATE. Type and behavioral modes use fixed
    templates so the mode switch takes effect immediately mid-interview.
    """
    if _session_mode == "read":
        return READ_MODE_CHUNK_TEMPLATE
    if _session_mode == "type":
        return TYPE_MODE_CHUNK_TEMPLATE
    if _session_mode == "behavioral":
        return BEHAVIORAL_MODE_CHUNK_TEMPLATE
    return None


# One-shot UI notice when HTTP fallback is used (avoid spamming every chunk).
_http_fallback_notified = False
# Dedupe live partial updates from /interview-live polling.
_last_live_poll_for_rid: Tuple[str, str] = ("", "")
# Typed-directly-in-ChatGPT mirror (extension must use same id — see extension/content.js).
MANUAL_GPT_REQUEST_ID = "__manual_chatgpt_tab__"
_last_manual_live_poll_for_rid: Tuple[str, str] = ("", "")
_last_manual_final_seen: str = ""

prompt_store = PromptStore()
app_prompt_files.ensure_prompt_template_files()
app_prompt_files.load_prompt_templates_into_store(prompt_store)
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
    global pending_request_id, last_answer_request_id, _last_live_poll_for_rid, _last_manual_final_seen
    typ = str(payload.get("type", "")).strip()
    if typ == "MANUAL_GPT_LIVE":
        # Latest assistant text when the user typed in ChatGPT (no caption / bridge prompt).
        if pending_request_id:
            return
        text = str(payload.get("text", "")).strip()
        if text:
            queue_gpt_stream_partial(text)
        return
    if typ == "MANUAL_GPT_FINAL":
        if pending_request_id:
            return
        answer = str(payload.get("answer", "")).strip()
        if not answer or answer == _last_manual_final_seen:
            return
        _last_manual_final_seen = answer
        queue_gpt_final(answer, push_history=False)
        return
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
        _last_manual_final_seen = ""
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
        # Top-level QWidget does not quit QApplication on close by default; without this,
        # the window hides but python.exe keeps running (still in Task Manager).
        self.setAttribute(Qt.WidgetAttribute.WA_QuitOnClose, True)
        self.drag_offset = None
        self.resize_margin = 8
        self.resize_edges = None
        self.resize_start_geom = None
        self.resize_start_pos = None
        self.min_w = 520
        self.min_h = 320
        self._bridge_base = bridge_base.rstrip("/")
        self._start_with_prep = start_with_prep
        self._exclude_from_capture = os.name == "nt"
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

        # Tool window: no taskbar button on Windows; we do not use the system tray.
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.FramelessWindowHint
            | Qt.WindowStaysOnTopHint
            | Qt.WindowType.NoDropShadowWindowHint
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
        self.top_center_drag_host = QWidget()
        self.top_center_drag_host.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Preferred)
        self.top_center_drag_host.setMinimumHeight(26)
        self.top_center_drag_host.setCursor(Qt.OpenHandCursor)
        self.top_center_drag_host.installEventFilter(self)
        cen = QHBoxLayout(self.top_center_drag_host)
        cen.setContentsMargins(0, 0, 0, 0)
        cen.setSpacing(8)
        cen.addStretch(1)
        self.opacity_lbl = QLabel("Bg")
        self.opacity_lbl.setStyleSheet("font-size: 11px; font-weight: 700; color: #222;")
        self.opacity_slider = QSlider(Qt.Orientation.Horizontal)
        self.opacity_slider.setRange(0, 100)
        self.opacity_slider.setValue(80)
        self.opacity_slider.setMinimumWidth(120)
        self.opacity_slider.setMaximumWidth(220)
        self.opacity_slider.valueChanged.connect(self._on_opacity_slider_changed)
        cen.addWidget(self.opacity_lbl, 0, Qt.AlignmentFlag.AlignVCenter)
        cen.addWidget(self.opacity_slider, 0, Qt.AlignmentFlag.AlignVCenter)
        cen.addStretch(1)
        topbar.addWidget(self.top_center_drag_host, stretch=1)
        _mode_icon_render_px = 16
        _mode_icon_display = QSize(28, 28)
        _mode_btn_h = 28
        _mode_icon_color = "#222222"
        self.session_mode_btn = QToolButton()
        self.session_mode_btn.setObjectName("ModeCycleBtn")
        self.session_mode_btn.setCheckable(False)
        self.session_mode_btn.setCursor(Qt.PointingHandCursor)
        self.session_mode_btn.setMinimumHeight(_mode_btn_h)
        self.session_mode_btn.setMinimumWidth(88)
        self.session_mode_btn.setIconSize(_mode_icon_display)
        self.session_mode_btn.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextBesideIcon)
        # Qt6 / PySide6: only DelayedPopup, MenuButtonPopup, InstantPopup (no MenuButtonInstantPopup).
        self.session_mode_btn.setPopupMode(QToolButton.ToolButtonPopupMode.InstantPopup)
        self.session_mode_btn.setStyleSheet(
            f"""
            QToolButton#ModeCycleBtn {{
                border: none;
                border-radius: 14px;
                background: {_TOPBAR_CYAN_BG};
                padding-left: 8px;
                padding-right: 2px;
                font-size: 11px;
                font-weight: 700;
                color: #222;
            }}
            QToolButton#ModeCycleBtn:hover {{
                background: {_TOPBAR_CYAN_HOVER};
            }}
            QToolButton#ModeCycleBtn::menu-indicator {{
                subcontrol-origin: padding;
                subcontrol-position: center right;
                width: 10px;
                height: 10px;
                right: 5px;
                left: unset;
                top: 0px;
            }}
            """
        )
        mode_menu = QMenu(self.session_mode_btn)
        for mid, title in (("read", "Read"), ("type", "Type"), ("behavioral", "Behavioral")):
            act = QAction(title, self)
            act.triggered.connect(lambda checked=False, m=mid: self._set_session_mode_from_menu(m))
            mode_menu.addAction(act)
        self.session_mode_btn.setMenu(mode_menu)
        self._session_mode_icon_render_px = _mode_icon_render_px
        self._session_mode_icon_color = _mode_icon_color
        self._update_session_mode_button()
        topbar.addWidget(self.session_mode_btn)
        self.save_transcript_btn = QPushButton()
        self.save_transcript_btn.setObjectName("SaveTranscriptTopBtn")
        self.save_transcript_btn.setFixedSize(28, 28)
        self.save_transcript_btn.setCursor(Qt.PointingHandCursor)
        self.save_transcript_btn.setIcon(self._build_save_transcript_svg_icon(16, "#111111"))
        self.save_transcript_btn.setIconSize(self.save_transcript_btn.size())
        self.save_transcript_btn.setAccessibleName("Save interview transcript")
        self.save_transcript_btn.setStyleSheet(
            f"""
            QPushButton#SaveTranscriptTopBtn {{
                border: none;
                border-radius: 14px;
                background: {_TOPBAR_CYAN_BG};
            }}
            QPushButton#SaveTranscriptTopBtn:hover {{
                background: {_TOPBAR_CYAN_HOVER};
            }}
            """
        )
        self.save_transcript_btn.clicked.connect(self._on_save_transcript_clicked)
        topbar.addWidget(self.save_transcript_btn)
        self.settings_btn = QPushButton()
        self.settings_btn.setObjectName("SettingsOverflowBtn")
        self.settings_btn.setFixedSize(28, 28)
        self.settings_btn.setCursor(Qt.PointingHandCursor)
        self.settings_btn.setIcon(self._build_overflow_kebab_svg_icon(16, "#111111"))
        self.settings_btn.setIconSize(self.settings_btn.size())
        self.settings_btn.setAccessibleName("Settings")
        self.settings_btn.setStyleSheet(
            f"""
            QPushButton#SettingsOverflowBtn {{
                border: none;
                border-radius: 14px;
                background: {_TOPBAR_CYAN_BG};
            }}
            QPushButton#SettingsOverflowBtn:hover {{
                background: {_TOPBAR_CYAN_HOVER};
            }}
            """
        )
        self.settings_btn.clicked.connect(self._on_settings_nav_clicked)
        topbar.addWidget(self.settings_btn)
        self.close_btn = QPushButton()
        self.close_btn.setFixedSize(28, 28)
        self.close_btn.setCursor(Qt.PointingHandCursor)
        self.close_btn.setIcon(self._build_close_svg_icon(16, "#111111"))
        self.close_btn.setIconSize(self.close_btn.size())
        self.close_btn.setStyleSheet(
            f"""
            QPushButton {{
                border: none;
                border-radius: 14px;
                background: {_TOPBAR_CYAN_BG};
            }}
            QPushButton:hover {{
                background: {_TOPBAR_CLOSE_HOVER_BG};
            }}
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
        # Cooldown extends on each interaction; after last scroll event + this many seconds, timer resumes sticking to bottom.
        self._user_scroll_cooldown_until = 0.0
        self._user_scroll_cooldown_seconds = 1.5
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
        self.gpt_result_title = QLabel("ChatGPT RESULT")
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
        self.gpt_copy_btn.setIcon(self._build_copy_glyph_svg_icon(14, "#455a64"))
        self.gpt_copy_btn.setIconSize(QSize(14, 14))
        self.gpt_copy_btn.setCursor(Qt.PointingHandCursor)
        self.gpt_copy_btn.setFixedSize(24, 24)
        self.gpt_copy_btn.setStyleSheet(
            "QToolButton { border: none; border-radius: 12px; background: transparent; font-weight: 700; }"
            "QToolButton:hover { background: rgba(0, 0, 0, 0.10); }"
        )
        self.gpt_copy_btn.clicked.connect(self._copy_current_gpt_result)
        controls_row.addWidget(self.gpt_copy_btn)
        controls_row.addStretch(1)
        gpt_layout.addWidget(self.gpt_result_title)
        gpt_layout.addWidget(self.gpt_result_body, 1)
        gpt_layout.addLayout(controls_row)

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

        self._settings_active_kind: str | None = None
        self._settings_snap_text = ""
        self._settings_editor_dirty = False
        self.session_stack = QStackedWidget()
        self.session_stack.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.settings_page = self._build_settings_page()
        self.session_stack.addWidget(self.interview_page)
        self.session_stack.addWidget(self.settings_page)

        self.main_stack = QStackedWidget()
        self.main_stack.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        if self._start_with_prep:
            self.prep_widget = PrepWizardWidget(self._bridge_base, parent=self.main_stack)
            self.main_stack.addWidget(self.prep_widget)
            self.main_stack.addWidget(self.session_stack)
            self.prep_widget.finished.connect(self._on_prep_finished)
            self.main_stack.currentChanged.connect(self._sync_shell_for_main_stack)
            self._sync_shell_for_main_stack(self.main_stack.currentIndex())
        else:
            self.main_stack.addWidget(self.session_stack)
            self._shell_phase_prep = False
            self._apply_shell_visual()
            interview_history_reset()
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

        self._exclude_capture_geom_timer = QTimer(self)
        self._exclude_capture_geom_timer.setSingleShot(True)
        self._exclude_capture_geom_timer.setInterval(80)
        self._exclude_capture_geom_timer.timeout.connect(self._sync_exclude_capture_after_geometry)

        self._toast_host = QWidget(self)
        self._toast_host.setObjectName("ToastHost")
        self._toast_host.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, True)
        self._toast_host.setStyleSheet("#ToastHost { background: transparent; border: none; }")
        self._toast_frames: list[QFrame] = []
        self._sync_toast_host_geometry()
        self._toast_host.raise_()

        # Hover cursor updates should work before clicking.
        self._enable_hover_tracking(self)

    def _build_settings_page(self) -> QWidget:
        page = QWidget()
        page.setObjectName("SettingsPage")
        outer = QVBoxLayout(page)
        outer.setContentsMargins(8, 8, 8, 8)
        outer.setSpacing(8)
        header = QHBoxLayout()
        self.settings_back_btn = QToolButton()
        self.settings_back_btn.setObjectName("SettingsBackBtn")
        self.settings_back_btn.setText("Back")
        self.settings_back_btn.setIcon(self._build_chevron_left_svg_icon(14, "#212121"))
        self.settings_back_btn.setIconSize(QSize(16, 16))
        self.settings_back_btn.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextBesideIcon)
        self.settings_back_btn.setCursor(Qt.PointingHandCursor)
        self.settings_back_btn.setAutoRaise(False)
        self.settings_back_btn.setStyleSheet(
            f"""
            QToolButton#SettingsBackBtn {{
                border: none;
                border-radius: 14px;
                background: {_TOPBAR_CYAN_BG};
                padding: 6px 14px 6px 10px;
                font-size: 12px;
                font-weight: 700;
                color: #212121;
            }}
            QToolButton#SettingsBackBtn:hover {{
                background: {_TOPBAR_CYAN_HOVER};
            }}
            """
        )
        self.settings_back_btn.clicked.connect(self._on_settings_back_clicked)
        header.addWidget(self.settings_back_btn)
        header.addStretch(1)
        outer.addLayout(header)

        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.setChildrenCollapsible(False)
        splitter.setStretchFactor(0, 3)
        splitter.setStretchFactor(1, 7)
        splitter.setHandleWidth(1)
        splitter.setStyleSheet(
            f"QSplitter::handle:horizontal {{ background-color: {CAPTION_ROW_BOTTOM_BORDER_COLOR}; "
            "width: 1px; border: none; }}"
        )
        left = QWidget()
        left.setMinimumWidth(140)
        lv = QVBoxLayout(left)
        lv.setContentsMargins(4, 4, 4, 4)
        lv.setSpacing(10)
        btn_style = (
            "QPushButton { padding: 8px 12px; border-radius: 8px; background: #f5f5f5; color: #263238; "
            "font-weight: 700; font-size: 12px; border: 1px solid #cfd8dc; text-align: left; }"
            "QPushButton:hover { background: #e8eaf6; }"
        )
        self._settings_tpl_buttons: dict[str, QPushButton] = {}
        for kind, label in (
            ("resume_summary", "Resume summary template"),
            ("jd_summary", "JD summary template"),
            ("initial_interview", "Initial interview template"),
            ("chunk_interview", "Per-caption interview template"),
            ("read_mode", "Read mode prompt"),
            ("type_mode", "Type mode prompt"),
            ("behavioral_mode", "Behavioral mode prompt"),
        ):
            b = QPushButton(label)
            b.setStyleSheet(btn_style)
            b.setCursor(Qt.PointingHandCursor)
            b.clicked.connect(lambda checked=False, k=kind: self._on_settings_pick_prompt_kind(k))
            lv.addWidget(b)
            self._settings_tpl_buttons[kind] = b
        lv.addStretch(1)
        splitter.addWidget(left)

        right = QWidget()
        right.setMinimumWidth(200)
        rv = QVBoxLayout(right)
        rv.setContentsMargins(4, 4, 4, 4)
        rv.setSpacing(8)
        self._settings_right_hint = QLabel("Select a prompt on the left to view or edit.")
        self._settings_right_hint.setWordWrap(True)
        self._settings_right_hint.setStyleSheet("font-size: 13px; font-weight: 700; color: #546e7a; padding: 8px;")
        rv.addWidget(self._settings_right_hint)
        self._settings_editor = QPlainTextEdit()
        self._settings_editor.setPlaceholderText("")
        self._settings_editor.setStyleSheet(
            "QPlainTextEdit { font-size: 13px; font-weight: 600; color: #212121; background: rgba(255,255,255,0.92); "
            "border: 1px solid rgba(17,17,17,90); border-radius: 8px; padding: 8px; }"
        )
        self._settings_editor.hide()
        self._settings_editor.textChanged.connect(self._on_settings_editor_text_changed)
        rv.addWidget(self._settings_editor, stretch=1)
        btn_row = QHBoxLayout()
        btn_row.addStretch(1)
        self._settings_prompt_save_btn = QPushButton("Save")
        self._settings_prompt_save_btn.setMinimumHeight(36)
        self._settings_prompt_save_btn.setCursor(Qt.PointingHandCursor)
        self._settings_prompt_save_btn.setStyleSheet(
            "QPushButton { padding: 8px 22px; border-radius: 10px; background: #3949ab; color: white; "
            "font-weight: 700; font-size: 13px; border: none; }"
            "QPushButton:hover { background: #5c6bc0; }"
        )
        self._settings_prompt_save_btn.clicked.connect(self._on_settings_prompt_save_clicked)
        self._settings_prompt_cancel_btn = QPushButton("Cancel")
        self._settings_prompt_cancel_btn.setMinimumHeight(36)
        self._settings_prompt_cancel_btn.setCursor(Qt.PointingHandCursor)
        self._settings_prompt_cancel_btn.setStyleSheet(
            "QPushButton { padding: 8px 22px; border-radius: 10px; background: #eceff1; color: #263238; "
            "font-weight: 700; font-size: 13px; border: none; }"
            "QPushButton:hover { background: #cfd8dc; }"
        )
        self._settings_prompt_cancel_btn.clicked.connect(self._on_settings_prompt_cancel_clicked)
        self._settings_prompt_save_btn.hide()
        self._settings_prompt_cancel_btn.hide()
        btn_row.addWidget(self._settings_prompt_save_btn)
        btn_row.addWidget(self._settings_prompt_cancel_btn)
        rv.addLayout(btn_row)
        splitter.addWidget(right)
        splitter.setSizes([260, 540])
        outer.addWidget(splitter, stretch=1)
        return page

    def _settings_effective_client_id(self) -> str:
        global _active_interview_client_id
        return (str(_active_interview_client_id or "").strip() or str(get_selected_client_id() or "").strip())

    def _on_settings_nav_clicked(self) -> None:
        if self.session_stack is None:
            return
        self.session_stack.setCurrentIndex(1)

    def _on_settings_back_clicked(self) -> None:
        if self._settings_editor_dirty:
            r = QMessageBox.question(
                self,
                "Unsaved edits",
                "Discard unsaved prompt edits and return to the interview?",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                QMessageBox.StandardButton.No,
            )
            if r != QMessageBox.StandardButton.Yes:
                return
        self._reset_settings_editor_state()
        if self.session_stack is not None:
            self.session_stack.setCurrentIndex(0)

    def _reset_settings_editor_state(self) -> None:
        self._settings_active_kind = None
        self._settings_snap_text = ""
        self._settings_editor_dirty = False
        self._settings_editor.blockSignals(True)
        self._settings_editor.clear()
        self._settings_editor.blockSignals(False)
        self._settings_editor.hide()
        self._settings_right_hint.show()
        self._settings_prompt_save_btn.hide()
        self._settings_prompt_cancel_btn.hide()

    def _on_settings_pick_prompt_kind(self, kind: str) -> None:
        if kind == "read_mode":
            text = READ_MODE_CHUNK_TEMPLATE
        elif kind == "type_mode":
            text = TYPE_MODE_CHUNK_TEMPLATE
        elif kind == "behavioral_mode":
            text = BEHAVIORAL_MODE_CHUNK_TEMPLATE
        elif kind in ("resume_summary", "jd_summary", "initial_interview", "chunk_interview"):
            text = prompt_store.get_template(kind)
        else:
            return
        self._settings_active_kind = kind
        self._settings_editor.blockSignals(True)
        self._settings_editor.setPlainText(text or "")
        self._settings_editor.blockSignals(False)
        self._settings_snap_text = self._settings_editor.toPlainText()
        self._settings_editor_dirty = False
        self._settings_editor.show()
        self._settings_right_hint.hide()
        self._settings_prompt_save_btn.show()
        self._settings_prompt_cancel_btn.show()

    def _on_settings_editor_text_changed(self) -> None:
        self._settings_editor_dirty = self._settings_editor.toPlainText() != self._settings_snap_text

    def _on_settings_prompt_cancel_clicked(self) -> None:
        if self._settings_active_kind is None:
            self._reset_settings_editor_state()
            return
        self._settings_editor.blockSignals(True)
        self._settings_editor.setPlainText(self._settings_snap_text)
        self._settings_editor.blockSignals(False)
        self._settings_editor_dirty = False

    def _on_settings_prompt_save_clicked(self) -> None:
        global READ_MODE_CHUNK_TEMPLATE, TYPE_MODE_CHUNK_TEMPLATE, BEHAVIORAL_MODE_CHUNK_TEMPLATE
        kind = self._settings_active_kind
        if not kind:
            return
        body = self._settings_editor.toPlainText()
        if kind == "read_mode":
            READ_MODE_CHUNK_TEMPLATE = body
            _persist_mode_prompts_to_json()
            self.show_toast("Read mode prompt saved.", level="success")
        elif kind == "type_mode":
            TYPE_MODE_CHUNK_TEMPLATE = body
            _persist_mode_prompts_to_json()
            self.show_toast("Type mode prompt saved.", level="success")
        elif kind == "behavioral_mode":
            BEHAVIORAL_MODE_CHUNK_TEMPLATE = body
            _persist_mode_prompts_to_json()
            self.show_toast("Behavioral mode prompt saved.", level="success")
        elif kind in ("resume_summary", "jd_summary", "initial_interview", "chunk_interview"):
            cid = self._settings_effective_client_id()
            url = f"{self._bridge_base}/context/template/{kind}"
            post_body: dict = {"text": body}
            if cid:
                post_body["client_id"] = cid
            payload, status = _http_post_json_local(url, post_body)
            if status >= 400 or str(payload.get("error", "")).strip():
                self.show_toast(
                    f"Could not save template: {payload.get('error', payload)!s}",
                    level="error",
                )
                return
            if str(payload.get("status", "")).lower() != "ok" and payload.get("ok") is not True:
                self.show_toast("Could not save template: unexpected bridge response.", level="error")
                return
            prompt_store.set_template(kind, body)
            if kind == "resume_summary":
                self.show_toast("Resume summary template saved.", level="success")
            elif kind == "jd_summary":
                self.show_toast("Job description template saved.", level="success")
            elif kind == "initial_interview":
                self.show_toast("Initial interview template saved.", level="success")
            else:
                self.show_toast("Per-caption interview template saved.", level="success")
        self._settings_snap_text = body
        self._settings_editor_dirty = False

    def _topbar_blocks_window_drag(self, pt_window) -> bool:
        for w in (
            self.close_btn,
            self.settings_btn,
            self.save_transcript_btn,
            self.session_mode_btn,
            self.opacity_slider,
            self.opacity_lbl,
            self.account_label,
            self.settings_back_btn,
        ):
            if w.isVisible() and w.rect().contains(w.mapFrom(self, pt_window)):
                return True
        return False

    def _on_opacity_slider_changed(self, value: int) -> None:
        self._shell_bg_opacity = max(0.0, min(1.0, value / 100.0))
        self._apply_shell_visual()

    def _set_session_mode_from_menu(self, mode: str) -> None:
        """Set chunk-guidance mode from the top-bar menu (read / type / behavioral)."""
        global _session_mode
        old = _session_mode
        m = (mode or "read").strip().lower()
        if m not in ("read", "type", "behavioral"):
            m = "read"
        _session_mode = m
        self._update_session_mode_button()
        if old == m:
            return
        if m == "type":
            self.show_toast("Switched to Type mode.", level="success")
        elif m == "behavioral":
            self.show_toast("Switched to Behavioral mode.", level="success")
        else:
            self.show_toast("Switched to Read mode.", level="success")

    def _update_session_mode_button(self) -> None:
        global _session_mode
        px = self._session_mode_icon_render_px
        col = self._session_mode_icon_color
        if _session_mode == "type":
            self.session_mode_btn.setIcon(
                self._build_type_mode_svg_icon(px, col)
            )
            self.session_mode_btn.setText("Type")
            self.session_mode_btn.setAccessibleName("Type mode")
        elif _session_mode == "behavioral":
            self.session_mode_btn.setIcon(
                self._build_behavioral_mode_svg_icon(px, col)
            )
            self.session_mode_btn.setText("Behavioral")
            self.session_mode_btn.setAccessibleName("Behavioral mode")
        else:
            self.session_mode_btn.setIcon(
                self._build_read_mode_svg_icon(px, col)
            )
            self.session_mode_btn.setText("Read")
            self.session_mode_btn.setAccessibleName("Read mode")

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
            QFrame#InterviewShell QToolButton {{
                font-weight: 700;
            }}
            """
        )

    def _sync_shell_for_main_stack(self, index: int) -> None:
        if self._start_with_prep and self.main_stack is not None:
            self._shell_phase_prep = index == 0
        in_prep = bool(self._start_with_prep and self.main_stack is not None and index == 0)
        self.session_mode_btn.setVisible(not in_prep)
        self.save_transcript_btn.setVisible(not in_prep)
        self.settings_btn.setVisible(not in_prep)
        self._apply_shell_visual()

    def _on_prep_finished(self, display_email: str, client_id: str) -> None:
        global _interview_ws, _main_interview_window, _active_interview_client_id
        global _session_initial_template
        interview_history_reset()
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
        if self.session_stack is not None:
            self.session_stack.setCurrentIndex(0)
        self._reset_settings_editor_state()
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

    def _sync_toast_host_geometry(self) -> None:
        host = getattr(self, "_toast_host", None)
        if host is None:
            return
        host.setGeometry(0, 0, max(1, self.width()), max(1, self.height()))

    def _toast_layout_positions(self) -> list[tuple[int, int, int]]:
        """Return (x, y, row_height) per toast, horizontally centered (variable width per toast)."""
        W = max(1, self.width())
        y = _TOAST_MARGIN_TOP
        out: list[tuple[int, int, int]] = []
        for frame in self._toast_frames:
            fw = max(1, frame.width())
            x = max(0, (W - fw) // 2)
            h = max(24, frame.sizeHint().height(), frame.height())
            out.append((x, y, h))
            y += h + _TOAST_GAP_PX
        return out

    def _relayout_toast_frames(self, *, animate_last_entrance: bool = False) -> None:
        self._sync_toast_host_geometry()
        self._toast_host.raise_()
        positions = self._toast_layout_positions()
        if not positions or not self._toast_frames:
            return
        last_i = len(self._toast_frames) - 1
        for i, frame in enumerate(self._toast_frames):
            x, y, _h = positions[i]
            end = QPoint(x, y)
            if animate_last_entrance and i == last_i:
                eff = frame.graphicsEffect()
                if isinstance(eff, QGraphicsOpacityEffect):
                    eff.setOpacity(0.0)
                frame.move(QPoint(x, y - _TOAST_SLIDE_PX))
                frame.show()
                grp = QParallelAnimationGroup(self)
                opa = QPropertyAnimation(eff, b"opacity", self)
                opa.setDuration(_TOAST_SHOW_MS)
                opa.setStartValue(0.0)
                opa.setEndValue(1.0)
                opa.setEasingCurve(QEasingCurve.Type.OutCubic)
                posa = QPropertyAnimation(frame, b"pos", self)
                posa.setDuration(_TOAST_SHOW_MS)
                posa.setStartValue(QPoint(x, y - _TOAST_SLIDE_PX))
                posa.setEndValue(end)
                posa.setEasingCurve(QEasingCurve.Type.OutCubic)
                grp.addAnimation(opa)
                grp.addAnimation(posa)
                grp.start(QAbstractAnimation.DeleteWhenStopped)
            else:
                frame.move(end)
                eff = frame.graphicsEffect()
                if isinstance(eff, QGraphicsOpacityEffect):
                    eff.setOpacity(1.0)
                frame.show()

    def _relayout_toast_frames_slide_to_target(self, duration_ms: int = 200) -> None:
        self._sync_toast_host_geometry()
        self._toast_host.raise_()
        positions = self._toast_layout_positions()
        for i, frame in enumerate(self._toast_frames):
            if i >= len(positions):
                break
            x, y, _h = positions[i]
            end = QPoint(x, y)
            if frame.pos() == end:
                continue
            posa = QPropertyAnimation(frame, b"pos", self)
            posa.setDuration(duration_ms)
            posa.setStartValue(frame.pos())
            posa.setEndValue(end)
            posa.setEasingCurve(QEasingCurve.Type.OutCubic)
            posa.start(QAbstractAnimation.DeleteWhenStopped)

    def _dismiss_toast_frame(self, frame: QFrame) -> None:
        if frame not in self._toast_frames:
            return
        eff = frame.graphicsEffect()
        if not isinstance(eff, QGraphicsOpacityEffect):
            try:
                self._toast_frames.remove(frame)
            except ValueError:
                pass
            frame.deleteLater()
            self._relayout_toast_frames_slide_to_target(200)
            return
        anim = QPropertyAnimation(eff, b"opacity", self)
        anim.setDuration(_TOAST_HIDE_MS)
        anim.setStartValue(float(eff.opacity()))
        anim.setEndValue(0.0)
        anim.setEasingCurve(QEasingCurve.Type.InCubic)

        def _after() -> None:
            try:
                self._toast_frames.remove(frame)
            except ValueError:
                pass
            frame.deleteLater()
            self._relayout_toast_frames_slide_to_target(220)

        anim.finished.connect(_after)
        anim.start(QAbstractAnimation.DeleteWhenStopped)

    def show_toast(
        self,
        message: str,
        *,
        level: str = "success",
        duration_ms: int | None = None,
    ) -> None:
        lvl = (level or "success").strip().lower()
        if lvl not in ("success", "warning", "error"):
            lvl = "success"
        host = getattr(self, "_toast_host", None)
        if host is None:
            return
        if lvl == "error":
            default_msg = "Something went wrong."
        elif lvl == "warning":
            default_msg = "Notice."
        else:
            default_msg = "Done."
        text = _toast_trim_message((message or "").strip() or default_msg)
        self._sync_toast_host_geometry()
        host.raise_()
        frame = QFrame(host)
        frame.setObjectName({"success": "ToastSuccess", "warning": "ToastWarning", "error": "ToastError"}[lvl])
        frame.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, True)
        fs = _TOAST_FONT_PX
        styles = {
            "success": f"""
                QFrame#ToastSuccess {{
                    background-color: rgba(46, 125, 50, 0.94);
                    border-radius: 8px;
                    border: 1px solid rgba(165, 214, 167, 0.55);
                }}
                QFrame#ToastSuccess QLabel {{
                    color: #e8f5e9;
                    font-size: {fs}px;
                    font-weight: 600;
                    padding: 5px 10px;
                    background: transparent;
                }}
            """,
            "warning": f"""
                QFrame#ToastWarning {{
                    background-color: rgba(230, 81, 0, 0.94);
                    border-radius: 8px;
                    border: 1px solid rgba(255, 224, 178, 0.55);
                }}
                QFrame#ToastWarning QLabel {{
                    color: #fff8e1;
                    font-size: {fs}px;
                    font-weight: 600;
                    padding: 5px 10px;
                    background: transparent;
                }}
            """,
            "error": f"""
                QFrame#ToastError {{
                    background-color: rgba(183, 28, 28, 0.94);
                    border-radius: 8px;
                    border: 1px solid rgba(255, 205, 210, 0.45);
                }}
                QFrame#ToastError QLabel {{
                    color: #ffebee;
                    font-size: {fs}px;
                    font-weight: 600;
                    padding: 5px 10px;
                    background: transparent;
                }}
            """,
        }
        frame.setStyleSheet(styles[lvl])
        lay = QHBoxLayout(frame)
        lay.setContentsMargins(0, 0, 0, 0)
        lbl = QLabel(text)
        lbl.setWordWrap(True)
        lbl.setMaximumWidth(_TOAST_LABEL_MAX_WIDTH)
        lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lbl.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, True)
        lay.addWidget(lbl)
        eff = QGraphicsOpacityEffect(frame)
        eff.setOpacity(0.0)
        frame.setGraphicsEffect(eff)
        frame.adjustSize()
        max_outer = min(_TOAST_MAX_OUTER, max(_TOAST_MIN_OUTER, self.width() - 16))
        fw = min(max_outer, max(_TOAST_MIN_OUTER, int(frame.sizeHint().width())))
        frame.setFixedWidth(fw)
        frame.adjustSize()
        self._toast_frames.append(frame)
        self._relayout_toast_frames(animate_last_entrance=True)
        if duration_ms is None:
            if lvl == "error":
                linger = _TOAST_LINGER_ERROR_MS
            elif lvl == "warning":
                linger = _TOAST_LINGER_WARNING_MS
            else:
                linger = _TOAST_LINGER_SUCCESS_MS
        else:
            linger = int(duration_ms)
        linger = max(700, linger)
        QTimer.singleShot(linger, lambda f=frame: self._dismiss_toast_frame(f))

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

    def _build_save_transcript_svg_icon(self, size: int, color: str) -> QIcon:
        # Tray + arrow (save / export), 16×16 viewBox — same render pipeline as close button.
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 16 16">
<path fill="{color}" d="M8 1 4 6h2.5v5h3V6H12L8 1zM2 12.5h12V15H2v-2.5z"/>
</svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    def _build_overflow_kebab_svg_icon(self, size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 16 16">
<circle cx="8" cy="3" r="1.35" fill="{color}"/>
<circle cx="8" cy="8" r="1.35" fill="{color}"/>
<circle cx="8" cy="13" r="1.35" fill="{color}"/>
</svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    def _build_chevron_left_svg_icon(self, size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 16 16">
<path fill="{color}" d="M10.5 3.5 6 8l4.5 4.5-1.06 1.06L3.88 8l5.56-5.56L10.5 3.5z"/>
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

    @staticmethod
    def _build_read_mode_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24">
<path fill="{color}" d="M8 2.5h10.5c.83 0 1.5.67 1.5 1.5v16c0 .83-.67 1.5-1.5 1.5H8A2.5 2.5 0 0 1 5.5 19V5A2.5 2.5 0 0 1 8 2.5zm1.5 5.25h7v1.5h-7zm0 3.5h7v1.5h-7zm0 3.5h4.5v1.5H9.5z"/>
<circle cx="6" cy="6.25" r="1" fill="{color}"/>
<circle cx="6" cy="10.25" r="1" fill="{color}"/>
<circle cx="6" cy="14.25" r="1" fill="{color}"/>
<circle cx="6" cy="18.25" r="1" fill="{color}"/>
</svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    @staticmethod
    def _build_type_mode_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24">
<path fill="{color}" d="M3 17.46V20.5c0 .28.22.5.5.5h3.04c.13 0 .26-.05.35-.15L17.81 9.94l-3.59-3.59L3.35 17.11c-.09.09-.15.22-.15.35zM20.71 7.04a1.003 1.003 0 0 0 0-1.41l-2.34-2.34a1.003 1.003 0 0 0-1.41 0l-1.83 1.83 3.59 3.59 1.99-1.67z"/>
</svg>"""
        renderer = QSvgRenderer(svg.encode("utf-8"))
        pixmap = QPixmap(size, size)
        pixmap.fill(Qt.GlobalColor.transparent)
        painter = QPainter(pixmap)
        renderer.render(painter)
        painter.end()
        return QIcon(pixmap)

    @staticmethod
    def _build_behavioral_mode_svg_icon(size: int, color: str) -> QIcon:
        svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24">
<path fill="{color}" d="M5 4.5h8.5A2.25 2.25 0 0 1 15.75 6.75v3.5A2.25 2.25 0 0 1 13.5 12.5h-1.9L9.25 15.5V12.5H5A2.25 2.25 0 0 1 2.75 10.25v-3.5A2.25 2.25 0 0 1 5 4.5z"/>
<path fill="{color}" d="M10.25 9.75h7A1.85 1.85 0 0 1 19.1 11.6v2.65a1.85 1.85 0 0 1-1.85 1.85h-1.35l-1.6 2.55v-2.55h-1.05A1.85 1.85 0 0 1 12.4 14.25v-2.65a1.85 1.85 0 0 1 1.85-1.85z"/>
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
        pt = event.position().toPoint()
        if event.button() == Qt.LeftButton and not self._topbar_blocks_window_drag(pt):
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
        drag_zone = getattr(self, "top_center_drag_host", None)
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
        self._schedule_sync_exclude_capture_after_geometry()

    def moveEvent(self, event):
        self._schedule_sync_exclude_capture_after_geometry()
        super().moveEvent(event)

    def _default_transcript_save_path(self) -> Path:
        return _documents_dir_fallback() / f"Interview_{datetime.now().strftime('%Y-%m-%d_%H%M%S')}.txt"

    def _write_transcript_file(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(interview_history_format_txt(), encoding="utf-8", newline="\n")

    def _prompt_save_transcript_interactive(self) -> bool:
        """True if a file was written; False if cancelled or write failed."""
        path_str, _sel = QFileDialog.getSaveFileName(
            self,
            "Save interview transcript",
            str(self._default_transcript_save_path()),
            "Text Files (*.txt)",
        )
        if not path_str:
            return False
        out = Path(path_str)
        if out.suffix.lower() != ".txt":
            out = out.with_suffix(".txt")
        try:
            self._write_transcript_file(out)
        except OSError as exc:
            self.show_toast(f"Could not save transcript file: {exc}", level="error")
            return False
        self.show_toast(f"Saved: {out.name}", level="success")
        return True

    def _on_save_transcript_clicked(self) -> None:
        if not interview_history_has_interviewer_lines():
            self.show_toast(
                "No interviewer captions in this session yet — nothing to save.",
                level="warning",
            )
            return
        self._prompt_save_transcript_interactive()

    def _shutdown_services(self) -> None:
        global app_running, _keyboard_listener, _interview_ws
        app_running = False
        try:
            bridge.stop()
        except Exception:
            pass
        try:
            if _interview_ws is not None:
                _interview_ws.stop()
        except Exception:
            pass
        try:
            if _keyboard_listener is not None:
                _keyboard_listener.stop()
        except Exception:
            pass

    def closeEvent(self, event):
        # Stop prep timers / register thread before bridge.stop() so /prep/clear and polls
        # do not hit a torn-down server (ConnectionAbortedError / QThread warnings).
        if self.prep_widget is not None:
            self.prep_widget.shutdown_for_handoff()
        # Autosave transcript when any interviewer text was captured (same scope as the old
        # save prompt). No dialog on success; time-based path under Documents (see _default_transcript_save_path).
        if interview_history_has_interviewer_lines():
            path = self._default_transcript_save_path()
            try:
                self._write_transcript_file(path)
            except OSError as exc:
                self.show_toast(
                    f"Could not autosave transcript to {path.name}: {exc}",
                    level="error",
                )
                event.ignore()
                return
        self._shutdown_services()
        super().closeEvent(event)
        app_inst = QApplication.instance()
        if app_inst is not None:
            app_inst.quit()

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
        self._sync_toast_host_geometry()
        self._relayout_toast_frames(animate_last_entrance=False)
        self._apply_initial_splitter_sizes()
        if self._exclude_from_capture:
            QTimer.singleShot(0, self._apply_exclude_from_capture_if_enabled)
            QTimer.singleShot(120, self._sync_exclude_capture_after_geometry)

    def _schedule_sync_exclude_capture_after_geometry(self) -> None:
        if not self._exclude_from_capture:
            return
        self._exclude_capture_geom_timer.start()

    def _sync_exclude_capture_after_geometry(self) -> None:
        if not self._exclude_from_capture or not self.isVisible():
            return
        self._sync_round_window_mask()
        self._apply_exclude_from_capture_if_enabled()

    def _apply_display_affinity_only(self) -> None:
        self._apply_display_affinity_only_impl(is_api_retry=False)

    def _apply_display_affinity_retry_once(self) -> None:
        self._display_affinity_retry_pending = False
        if not self._exclude_from_capture or not self.isVisible():
            return
        self._apply_display_affinity_only_impl(is_api_retry=True)

    def _apply_display_affinity_only_impl(self, *, is_api_retry: bool) -> None:
        if not self._exclude_from_capture or not self.isVisible():
            return
        hwnd = int(self.winId())
        if not hwnd:
            return
        aff = _desired_display_affinity_for_window(self)
        sk = _screen_key_at_window_center(self)
        rc = _win32_set_window_display_affinity(hwnd, aff)
        ok = bool(rc)

        if _env_debug_affinity():
            prev_aff = getattr(self, "_affinity_dbg_prev_logged_aff", _AFFINITY_DBG_PREV_UNSET)
            prev_sk = getattr(self, "_affinity_dbg_prev_logged_sk", _AFFINITY_DBG_PREV_UNSET)
            now = time.time()
            if ok:
                if prev_aff is _AFFINITY_DBG_PREV_UNSET or aff != prev_aff or sk != prev_sk:
                    line = (
                        f"[affinity] hwnd=0x{hwnd:x} {_affinity_constant_name(aff)} "
                        f"SetWindowDisplayAffinity=ok rc={rc} | {_primary_vs_screen_at_debug_suffix(self)}"
                    )
                    print(line, file=sys.stderr, flush=True)
                    self._affinity_dbg_prev_logged_aff = aff
                    self._affinity_dbg_prev_logged_sk = sk
            else:
                sig = (hwnd, aff, sk, rc)
                last_sig = getattr(self, "_affinity_dbg_last_fail_sig", None)
                last_ts = float(getattr(self, "_affinity_dbg_last_fail_ts", 0.0))
                if sig != last_sig or (now - last_ts) >= 0.5:
                    line = (
                        f"[affinity] hwnd=0x{hwnd:x} {_affinity_constant_name(aff)} "
                        f"SetWindowDisplayAffinity=fail rc={rc} | {_primary_vs_screen_at_debug_suffix(self)}"
                    )
                    print(line, file=sys.stderr, flush=True)
                    self._affinity_dbg_last_fail_sig = sig
                    self._affinity_dbg_last_fail_ts = now

        if not ok and not is_api_retry and not getattr(self, "_display_affinity_retry_pending", False):
            self._display_affinity_retry_pending = True
            QTimer.singleShot(200, self._apply_display_affinity_retry_once)

    def _apply_exclude_from_capture_if_enabled(self) -> None:
        if not self._exclude_from_capture or not self.isVisible():
            return
        hwnd = int(self.winId())
        if not hwnd:
            return
        scr = QGuiApplication.screenAt(self.geometry().center())
        prev = getattr(self, "_affinity_bump_screen", None)
        # Center can move to another virtual monitor during drag; staggered re-apply helps DWM.
        if scr is not prev:
            self._affinity_bump_screen = scr
            for ms in (100, 400, 850):
                QTimer.singleShot(ms, self._apply_display_affinity_only)
        self._apply_display_affinity_only()

    def resizeEvent(self, event):
        self._sync_round_window_mask()
        self._sync_toast_host_geometry()
        self._relayout_toast_frames(animate_last_entrance=False)
        self.resize_timer.start(20)
        if not self._splitter_initialized:
            QTimer.singleShot(0, self._apply_initial_splitter_sizes)
        self._schedule_sync_exclude_capture_after_geometry()
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
        """Periodic stick-to-bottom for live captions. Disabled in caption edit mode and for 3s after user scroll."""
        if self._home_edit_active:
            return
        now = time.time()
        try:
            cooldown_until = float(getattr(self, "_user_scroll_cooldown_until", 0.0))
        except (TypeError, ValueError):
            cooldown_until = 0.0
        if now < cooldown_until:
            return
        bar = self.caption_scroll.verticalScrollBar()
        self._suppress_user_scroll_signal = True
        try:
            bar.setValue(bar.maximum())
        finally:
            self._suppress_user_scroll_signal = False

    def _set_gpt_result_text(self, text: str) -> None:
        self.gpt_result_body.setPlainText((text or "").strip() or " ")

    def _refresh_gpt_history_nav(self) -> None:
        """History nav UI removed; title stays fixed as ChatGPT RESULT."""
        return

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
        self, side: str, with_copy_button: bool = False
    ) -> tuple[QWidget, QFrame, QLabel]:
        row = QWidget()
        h = QHBoxLayout(row)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(0)

        panel = QFrame()
        panel.setMinimumHeight(44)
        panel.setMaximumHeight(2000)
        panel.setStyleSheet(
            f"QFrame {{ background: transparent; border: none; border-bottom: 1px solid {CAPTION_ROW_BOTTOM_BORDER_COLOR}; }}"
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
        row, panel, label = self.create_row(side, with_copy_button=True)
        label.setText(text or " ")
        if side == "right":
            _, default_p = build_chunk_prompts(
                text or "", prompt_store, template_override=_active_chunk_template_override()
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
                "right", with_copy_button=True
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
            text or "", prompt_store, template_override=_active_chunk_template_override()
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
                text or "", prompt_store, template_override=_active_chunk_template_override()
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
        interview_history_append_interviewer(text, "home_capture")
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
        _, default_p = build_chunk_prompts(text or "", prompt_store, template_override=_active_chunk_template_override())
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
            interview_history_append_interviewer(edited, "rejected")
            self._restore_active_draft_panel_as_final(edited, None)
        else:
            _, clip_prompt = build_chunk_prompts(
                edited, prompt_store, template_override=_active_chunk_template_override()
            )
            self._restore_active_draft_panel_as_final(edited, clip_prompt)
            if edited:
                threading.Thread(target=process_chunk, args=(edited,), daemon=True).start()
        # Keep current active draft row (created after entering edit) for ongoing interviewer speech.
        self._caption_edit_text_edit = None
        self._caption_edit_panel = None
        self._caption_edit_cap_id = None
        self._caption_edit_sent_once = False
        self._home_edit_active = False
        self._tick_caption_autoscroll()
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
        _remember_last_gpt_answer(body)

    def finalize_gpt_stream_to_answer_row(self, answer: str, *, push_history: bool = True) -> None:
        self._remove_gpt_fallback_notice_row()
        self._remove_all_gpt_status_rows()
        ans = (answer or "").strip()
        self._remove_gpt_stream_row()
        shown = ans or (self.gpt_result_body.toPlainText() or "").strip()
        self._set_gpt_result_text(shown)
        if shown:
            interview_history_append_gpt(shown)
            if push_history:
                self._push_gpt_result_history(shown)
            else:
                self.gpt_result_history = [shown]
                self.gpt_history_index = 0
                self._refresh_gpt_history_nav()
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
            getattr(self, "_user_scroll_cooldown_seconds", 3.0)
        )

    def _on_scrollbar_user_action(self, _action: int) -> None:
        # Any user-driven scrollbar action (arrows, page, slider) extends the cooldown.
        self._mark_user_scroll_activity()

    def scroll_to_bottom(self):
        """Auto-stick to bottom only while the interviewer is actively speaking.

        Skip auto-scroll when:
          - no recent live-caption draft update (interviewer not currently saying),
          - the scrollbar slider is being dragged,
          - a recent wheel/keyboard scroll just happened (cooldown: _user_scroll_cooldown_seconds after last interaction),
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
                self.finalize_gpt_stream_to_answer_row(
                    event.get("text", ""),
                    push_history=bool(event.get("push_history", True)),
                )
            elif t == "new_empty_draft":
                self.create_new_empty_draft()
            elif t == "caption_edit_hotkey":
                self._try_enter_caption_edit_hotkey()
            elif t == "caption_edit_reject_hotkey":
                self._finish_caption_edit(True, self._caption_edit_cap_id)
            elif t == "caption_edit_spawn_new_draft":
                self._spawn_new_draft_while_editing()
            elif t == "message":
                self.show_toast(str(event.get("text", "") or "").strip() or "Notice.", level="warning")
            elif t == "f9_paste":
                text = str(event.get("text", "") or "")
                replace_all = bool(event.get("replace_all", False))
                app_inst = QApplication.instance()
                if app_inst is None:
                    self.show_toast("Could not set clipboard.", level="error")
                    continue
                try:
                    app_inst.clipboard().setText(text)
                except Exception:
                    if os.name == "nt":
                        try:
                            _win32_set_clipboard_unicode(text)
                        except OSError:
                            self.show_toast("Could not set clipboard.", level="error")
                            continue
                    else:
                        self.show_toast("Could not set clipboard.", level="error")
                        continue
                threading.Thread(
                    target=_do_keyboard_paste_only, args=(replace_all,), daemon=True
                ).start()
            elif t == "ws_ext":
                handle_ws_extension_payload(event.get("payload") or {})


def _interview_history_now_tag() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def interview_history_reset() -> None:
    with _interview_history_lock:
        _interview_history_events.clear()


def interview_history_has_interviewer_lines() -> bool:
    with _interview_history_lock:
        return any((e.get("role") or "") == "interviewer" for e in _interview_history_events)


def interview_history_append_interviewer(body: str, source: str) -> None:
    raw = body if isinstance(body, str) else ""
    stripped = raw.strip()
    if not stripped:
        if source != "rejected":
            return
        stripped = "(empty)"
        raw = stripped
    with _interview_history_lock:
        _interview_history_events.append(
            {"time": _interview_history_now_tag(), "role": "interviewer", "source": str(source or ""), "body": raw.strip()}
        )


def interview_history_append_gpt(body: str) -> None:
    display = (body or "").strip() or "(no text)"
    with _interview_history_lock:
        _interview_history_events.append(
            {"time": _interview_history_now_tag(), "role": "gpt", "source": "final", "body": display}
        )


def interview_history_append_gpt_placeholder(reason: str) -> None:
    msg = (reason or "").strip() or "GPT: (no final output for the previous request)"
    with _interview_history_lock:
        _interview_history_events.append(
            {"time": _interview_history_now_tag(), "role": "gpt", "source": "placeholder", "body": msg}
        )


def interview_history_format_txt() -> str:
    with _interview_history_lock:
        events = list(_interview_history_events)
    lines: list[str] = [
        "Interview Assistant — transcript",
        f"Saved (local): {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
        "",
    ]
    for e in events:
        role = e.get("role", "")
        ts = e.get("time", "")
        tag = e.get("source", "")
        body = e.get("body", "")
        lines.append("=" * 60)
        if role == "interviewer":
            lines.append(f'Interviewer ({tag}) [{ts}]::')
            lines.append("-" * 60)
            lines.append(body)
        elif role == "gpt":
            if tag == "placeholder":
                lines.append(f"GPT [{ts}]:: (no final output)")
            else:
                lines.append(f"GPT [{ts}]::")
            lines.append("-" * 60)
            lines.append(body)
        lines.append("")
    lines.append("=" * 60)
    lines.append("End of transcript")
    return "\n".join(lines)


def _documents_dir_fallback() -> Path:
    docs = Path.home() / "Documents"
    if docs.is_dir():
        return docs
    return Path.cwd()


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


def queue_gpt_final(answer: str, *, push_history: bool = True) -> None:
    _remember_last_gpt_answer(answer)
    ui_queue.put({"type": "gpt_final", "text": answer, "push_history": push_history})


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


def _realign_chunk_boundary(new_text: str, anchor_full: str, fallback_idx: int) -> tuple[int, bool]:
    """Map next_chunk_start_index into new_text after refined_full_caption changed.

    anchor_full is refined_full_caption at last End/Delete (full snapshot).
    fallback_idx is the cursor before this frame, clamped to the pre-update string length.

    Returns (index, is_confident). Prefer append-only (startswith), else tail matches with tie-break
    near fallback_idx (Live Captions may repeat phrases; first find() is often wrong).
    """
    n = len(new_text)
    fb = max(0, min(int(fallback_idx), n))
    af = str(anchor_full or "")
    if not af:
        return fb, False
    if len(new_text) >= len(af) and new_text.startswith(af):
        return min(len(af), n), True
    tail_len = min(_CHUNK_ANCHOR_TAIL_CHARS, len(af))
    tail = af[-tail_len:] if tail_len else ""
    if len(tail) < 8:
        overlap = _suffix_prefix_overlap_len(af, new_text, max(_MIN_STRONG_OVERLAP_CHARS, len(tail)))
        if overlap >= _MIN_STRONG_OVERLAP_CHARS:
            return min(overlap, n), True
        return fb, False
    target = min(fb, n)
    max_reasonable_dist = max(320, len(af) // 3, len(new_text) // 4)
    best_pos = -1
    best_dist: int | None = None
    search_from = 0
    while True:
        pos = new_text.find(tail, search_from)
        if pos < 0:
            break
        mapped_end = pos + len(tail)
        dist = abs(mapped_end - target)
        if (
            best_dist is None
            or dist < best_dist
            or (dist == best_dist and pos > best_pos)
        ):
            best_pos, best_dist = pos, dist
        search_from = pos + 1
    if best_pos >= 0 and best_dist is not None and best_dist <= max_reasonable_dist:
        return min(best_pos + len(tail), n), True
    rp = new_text.rfind(tail)
    if rp >= 0:
        mapped_r = rp + len(tail)
        if abs(mapped_r - target) <= max_reasonable_dist:
            return min(mapped_r, n), True
    overlap = _suffix_prefix_overlap_len(af, new_text, tail_len)
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
    skipped = ""
    with capture_lock:
        full = refined_full_caption
        start = min(next_chunk_start_index, len(full))
        skipped = full[start:].strip()
        next_chunk_start_index = len(refined_full_caption)
        _caption_chunk_anchor_prefix = refined_full_caption
        _boundary_shift_candidate_idx = -1
        _boundary_shift_candidate_hits = 0
    if skipped:
        interview_history_append_interviewer(skipped, "delete_skip")


def process_chunk(raw_chunk):
    global pending_request_id, _interview_ws, _http_fallback_notified, _last_live_poll_for_rid
    prev_rid = (pending_request_id or "").strip()
    chunk = (raw_chunk or "").strip()
    if not chunk:
        return
    if prev_rid:
        interview_history_append_gpt_placeholder(
            "Previous GPT request had no recorded final output (superseded by a new interviewer chunk)."
        )
    interview_history_append_interviewer(chunk, "sent_gpt")
    _cleaned, final_prompt = build_chunk_prompts(
        raw_chunk, prompt_store, template_override=_active_chunk_template_override()
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
        template_override=_active_chunk_template_override(),
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
                    text, prompt_store, template_override=_active_chunk_template_override()
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
    global _keyboard_listener
    _keyboard_listener = keyboard.Listener(on_press=on_press, on_release=on_release)
    _keyboard_listener.start()
    _keyboard_listener.join()
    _keyboard_listener = None


def run_capture_loop():
    global refined_full_caption, next_chunk_start_index, _caption_chunk_anchor_prefix, previous_refined_text
    global _boundary_shift_candidate_idx, _boundary_shift_candidate_hits, fixed_caption
    global next_chunk_start_text
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
                    old_next = next_chunk_start_index
                    # print(len(refined_full_caption), len(previous_refined_text), len(refined_text))
                    if len(previous_refined_text) - len(refined_text) > 100:
                        proper_index = previous_refined_text.find(refined_text[:500])
                        if proper_index >= 0:
                            fixed_caption += previous_refined_text[:proper_index]

                    refined_full_caption = fixed_caption + refined_text
                    previous_refined_text = refined_text
                    new_full = refined_full_caption
                    if _caption_chunk_anchor_prefix:
                        mapped, _ = _realign_chunk_boundary(
                            new_full,
                            _caption_chunk_anchor_prefix,
                            min(old_next, len(old_full)),
                        )
                        next_chunk_start_index = max(0, min(mapped, len(new_full)))
                    else:
                        next_chunk_start_index = max(0, min(old_next, len(new_full)))
                    draft_tail = refined_full_caption[next_chunk_start_index:]

                queue_draft_input(draft_tail)
            except Exception:
                time.sleep(0.3)
            time.sleep(0.2)


def poll_latest_answer_loop():
    global pending_request_id, last_answer_request_id, _last_live_poll_for_rid
    global _last_manual_live_poll_for_rid, _last_manual_final_seen
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
            else:
                # HTTP fallback for manually typed ChatGPT replies (when WebSocket is down).
                man_live = _fetch_interview_live_http(MANUAL_GPT_REQUEST_ID)
                if man_live and (MANUAL_GPT_REQUEST_ID, man_live) != _last_manual_live_poll_for_rid:
                    _last_manual_live_poll_for_rid = (MANUAL_GPT_REQUEST_ID, man_live)
                    queue_gpt_stream_partial(man_live)
                if (
                    request_id == MANUAL_GPT_REQUEST_ID
                    and answer
                    and answer != _last_manual_final_seen
                ):
                    _last_manual_final_seen = answer
                    queue_gpt_final(answer, push_history=False)
        except Exception:
            pass
        time.sleep(sleep_s)


def main():
    global _main_interview_window
    ensure_extension_deployed()
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
