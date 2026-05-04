"""Three-step setup UI before the main interview window (account, resume summary, JD summary)."""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any, Callable

from PySide6.QtCore import QEasingCurve, QPropertyAnimation, Qt, QTimer, Signal
from PySide6.QtWidgets import (
    QFrame,
    QGraphicsOpacityEffect,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QStackedWidget,
    QVBoxLayout,
    QWidget,
)


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


def _build_resume_summary_prompt(resume: str) -> str:
    return (
        "Summarize this resume for quick interview reference. "
        "Output plain text only: short bullet-style lines, key skills, years of experience, domains. "
        "No preamble, no headings like 'Summary:'.\n\n"
        f"Resume:\n---\n{resume.strip()}\n---"
    )


def _build_jd_summary_prompt(jd: str) -> str:
    return (
        "Summarize this job description for interview prep. "
        "Output plain text only: role, must-have skills, responsibilities, location/mode if stated. "
        "No preamble.\n\n"
        f"Job description:\n---\n{jd.strip()}\n---"
    )


class PrepWizardWindow(QWidget):
    """Setup flow: pick registered client → resume GPT summary → JD GPT summary."""

    finished = Signal(str, str)

    def __init__(self, bridge_base: str = "http://127.0.0.1:8765") -> None:
        super().__init__()
        self.bridge_base = bridge_base.rstrip("/")
        self.selected_client_id = ""
        self.display_email = ""
        self._prep_poll_timer = QTimer(self)
        self._prep_poll_timer.setInterval(450)
        self._prep_poll_timer.timeout.connect(self._on_prep_poll_tick)
        self._waiting_job_id = ""
        self._fade_anim: QPropertyAnimation | None = None

        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.resize(720, 520)

        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)

        self.shell = QFrame()
        self.shell.setObjectName("prepShell")
        self.shell.setStyleSheet(
            """
            #prepShell {
                background-color: rgba(255, 255, 255, 235);
                border-radius: 22px;
                border: 1px solid #d0d4e0;
            }
            """
        )
        root.addWidget(self.shell)

        layout = QVBoxLayout(self.shell)
        layout.setContentsMargins(28, 22, 28, 22)
        layout.setSpacing(14)

        top = QHBoxLayout()
        self.step_badge = QLabel("1 / 3")
        self.step_badge.setStyleSheet("font-size: 13px; font-weight: 700; color: #3949ab;")
        top.addWidget(self.step_badge)
        top.addStretch(1)
        close_btn = QPushButton("✕")
        close_btn.setFixedSize(32, 32)
        close_btn.setCursor(Qt.PointingHandCursor)
        close_btn.setStyleSheet(
            "QPushButton { border: none; border-radius: 16px; background: #eee; font-size: 16px; }"
            "QPushButton:hover { background: #f88; color: white; }"
        )
        close_btn.clicked.connect(self.close)
        top.addWidget(close_btn)
        layout.addLayout(top)

        self.title_label = QLabel("Select your account")
        self.title_label.setStyleSheet("font-size: 20px; font-weight: 700; color: #111;")
        layout.addWidget(self.title_label)

        self.sub_label = QLabel("Choose the Chrome profile you registered with the extension.")
        self.sub_label.setWordWrap(True)
        self.sub_label.setStyleSheet("font-size: 13px; color: #444;")
        layout.addWidget(self.sub_label)

        self.stack_host = QWidget()
        self.stack_host.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        host_layout = QVBoxLayout(self.stack_host)
        host_layout.setContentsMargins(0, 8, 0, 0)
        self.stack = QStackedWidget()
        host_layout.addWidget(self.stack)
        layout.addWidget(self.stack_host, stretch=1)

        self._page1 = self._build_step1()
        self._page2 = self._build_step2()
        self._page3 = self._build_step3()
        self.stack.addWidget(self._page1)
        self.stack.addWidget(self._page2)
        self.stack.addWidget(self._page3)

        self.bottom_hint = QLabel("")
        self.bottom_hint.setWordWrap(True)
        self.bottom_hint.setStyleSheet("font-size: 11px; color: #666;")
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
        v.setSpacing(12)
        self.step1_error = QLabel("")
        self.step1_error.setWordWrap(True)
        self.step1_error.setStyleSheet("color: #c62828; font-size: 13px;")
        self.step1_error.hide()
        v.addWidget(self.step1_error)

        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        scroll.setFrameShape(QFrame.NoFrame)
        scroll.setStyleSheet("QScrollArea { background: transparent; border: none; }")
        inner = QWidget()
        self.chips_layout = QHBoxLayout(inner)
        self.chips_layout.setSpacing(12)
        self.chips_layout.setAlignment(Qt.AlignLeft)
        scroll.setWidget(inner)
        v.addWidget(scroll, stretch=1)
        return page

    def _build_step2(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setSpacing(12)
        self.step2_error = QLabel("")
        self.step2_error.setWordWrap(True)
        self.step2_error.setStyleSheet("color: #c62828; font-size: 13px;")
        self.step2_error.hide()
        v.addWidget(self.step2_error)
        self.step2_success = QLabel("")
        self.step2_success.setWordWrap(True)
        self.step2_success.setStyleSheet("color: #2e7d32; font-size: 13px;")
        self.step2_success.hide()
        v.addWidget(self.step2_success)
        self.btn_resume_summary = QPushButton("Get resume summary")
        self.btn_resume_summary.setCursor(Qt.PointingHandCursor)
        self.btn_resume_summary.setStyleSheet(
            "QPushButton { padding: 12px 20px; border-radius: 12px; background: #3949ab; color: white; "
            "font-weight: 600; font-size: 14px; border: none; }"
            "QPushButton:hover { background: #5c6bc0; }"
            "QPushButton:pressed { background: #283593; }"
            "QPushButton:disabled { background: #aaa; }"
        )
        self.btn_resume_summary.clicked.connect(self._on_resume_summary_clicked)
        v.addWidget(self.btn_resume_summary)
        self.btn_refresh_resume = QPushButton("Refresh from extension")
        self.btn_refresh_resume.setFlat(True)
        self.btn_refresh_resume.setCursor(Qt.PointingHandCursor)
        self.btn_refresh_resume.setStyleSheet("color: #3949ab; font-size: 12px; text-align: left;")
        self.btn_refresh_resume.clicked.connect(self._prepare_step2)
        v.addWidget(self.btn_refresh_resume)
        v.addStretch(1)
        return page

    def _build_step3(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setSpacing(12)
        self.step3_error = QLabel("")
        self.step3_error.setWordWrap(True)
        self.step3_error.setStyleSheet("color: #c62828; font-size: 13px;")
        self.step3_error.hide()
        v.addWidget(self.step3_error)
        self.step3_success = QLabel("")
        self.step3_success.setWordWrap(True)
        self.step3_success.setStyleSheet("color: #2e7d32; font-size: 13px;")
        self.step3_success.hide()
        v.addWidget(self.step3_success)
        self.btn_jd_summary = QPushButton("Get job description summary")
        self.btn_jd_summary.setCursor(Qt.PointingHandCursor)
        self.btn_jd_summary.setStyleSheet(
            "QPushButton { padding: 12px 20px; border-radius: 12px; background: #3949ab; color: white; "
            "font-weight: 600; font-size: 14px; border: none; }"
            "QPushButton:hover { background: #5c6bc0; }"
            "QPushButton:pressed { background: #283593; }"
            "QPushButton:disabled { background: #aaa; }"
        )
        self.btn_jd_summary.clicked.connect(self._on_jd_summary_clicked)
        v.addWidget(self.btn_jd_summary)
        self.btn_refresh_jd = QPushButton("Refresh from extension")
        self.btn_refresh_jd.setFlat(True)
        self.btn_refresh_jd.setCursor(Qt.PointingHandCursor)
        self.btn_refresh_jd.setStyleSheet("color: #3949ab; font-size: 12px; text-align: left;")
        self.btn_refresh_jd.clicked.connect(self._prepare_step3)
        v.addWidget(self.btn_refresh_jd)
        v.addStretch(1)
        return page

    def showEvent(self, event) -> None:  # noqa: N802
        super().showEvent(event)
        self._reload_step1()

    def _reload_step1(self) -> None:
        self._clear_layout(self.chips_layout)
        self.step1_error.hide()
        if self._bridge_unreachable():
            self.step1_error.setText("Python app not reachable. Start live.py first.")
            self.step1_error.show()
            return
        try:
            data = _http_get_json(self.bridge_base, "/registered-clients")
        except OSError:
            self.step1_error.setText("Python app not reachable. Start live.py first.")
            self.step1_error.show()
            return
        clients = data.get("clients") or []
        if not clients:
            self.step1_error.setText("No registered clients. Open the extension popup and click Register.")
            self.step1_error.show()
            return
        for c in clients:
            if not isinstance(c, dict):
                continue
            cid = str(c.get("client_id", "")).strip()
            if not cid:
                continue
            label = (c.get("email") or c.get("label") or cid[:8]).strip()
            btn = QPushButton(label)
            btn.setMinimumHeight(52)
            btn.setCursor(Qt.PointingHandCursor)
            btn.setStyleSheet(
                "QPushButton {"
                "  border-radius: 22px;"
                "  padding: 10px 22px;"
                "  background: #eef1ff;"
                "  color: #1a1d23;"
                "  border: 2px solid #c5cae9;"
                "  font-size: 15px;"
                "}"
                "QPushButton:hover { background: #dde3ff; border-color: #3949ab; }"
                "QPushButton:pressed { background: #c5cae9; }"
            )
            btn.clicked.connect(lambda _=False, i=cid, lbl=label: self._on_client_chosen(i, lbl))
            self.chips_layout.addWidget(btn)

    @staticmethod
    def _clear_layout(layout: QHBoxLayout | QVBoxLayout) -> None:
        while layout.count():
            item = layout.takeAt(0)
            w = item.widget()
            if w is not None:
                w.deleteLater()

    def _on_client_chosen(self, client_id: str, display: str) -> None:
        if self._bridge_unreachable():
            self.step1_error.setText("Python app not reachable. Start live.py first.")
            self.step1_error.show()
            return
        body, status = _http_post_json(self.bridge_base, "/selected-client", {"client_id": client_id})
        if status >= 400 or not body.get("ok"):
            self.step1_error.setText(body.get("error", "Could not select client."))
            self.step1_error.show()
            return
        self.selected_client_id = client_id
        self.display_email = display
        _, st2 = _http_post_json(self.bridge_base, "/sync-store-to-selected-client", {})
        if st2 >= 400:
            pass
        self._animate_to_step(1)

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

        self.step_badge.setText(f"{index + 1} / 3")
        if index == 0:
            self.title_label.setText("Select your account")
            self.sub_label.setText("Choose the Chrome profile you registered with the extension.")
        elif index == 1:
            self.title_label.setText("Resume summary")
            self.sub_label.setText("We ask ChatGPT (via your extension) to summarize your resume.")
            self._prepare_step2()
        else:
            self.title_label.setText("Job description summary")
            self.sub_label.setText("Same ChatGPT tab; we start a new chat for the JD.")
            self._prepare_step3()

    def _client_record(self) -> dict[str, Any] | None:
        try:
            data = _http_get_json(self.bridge_base, "/registered-clients")
        except OSError:
            return None
        for c in data.get("clients") or []:
            if isinstance(c, dict) and str(c.get("client_id", "")).strip() == self.selected_client_id:
                return c
        return None

    def _prepare_step2(self) -> None:
        self.step2_error.hide()
        self.step2_success.hide()
        self.btn_resume_summary.setEnabled(True)
        self.btn_resume_summary.setText("Get resume summary")
        rec = self._client_record()
        resume = ""
        if rec:
            resume = (rec.get("resume_text") or "").strip()
        if not resume:
            self.step2_error.setText(
                "No resume stored for this account. Open the extension, expand Resume, paste your resume, "
                "then return here and we will sync when you pick the account again — or paste in extension first, "
                "then re-open this app."
            )
            self.step2_error.show()
            self.btn_resume_summary.setEnabled(False)
            return

    def _prepare_step3(self) -> None:
        self.step3_error.hide()
        self.step3_success.hide()
        self.btn_jd_summary.setEnabled(True)
        self.btn_jd_summary.setText("Get job description summary")
        rec = self._client_record()
        jd = ""
        if rec:
            jd = (rec.get("jd_text") or "").strip()
        if not jd:
            self.step3_error.setText(
                "No job description for this account. Paste JD in the extension (JD panel), "
                "then restart the wizard or re-select your account after pasting."
            )
            self.step3_error.show()
            self.btn_jd_summary.setEnabled(False)
            return

    def _on_resume_summary_clicked(self) -> None:
        rec = self._client_record()
        resume = (rec.get("resume_text") or "").strip() if rec else ""
        if not resume:
            return
        prompt = _build_resume_summary_prompt(resume)
        body, status = _http_post_json(
            self.bridge_base,
            "/prep/start",
            {"phase": "resume_summary", "prompt": prompt, "open_new_tab": True},
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
        self.btn_resume_summary.setText("Working… open ChatGPT if prompted")
        self.step2_error.hide()
        self.step2_success.hide()
        self._prep_poll_timer.start()

    def _on_jd_summary_clicked(self) -> None:
        rec = self._client_record()
        jd = (rec.get("jd_text") or "").strip() if rec else ""
        if not jd:
            return
        prompt = _build_jd_summary_prompt(jd)
        body, status = _http_post_json(
            self.bridge_base,
            "/prep/start",
            {"phase": "jd_summary", "prompt": prompt, "open_new_tab": False},
        )
        if status >= 400 or not body.get("ok"):
            self.step3_error.setText("Could not start JD summary job.")
            self.step3_error.show()
            return
        job_id = str(body.get("job_id", "")).strip()
        if not job_id:
            return
        self._waiting_job_id = job_id
        self.btn_jd_summary.setEnabled(False)
        self.btn_jd_summary.setText("Working…")
        self.step3_error.hide()
        self.step3_success.hide()
        self._prep_poll_timer.start()

    def _on_prep_poll_tick(self) -> None:
        if not self._waiting_job_id:
            self._prep_poll_timer.stop()
            return
        try:
            job = _http_get_json(self.bridge_base, "/prep/job")
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
        if st == "done":
            if idx == 1:
                self.step2_success.setText("Resume summary saved. Continuing…")
                self.step2_success.show()
                _http_post_json(self.bridge_base, "/prep/clear", {})
                QTimer.singleShot(900, lambda: self._animate_to_step(2))
            elif idx == 2:
                self.step3_success.setText("Job description summary saved. Opening interview…")
                self.step3_success.show()
                _http_post_json(self.bridge_base, "/prep/clear", {})
                QTimer.singleShot(900, lambda: self.finished.emit(self.display_email, self.selected_client_id))
        else:
            err = str(job.get("error", "") or "Unknown error")
            if idx == 1:
                self.step2_error.setText(f"Summary failed: {err}")
                self.step2_error.show()
                self.btn_resume_summary.setEnabled(True)
                self.btn_resume_summary.setText("Get resume summary")
                _http_post_json(self.bridge_base, "/prep/clear", {})
            elif idx == 2:
                self.step3_error.setText(f"JD summary failed: {err}")
                self.step3_error.show()
                self.btn_jd_summary.setEnabled(True)
                self.btn_jd_summary.setText("Get job description summary")
                _http_post_json(self.bridge_base, "/prep/clear", {})
