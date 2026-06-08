/** Centered page toasts — mirrors c#project ToastOverlay (top center of browser). */
window.IaToast = (function () {
  const MAX_VISIBLE = 3;
  const MAX_CHARS = 96;

  const DURATION = {
    success: 2400,
    warning: 3200,
    error: 4200,
    info: 2600,
  };

  const THEMES = {
    success: { bg: "#ecfdf5", border: "#a7f3d0", icon: "#059669", glyph: "✓", text: "#065f46" },
    warning: { bg: "#fff9e6", border: "#ffeba0", icon: "#e67e00", glyph: "!", text: "#856404" },
    error: { bg: "#fff0f0", border: "#f5c6cb", icon: "#d93025", glyph: "×", text: "#721c24" },
    info: { bg: "#eff6ff", border: "#bfdbfe", icon: "#2563eb", glyph: "i", text: "#1e40af" },
  };

  let stackEl = null;
  let loadingCard = null;

  function trim(text, max) {
    const t = String(text || "").trim();
    const limit = max || MAX_CHARS;
    if (t.length <= limit) return t;
    return t.slice(0, limit - 1) + "…";
  }

  function ensureStack() {
    if (stackEl?.isConnected) return stackEl;
    let host = document.getElementById("ia-toast-host");
    if (!host) {
      host = document.createElement("div");
      host.id = "ia-toast-host";
      document.documentElement.appendChild(host);
    }
    stackEl = host.querySelector(".ia-toast-stack");
    if (!stackEl) {
      stackEl = document.createElement("div");
      stackEl.className = "ia-toast-stack";
      host.appendChild(stackEl);
    }
    return stackEl;
  }

  function dismiss(card) {
    if (!card?.isConnected) return;
    card.classList.remove("ia-toast--visible");
    setTimeout(() => card.remove(), 180);
  }

  function show(message, level) {
    const lvl = THEMES[level] ? level : "success";
    const theme = THEMES[lvl];
    let text = trim(message);
    if (!text) {
      text =
        lvl === "error"
          ? "Failed."
          : lvl === "warning"
            ? "Warning."
            : lvl === "info"
              ? "Note."
              : "Done.";
    }

    const stack = ensureStack();
    while (stack.children.length >= MAX_VISIBLE) {
      stack.removeChild(stack.firstElementChild);
    }

    const card = document.createElement("div");
    card.className = `ia-toast ia-toast--${lvl}`;
    card.innerHTML =
      `<span class="ia-toast-icon" aria-hidden="true">${theme.glyph}</span>` +
      `<span class="ia-toast-text"></span>`;
    card.querySelector(".ia-toast-text").textContent = text;
    card.style.setProperty("--ia-toast-bg", theme.bg);
    card.style.setProperty("--ia-toast-border", theme.border);
    card.style.setProperty("--ia-toast-icon", theme.icon);
    card.style.setProperty("--ia-toast-text", theme.text);

    stack.appendChild(card);
    requestAnimationFrame(() => card.classList.add("ia-toast--visible"));

    setTimeout(() => dismiss(card), DURATION[lvl] || 2400);
  }

  function clearLoading() {
    if (!loadingCard) return;
    dismiss(loadingCard);
    loadingCard = null;
  }

  function loading(message) {
    clearLoading();
    const theme = THEMES.info;
    let text = trim(message, MAX_CHARS * 2);
    if (!text) text = "Working…";

    const stack = ensureStack();
    while (stack.children.length >= MAX_VISIBLE) {
      stack.removeChild(stack.firstElementChild);
    }

    const card = document.createElement("div");
    card.className = "ia-toast ia-toast--info ia-toast--loading";
    card.innerHTML =
      `<span class="ia-toast-icon" aria-hidden="true">…</span>` +
      `<span class="ia-toast-text"></span>`;
    card.querySelector(".ia-toast-text").textContent = text;
    card.style.setProperty("--ia-toast-bg", theme.bg);
    card.style.setProperty("--ia-toast-border", theme.border);
    card.style.setProperty("--ia-toast-icon", theme.icon);
    card.style.setProperty("--ia-toast-text", theme.text);

    stack.appendChild(card);
    requestAnimationFrame(() => card.classList.add("ia-toast--visible"));
    loadingCard = card;
  }

  return {
    show,
    success: (m) => show(m, "success"),
    warning: (m) => show(m, "warning"),
    error: (m) => show(m, "error"),
    info: (m) => show(m, "info"),
    loading,
    clearLoading,
  };
})();
