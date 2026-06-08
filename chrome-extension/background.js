const API_BASE = "http://127.0.0.1:1212";

function isChatGptUrl(url) {
  if (!url) return false;
  try {
    const h = new URL(url).hostname.replace(/^www\./, "");
    return h === "chatgpt.com" || h === "chat.openai.com";
  } catch {
    return false;
  }
}

async function apiRequest(method, path, body) {
  const hasBody = body !== undefined && body !== null;
  const res = await fetch(`${API_BASE}${path}`, {
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
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type !== "ia_api") return false;

  apiRequest(msg.method, msg.path || "/", msg.body)
    .then((result) => sendResponse(result))
    .catch((e) => sendResponse({ ok: false, error: String(e.message || e) }));

  return true;
});

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== "ia_sse") return;

  let es = null;

  function closeEs() {
    if (!es) return;
    try {
      es.close();
    } catch {
      // ignore
    }
    es = null;
  }

  try {
    es = new EventSource(`${API_BASE}/events`);
  } catch (e) {
    try {
      port.postMessage({ type: "sse_error", error: String(e.message || e) });
    } catch {
      // port closed
    }
    return;
  }

  es.onopen = () => {
    try {
      port.postMessage({ type: "sse_open" });
    } catch {
      closeEs();
    }
  };

  es.onmessage = (ev) => {
    try {
      port.postMessage({ type: "sse", data: ev.data });
    } catch {
      closeEs();
    }
  };

  es.onerror = () => {
    try {
      port.postMessage({ type: "sse_error" });
    } catch {
      // port closed
    }
    closeEs();
  };

  port.onDisconnect.addListener(closeEs);
});

chrome.action.onClicked.addListener(async (tab) => {
  if (!tab?.id) return;
  if (!isChatGptUrl(tab.url)) {
    await chrome.action.setBadgeText({ tabId: tab.id, text: "!" });
    await chrome.action.setTitle({
      tabId: tab.id,
      title: "Interview Assistant — open chatgpt.com first",
    });
    setTimeout(() => chrome.action.setBadgeText({ tabId: tab.id, text: "" }), 2000);
    return;
  }
  try {
    await chrome.tabs.sendMessage(tab.id, { type: "ia_toggle_panel" });
  } catch {
    // Content script not ready
  }
});
