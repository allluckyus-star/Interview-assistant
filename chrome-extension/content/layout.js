(function () {
  const PANEL_ID = "ia-interview-panel-host";
  const STORAGE_KEY = "iaPanelCollapsed";
  const LS_KEY = "iaPanelCollapsed";
  const CHATGPT_HOSTS = new Set(["chatgpt.com", "chat.openai.com"]);

  function isChatGptPage() {
    return CHATGPT_HOSTS.has(location.hostname.replace(/^www\./, ""));
  }

  /* Persist to localStorage so layout-early.js can read it at document_start */
  function syncLocal(collapsed) {
    try { localStorage.setItem(LS_KEY, collapsed ? "1" : "0"); } catch { /* */ }
  }

  async function loadCollapsed() {
    try {
      const data = await chrome.storage.local.get(STORAGE_KEY);
      if (typeof data[STORAGE_KEY] === "boolean") {
        syncLocal(data[STORAGE_KEY]);
        return data[STORAGE_KEY];
      }
    } catch { /* */ }
    try { return localStorage.getItem(LS_KEY) === "1"; } catch { return false; }
  }

  function setCollapsed(collapsed) {
    document.documentElement.classList.toggle("ia-panel-collapsed", collapsed);
    syncLocal(collapsed);
    try { chrome.storage.local.set({ [STORAGE_KEY]: collapsed }); } catch { /* */ }

    const edge = document.getElementById("ia-panel-edge-tab");
    const btn  = document.getElementById("ia-panel-collapse-btn");
    const chevR = window.IaIcons?.chevronRight || "›";
    const chevL = window.IaIcons?.chevronLeft  || "‹";

    if (edge) edge.setAttribute("aria-expanded", String(!collapsed));
    if (btn) {
      btn.innerHTML = collapsed ? chevL : chevR;
      btn.title = collapsed ? "Expand panel" : "Collapse panel";
    }
  }

  function toggleCollapsed() {
    const next = !document.documentElement.classList.contains("ia-panel-collapsed");
    setCollapsed(next);
    return next;
  }

  function isOverlayPortal(el) {
    if (!el || el.nodeType !== 1) return true;
    if (el.id === "ia-toast-host") return true;
    if (el.hasAttribute("data-radix-portal")) return true;
    if (el.hasAttribute("data-radix-popper-content-wrapper")) return true;
    const role = el.getAttribute("role");
    if (role === "tooltip" || role === "menu" || role === "dialog" || role === "listbox") return true;
    const style = window.getComputedStyle(el);
    if (style.position === "fixed" && el.childElementCount <= 1) {
      const inner = el.firstElementChild;
      if (inner?.hasAttribute?.("data-radix-popper-content-wrapper")) return true;
      const innerRole = inner?.getAttribute?.("role");
      if (innerRole === "menu" || innerRole === "tooltip" || innerRole === "dialog") return true;
    }
    return false;
  }

  function findGptShell() {
    const candidates = [...document.body.children].filter(
      (el) => el.tagName === "DIV" && !isOverlayPortal(el)
    );
    if (!candidates.length) return null;
    candidates.sort((a, b) => {
      const score = (node) =>
        node.querySelectorAll("[class*='w-screen']").length * 10 +
        node.querySelectorAll("[data-composer-surface]").length * 20 +
        node.childElementCount;
      return score(b) - score(a);
    });
    return candidates[0];
  }

  function ensureGptShellMarker() {
    const shell = findGptShell();
    if (!shell) return null;
    for (const marked of document.querySelectorAll(".ia-gpt-shell")) {
      if (marked !== shell) marked.classList.remove("ia-gpt-shell");
    }
    shell.classList.add("ia-gpt-shell");
    return shell;
  }

  function teardown() {
    document.documentElement.classList.remove("ia-docked", "ia-panel-collapsed");
    document.querySelectorAll(".ia-gpt-shell").forEach((el) => el.classList.remove("ia-gpt-shell"));
    document.getElementById(PANEL_ID)?.remove();
  }

  function ensureEdgeTab(host) {
    if (host.querySelector("#ia-panel-edge-tab")) return;
    const tab = document.createElement("button");
    tab.id   = "ia-panel-edge-tab";
    tab.type = "button";
    tab.innerHTML = window.IaIcons?.chevronLeft || "‹";
    tab.title = "Show Interview Assistant";
    tab.setAttribute("aria-label", "Expand Interview Assistant panel");
    tab.addEventListener("click", (e) => { e.stopPropagation(); setCollapsed(false); });
    host.appendChild(tab);
  }

  function ensurePanelInner(host) {
    let inner = host.querySelector(".ia-panel-inner");
    if (!inner) {
      inner = document.createElement("div");
      inner.className = "ia-panel-inner";
      inner.style.cssText = "width:100%;height:100%;";
      host.insertBefore(inner, host.firstChild);
    }
    return inner;
  }

  function ensurePanelHost() {
    let host = document.getElementById(PANEL_ID);
    if (!host) {
      host = document.createElement("div");
      host.id = PANEL_ID;
      /* Append to <html>, NOT <body>, so it's outside the transformed body */
      document.documentElement.appendChild(host);
    }

    ensureEdgeTab(host);
    const token = window.IaPanel?.scriptToken || "0";
    let inner = host.querySelector(".ia-panel-inner");
    if (inner?.dataset.iaScript !== token) {
      inner?.remove();
      inner = ensurePanelInner(host);
      inner.dataset.iaScript = token;
    } else if (!inner) {
      inner = ensurePanelInner(host);
      inner.dataset.iaScript = token;
    }

    if (window.IaPanel) {
      if (inner.shadowRoot) window.IaPanel.activate(inner);
      else window.IaPanel.mount(inner);
    }

    return host;
  }

  async function applyLayout() {
    if (!isChatGptPage()) { teardown(); return; }
    document.documentElement.classList.add("ia-docked");
    ensureGptShellMarker();
    ensurePanelHost();
    const collapsed = await loadCollapsed();
    setCollapsed(collapsed);
  }

  /* Public API */
  window.IaLayout = { isChatGptPage, toggleCollapsed, setCollapsed, teardown };

  if (!isChatGptPage()) return;
  if (window.__iaLayoutInstalled) { void applyLayout(); return; }
  window.__iaLayoutInstalled = true;

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => void applyLayout(), { once: true });
  } else {
    void applyLayout();
  }

  /* Retry mounting the panel host until ChatGPT finishes rendering its DOM */
  let retries = 0;
  const retryId = setInterval(() => {
    if (!isChatGptPage()) { clearInterval(retryId); return; }
    ensureGptShellMarker();
    ensurePanelHost();
    if (++retries >= 10) clearInterval(retryId);
  }, 500);

  let shellMarkTimer = null;
  const shellObserver = new MutationObserver(() => {
    if (!isChatGptPage()) return;
    if (shellMarkTimer) clearTimeout(shellMarkTimer);
    shellMarkTimer = setTimeout(() => {
      shellMarkTimer = null;
      ensureGptShellMarker();
    }, 120);
  });
  shellObserver.observe(document.documentElement, { childList: true, subtree: true });

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type === "ia_toggle_panel") {
      if (!isChatGptPage()) { sendResponse({ ok: false, reason: "not_chatgpt" }); return; }
      sendResponse({ ok: true, collapsed: toggleCollapsed() });
      return true;
    }
    if (msg?.type === "ia_get_panel_state") {
      sendResponse({
        ok: true,
        chatgpt: isChatGptPage(),
        collapsed: document.documentElement.classList.contains("ia-panel-collapsed"),
      });
      return true;
    }
  });

  window.addEventListener("popstate", () => {
    if (!isChatGptPage()) teardown(); else void applyLayout();
  });
})();
