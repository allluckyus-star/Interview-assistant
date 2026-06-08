/** Companion API — via extension background (PNA-safe). Long ShareX waits use ia_clipboard port. */
window.IaApi = {
  base: "http://127.0.0.1:1212",

  isShareXWaitPath(path) {
    const p = String(path || "");
    return p === "/sharex/wait-image" || p === "/sharex/wait-text";
  },

  requestViaClipboardPort(method, path, body) {
    return new Promise((resolve) => {
      if (!chrome?.runtime?.connect) {
        resolve({ ok: false, error: "extension_runtime_unavailable" });
        return;
      }

      let port;
      try {
        port = chrome.runtime.connect({ name: "ia_clipboard" });
      } catch (e) {
        resolve({ ok: false, error: String(e.message || e) });
        return;
      }

      let settled = false;
      function finish(result) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        try {
          port.disconnect();
        } catch {
          // ignore
        }
        resolve(result);
      }

      const timer = setTimeout(() => {
        finish({ ok: false, error: "sharex_timeout" });
      }, 130000);

      port.onMessage.addListener((msg) => {
        if (msg?.type !== "ia_clipboard_result") return;
        finish(msg.result || { ok: false, error: "no_response" });
      });

      port.onDisconnect.addListener(() => {
        if (!settled) finish({ ok: false, error: "companion_disconnected" });
      });

      try {
        port.postMessage({
          type: "ia_clipboard_request",
          method: method || "POST",
          path,
          body: body !== undefined && body !== null ? body : undefined,
        });
      } catch (e) {
        finish({ ok: false, error: String(e.message || e) });
      }
    });
  },

  async request(method, path, body) {
    if (this.isShareXWaitPath(path)) {
      return this.requestViaClipboardPort(method, path, body);
    }

    try {
      if (!chrome?.runtime?.sendMessage) {
        return { ok: false, error: "extension_runtime_unavailable" };
      }
      const res = await chrome.runtime.sendMessage({
        type: "ia_api",
        method: method || "GET",
        path,
        body: body !== undefined && body !== null ? body : undefined,
      });
      if (!res) {
        return { ok: false, error: "no_response" };
      }
      return res;
    } catch (e) {
      return { ok: false, error: String(e.message || e) };
    }
  },

  get(path) {
    return this.request("GET", path);
  },

  post(path, body) {
    return this.request("POST", path, body);
  },

  del(path) {
    return this.request("DELETE", path);
  },

  patch(path, body) {
    return this.request("PATCH", path, body);
  },

  connectEvents(onEvent) {
    if (!chrome?.runtime?.connect) {
      onEvent({ type: "connection", payload: { ok: false } });
      return () => {};
    }

    const port = chrome.runtime.connect({ name: "ia_sse" });

    port.onMessage.addListener((msg) => {
      if (msg?.type === "sse_open") {
        onEvent({ type: "connection", payload: { ok: true } });
        return;
      }
      if (msg?.type === "sse_error") {
        onEvent({ type: "connection", payload: { ok: false } });
        return;
      }
      if (msg?.type === "sse" && msg.data) {
        try {
          onEvent(JSON.parse(msg.data));
        } catch {
          // ignore malformed SSE payload
        }
      }
    });

    port.onDisconnect.addListener(() => {
      onEvent({ type: "connection", payload: { ok: false } });
    });

    return () => {
      try {
        port.disconnect();
      } catch {
        // ignore
      }
    };
  },
};
