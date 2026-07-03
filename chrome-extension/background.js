const API_BASE = "http://127.0.0.1:1212";
const SHAREX_STORAGE_KEY = "ia_sharex_b64";
const GPT_URL_PATTERNS = ["https://chatgpt.com/*", "https://chat.openai.com/*"];
const GLOBAL_SSE_RELAY_TYPES = new Set(["sharex_image", "sharex_text", "panel_shortcut"]);

let globalEs = null;
let globalEsReconnectTimer = null;

function isChatGptUrl(url) {
  if (!url) return false;
  try {
    const h = new URL(url).hostname.replace(/^www\./, "");
    return h === "chatgpt.com" || h === "chat.openai.com";
  } catch {
    return false;
  }
}

function arrayBufferToBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
  }
  return btoa(binary);
}

async function broadcastPanelEvent(msg) {
  if (!msg?.type) return;
  let tabs = [];
  try {
    tabs = await chrome.tabs.query({ url: GPT_URL_PATTERNS });
  } catch {
    return;
  }
  for (const tab of tabs) {
    if (!tab?.id) continue;
    try {
      await chrome.tabs.sendMessage(tab.id, { type: "ia_panel_sse", msg });
    } catch {
      // content script not ready or tab discarded
    }
  }
}

function stopGlobalSse() {
  if (globalEsReconnectTimer) {
    clearTimeout(globalEsReconnectTimer);
    globalEsReconnectTimer = null;
  }
  if (!globalEs) return;
  try {
    globalEs.close();
  } catch {
    // ignore
  }
  globalEs = null;
}

function scheduleGlobalSseReconnect() {
  if (globalEsReconnectTimer) return;
  globalEsReconnectTimer = setTimeout(() => {
    globalEsReconnectTimer = null;
    startGlobalSse();
  }, 2000);
}

function startGlobalSse() {
  if (globalEs) return;
  try {
    globalEs = new EventSource(`${API_BASE}/events`);
  } catch {
    scheduleGlobalSseReconnect();
    return;
  }

  globalEs.onmessage = (ev) => {
    if (!ev?.data) return;
    let msg;
    try {
      msg = JSON.parse(ev.data);
    } catch {
      return;
    }
    if (!GLOBAL_SSE_RELAY_TYPES.has(msg?.type)) return;
    void broadcastPanelEvent(msg);
  };

  globalEs.onerror = () => {
    stopGlobalSse();
    scheduleGlobalSseReconnect();
  };
}

startGlobalSse();
chrome.runtime.onStartup.addListener(startGlobalSse);
chrome.runtime.onInstalled.addListener(startGlobalSse);

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

async function runShareXWaitImage() {
  const post = await apiRequest("POST", "/sharex/wait-image", {});
  if (!post.ok) return post;

  const data = post.data || {};
  if (data.cancelled || data.ok === false) {
    return { ok: post.ok, status: post.status, data };
  }

  const imageId = data.image_id;
  if (!imageId) {
    return { ok: false, data: { ok: false, error: "image_id_missing" } };
  }

  const binRes = await fetch(`${API_BASE}/sharex/image/${encodeURIComponent(imageId)}`);
  if (!binRes.ok) {
    return { ok: false, data: { ok: false, error: "image_fetch_failed_" + binRes.status } };
  }

  const buf = await binRes.arrayBuffer();
  const b64 = arrayBufferToBase64(buf);
  await chrome.storage.session.set({ [SHAREX_STORAGE_KEY]: b64 });

  return {
    ok: true,
    status: 200,
    data: { ok: true, sharex_storage_key: SHAREX_STORAGE_KEY },
  };
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === "ia_fetch_sharex_image") {
    const imageId = msg.image_id;
    if (!imageId) {
      sendResponse({ ok: false, error: "image_id_missing" });
      return false;
    }

    fetch(`${API_BASE}/sharex/image/${encodeURIComponent(imageId)}`)
      .then(async (binRes) => {
        if (!binRes.ok) {
          return { ok: false, error: "image_fetch_failed_" + binRes.status };
        }
        const buf = await binRes.arrayBuffer();
        const b64 = arrayBufferToBase64(buf);
        return { ok: true, data: { image_base64: b64, sharex_storage_key: SHAREX_STORAGE_KEY } };
      })
      .then((result) => sendResponse(result))
      .catch((e) => sendResponse({ ok: false, error: String(e.message || e) }));

    return true;
  }

  if (msg?.type !== "ia_api") return false;

  apiRequest(msg.method, msg.path || "/", msg.body)
    .then((result) => sendResponse(result))
    .catch((e) => sendResponse({ ok: false, error: String(e.message || e) }));

  return true;
});

chrome.runtime.onConnect.addListener((port) => {
  if (port.name === "ia_clipboard") {
    port.onMessage.addListener((msg) => {
      if (msg?.type !== "ia_clipboard_request") return;

      const path = msg.path || "/";
      const run =
        path === "/sharex/wait-image"
          ? runShareXWaitImage()
          : apiRequest(msg.method || "POST", path, msg.body);

      run
        .then((result) => {
          try {
            port.postMessage({ type: "ia_clipboard_result", result });
          } catch {
            // port closed
          }
        })
        .catch((e) => {
          try {
            port.postMessage({
              type: "ia_clipboard_result",
              result: { ok: false, error: String(e.message || e) },
            });
          } catch {
            // port closed
          }
        });
    });
    return;
  }

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
