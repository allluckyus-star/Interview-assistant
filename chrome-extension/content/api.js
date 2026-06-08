/** Companion API — always via extension background (never page fetch to 127.0.0.1; PNA blocks that on chatgpt.com). */
window.IaApi = {
  base: "http://127.0.0.1:1212",

  async request(method, path, body) {
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
