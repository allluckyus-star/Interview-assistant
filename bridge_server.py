"""Local HTTP bridge for browser extension prompt pickup."""

from __future__ import annotations

import json
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Dict, List, Tuple
from urllib.parse import urlparse

REGISTERED_CLIENTS_PATH = Path(__file__).resolve().parent / "registered_clients.json"
_registry_lock = threading.Lock()


def _default_registry() -> Dict[str, Any]:
    return {"clients": [], "selected_client_id": None}


def _read_registry_unlocked() -> Dict[str, Any]:
    if not REGISTERED_CLIENTS_PATH.is_file():
        return _default_registry()
    try:
        with open(REGISTERED_CLIENTS_PATH, "r", encoding="utf-8") as handle:
            raw = json.load(handle)
    except (json.JSONDecodeError, OSError):
        return _default_registry()
    clients = raw.get("clients")
    if not isinstance(clients, list):
        clients = []
    sel = raw.get("selected_client_id")
    if sel in ("", None):
        sel = None
    else:
        sel = str(sel)
    return {"clients": clients, "selected_client_id": sel}


def _write_registry_unlocked(data: Dict[str, Any]) -> None:
    REGISTERED_CLIENTS_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = REGISTERED_CLIENTS_PATH.with_suffix(".json.tmp")
    encoded = json.dumps(data, indent=2)
    with open(tmp, "w", encoding="utf-8") as handle:
        handle.write(encoded)
    tmp.replace(REGISTERED_CLIENTS_PATH)


def load_registered_clients() -> Dict[str, Any]:
    with _registry_lock:
        data = _read_registry_unlocked()
        return {
            "clients": list(data["clients"]),
            "selected_client_id": data["selected_client_id"],
        }


def save_registered_clients(data: Dict[str, Any]) -> None:
    with _registry_lock:
        _write_registry_unlocked(
            {
                "clients": list(data.get("clients", [])),
                "selected_client_id": data.get("selected_client_id"),
            }
        )


def get_registered_clients() -> Dict[str, Any]:
    return load_registered_clients()


def get_selected_client_id() -> str | None:
    sel = get_registered_clients().get("selected_client_id")
    if sel in (None, ""):
        return None
    return str(sel).strip()


def register_client(payload: Dict[str, Any]) -> Tuple[Dict[str, Any], int]:
    client_id = str(payload.get("client_id", "")).strip()
    if not client_id:
        return {"ok": False, "error": "client_id required"}, 400

    email = str(payload.get("email", "")).strip()
    profile_id = str(payload.get("profile_id", "")).strip()
    label = str(payload.get("label", "")).strip()
    if not label:
        label = email or f"Chrome client {client_id[:6]}"

    now_ts = int(time.time())

    with _registry_lock:
        data = _read_registry_unlocked()
        clients: List[Dict[str, Any]] = list(data["clients"])
        idx: int | None = None
        for i, c in enumerate(clients):
            if isinstance(c, dict) and str(c.get("client_id", "")).strip() == client_id:
                idx = i
                break

        if idx is not None:
            existing = dict(clients[idx])
            existing["client_id"] = client_id
            existing["email"] = email
            existing["profile_id"] = profile_id
            existing["label"] = label
            existing["last_registered_at"] = now_ts
            existing.setdefault("registered_at", now_ts)
            _ensure_client_profile_fields(existing)
            clients[idx] = existing
            client_out = existing
        else:
            client_out = {
                "client_id": client_id,
                "email": email,
                "profile_id": profile_id,
                "label": label,
                "registered_at": now_ts,
                "last_registered_at": now_ts,
                "resume_text": "",
                "jd_text": "",
                "resume_summary": "",
                "jd_summary": "",
            }
            clients.append(client_out)

        selected = data.get("selected_client_id")
        if selected in (None, ""):
            selected = client_id
        data["clients"] = clients
        data["selected_client_id"] = selected
        _write_registry_unlocked(data)

    return {
        "ok": True,
        "client": client_out,
        "selected_client_id": selected,
    }, 200


def set_selected_client_id(client_id: str) -> Tuple[Dict[str, Any], int]:
    cid = str(client_id or "").strip()
    if not cid:
        return {"ok": False, "error": "client_id required"}, 400

    with _registry_lock:
        data = _read_registry_unlocked()
        clients = data.get("clients") or []
        if not any(isinstance(c, dict) and str(c.get("client_id", "")).strip() == cid for c in clients):
            return {"ok": False, "error": "unknown client_id"}, 404
        data["selected_client_id"] = cid
        _write_registry_unlocked(data)

    return {"ok": True, "selected_client_id": cid}, 200


def _ensure_client_profile_fields(client: Dict[str, Any]) -> None:
    if not isinstance(client, dict):
        return
    client.setdefault("resume_text", "")
    client.setdefault("jd_text", "")
    client.setdefault("resume_summary", "")
    client.setdefault("jd_summary", "")


def sync_resume_to_selected_client_in_registry(text: str) -> None:
    with _registry_lock:
        data = _read_registry_unlocked()
        sel = data.get("selected_client_id")
        if not sel:
            return
        for c in data["clients"]:
            if isinstance(c, dict) and str(c.get("client_id", "")).strip() == sel:
                _ensure_client_profile_fields(c)
                c["resume_text"] = text or ""
                break
        _write_registry_unlocked(data)


def sync_jd_to_selected_client_in_registry(text: str) -> None:
    with _registry_lock:
        data = _read_registry_unlocked()
        sel = data.get("selected_client_id")
        if not sel:
            return
        for c in data["clients"]:
            if isinstance(c, dict) and str(c.get("client_id", "")).strip() == sel:
                _ensure_client_profile_fields(c)
                c["jd_text"] = text or ""
                break
        _write_registry_unlocked(data)


def sync_store_into_selected_client_in_registry(store: "PromptStore") -> None:
    """Copy in-memory resume/JD from PromptStore into the selected client's registry row."""
    ctx = store.get_context()
    with _registry_lock:
        data = _read_registry_unlocked()
        sel = data.get("selected_client_id")
        if not sel:
            return
        for c in data["clients"]:
            if isinstance(c, dict) and str(c.get("client_id", "")).strip() == sel:
                _ensure_client_profile_fields(c)
                c["resume_text"] = str(ctx.get("resume", "") or "")
                c["jd_text"] = str(ctx.get("job_description", "") or "")
                break
        _write_registry_unlocked(data)


def apply_selected_client_context_to_prompt_store(store: "PromptStore") -> Tuple[str, str]:
    """Copy selected client's resume/JD (summaries preferred) into PromptStore."""
    data = get_registered_clients()
    sel = data.get("selected_client_id")
    display = ""
    rid = ""
    for c in data.get("clients") or []:
        if not isinstance(c, dict):
            continue
        if str(c.get("client_id", "")).strip() != str(sel or "").strip():
            continue
        rid = str(c.get("client_id", "")).strip()
        display = (c.get("email") or c.get("label") or "").strip() or (rid[:8] if rid else "")
        res = (c.get("resume_summary") or c.get("resume_text") or "").strip()
        jd = (c.get("jd_summary") or c.get("jd_text") or "").strip()
        store.set_resume_text(res)
        store.set_job_description_text(jd)
        break
    return display, rid


def _persist_prep_summary_to_selected_client(phase: str, result: str) -> None:
    if not result.strip():
        return
    with _registry_lock:
        data = _read_registry_unlocked()
        sel = data.get("selected_client_id")
        if not sel:
            return
        for c in data["clients"]:
            if isinstance(c, dict) and str(c.get("client_id", "")).strip() == sel:
                _ensure_client_profile_fields(c)
                if phase == "resume_summary":
                    c["resume_summary"] = result.strip()
                elif phase == "jd_summary":
                    c["jd_summary"] = result.strip()
                break
        _write_registry_unlocked(data)


class PromptStore:
    """Thread-safe in-memory prompt storage."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._resume_text = ""
        self._job_description_text = ""
        self._latest: Dict[str, object] = {
            "request_id": "",
            "created_at": 0,
            "prompt": "",
        }
        self._latest_answer: Dict[str, object] = {
            "request_id": "",
            "created_at": 0,
            "answer": "",
        }
        self._prep_job: Dict[str, Any] = {
            "job_id": "",
            "phase": "",
            "prompt": "",
            "open_new_tab": True,
            "status": "idle",
            "result": "",
            "error": "",
            "created_at": 0,
        }

    def set_prompt(self, prompt: str) -> Dict[str, object]:
        with self._lock:
            self._latest = {
                "request_id": str(uuid.uuid4()),
                "created_at": int(time.time()),
                "prompt": prompt,
            }
            return dict(self._latest)

    def set_prompt_with_id(self, request_id: str, prompt: str) -> Dict[str, object]:
        rid = str(request_id or "").strip() or str(uuid.uuid4())
        with self._lock:
            self._latest = {
                "request_id": rid,
                "created_at": int(time.time()),
                "prompt": prompt,
            }
            return dict(self._latest)

    def get_prompt(self) -> Dict[str, object]:
        with self._lock:
            return dict(self._latest)

    def set_answer(self, request_id: str, answer: str) -> Dict[str, object]:
        with self._lock:
            self._latest_answer = {
                "request_id": request_id,
                "created_at": int(time.time()),
                "answer": answer,
            }
            return dict(self._latest_answer)

    def get_answer(self) -> Dict[str, object]:
        with self._lock:
            return dict(self._latest_answer)

    def get_context(self) -> Dict[str, str]:
        with self._lock:
            return {
                "resume": self._resume_text,
                "job_description": self._job_description_text,
            }

    def set_resume_text(self, text: str) -> None:
        with self._lock:
            self._resume_text = text or ""

    def set_job_description_text(self, text: str) -> None:
        with self._lock:
            self._job_description_text = text or ""

    def get_prep_job(self) -> Dict[str, Any]:
        with self._lock:
            return dict(self._prep_job)

    def start_prep_job(self, phase: str, prompt: str, open_new_tab: bool) -> str:
        job_id = str(uuid.uuid4())
        with self._lock:
            self._prep_job = {
                "job_id": job_id,
                "phase": phase,
                "prompt": prompt,
                "open_new_tab": bool(open_new_tab),
                "status": "pending",
                "result": "",
                "error": "",
                "created_at": int(time.time()),
            }
        return job_id

    def complete_prep_job(self, job_id: str, result: str, error: str) -> Tuple[Dict[str, Any], int]:
        phase = ""
        err = ""
        res = ""
        final_status = "idle"
        with self._lock:
            if str(self._prep_job.get("job_id", "")) != str(job_id):
                return {"ok": False, "error": "invalid job_id"}, 400
            phase = str(self._prep_job.get("phase", ""))
            err = (error or "").strip()
            res = (result or "").strip()
            if err:
                self._prep_job["status"] = "error"
                self._prep_job["error"] = err
                self._prep_job["result"] = ""
                final_status = "error"
            else:
                self._prep_job["status"] = "done"
                self._prep_job["error"] = ""
                self._prep_job["result"] = res
                final_status = "done"
        if not err and res and phase in ("resume_summary", "jd_summary"):
            _persist_prep_summary_to_selected_client(phase, res)
        return {"ok": True, "phase": phase, "status": final_status}, 200

    def clear_prep_job(self) -> None:
        with self._lock:
            self._prep_job = {
                "job_id": "",
                "phase": "",
                "prompt": "",
                "open_new_tab": True,
                "status": "idle",
                "result": "",
                "error": "",
                "created_at": 0,
            }


class PromptBridgeServer:
    """HTTP server exposing prompt + answer endpoints for extension."""

    def __init__(self, store: PromptStore, host: str = "127.0.0.1", port: int = 8765) -> None:
        self.store = store
        self.host = host
        self.port = port
        self.httpd: ThreadingHTTPServer | None = None
        self.thread: threading.Thread | None = None

    def start(self) -> None:
        if self.httpd:
            return
        handler = self._build_handler(self.store)
        self.httpd = ThreadingHTTPServer((self.host, self.port), handler)
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()

    def stop(self) -> None:
        if self.httpd:
            self.httpd.shutdown()
            self.httpd.server_close()
            self.httpd = None

    @staticmethod
    def _build_handler(store: PromptStore):
        class Handler(BaseHTTPRequestHandler):
            def _send_json(self, payload: Dict[str, object], status: int = 200) -> None:
                encoded = json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Content-Length", str(len(encoded)))
                self.end_headers()
                self.wfile.write(encoded)

            def do_GET(self) -> None:  # noqa: N802
                parsed = urlparse(self.path)
                if parsed.path == "/next-prompt":
                    self._send_json(store.get_prompt(), 200)
                    return
                if parsed.path == "/latest-answer":
                    self._send_json(store.get_answer(), 200)
                    return
                if parsed.path == "/context":
                    ctx = store.get_context()
                    self._send_json({"resume": ctx["resume"], "job_description": ctx["job_description"]}, 200)
                    return
                if parsed.path == "/registered-clients":
                    self._send_json(get_registered_clients(), 200)
                    return
                if parsed.path == "/prep/job":
                    self._send_json(store.get_prep_job(), 200)
                    return
                self._send_json({"error": "not found"}, 404)

            def do_POST(self) -> None:  # noqa: N802
                parsed = urlparse(self.path)
                if parsed.path == "/ack":
                    self._send_json({"status": "ok"}, 200)
                    return
                if parsed.path == "/answer":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        payload = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return

                    request_id = str(payload.get("request_id", "")).strip()
                    answer = str(payload.get("answer", "")).strip()
                    if not request_id or not answer:
                        self._send_json({"error": "request_id and answer required"}, 400)
                        return

                    stored = store.set_answer(request_id=request_id, answer=answer)
                    self._send_json({"status": "ok", "saved": stored}, 200)
                    return
                if parsed.path == "/context/resume":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        payload = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    text = str(payload.get("text", ""))
                    store.set_resume_text(text)
                    sync_resume_to_selected_client_in_registry(text)
                    self._send_json({"status": "ok"}, 200)
                    return
                if parsed.path == "/context/jd":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        payload = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    text = str(payload.get("text", ""))
                    store.set_job_description_text(text)
                    sync_jd_to_selected_client_in_registry(text)
                    self._send_json({"status": "ok"}, 200)
                    return
                if parsed.path == "/sync-store-to-selected-client":
                    sync_store_into_selected_client_in_registry(store)
                    self._send_json({"status": "ok"}, 200)
                    return
                if parsed.path == "/prep/start":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        payload = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    pl = payload if isinstance(payload, dict) else {}
                    phase = str(pl.get("phase", "")).strip()
                    prompt = str(pl.get("prompt", "")).strip()
                    if phase not in ("resume_summary", "jd_summary") or not prompt:
                        self._send_json({"error": "phase and prompt required"}, 400)
                        return
                    open_new = bool(pl.get("open_new_tab", True))
                    job_id = store.start_prep_job(phase, prompt, open_new)
                    self._send_json({"ok": True, "job_id": job_id}, 200)
                    return
                if parsed.path == "/prep/complete":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        payload = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    pl = payload if isinstance(payload, dict) else {}
                    body, status = store.complete_prep_job(
                        str(pl.get("job_id", "")),
                        str(pl.get("result", "")),
                        str(pl.get("error", "")),
                    )
                    self._send_json(body, status)
                    return
                if parsed.path == "/prep/clear":
                    store.clear_prep_job()
                    self._send_json({"ok": True}, 200)
                    return
                if parsed.path == "/register-client":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        payload = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    body, status = register_client(payload if isinstance(payload, dict) else {})
                    self._send_json(body, status)
                    return
                if parsed.path == "/selected-client":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        payload = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    pl = payload if isinstance(payload, dict) else {}
                    body, status = set_selected_client_id(str(pl.get("client_id", "")))
                    self._send_json(body, status)
                    return
                self._send_json({"error": "not found"}, 404)

            def log_message(self, format: str, *args) -> None:  # noqa: A003
                return

        return Handler
