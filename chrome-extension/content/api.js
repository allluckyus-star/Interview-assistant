window.IaApi = {
  base: "http://127.0.0.1:1212",

  async request(method, path, body) {
    try {
      const hasBody = body !== undefined && body !== null;
      const res = await fetch(`${this.base}${path}`, {
        method: method || "GET",
        headers: hasBody ? { "Content-Type": "application/json" } : undefined,
        body: hasBody ? JSON.stringify(body) : undefined,
      });
      const text = await res.text();
      let data = null;
      try {
        data = text ? JSON.parse(text) : null;
      } catch {
        data = { raw: text };
      }
      return { ok: res.ok, status: res.status, data };
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
    const es = new EventSource(`${this.base}/events`);

    es.onopen = () => onEvent({ type: "connection", payload: { ok: true } });

    es.onmessage = (ev) => {
      try {
        const msg = JSON.parse(ev.data);
        onEvent(msg);
      } catch {
        // ignore
      }
    };

    es.onerror = () => onEvent({ type: "connection", payload: { ok: false } });

    return () => es.close();
  },
};
