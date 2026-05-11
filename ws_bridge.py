"""WebSocket hub for live interview traffic (separate port from HTTP bridge)."""

from __future__ import annotations

import asyncio
import json
import queue
import threading
from typing import Any, Dict, Optional

# stdlib HTTP server already uses 8765; WebSocket listens here.
WS_HOST = "127.0.0.1"
WS_PORT = 8766


class InterviewWSServer:
    """Tracks one socket per client_id; forwards extension messages into ui_queue."""

    def __init__(self, ui_queue: "queue.Queue[Any]") -> None:
        self._ui_queue = ui_queue
        self._connections: Dict[str, Any] = {}
        self._conn_lock = threading.Lock()
        self._loop: Optional[asyncio.AbstractEventLoop] = None
        self._thread: Optional[threading.Thread] = None
        self._started = threading.Event()

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return

        def runner() -> None:
            self._loop = asyncio.new_event_loop()
            asyncio.set_event_loop(self._loop)
            self._started.set()
            self._loop.run_until_complete(self._serve_loop())

        self._thread = threading.Thread(target=runner, daemon=True)
        self._thread.start()
        self._started.wait(timeout=8.0)

    async def _serve_loop(self) -> None:
        import websockets

        async def handler(websocket: Any, path: str) -> None:
            await self._handle_connection(websocket, path)

        async with websockets.serve(handler, WS_HOST, WS_PORT):
            await asyncio.Future()

    def stop(self) -> None:
        """Request the asyncio loop to stop (frees WS_PORT; thread may exit uncleanly)."""
        loop = self._loop
        if loop is None:
            return

        def _stop() -> None:
            loop.stop()

        try:
            loop.call_soon_threadsafe(_stop)
        except RuntimeError:
            pass

    async def _handle_connection(self, websocket: Any, path: str) -> None:
        from urllib.parse import parse_qs, urlparse

        parsed = urlparse(path)
        if parsed.path != "/ws":
            await websocket.close(code=1008, reason="invalid path")
            return
        client_id = (parse_qs(parsed.query).get("client_id") or [""])[0].strip()
        if not client_id:
            await websocket.close(code=1008, reason="client_id required")
            return

        with self._conn_lock:
            old = self._connections.get(client_id)
            if old is not None and old is not websocket:
                try:
                    await old.close(code=1000, reason="replaced")
                except Exception:
                    pass
            self._connections[client_id] = websocket

        try:
            async for message in websocket:
                try:
                    data = json.loads(message)
                except json.JSONDecodeError:
                    continue
                if isinstance(data, dict):
                    self._ui_queue.put({"type": "ws_ext", "payload": data})
        finally:
            with self._conn_lock:
                if self._connections.get(client_id) is websocket:
                    del self._connections[client_id]

    def send_action(self, client_id: str, payload: Dict[str, Any]) -> bool:
        if not client_id or not self._loop:
            return False
        coro = self._send_json(client_id, json.dumps(payload))
        fut = asyncio.run_coroutine_threadsafe(coro, self._loop)
        try:
            return bool(fut.result(timeout=4.0))
        except Exception:
            return False

    async def _send_json(self, client_id: str, text: str) -> bool:
        with self._conn_lock:
            ws = self._connections.get(client_id)
        if ws is None:
            return False
        try:
            await ws.send(text)
            return True
        except Exception:
            with self._conn_lock:
                if self._connections.get(client_id) is ws:
                    del self._connections[client_id]
            return False


def send_ws_action(server: Optional[InterviewWSServer], client_id: str, message: Dict[str, Any]) -> bool:
    if server is None or not client_id:
        return False
    return server.send_action(client_id, message)
