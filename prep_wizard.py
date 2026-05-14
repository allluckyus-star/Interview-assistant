"""Three-step setup UI before the main interview window (register, resume summary, JD summary)."""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any
from urllib.parse import quote

from PySide6.QtCore import QEasingCurve, QPropertyAnimation, Qt, QThread, QTimer, Signal
from PySide6.QtGui import QGuiApplication
from PySide6.QtWidgets import (
    QFrame,
    QGraphicsOpacityEffect,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSizePolicy,
    QStackedWidget,
    QVBoxLayout,
    QWidget,
)


# Rounded shell QSS must match top-level mask — keep equal to live.APP_SHELL_CORNER_RADIUS_PX.
APP_SHELL_CORNER_RADIUS_PX = 0


def _http_get_json(base: str, path: str) -> dict[str, Any]:
    req = urllib.request.Request(f"{base}{path}", method="GET", headers={"Cache-Control": "no-store"})
    with urllib.request.urlopen(req, timeout=8) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _http_post_json(base: str, path: str, payload: dict[str, Any]) -> tuple[dict[str, Any], int]:
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{base}{path}",
        data=body,
        method="POST",
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            return json.loads(resp.read().decode("utf-8")), resp.status
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            return json.loads(raw), exc.code
        except json.JSONDecodeError:
            return {"ok": False, "error": raw or str(exc)}, exc.code
    except OSError as exc:
        # Bridge stopped, tab closed, or RST mid-request (e.g. WinError 10053).
        return {"ok": False, "error": str(exc)}, 0


_TEMPLATE_KEYS = ("resume_summary", "jd_summary", "initial_interview")


def _apply_resume_summary_template(template: str, resume: str) -> str:
    return template.replace("{resume_text}", resume.strip())


def _apply_jd_summary_template(template: str, jd: str) -> str:
    return template.replace("{jd_text}", jd.strip())


def _apply_initial_interview_template(template: str, resume: str, jd: str) -> str:
    return (
        template.replace("{resume_text}", resume.strip()).replace("{jd_text}", jd.strip())
    )


class _WaitRegisterThread(QThread):
    """One blocking long-poll to the bridge (no UI timer polling)."""

    outcome = Signal(dict)

    def __init__(self, bridge_base: str, since_seq: int, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._bridge_base = bridge_base.rstrip("/")
        self._since_seq = since_seq

    def run(self) -> None:
        payload: dict[str, Any] = {"ok": False, "error": ""}
        try:
            path = f"/wait-register-event?since_seq={int(self._since_seq)}"
            req = urllib.request.Request(
                f"{self._bridge_base}{path}",
                method="GET",
                headers={"Cache-Control": "no-store"},
            )
            with urllib.request.urlopen(req, timeout=130) as resp:
                body = json.loads(resp.read().decode("utf-8"))
            payload = dict(body)
            req2 = urllib.request.Request(
                f"{self._bridge_base}/registered-clients",
                method="GET",
                headers={"Cache-Control": "no-store"},
            )
            with urllib.request.urlopen(req2, timeout=8) as resp2:
                payload["registry"] = json.loads(resp2.read().decode("utf-8"))
        except Exception as exc:
            payload = {"ok": False, "error": str(exc)}
        self.outcome.emit(payload)


class PrepWizardWidget(QWidget):
    """Setup: register → resume summary → JD summary → ChatGPT interview priming (embedded in main window)."""

    finished = Signal(str, str)

    def __init__(self, bridge_base: str = "http://127.0.0.1:8765", parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.bridge_base = bridge_base.rstrip("/")
        self.selected_client_id = ""
        self.display_email = ""
        self._prep_poll_timer = QTimer(self)
        self._prep_poll_timer.setInterval(450)
        self._prep_poll_timer.timeout.connect(self._on_prep_poll_tick)
        self._waiting_job_id = ""
        self._fade_anim: QPropertyAnimation | None = None

        self._register_seq_baseline = 0
        self._step1_advancing = False
        self._waiting_for_step1_chatgpt_tab = False
        self._reg_thread: _WaitRegisterThread | None = None
        self._abandon_register_wait = False
        self._resume_listen_timer = QTimer(self)
        self._resume_listen_timer.setInterval(400)
        self._resume_listen_timer.timeout.connect(self._tick_resume_listen)
        self._jd_listen_timer = QTimer(self)
        self._jd_listen_timer.setInterval(400)
        self._jd_listen_timer.timeout.connect(self._tick_jd_listen)
        self._step4_listen_timer = QTimer(self)
        self._step4_listen_timer.setInterval(400)
        self._step4_listen_timer.timeout.connect(self._tick_step4_listen)

        self.setObjectName("PrepWizardWidget")
        self.setStyleSheet(
            "#PrepWizardWidget { background: transparent; border: none; outline: none; }"
        )
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)

        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)

        self.shell = QFrame()
        self.shell.setObjectName("prepShell")
        self.shell.setFrameShape(QFrame.Shape.NoFrame)
        self.shell.setFrameShadow(QFrame.Shadow.Plain)
        _r = APP_SHELL_CORNER_RADIUS_PX
        self.shell.setStyleSheet(
            f"""
            #prepShell {{
                background: transparent;
                border-radius: {_r}px;
                border: none;
                outline: none;
            }}
            #prepShell QLabel {{
                font-weight: 700;
            }}
            #prepShell QPushButton {{
                font-weight: 700;
            }}
            """
        )
        root.addWidget(self.shell)

        layout = QVBoxLayout(self.shell)
        layout.setContentsMargins(40, 32, 40, 32)
        layout.setSpacing(10)

        top = QHBoxLayout()
        self.step_badge = QLabel("1 / 4")
        self.step_badge.setStyleSheet(
            "font-size: 15px; font-weight: 700; color: #3949ab; letter-spacing: 0.02em;"
        )
        top.addWidget(self.step_badge)
        top.addStretch(1)
        layout.addLayout(top)
        layout.addSpacing(16)

        self.title_label = QLabel("Register your extension")
        self.title_label.setAlignment(Qt.AlignHCenter)
        self.title_label.setStyleSheet(
            "font-size: 26px; font-weight: 700; color: #0d1117; padding: 8px 12px 4px 12px;"
        )
        layout.addWidget(self.title_label)

        self.sub_label = QLabel("")
        self.sub_label.setAlignment(Qt.AlignHCenter)
        self.sub_label.setWordWrap(True)
        self.sub_label.setStyleSheet(
            "font-size: 16px; font-weight: 700; color: #37474f; line-height: 1.45; padding: 4px 24px 8px 24px;"
        )
        layout.addWidget(self.sub_label)

        self.stack_host = QWidget()
        self.stack_host.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        host_layout = QVBoxLayout(self.stack_host)
        host_layout.setContentsMargins(8, 20, 8, 8)
        self.stack = QStackedWidget()
        host_layout.addWidget(self.stack)
        layout.addWidget(self.stack_host, stretch=1)

        self._page1 = self._build_step1()
        self._page2 = self._build_step2()
        self._page3 = self._build_step3()
        self._page4 = self._build_step4()
        self.stack.addWidget(self._page1)
        self.stack.addWidget(self._page2)
        self.stack.addWidget(self._page3)
        self.stack.addWidget(self._page4)

        layout.addSpacing(8)
        self.bottom_hint = QLabel("")
        self.bottom_hint.setWordWrap(True)
        self.bottom_hint.setAlignment(Qt.AlignHCenter)
        self.bottom_hint.setStyleSheet(
            "font-size: 14px; font-weight: 700; color: #546e7a; padding: 12px 20px 4px 20px; line-height: 1.4;"
        )
        layout.addWidget(self.bottom_hint)

    def _bridge_unreachable(self) -> bool:
        try:
            _http_get_json(self.bridge_base, "/registered-clients")
        except OSError:
            return True
        return False

    def _build_step1(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(16, 8, 16, 24)
        v.setSpacing(22)
        v.setAlignment(Qt.AlignHCenter)
        v.addStretch(1)
        self.step1_instructions = QLabel(
            "1. Open the Chrome window where you want to use ChatGPT.\n"
            "2. In the Interview Assistant extension popup, click Register.\n"
            "3. After Register, a ChatGPT tab opens here automatically before step 2."
        )
        self.step1_instructions.setAlignment(Qt.AlignHCenter)
        self.step1_instructions.setWordWrap(True)
        self.step1_instructions.setStyleSheet(
            "font-size: 17px; color: #263238; line-height: 1.55; padding: 12px 20px;"
        )
        v.addWidget(self.step1_instructions)
        self.step1_error = QLabel("")
        self.step1_error.setAlignment(Qt.AlignHCenter)
        self.step1_error.setWordWrap(True)
        self.step1_error.setStyleSheet(
            "color: #b71c1c; font-size: 16px; font-weight: 700; padding: 12px 20px; line-height: 1.45;"
        )
        self.step1_error.hide()
        v.addWidget(self.step1_error)
        self.step1_success = QLabel("")
        self.step1_success.setAlignment(Qt.AlignHCenter)
        self.step1_success.setWordWrap(True)
        self.step1_success.setStyleSheet(
            "color: #1b5e20; font-size: 17px; font-weight: 700; padding: 12px 20px; line-height: 1.45;"
        )
        self.step1_success.hide()
        v.addWidget(self.step1_success)
        self.step1_wait_hint = QLabel("")
        self.step1_wait_hint.setAlignment(Qt.AlignHCenter)
        self.step1_wait_hint.setWordWrap(True)
        self.step1_wait_hint.setStyleSheet(
            "font-size: 15px; font-weight: 700; color: #455a64; padding: 10px 24px; line-height: 1.5;"
        )
        self.step1_wait_hint.hide()
        v.addWidget(self.step1_wait_hint)
        self.step1_retry = QPushButton("Wait again")
        self.step1_retry.setMinimumHeight(44)
        self.step1_retry.setMinimumWidth(200)
        self.step1_retry.setCursor(Qt.PointingHandCursor)
        self.step1_retry.setStyleSheet(
            "QPushButton { padding: 12px 28px; border-radius: 12px; background: #eceff1; color: #263238; "
            "font-weight: 700; font-size: 15px; border: none; }"
            "QPushButton:hover { background: #cfd8dc; }"
        )
        self.step1_retry.hide()
        self.step1_retry.clicked.connect(self._start_register_wait_thread)
        v.addWidget(self.step1_retry, alignment=Qt.AlignHCenter)
        v.addStretch(2)
        return page

    def _prep_action_button_width_px(self) -> int:
        """Half of ~30% primary width — main and Skip share this width."""
        scr = QGuiApplication.primaryScreen()
        if scr is None:
            return 160
        aw = max(640, scr.availableGeometry().width())
        return max(140, min(260, int(aw * 0.15)))

    def _build_step2(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(16, 8, 16, 24)
        v.setSpacing(22)
        v.setAlignment(Qt.AlignHCenter)
        v.addStretch(1)
        self.step2_waiting = QLabel("")
        self.step2_waiting.setAlignment(Qt.AlignHCenter)
        self.step2_waiting.setWordWrap(True)
        self.step2_waiting.setStyleSheet(
            "font-size: 17px; color: #455a64; padding: 16px 28px; line-height: 1.5; font-weight: 700;"
        )
        self.step2_waiting.hide()
        v.addWidget(self.step2_waiting)
        self.step2_error = QLabel("")
        self.step2_error.setAlignment(Qt.AlignHCenter)
        self.step2_error.setWordWrap(True)
        self.step2_error.setStyleSheet(
            "color: #b71c1c; font-size: 16px; font-weight: 700; padding: 12px 24px; line-height: 1.45;"
        )
        self.step2_error.hide()
        v.addWidget(self.step2_error)
        bw = self._prep_action_button_width_px()
        self.btn_resume_summary = QPushButton("Get Resume summary")
        self.btn_resume_summary.setFixedSize(bw, 52)
        self.btn_resume_summary.setCursor(Qt.PointingHandCursor)
        self.btn_resume_summary.setStyleSheet(
            "QPushButton { padding: 14px 28px; border-radius: 14px; background: #3949ab; color: white; "
            "font-weight: 700; font-size: 16px; border: none; }"
            "QPushButton:hover { background: #5c6bc0; }"
            "QPushButton:pressed { background: #283593; }"
            "QPushButton:disabled { background: #9e9e9e; color: #eceff1; }"
        )
        self.btn_resume_summary.clicked.connect(self._on_resume_summary_clicked)
        self.step2_success = QLabel("")
        self.step2_success.setAlignment(Qt.AlignHCenter)
        self.step2_success.setWordWrap(True)
        self.step2_success.setStyleSheet(
            "color: #1b5e20; font-size: 16px; font-weight: 700; padding: 12px 24px; line-height: 1.45;"
        )
        self.step2_success.hide()
        v.addWidget(self.step2_success)
        self.step2_skip = QPushButton("Skip to interview")
        self.step2_skip.setFixedSize(bw, 52)
        self.step2_skip.setCursor(Qt.PointingHandCursor)
        self.step2_skip.setStyleSheet(
            "QPushButton { padding: 8px 18px; border-radius: 14px; background: #eceff1; color: #37474f; "
            "font-weight: 700; font-size: 13px; border: 1px solid #b0bec5; }"
            "QPushButton:hover { background: #cfd8dc; }"
        )
        self.step2_skip.clicked.connect(self._skip_to_interview)
        step2_actions = QWidget()
        sav = QVBoxLayout(step2_actions)
        sav.setContentsMargins(0, 0, 0, 0)
        sav.setSpacing(12)
        sav.addWidget(self.btn_resume_summary, 0, Qt.AlignmentFlag.AlignHCenter)
        sav.addWidget(self.step2_skip, 0, Qt.AlignmentFlag.AlignHCenter)
        skip2 = QHBoxLayout()
        skip2.addStretch(1)
        skip2.addWidget(step2_actions)
        skip2.addStretch(1)
        v.addLayout(skip2)
        v.addStretch(2)
        return page

    def _build_step3(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(16, 8, 16, 24)
        v.setSpacing(22)
        v.setAlignment(Qt.AlignHCenter)
        v.addStretch(1)
        self.step3_waiting = QLabel("")
        self.step3_waiting.setAlignment(Qt.AlignHCenter)
        self.step3_waiting.setWordWrap(True)
        self.step3_waiting.setStyleSheet(
            "font-size: 17px; color: #455a64; padding: 16px 28px; line-height: 1.5; font-weight: 700;"
        )
        self.step3_waiting.hide()
        v.addWidget(self.step3_waiting)
        self.step3_error = QLabel("")
        self.step3_error.setAlignment(Qt.AlignHCenter)
        self.step3_error.setWordWrap(True)
        self.step3_error.setStyleSheet(
            "color: #b71c1c; font-size: 16px; font-weight: 700; padding: 12px 24px; line-height: 1.45;"
        )
        self.step3_error.hide()
        v.addWidget(self.step3_error)
        bw3 = self._prep_action_button_width_px()
        self.btn_jd_summary = QPushButton("Get JD summary")
        self.btn_jd_summary.setFixedSize(bw3, 52)
        self.btn_jd_summary.setCursor(Qt.PointingHandCursor)
        self.btn_jd_summary.setStyleSheet(
            "QPushButton { padding: 14px 28px; border-radius: 14px; background: #3949ab; color: white; "
            "font-weight: 700; font-size: 16px; border: none; }"
            "QPushButton:hover { background: #5c6bc0; }"
            "QPushButton:pressed { background: #283593; }"
            "QPushButton:disabled { background: #9e9e9e; color: #eceff1; }"
        )
        self.btn_jd_summary.clicked.connect(self._on_jd_summary_clicked)
        self.step3_success = QLabel("")
        self.step3_success.setAlignment(Qt.AlignHCenter)
        self.step3_success.setWordWrap(True)
        self.step3_success.setStyleSheet(
            "color: #1b5e20; font-size: 16px; font-weight: 700; padding: 12px 24px; line-height: 1.45;"
        )
        self.step3_success.hide()
        v.addWidget(self.step3_success)
        self.step3_skip = QPushButton("Skip to interview")
        self.step3_skip.setFixedSize(bw3, 52)
        self.step3_skip.setCursor(Qt.PointingHandCursor)
        self.step3_skip.setStyleSheet(
            "QPushButton { padding: 8px 18px; border-radius: 14px; background: #eceff1; color: #37474f; "
            "font-weight: 700; font-size: 13px; border: 1px solid #b0bec5; }"
            "QPushButton:hover { background: #cfd8dc; }"
        )
        self.step3_skip.clicked.connect(self._skip_to_interview)
        step3_actions = QWidget()
        s3v = QVBoxLayout(step3_actions)
        s3v.setContentsMargins(0, 0, 0, 0)
        s3v.setSpacing(12)
        s3v.addWidget(self.btn_jd_summary, 0, Qt.AlignmentFlag.AlignHCenter)
        s3v.addWidget(self.step3_skip, 0, Qt.AlignmentFlag.AlignHCenter)
        skip3 = QHBoxLayout()
        skip3.addStretch(1)
        skip3.addWidget(step3_actions)
        skip3.addStretch(1)
        v.addLayout(skip3)
        v.addStretch(2)
        return page

    def _build_step4(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(16, 8, 16, 24)
        v.setSpacing(22)
        v.setAlignment(Qt.AlignHCenter)
        v.addStretch(1)
        self.step4_waiting = QLabel("")
        self.step4_waiting.setAlignment(Qt.AlignHCenter)
        self.step4_waiting.setWordWrap(True)
        self.step4_waiting.setStyleSheet(
            "font-size: 17px; color: #455a64; padding: 16px 28px; line-height: 1.5; font-weight: 700;"
        )
        self.step4_waiting.hide()
        v.addWidget(self.step4_waiting)
        self.step4_error = QLabel("")
        self.step4_error.setAlignment(Qt.AlignHCenter)
        self.step4_error.setWordWrap(True)
        self.step4_error.setStyleSheet(
            "color: #b71c1c; font-size: 16px; font-weight: 700; padding: 12px 24px; line-height: 1.45;"
        )
        self.step4_error.hide()
        v.addWidget(self.step4_error)
        bw4 = self._prep_action_button_width_px()
        self.btn_interview_setup = QPushButton("Prepare interview")
        self.btn_interview_setup.setFixedSize(bw4, 52)
        self.btn_interview_setup.setCursor(Qt.PointingHandCursor)
        self.btn_interview_setup.setStyleSheet(
            "QPushButton { padding: 14px 28px; border-radius: 14px; background: #3949ab; color: white; "
            "font-weight: 700; font-size: 16px; border: none; }"
            "QPushButton:hover { background: #5c6bc0; }"
            "QPushButton:pressed { background: #283593; }"
            "QPushButton:disabled { background: #9e9e9e; color: #eceff1; }"
        )
        self.btn_interview_setup.clicked.connect(self._on_interview_setup_clicked)
        self.step4_success = QLabel("")
        self.step4_success.setAlignment(Qt.AlignHCenter)
        self.step4_success.setWordWrap(True)
        self.step4_success.setStyleSheet(
            "color: #1b5e20; font-size: 16px; font-weight: 700; padding: 12px 24px; line-height: 1.45;"
        )
        self.step4_success.hide()
        v.addWidget(self.step4_success)
        self.step4_skip = QPushButton("Skip to interview")
        self.step4_skip.setFixedSize(bw4, 52)
        self.step4_skip.setCursor(Qt.PointingHandCursor)
        self.step4_skip.setStyleSheet(
            "QPushButton { padding: 8px 18px; border-radius: 14px; background: #eceff1; color: #37474f; "
            "font-weight: 700; font-size: 13px; border: 1px solid #b0bec5; }"
            "QPushButton:hover { background: #cfd8dc; }"
        )
        self.step4_skip.clicked.connect(self._skip_to_interview)
        step4_actions = QWidget()
        s4v = QVBoxLayout(step4_actions)
        s4v.setContentsMargins(0, 0, 0, 0)
        s4v.setSpacing(12)
        s4v.addWidget(self.btn_interview_setup, 0, Qt.AlignmentFlag.AlignHCenter)
        s4v.addWidget(self.step4_skip, 0, Qt.AlignmentFlag.AlignHCenter)
        skip4 = QHBoxLayout()
        skip4.addStretch(1)
        skip4.addWidget(step4_actions)
        skip4.addStretch(1)
        v.addLayout(skip4)
        v.addStretch(2)
        return page

    def closeEvent(self, event) -> None:  # noqa: N802
        self._stop_register_wait_thread()
        self._resume_listen_timer.stop()
        self._jd_listen_timer.stop()
        self._step4_listen_timer.stop()
        self._prep_poll_timer.stop()
        super().closeEvent(event)

    def _stop_register_wait_thread(self) -> None:
        """Join or detach the step-1 long-poll thread so it is not running when this widget is destroyed."""
        t = self._reg_thread
        if t is None:
            return
        self._abandon_register_wait = True
        try:
            t.outcome.disconnect(self._on_register_wait_outcome)
        except TypeError:
            pass
        try:
            t.finished.disconnect(self._on_register_thread_finished)
        except TypeError:
            pass
        t.setParent(None)
        self._reg_thread = None
        if t.isRunning():
            # Long-poll can block up to ~130s; do not wait for a clean return on close/handoff.
            t.terminate()
            t.wait(400)
        t.deleteLater()

    def shutdown_for_handoff(self) -> None:
        """Stop timers/animation before the wizard widget is destroyed (avoids stray timeouts)."""
        self._stop_register_wait_thread()
        self._resume_listen_timer.stop()
        self._jd_listen_timer.stop()
        self._step4_listen_timer.stop()
        self._prep_poll_timer.stop()
        self._waiting_job_id = ""
        self._waiting_for_step1_chatgpt_tab = False
        anim = self._fade_anim
        if anim is not None:
            try:
                anim.stop()
            except Exception:
                pass
            self._fade_anim = None

    def showEvent(self, event) -> None:  # noqa: N802
        super().showEvent(event)
        if self.stack.currentIndex() != 0 or self._step1_advancing:
            return
        if self._reg_thread is not None and self._reg_thread.isRunning():
            return
        self._reset_step1_baseline_from_bridge()
        self._start_register_wait_thread()

    def _reset_step1_baseline_from_bridge(self) -> None:
        self.step1_success.hide()
        self.step1_error.hide()
        self.step1_retry.hide()
        self.step1_wait_hint.hide()
        try:
            data = _http_get_json(self.bridge_base, "/registered-clients")
        except OSError:
            self._register_seq_baseline = 0
            return
        ev = data.get("register_event") or {}
        self._register_seq_baseline = int(ev.get("seq") or 0)

    def _start_register_wait_thread(self) -> None:
        if self._abandon_register_wait or self._step1_advancing or self.stack.currentIndex() != 0:
            return
        if self._reg_thread is not None and self._reg_thread.isRunning():
            return
        self.step1_error.hide()
        self.step1_retry.hide()
        self.step1_success.hide()
        self.step1_wait_hint.setText(
            "Waiting on the bridge for your next Register click in the extension (no background polling)."
        )
        self.step1_wait_hint.show()
        since = self._register_seq_baseline
        self._reg_thread = _WaitRegisterThread(self.bridge_base, since, self)
        self._reg_thread.outcome.connect(self._on_register_wait_outcome)
        self._reg_thread.finished.connect(self._on_register_thread_finished)
        self._reg_thread.start()

    def _on_register_thread_finished(self) -> None:
        self._reg_thread = None

    def _on_register_wait_outcome(self, payload: dict[str, Any]) -> None:
        self.step1_wait_hint.hide()
        if self._abandon_register_wait or self.stack.currentIndex() != 0 or self._step1_advancing:
            return
        if payload.get("ok") is False:
            self.step1_error.setText(
                f"Could not reach the bridge ({payload.get('error', '')}). Start live.py, then Wait again."
            )
            self.step1_error.show()
            self.step1_retry.show()
            return
        timed_out = bool(payload.get("timed_out"))
        data = payload.get("registry") or {}
        ev = data.get("register_event") or {}
        if not timed_out:
            msg = (ev.get("message") or "").strip() or "[Extension] is registered."
            self._advance_step1_from_bridge(data, msg)
            return
        self.step1_error.setText(
            "No new Register yet. Open the Interview Assistant extension, click Register, then Wait again."
        )
        self.step1_error.show()
        self.step1_retry.show()

    def _advance_step1_from_bridge(self, data: dict[str, Any], message: str) -> None:
        if self._step1_advancing:
            return
        self._step1_advancing = True
        sel = str(data.get("selected_client_id") or "").strip()
        ev = data.get("register_event") or {}
        cid = str(ev.get("client_id") or "").strip()
        self.selected_client_id = cid or sel
        self.display_email = ""
        self.step1_error.hide()
        self.step1_success.setText(message)
        self.step1_success.show()
        try:
            _http_post_json(self.bridge_base, "/prep/clear", {})
        except OSError:
            pass
        _http_post_json(self.bridge_base, "/sync-store-to-selected-client", {})
        if not self._queue_open_chatgpt_tab_job():
            self.step1_success.hide()
            self.step1_error.setText(
                "Registered, but the bridge could not queue opening ChatGPT. "
                "Ensure live.py is running, then use Wait again."
            )
            self.step1_error.show()
            self.step1_retry.show()
            self._step1_advancing = False
            return

    def _queue_open_chatgpt_tab_job(self) -> bool:
        """Queue extension job that only opens/focuses ChatGPT (no prompt). Returns False on bridge error."""
        cid = (self.selected_client_id or "").strip()
        if not cid:
            return False
        body, status = _http_post_json(
            self.bridge_base,
            "/prep/start",
            {"phase": "open_chatgpt", "prompt": " ", "open_new_tab": True, "client_id": cid},
        )
        if status >= 400 or not body.get("ok"):
            return False
        job_id = str(body.get("job_id", "")).strip()
        if not job_id:
            return False
        self._waiting_for_step1_chatgpt_tab = True
        self._waiting_job_id = job_id
        self._prep_poll_timer.start()
        return True

    def _animate_to_step(self, index: int) -> None:
        """Cross-fade to stacked page index."""
        prev = self.stack.currentWidget()
        if prev is None:
            self._finish_step_transition(index)
            return
        eff = QGraphicsOpacityEffect(prev)
        prev.setGraphicsEffect(eff)
        self._fade_anim = QPropertyAnimation(eff, b"opacity")
        self._fade_anim.setDuration(200)
        self._fade_anim.setStartValue(1.0)
        self._fade_anim.setEndValue(0.0)
        self._fade_anim.setEasingCurve(QEasingCurve.Type.InOutQuad)

        def on_done() -> None:
            self._fade_anim = None
            prev.setGraphicsEffect(None)
            self._finish_step_transition(index)

        self._fade_anim.finished.connect(on_done)
        self._fade_anim.start()

    def _finish_step_transition(self, index: int) -> None:
        self.stack.setCurrentIndex(index)
        cur = self.stack.currentWidget()
        if cur is None:
            return
        eff2 = QGraphicsOpacityEffect(cur)
        cur.setGraphicsEffect(eff2)
        self._fade_anim = QPropertyAnimation(eff2, b"opacity")
        self._fade_anim.setDuration(220)
        self._fade_anim.setStartValue(0.0)
        self._fade_anim.setEndValue(1.0)
        self._fade_anim.setEasingCurve(QEasingCurve.Type.OutCubic)

        def cleanup() -> None:
            self._fade_anim = None
            cur.setGraphicsEffect(None)

        self._fade_anim.finished.connect(cleanup)
        self._fade_anim.start()

        self.step_badge.setText(f"{index + 1} / 4")
        if index == 0:
            self._resume_listen_timer.stop()
            self._jd_listen_timer.stop()
            self._step4_listen_timer.stop()
            self._step1_advancing = False
            self.title_label.setText("Register your extension")
            self.sub_label.setText(
                "After you click Register in the extension, this app continues automatically (one long wait, not polling)."
            )
            self.sub_label.show()
            self.bottom_hint.setText("")
            self._reset_step1_baseline_from_bridge()
            self._start_register_wait_thread()
        elif index == 1:
            self._step1_advancing = False
            self._jd_listen_timer.stop()
            self._step4_listen_timer.stop()
            self.title_label.setText("Resume summary")
            self.sub_label.hide()
            self.bottom_hint.setText("")
            self._prepare_step2()
        elif index == 2:
            self._resume_listen_timer.stop()
            self._step4_listen_timer.stop()
            self.title_label.setText("Job description summary")
            self.sub_label.hide()
            self.bottom_hint.setText("")
            self._prepare_step3()
        else:
            self._resume_listen_timer.stop()
            self._jd_listen_timer.stop()
            self.title_label.setText("Set ChatGPT for interview")
            self.bottom_hint.setText("")
            self._prepare_step4()

    def _client_record(self) -> dict[str, Any] | None:
        try:
            data = _http_get_json(self.bridge_base, "/registered-clients")
        except OSError:
            return None
        for c in data.get("clients") or []:
            if isinstance(c, dict) and str(c.get("client_id", "")).strip() == self.selected_client_id:
                return c
        return None

    def _resume_text_for_selected(self) -> str:
        """Use this wizard session's selected client only (never another profile's global store)."""
        rec = self._client_record()
        from_reg = (rec.get("resume_text") or "").strip() if rec else ""
        if from_reg:
            return from_reg
        cid = (self.selected_client_id or "").strip()
        if not cid:
            return ""
        try:
            path = f"/context?client_id={quote(cid, safe='')}"
            data = _http_get_json(self.bridge_base, path)
            return (data.get("resume") or "").strip()
        except OSError:
            return ""

    def _jd_text_for_selected(self) -> str:
        rec = self._client_record()
        from_reg = (rec.get("jd_text") or "").strip() if rec else ""
        if from_reg:
            return from_reg
        cid = (self.selected_client_id or "").strip()
        if not cid:
            return ""
        try:
            path = f"/context?client_id={quote(cid, safe='')}"
            data = _http_get_json(self.bridge_base, path)
            return (data.get("job_description") or "").strip()
        except OSError:
            return ""

    def _template_for_selected(self, key: str) -> str:
        """Fetch a prompt template from the main app (bridge serves PromptStore / root .txt files)."""
        if key not in _TEMPLATE_KEYS:
            return ""
        rec = self._client_record()
        if rec:
            from_reg = str(rec.get(f"tpl_{key}") or "").strip()
            if from_reg:
                return from_reg
        cid = (self.selected_client_id or "").strip()
        if not cid:
            return ""
        try:
            path = f"/context?client_id={quote(cid, safe='')}"
            data = _http_get_json(self.bridge_base, path)
            templates = data.get("templates") or {}
            if isinstance(templates, dict):
                return str(templates.get(key) or "").strip()
        except OSError:
            return ""
        return ""

    def _prepare_step2(self) -> None:
        self.step2_success.hide()
        self.btn_resume_summary.setEnabled(True)
        self.btn_resume_summary.setText("Get Resume summary")
        resume = self._resume_text_for_selected()
        template = self._template_for_selected("resume_summary")
        missing: list[str] = []
        if not resume:
            missing.append("resume in the extension")
        if not template:
            missing.append(
                "resume summary prompt template (Settings → Prompts, or prompt_resume_summary.txt beside the app)"
            )
        if missing:
            self.step2_waiting.setText("Waiting for: " + ", ".join(missing) + "…")
            self.step2_waiting.show()
            self.step2_error.hide()
            self.btn_resume_summary.setEnabled(False)
            if self.stack.currentIndex() == 1 and not self._waiting_job_id:
                if not self._resume_listen_timer.isActive():
                    self._resume_listen_timer.start()
            return
        self._resume_listen_timer.stop()
        self.step2_waiting.hide()
        self.step2_error.hide()

    def _tick_resume_listen(self) -> None:
        if self.stack.currentIndex() != 1 or self._waiting_job_id:
            self._resume_listen_timer.stop()
            return
        self._prepare_step2()

    def _tick_jd_listen(self) -> None:
        if self.stack.currentIndex() != 2 or self._waiting_job_id:
            self._jd_listen_timer.stop()
            return
        self._prepare_step3()

    def _tick_step4_listen(self) -> None:
        if self.stack.currentIndex() != 3 or self._waiting_job_id:
            self._step4_listen_timer.stop()
            return
        self._prepare_step4()

    def _prepare_step4(self) -> None:
        self.step4_success.hide()
        self.btn_interview_setup.setEnabled(True)
        self.btn_interview_setup.setText("Prepare interview")
        jd = self._jd_text_for_selected()
        initial_tpl = self._template_for_selected("initial_interview")
        missing: list[str] = []
        if not jd:
            missing.append("job description in the extension")
        if not initial_tpl:
            missing.append(
                "initial interview prompt (Settings → Prompts, or prompt_initial_interview.txt beside the app)"
            )
        if missing:
            self.step4_waiting.setText("Waiting for: " + ", ".join(missing) + "…")
            self.step4_waiting.show()
            self.step4_error.hide()
            self.btn_interview_setup.setEnabled(False)
            if self.stack.currentIndex() == 3 and not self._waiting_job_id:
                if not self._step4_listen_timer.isActive():
                    self._step4_listen_timer.start()
            return
        self._step4_listen_timer.stop()
        self.step4_waiting.hide()
        self.step4_error.hide()

    def _prepare_step3(self) -> None:
        self.step3_success.hide()
        self.btn_jd_summary.setEnabled(True)
        self.btn_jd_summary.setText("Get JD summary")
        jd = self._jd_text_for_selected()
        template = self._template_for_selected("jd_summary")
        missing: list[str] = []
        if not jd:
            missing.append("job description in the extension")
        if not template:
            missing.append(
                "JD summary prompt template (Settings → Prompts, or prompt_jd_summary.txt beside the app)"
            )
        if missing:
            self.step3_waiting.setText("Waiting for: " + ", ".join(missing) + "…")
            self.step3_waiting.show()
            self.step3_error.hide()
            self.btn_jd_summary.setEnabled(False)
            if self.stack.currentIndex() == 2 and not self._waiting_job_id:
                if not self._jd_listen_timer.isActive():
                    self._jd_listen_timer.start()
            return
        self._jd_listen_timer.stop()
        self.step3_waiting.hide()
        self.step3_error.hide()

    def _on_resume_summary_clicked(self) -> None:
        resume = self._resume_text_for_selected()
        template = self._template_for_selected("resume_summary")
        if not resume or not template:
            return
        cid = (self.selected_client_id or "").strip()
        if not cid:
            self.step2_error.setText("No registered Chrome client id. Finish step 1 in this profile first.")
            self.step2_error.show()
            return
        prompt = _apply_resume_summary_template(template, resume)
        body, status = _http_post_json(
            self.bridge_base,
            "/prep/start",
            {"phase": "resume_summary", "prompt": prompt, "open_new_tab": False, "client_id": cid},
        )
        if status >= 400 or not body.get("ok"):
            self.step2_error.setText("Could not start summary job. Is the extension installed?")
            self.step2_error.show()
            return
        job_id = str(body.get("job_id", "")).strip()
        if not job_id:
            self.step2_error.setText("Bridge did not return a job id.")
            self.step2_error.show()
            return
        self._waiting_job_id = job_id
        self.btn_resume_summary.setEnabled(False)
        self.btn_resume_summary.setText("Working…")
        self._resume_listen_timer.stop()
        self.step2_waiting.hide()
        self.step2_error.hide()
        self.step2_success.hide()
        self._prep_poll_timer.start()

    def _on_jd_summary_clicked(self) -> None:
        jd = self._jd_text_for_selected()
        template = self._template_for_selected("jd_summary")
        if not jd or not template:
            return
        cid = (self.selected_client_id or "").strip()
        if not cid:
            self.step3_error.setText("No registered Chrome client id. Finish step 1 in this profile first.")
            self.step3_error.show()
            return
        prompt = _apply_jd_summary_template(template, jd)
        body, status = _http_post_json(
            self.bridge_base,
            "/prep/start",
            {"phase": "jd_summary", "prompt": prompt, "open_new_tab": False, "client_id": cid},
        )
        if status >= 400 or not body.get("ok"):
            self.step3_error.setText("Could not start summary job. Is the extension installed?")
            self.step3_error.show()
            return
        job_id = str(body.get("job_id", "")).strip()
        if not job_id:
            self.step3_error.setText("Bridge did not return a job id.")
            self.step3_error.show()
            return
        self._waiting_job_id = job_id
        self.btn_jd_summary.setEnabled(False)
        self.btn_jd_summary.setText("Working… ")
        self._jd_listen_timer.stop()
        self.step3_waiting.hide()
        self.step3_error.hide()
        self.step3_success.hide()
        self._prep_poll_timer.start()

    def _on_interview_setup_clicked(self) -> None:
        jd = self._jd_text_for_selected()
        initial_tpl = self._template_for_selected("initial_interview")
        if not jd or not initial_tpl:
            return
        cid = (self.selected_client_id or "").strip()
        if not cid:
            self.step4_error.setText("No registered Chrome client id. Finish step 1 in this profile first.")
            self.step4_error.show()
            return
        resume = self._resume_text_for_selected()
        prompt = _apply_initial_interview_template(initial_tpl, resume, jd)
        body, status = _http_post_json(
            self.bridge_base,
            "/prep/start",
            {"phase": "interview_gpt_setup", "prompt": prompt, "open_new_tab": False, "client_id": cid},
        )
        if status >= 400 or not body.get("ok"):
            self.step4_error.setText("Could not start summary job. Is the extension installed?")
            self.step4_error.show()
            return
        job_id = str(body.get("job_id", "")).strip()
        if not job_id:
            self.step4_error.setText("Bridge did not return a job id.")
            self.step4_error.show()
            return
        self._waiting_job_id = job_id
        self.btn_interview_setup.setEnabled(False)
        self.btn_interview_setup.setText("Working… ")
        self._step4_listen_timer.stop()
        self.step4_waiting.hide()
        self.step4_error.hide()
        self.step4_success.hide()
        self._prep_poll_timer.start()

    def _skip_to_interview(self) -> None:
        """Jump to the main interview (same as completing the wizard), best-effort sync with the bridge."""
        cid = (self.selected_client_id or "").strip()
        if not cid:
            idx = self.stack.currentIndex()
            msg = "Register the extension on step 1 first (no client id)."
            if idx == 1:
                self.step2_error.setText(msg)
                self.step2_error.show()
            elif idx == 2:
                self.step3_error.setText(msg)
                self.step3_error.show()
            else:
                self.step4_error.setText(msg)
                self.step4_error.show()
            return
        self._prep_poll_timer.stop()
        self._waiting_job_id = ""
        self._waiting_for_step1_chatgpt_tab = False
        try:
            _http_post_json(self.bridge_base, "/prep/clear", {})
        except OSError:
            pass
        self._resume_listen_timer.stop()
        self._jd_listen_timer.stop()
        self._step4_listen_timer.stop()
        try:
            _http_post_json(self.bridge_base, "/sync-store-to-selected-client", {})
        except OSError:
            pass
        self.finished.emit((self.display_email or "").strip(), cid)

    def _on_prep_poll_tick(self) -> None:
        if not self._waiting_job_id:
            self._prep_poll_timer.stop()
            return
        cid = (self.selected_client_id or "").strip()
        try:
            job_path = "/prep/job" if not cid else f"/prep/job?client_id={quote(cid, safe='')}"
            job = _http_get_json(self.bridge_base, job_path)
        except OSError:
            return
        if str(job.get("job_id", "")) != self._waiting_job_id:
            return
        st = str(job.get("status", ""))
        if st == "pending" or st == "running":
            return
        self._prep_poll_timer.stop()
        self._waiting_job_id = ""
        idx = self.stack.currentIndex()

        if self._waiting_for_step1_chatgpt_tab:
            self._waiting_for_step1_chatgpt_tab = False
            try:
                _http_post_json(self.bridge_base, "/prep/clear", {})
            except OSError:
                pass
            if st == "done":
                QTimer.singleShot(300, lambda: self._animate_to_step(1))
            else:
                err = str(job.get("error", "") or "Unknown error")
                self.step1_success.hide()
                self.step1_error.setText(f"Could not open ChatGPT tab: {err}")
                self.step1_error.show()
                self.step1_retry.show()
                self._step1_advancing = False
            return

        if st == "done":
            if idx == 1:
                self.step2_success.setText("Resume summary saved. Continuing…")
                self.step2_success.show()
                _http_post_json(self.bridge_base, "/prep/clear", {})
                QTimer.singleShot(900, lambda: self._animate_to_step(2))
            elif idx == 2:
                self.step3_success.setText("Job description summary saved. Continuing…")
                self.step3_success.show()
                _http_post_json(self.bridge_base, "/prep/clear", {})
                QTimer.singleShot(900, lambda: self._animate_to_step(3))
            elif idx == 3:
                self.step4_success.setText("ChatGPT is ready. Opening interview…")
                self.step4_success.show()
                _http_post_json(self.bridge_base, "/prep/clear", {})
                QTimer.singleShot(900, lambda: self.finished.emit(self.display_email, self.selected_client_id))
        else:
            err = str(job.get("error", "") or "Unknown error")
            if idx == 1:
                self.step2_waiting.hide()
                self.step2_error.setText(f"Summary failed: {err}")
                self.step2_error.show()
                self.btn_resume_summary.setEnabled(True)
                self.btn_resume_summary.setText("Get Resume summary")
                _http_post_json(self.bridge_base, "/prep/clear", {})
                self._prepare_step2()
            elif idx == 2:
                self.step3_waiting.hide()
                self.step3_error.setText(f"Summary failed: {err}")
                self.step3_error.show()
                self.btn_jd_summary.setEnabled(True)
                self.btn_jd_summary.setText("Get JD summary")
                _http_post_json(self.bridge_base, "/prep/clear", {})
                self._prepare_step3()
            elif idx == 3:
                self.step4_waiting.hide()
                self.step4_error.setText(f"Summary failed: {err}")
                self.step4_error.show()
                self.btn_interview_setup.setEnabled(True)
                self.btn_interview_setup.setText("Prepare interview")
                _http_post_json(self.bridge_base, "/prep/clear", {})
                self._prepare_step4()


PrepWizardWindow = PrepWizardWidget  # backward-compatible name
