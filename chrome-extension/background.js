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

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === "ia_api") {
    const path = msg.path || "/";
    const method = msg.method || "GET";
    const body = msg.body;
    fetch(`${API_BASE}${path}`, {
      method,
      headers: body ? { "Content-Type": "application/json" } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    })
      .then(async (r) => {
        const text = await r.text();
        let data = null;
        try {
          data = text ? JSON.parse(text) : null;
        } catch {
          data = { raw: text };
        }
        sendResponse({ ok: r.ok, status: r.status, data });
      })
      .catch((e) => sendResponse({ ok: false, error: String(e.message || e) }));
    return true;
  }
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
    // Content script not ready — inject not possible without scripting permission
  }
});
