"""Local HTTP bridge for browser extension prompt pickup."""

from __future__ import annotations

import json
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Dict, List, Tuple
from urllib.parse import parse_qs, urlparse

REGISTERED_CLIENTS_PATH = Path(__file__).resolve().parent / "registered_clients.json"
_registry_lock = threading.Lock()
# Raw resume / full JD live only in RAM until the extension POSTs again (not persisted to disk).
_extension_docs_lock = threading.Lock()
_extension_docs: Dict[str, Dict[str, str]] = {}
_prep_poll_status_last_ts: Dict[str, float] = {}
_interview_live_lock = threading.Lock()
# Latest streaming assistant text per interview request_id (HTTP relay when WebSocket is down).
_interview_live_text: Dict[str, str] = {}

# Per bridge process: extension "Registered" UI is session-scoped (JSON may still list clients from last run).
_bridge_session_id = ""
_session_registered_client_ids: set[str] = set()
_session_state_lock = threading.Lock()


def reset_bridge_session() -> None:
    """Call when live.py starts the HTTP bridge so extensions show Please register until Register is clicked again."""
    global _bridge_session_id
    with _session_state_lock:
        _bridge_session_id = str(uuid.uuid4())
        _session_registered_client_ids.clear()


def note_session_registration(client_id: str) -> None:
    cid = str(client_id or "").strip()
    if not cid:
        return
    with _session_state_lock:
        _session_registered_client_ids.add(cid)


def bridge_session_api_fields() -> Dict[str, Any]:
    with _session_state_lock:
        return {
            "bridge_session_id": _bridge_session_id,
            "session_registered_client_ids": sorted(_session_registered_client_ids),
        }


def _set_interview_live_text(request_id: str, text: str) -> None:
    rid = str(request_id or "").strip()
    if not rid:
        return
    with _interview_live_lock:
        _interview_live_text[rid] = str(text or "")


def _get_interview_live_text(request_id: str) -> str:
    rid = str(request_id or "").strip()
    if not rid:
        return ""
    with _interview_live_lock:
        return str(_interview_live_text.get(rid, "") or "")


def _clear_interview_live_text(request_id: str) -> None:
    rid = str(request_id or "").strip()
    if not rid:
        return
    with _interview_live_lock:
        _interview_live_text.pop(rid, None)

def _print_prep_terminal_block(kind: str, phase: str, job_id: str, text: str) -> None:
    """Echo prep prompt/result to the bridge terminal: first 20 chars + total length only."""
    raw = (text or "").replace("\r\n", "\n")
    total = len(raw)
    head = raw[:20].replace("\n", "\\n").replace("\r", "\\r")
    # print(
    #     f"[prep:{kind}] phase={phase or '?'} job={job_id or '?'} "
    #     f"total_chars={total} chars[0:20]={head!r}",
    #     flush=True,
    # )


def _derive_gpt_ui_stage(pl: Dict[str, Any]) -> str:
    """Human-readable ChatGPT UI phase from extension DOM snapshot."""
    if pl.get("stop") is True:
        return "STREAMING"
    if pl.get("composer_idle") is True:
        return "IDLE_AFTER_TURN"
    if pl.get("send_present") and not pl.get("send_disabled"):
        return "READY_TO_SEND"
    try:
        ac = int(pl.get("assistant_chars") or 0)
    except (TypeError, ValueError):
        ac = 0
    if ac > 30:
        return "HAS_ASSISTANT_TEXT"
    return "WAITING_OR_LOADING"


def _print_prep_ext_line(pl: Dict[str, Any]) -> None:
    """Extension background (or content) — visible in live.py terminal."""
    ev = str(pl.get("event", "") or "?").replace("\n", " ")[:56]
    job = str(pl.get("job_id", "") or pl.get("jobId", "") or "").strip()[:40]
    parts = [f"event={ev!r}", f"job_id={job!r}"]
    for key in ("tab_id", "attempt", "open_new_tab", "tab_complete", "source", "detail", "error"):
        if key not in pl or pl.get(key) in (None, ""):
            continue
        v = pl.get(key)
        if isinstance(v, str) and len(v) > 72:
            v = v[:72] + "…"
        parts.append(f"{key}={v!r}")
    # print("[prep:ext] " + " ".join(parts), flush=True)


TEMPLATE_KEYS = ("resume_summary", "jd_summary", "initial_interview", "chunk_interview")

from app_prompt_files import load_prompt_templates_into_store, save_template_text


def _empty_extension_doc_row() -> Dict[str, str]:
    row = {"resume": "", "jd": ""}
    for k in TEMPLATE_KEYS:
        row[f"tpl_{k}"] = ""
    return row


def _extension_docs_row(client_id: str) -> Dict[str, str]:
    cid = str(client_id or "").strip()
    empty = _empty_extension_doc_row()
    if not cid:
        return empty
    with _extension_docs_lock:
        row = _extension_docs.get(cid)
        if not row:
            return empty
        out = dict(empty)
        for key in out.keys():
            out[key] = str(row.get(key) or "")
        return out


def sync_resume_to_client_in_registry(client_id: str, text: str) -> None:
    """Store full resume for this Chrome client in RAM only (extension is the durable copy)."""
    cid = str(client_id or "").strip()
    if not cid:
        return
    with _extension_docs_lock:
        slot = _extension_docs.setdefault(cid, _empty_extension_doc_row())
        slot["resume"] = text or ""


def sync_jd_to_client_in_registry(client_id: str, text: str) -> None:
    """Store full JD for this Chrome client in RAM only (extension is the durable copy)."""
    cid = str(client_id or "").strip()
    if not cid:
        return
    with _extension_docs_lock:
        slot = _extension_docs.setdefault(cid, _empty_extension_doc_row())
        slot["jd"] = text or ""


def sync_template_to_client_in_registry(client_id: str, template_key: str, text: str) -> bool:
    """Store a per-client prompt template body (RAM only). Returns False on unknown key."""
    cid = str(client_id or "").strip()
    key = str(template_key or "").strip().lower()
    if not cid or key not in TEMPLATE_KEYS:
        return False
    with _extension_docs_lock:
        slot = _extension_docs.setdefault(cid, _empty_extension_doc_row())
        slot[f"tpl_{key}"] = text or ""
    return True


def get_templates_for_client_id(client_id: str) -> Dict[str, str]:
    """Return {key: text} for all known template keys (empty strings when missing)."""
    docs = _extension_docs_row(client_id)
    return {k: str(docs.get(f"tpl_{k}") or "") for k in TEMPLATE_KEYS}


# In-memory only: lets the prep wizard show a one-line message (e.g. account name)
# right after extension POST /register-client, without persisting that string to disk.
_register_event_lock = threading.Lock()
_register_event_seq = 0
_register_event_message = ""
_register_event_client_id = ""


def _default_registry() -> Dict[str, Any]:
    return {"clients": [], "selected_client_id": None}


def _strip_pii_from_client_row(client: Dict[str, Any]) -> Dict[str, Any]:
    """Do not persist or return email / profile identifiers from the registry."""
    if not isinstance(client, dict):
        return {}
    out = dict(client)
    out.pop("email", None)
    out.pop("profile_id", None)
    return out


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
    cleaned: List[Dict[str, Any]] = []
    for c in clients:
        if isinstance(c, dict):
            row = _strip_pii_from_client_row(c)
            row.pop("resume_text", None)
            row.pop("jd_text", None)
            cleaned.append(row)
    sel = raw.get("selected_client_id")
    if sel in ("", None):
        sel = None
    else:
        sel = str(sel)
    return {"clients": cleaned, "selected_client_id": sel}


def _registry_clients_for_disk(data: Dict[str, Any]) -> Dict[str, Any]:
    """Never persist full resume/JD bodies to registered_clients.json (extension holds those)."""
    out_clients: List[Dict[str, Any]] = []
    for c in data.get("clients") or []:
        if not isinstance(c, dict):
            continue
        row = dict(c)
        row.pop("resume_text", None)
        row.pop("jd_text", None)
        out_clients.append(row)
    return {"clients": out_clients, "selected_client_id": data.get("selected_client_id")}


def _write_registry_unlocked(data: Dict[str, Any]) -> None:
    REGISTERED_CLIENTS_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = REGISTERED_CLIENTS_PATH.with_suffix(".json.tmp")
    encoded = json.dumps(_registry_clients_for_disk(data), indent=2)
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


def _snapshot_register_event_unlocked() -> Dict[str, Any]:
    return {
        "seq": int(_register_event_seq),
        "message": str(_register_event_message or ""),
        "client_id": str(_register_event_client_id or ""),
    }


def bump_register_event(client_id: str, message: str) -> int:
    """Notify listeners (prep wizard) of a fresh extension registration."""
    global _register_event_seq, _register_event_message, _register_event_client_id
    with _register_event_lock:
        _register_event_seq += 1
        _register_event_message = message.strip()
        _register_event_client_id = client_id.strip()
        return int(_register_event_seq)


def get_register_event_snapshot() -> Dict[str, Any]:
    with _register_event_lock:
        return _snapshot_register_event_unlocked()


def get_registered_clients_api_payload(store: "PromptStore") -> Dict[str, Any]:
    """Registry for HTTP GET: sanitized rows + latest register event (in-memory).

    Resume and JD text are merged from extension-backed RAM; prompt templates come from
    the desktop app (PromptStore / root .txt files), not the extension.
    """
    data = load_registered_clients()
    merged_clients: List[Dict[str, Any]] = []
    for c in data.get("clients") or []:
        if not isinstance(c, dict):
            continue
        cid = str(c.get("client_id", "")).strip()
        row = dict(c)
        docs = _extension_docs_row(cid)
        row["resume_text"] = docs["resume"]
        row["jd_text"] = docs["jd"]
        for key in TEMPLATE_KEYS:
            row[f"tpl_{key}"] = store.get_template(key)
        merged_clients.append(row)
    out = {"clients": merged_clients, "selected_client_id": data.get("selected_client_id")}
    with _register_event_lock:
        ev = _snapshot_register_event_unlocked()
    return {**out, "register_event": ev, **bridge_session_api_fields()}


def wait_for_register_event_after_seq(
    since_seq: int, timeout_sec: float = 120.0, poll_interval: float = 0.25
) -> Tuple[Dict[str, Any], bool]:
    """Block until register_event seq increases or timeout.

    Returns (register_event_snapshot, timed_out) where timed_out is True only if
    the deadline elapsed without seq passing since_seq.
    """
    deadline = time.time() + timeout_sec
    since_seq = int(since_seq)
    while time.time() < deadline:
        with _register_event_lock:
            if int(_register_event_seq) > since_seq:
                return _snapshot_register_event_unlocked(), False
        time.sleep(poll_interval)
    with _register_event_lock:
        ev = _snapshot_register_event_unlocked()
    return ev, int(ev["seq"]) <= since_seq


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


def get_context_for_client_id(client_id: str, store: "PromptStore") -> Dict[str, Any]:
    """Resume/JD for this client from extension RAM; templates from the main app (store / .txt)."""
    cid = str(client_id or "").strip()
    docs = _extension_docs_row(cid)
    templates = {k: str(store.get_template(k) or "") for k in TEMPLATE_KEYS}
    return {
        "resume": docs["resume"],
        "job_description": docs["jd"],
        "templates": templates,
    }


def register_client(payload: Dict[str, Any]) -> Tuple[Dict[str, Any], int]:
    client_id = str(payload.get("client_id", "")).strip()
    if not client_id:
        return {"ok": False, "error": "client_id required"}, 400

    email = str(payload.get("email", "")).strip()
    profile_id = str(payload.get("profile_id", "")).strip()
    label = str(payload.get("label", "")).strip()
    if not label:
        label = email or f"Chrome client {client_id[:6]}"

    # Shown once in the wizard via register_event; not written to registered_clients.json
    display_name = (label or email or "").strip() or f"Chrome client {client_id[:6]}"
    flash_message = f"[{display_name}] is registered."

    # Persist only a non-identifying label (no Gmail / profile id on disk).
    persist_label = f"Chrome client {client_id[:6]}"

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
            existing = _strip_pii_from_client_row(dict(clients[idx]))
            existing["client_id"] = client_id
            existing["label"] = persist_label
            existing["last_registered_at"] = now_ts
            existing.setdefault("registered_at", now_ts)
            _ensure_client_profile_fields(existing)
            clients[idx] = existing
            client_disk = existing
        else:
            client_disk = {
                "client_id": client_id,
                "label": persist_label,
                "registered_at": now_ts,
                "last_registered_at": now_ts,
                "resume_summary": "",
                "jd_summary": "",
                "interview_gpt_brief": "",
            }
            clients.append(client_disk)

        selected = data.get("selected_client_id")
        if selected in (None, ""):
            selected = client_id
        data["clients"] = clients
        data["selected_client_id"] = selected
        _write_registry_unlocked(data)

    bump_register_event(client_id, flash_message)
    note_session_registration(client_id)

    # Extension may still use email/label in its own UI; omit from persisted registry above.
    client_response = {
        "client_id": client_id,
        "label": label,
        "email": email,
        "profile_id": profile_id,
    }

    return {
        "ok": True,
        "client": client_response,
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
    client.setdefault("resume_summary", "")
    client.setdefault("jd_summary", "")
    client.setdefault("interview_gpt_brief", "")


def sync_resume_to_selected_client_in_registry(text: str) -> None:
    sel = get_selected_client_id()
    if not sel:
        return
    sync_resume_to_client_in_registry(str(sel), text)


def sync_jd_to_selected_client_in_registry(text: str) -> None:
    sel = get_selected_client_id()
    if not sel:
        return
    sync_jd_to_client_in_registry(str(sel), text)


def sync_store_into_selected_client_in_registry(store: "PromptStore") -> None:
    """Copy PromptStore resume/JD into extension-backed RAM for the selected client."""
    ctx = store.get_context()
    r = str(ctx.get("resume", "") or "").strip()
    jd = str(ctx.get("job_description", "") or "").strip()
    sel = get_selected_client_id()
    if not sel:
        return
    if r:
        sync_resume_to_client_in_registry(str(sel), r)
    if jd:
        sync_jd_to_client_in_registry(str(sel), jd)


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
        display = (c.get("label") or "").strip() or (rid[:8] if rid else "")
        docs = _extension_docs_row(rid)
        res = (c.get("resume_summary") or docs["resume"] or "").strip()
        jd = (c.get("jd_summary") or docs["jd"] or "").strip()
        store.set_resume_text(res)
        store.set_job_description_text(jd)
        load_prompt_templates_into_store(store)
        break
    return display, rid


def _persist_prep_summary_to_selected_client(phase: str, result: str, client_id: str = "") -> None:
    if not result.strip():
        return
    target = str(client_id or "").strip()
    with _registry_lock:
        data = _read_registry_unlocked()
        sel = str(data.get("selected_client_id") or "").strip()
        match_id = target or sel
        if not match_id:
            return
        for c in data["clients"]:
            if isinstance(c, dict) and str(c.get("client_id", "")).strip() == match_id:
                _ensure_client_profile_fields(c)
                if phase == "resume_summary":
                    c["resume_summary"] = result.strip()
                elif phase == "jd_summary":
                    c["jd_summary"] = result.strip()
                elif phase == "interview_gpt_setup":
                    c["interview_gpt_brief"] = result.strip()
                break
        _write_registry_unlocked(data)


class PromptStore:
    """Thread-safe in-memory prompt storage."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._resume_text = ""
        self._job_description_text = ""
        self._templates: Dict[str, str] = {k: "" for k in TEMPLATE_KEYS}
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
            "client_id": "",
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

    def get_context(self) -> Dict[str, Any]:
        with self._lock:
            return {
                "resume": self._resume_text,
                "job_description": self._job_description_text,
                "templates": dict(self._templates),
            }

    def set_resume_text(self, text: str) -> None:
        with self._lock:
            self._resume_text = text or ""

    def set_job_description_text(self, text: str) -> None:
        with self._lock:
            self._job_description_text = text or ""

    def set_template(self, key: str, text: str) -> bool:
        if key not in TEMPLATE_KEYS:
            return False
        with self._lock:
            self._templates[key] = text or ""
        return True

    def get_template(self, key: str) -> str:
        if key not in TEMPLATE_KEYS:
            return ""
        with self._lock:
            return str(self._templates.get(key, "") or "")

    @staticmethod
    def _prep_job_idle() -> Dict[str, Any]:
        return {
            "job_id": "",
            "phase": "",
            "prompt": "",
            "open_new_tab": True,
            "status": "idle",
            "result": "",
            "error": "",
            "created_at": 0,
            "client_id": "",
        }

    def get_prep_job(self, viewer_client_id: str | None = None) -> Dict[str, Any]:
        """Return prep job for polling. Scoped pending jobs are visible only to the matching Chrome client_id."""
        viewer = str(viewer_client_id or "").strip()
        with self._lock:
            j = dict(self._prep_job)
        if str(j.get("status", "")) != "pending":
            return j
        job_cid = str(j.get("client_id", "")).strip()
        if not job_cid:
            return j
        if viewer and viewer == job_cid:
            return j
        return self._prep_job_idle()

    def start_prep_job(self, phase: str, prompt: str, open_new_tab: bool, client_id: str = "") -> str:
        job_id = str(uuid.uuid4())
        cid = str(client_id or "").strip()
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
                "client_id": cid,
            }
        return job_id

    def complete_prep_job(
        self, job_id: str, result: str, error: str, requesting_client_id: str | None = None
    ) -> Tuple[Dict[str, Any], int]:
        phase = ""
        err = ""
        res = ""
        final_status = "idle"
        with self._lock:
            if str(self._prep_job.get("job_id", "")) != str(job_id):
                return {"ok": False, "error": "invalid job_id"}, 400
            job_cid = str(self._prep_job.get("client_id", "")).strip()
            req_cid = str(requesting_client_id or "").strip()
            if job_cid and (not req_cid or req_cid != job_cid):
                return {"ok": False, "error": "client_id mismatch"}, 403
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
        if not err and res and phase in ("resume_summary", "jd_summary", "interview_gpt_setup"):
            _persist_prep_summary_to_selected_client(phase, res, str(requesting_client_id or "").strip())
        return {"ok": True, "phase": phase, "status": final_status}, 200

    def clear_prep_job(self) -> None:
        with self._lock:
            self._prep_job = self._prep_job_idle()


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
        reset_bridge_session()
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
                if parsed.path == "/interview-live":
                    qs = parse_qs(parsed.query or "")
                    rid_list = qs.get("request_id") or []
                    rid = str(rid_list[0]).strip() if rid_list else ""
                    text = _get_interview_live_text(rid) if rid else ""
                    self._send_json({"request_id": rid, "text": text}, 200)
                    return
                if parsed.path == "/context":
                    qs = parse_qs(parsed.query or "")
                    cid_list = qs.get("client_id") or []
                    cid = str(cid_list[0]).strip() if cid_list else ""
                    if cid:
                        ctx = get_context_for_client_id(cid, store)
                        self._send_json(
                            {
                                "resume": ctx["resume"],
                                "job_description": ctx["job_description"],
                                "templates": ctx.get("templates") or {},
                            },
                            200,
                        )
                        return
                    ctx = store.get_context()
                    self._send_json(
                        {
                            "resume": ctx["resume"],
                            "job_description": ctx["job_description"],
                            "templates": ctx.get("templates") or {},
                        },
                        200,
                    )
                    return
                if parsed.path == "/registered-clients":
                    self._send_json(get_registered_clients_api_payload(store), 200)
                    return
                if parsed.path == "/wait-register-event":
                    qs = parse_qs(parsed.query or "")
                    try:
                        since_seq = int((qs.get("since_seq") or ["0"])[0])
                    except ValueError:
                        since_seq = 0
                    ev, timed_out = wait_for_register_event_after_seq(since_seq, timeout_sec=120.0)
                    self._send_json(
                        {"ok": True, "timed_out": timed_out, "register_event": ev},
                        200,
                    )
                    return
                if parsed.path == "/prep/job":
                    qs_job = parse_qs(parsed.query or "")
                    viewer_cid = str((qs_job.get("client_id") or [""])[0]).strip()
                    job = store.get_prep_job(viewer_cid if viewer_cid else None)
                    if isinstance(job, dict) and str(job.get("status", "")) == "pending" and job.get("job_id"):
                        jid = str(job["job_id"])
                        now = time.time()
                        last = _prep_poll_status_last_ts.get(jid, 0.0)
                        if now - last >= 2.0:
                            _prep_poll_status_last_ts[jid] = now
                            
                    self._send_json(job, 200)
                    return
                self._send_json({"error": "not found"}, 404)

            def do_POST(self) -> None:  # noqa: N802
                parsed = urlparse(self.path)
                if parsed.path == "/ack":
                    self._send_json({"status": "ok"}, 200)
                    return
                if parsed.path == "/prep/extension-status":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        pl = json.loads(raw)
                    except json.JSONDecodeError:
                        # print("[prep:ext] event='bad_json' detail='POST body not JSON'", flush=True)
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    if not isinstance(pl, dict):
                        pl = {}
                    _print_prep_ext_line(pl)
                    self._send_json({"ok": True}, 200)
                    return
                if parsed.path == "/interview-live":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        pl = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    if not isinstance(pl, dict):
                        pl = {}
                    rid = str(pl.get("request_id", "")).strip()
                    if not rid:
                        self._send_json({"error": "request_id required"}, 400)
                        return
                    if pl.get("clear") is True:
                        _clear_interview_live_text(rid)
                        self._send_json({"ok": True}, 200)
                        return
                    _set_interview_live_text(rid, str(pl.get("text", "")))
                    self._send_json({"ok": True}, 200)
                    return
                if parsed.path == "/gpt-state":
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        pl = json.loads(raw)
                    except json.JSONDecodeError:
                        # print("[gpt-ui] reason='bad_json'", flush=True)
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    if not isinstance(pl, dict):
                        pl = {}
                    reason = str(pl.get("reason", "") or "tick").replace("\n", " ")[:48]
                    parts = [f"reason={reason!r}"]
                    for key in (
                        "phase",
                        "job_id",
                        "ui_stage",
                        "stop",
                        "composer_idle",
                        "send_present",
                        "send_disabled",
                        "start_voice",
                        "has_surface",
                        "assistant_chars",
                        "stable_ms",
                        "preview",
                    ):
                        if key not in pl:
                            continue
                        val = pl.get(key)
                        if isinstance(val, str) and len(val) > 60:
                            val = val[:60] + "…"
                        parts.append(f"{key}={val!r}")
                    stage = str(pl.get("ui_stage") or "").strip() or _derive_gpt_ui_stage(pl)
                    # print("[gpt-ui] " + " ".join(parts), flush=True)
                    # print(f"[gpt-ui] stage={stage!r} (derived if missing)", flush=True)
                    self._send_json({"ok": True}, 200)
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
                    _clear_interview_live_text(request_id)
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
                    pl = payload if isinstance(payload, dict) else {}
                    text = str(pl.get("text", ""))
                    post_cid = str(pl.get("client_id", "")).strip()
                    target = post_cid or (get_selected_client_id() or "")
                    if target:
                        sync_resume_to_client_in_registry(target, text)
                        sel = get_selected_client_id()
                        if sel and str(sel).strip() == str(target).strip():
                            store.set_resume_text(text)
                    else:
                        store.set_resume_text(text)
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
                    pl = payload if isinstance(payload, dict) else {}
                    text = str(pl.get("text", ""))
                    post_cid = str(pl.get("client_id", "")).strip()
                    target = post_cid or (get_selected_client_id() or "")
                    if target:
                        sync_jd_to_client_in_registry(target, text)
                        sel = get_selected_client_id()
                        if sel and str(sel).strip() == str(target).strip():
                            store.set_job_description_text(text)
                    else:
                        store.set_job_description_text(text)
                    self._send_json({"status": "ok"}, 200)
                    return
                if parsed.path.startswith("/context/template/"):
                    template_key = parsed.path.rsplit("/", 1)[-1].strip().lower()
                    if template_key not in TEMPLATE_KEYS:
                        self._send_json({"error": "unknown template key"}, 404)
                        return
                    content_length = int(self.headers.get("Content-Length", "0"))
                    raw = self.rfile.read(content_length).decode("utf-8") if content_length > 0 else "{}"
                    try:
                        payload = json.loads(raw)
                    except json.JSONDecodeError:
                        self._send_json({"error": "invalid json"}, 400)
                        return
                    pl = payload if isinstance(payload, dict) else {}
                    text = str(pl.get("text", ""))
                    post_cid = str(pl.get("client_id", "")).strip()
                    target = post_cid or (get_selected_client_id() or "")
                    store.set_template(template_key, text)
                    save_template_text(template_key, text)
                    if target:
                        sync_template_to_client_in_registry(target, template_key, text)
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
                    if phase not in ("resume_summary", "jd_summary", "interview_gpt_setup") or not prompt:
                        self._send_json({"error": "phase and prompt required"}, 400)
                        return
                    open_new = bool(pl.get("open_new_tab", True))
                    job_client = str(pl.get("client_id", "")).strip() or str(get_selected_client_id() or "").strip()
                    if not job_client:
                        self._send_json(
                            {
                                "error": "client_id required",
                                "detail": "Register the extension (step 1) so the bridge knows which Chrome profile should run prep.",
                            },
                            400,
                        )
                        return
                    job_id = store.start_prep_job(phase, prompt, open_new, job_client)
                    _print_prep_terminal_block("PROMPT", phase, job_id, prompt)
                    _print_prep_ext_line(
                        {
                            "event": "prep_job_queued",
                            "job_id": job_id,
                            "client_id": job_client[:24],
                            "detail": f"phase={phase} open_new_tab={open_new} client_id={job_client[:12]}… (waiting for extension)",
                        }
                    )
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
                    jid = str(pl.get("job_id", "")).strip()
                    res_in = str(pl.get("result", ""))
                    err_in = str(pl.get("error", ""))
                    ext_cid = str(pl.get("client_id", "")).strip()
                    # print(
                    #     f"[prep:incoming_complete] job={jid!r} err_chars={len(err_in)} res_chars={len(res_in)}",
                    #     flush=True,
                    # )
                    body, status = store.complete_prep_job(jid, res_in, err_in, ext_cid)
                    phase_done = str(body.get("phase", "")) if isinstance(body, dict) else ""
                    if not isinstance(body, dict) or not body.get("ok") or status >= 400:
                        detail = json.dumps(body) if isinstance(body, dict) else repr(body)
                        _print_prep_terminal_block("COMPLETE_REJECTED", phase_done or "?", jid, detail[:500])
                    elif err_in.strip():
                        _print_prep_terminal_block("EXTENSION_ERROR", phase_done, jid, err_in.strip())
                    elif res_in.strip():
                        _print_prep_terminal_block("RESULT", phase_done, jid, res_in.strip())
                    else:
                        # print(
                        #     f"[prep:complete_ok_empty] phase={phase_done or '?'} job={jid!r} (no err, no result body)",
                        #     flush=True,
                        # )
                        pass
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
