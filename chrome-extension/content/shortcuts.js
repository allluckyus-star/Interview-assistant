window.IaShortcuts = (function () {
  const STORAGE_KEY = "ia_panel_settings_v1";

  const ACTION_LABELS = {
    send: "Send (green captions → ChatGPT)",
    copyGpt: "Copy latest GPT reply",
    imageCapture: "Image capture (ShareX)",
    ocr: "OCR capture (ShareX)",
  };

  const SHAREX_ACTIONS = ["imageCapture", "ocr"];
  const PANEL_ACTIONS = ["send", "copyGpt"];

  const DEFAULTS = {
    send: { ctrl: true, shift: true, alt: false, meta: false, code: "Enter", key: "Enter" },
    copyGpt: { ctrl: true, shift: true, alt: false, meta: false, code: "KeyG", key: "g" },
    imageCapture: { ctrl: true, shift: false, alt: false, meta: false, code: "PrintScreen", key: "PrintScreen" },
    ocr: { ctrl: false, shift: false, alt: true, meta: false, code: "Period", key: "." },
  };

  const CODE_TO_VK = {
    PrintScreen: 0x2c,
    Period: 0xbe,
    NumpadDecimal: 0x6e,
    Enter: 0x0d,
    NumpadEnter: 0x0d,
    KeyA: 0x41, KeyB: 0x42, KeyC: 0x43, KeyD: 0x44, KeyE: 0x45, KeyF: 0x46,
    KeyG: 0x47, KeyH: 0x48, KeyI: 0x49, KeyJ: 0x4a, KeyK: 0x4b, KeyL: 0x4c,
    KeyM: 0x4d, KeyN: 0x4e, KeyO: 0x4f, KeyP: 0x50, KeyQ: 0x51, KeyR: 0x52,
    KeyS: 0x53, KeyT: 0x54, KeyU: 0x55, KeyV: 0x56, KeyW: 0x57, KeyX: 0x58,
    KeyY: 0x59, KeyZ: 0x5a,
    Digit0: 0x30, Digit1: 0x31, Digit2: 0x32, Digit3: 0x33, Digit4: 0x34,
    Digit5: 0x35, Digit6: 0x36, Digit7: 0x37, Digit8: 0x38, Digit9: 0x39,
    F1: 0x70, F2: 0x71, F3: 0x72, F4: 0x73, F5: 0x74, F6: 0x75,
    F7: 0x76, F8: 0x77, F9: 0x78, F10: 0x79, F11: 0x7a, F12: 0x7b,
    Insert: 0x2d, Delete: 0x2e, Home: 0x24, End: 0x23,
    PageUp: 0x21, PageDown: 0x22, ArrowUp: 0x26, ArrowDown: 0x28,
    ArrowLeft: 0x25, ArrowRight: 0x27, Space: 0x20, Tab: 0x09,
    Backspace: 0x08, Escape: 0x1b,
  };

  function cloneShortcut(s) {
    return {
      ctrl: !!s.ctrl,
      shift: !!s.shift,
      alt: !!s.alt,
      meta: !!s.meta,
      code: String(s.code || ""),
      key: String(s.key || ""),
    };
  }

  function cloneDefaults() {
    const out = {};
    for (const id of Object.keys(DEFAULTS)) out[id] = cloneShortcut(DEFAULTS[id]);
    return out;
  }

  function normalizeShortcut(raw, fallback) {
    const base = fallback || { ctrl: false, shift: false, alt: false, meta: false, code: "", key: "" };
    return {
      ctrl: raw?.ctrl != null ? !!raw.ctrl : base.ctrl,
      shift: raw?.shift != null ? !!raw.shift : base.shift,
      alt: raw?.alt != null ? !!raw.alt : base.alt,
      meta: raw?.meta != null ? !!raw.meta : base.meta,
      code: String(raw?.code || base.code || ""),
      key: String(raw?.key || base.key || ""),
    };
  }

  function formatCombo(s) {
    if (!s?.code) return "—";
    const parts = [];
    if (s.ctrl) parts.push("Ctrl");
    if (s.alt) parts.push("Alt");
    if (s.shift) parts.push("Shift");
    if (s.meta) parts.push("Win");
    parts.push(formatKeyLabel(s));
    return parts.join("+");
  }

  function formatKeyLabel(s) {
    const code = s.code || "";
    if (code === "PrintScreen") return "PrtSc";
    if (code.startsWith("Key")) return code.slice(3);
    if (code.startsWith("Digit")) return code.slice(5);
    if (code === "Period") return ".";
    if (code === "NumpadDecimal") return "Num .";
    if (code === "Space") return "Space";
    if (code.length) return code.replace(/^Numpad/, "Num ");
    return s.key || "?";
  }

  function matchesEvent(ev, s) {
    if (!s?.code || ev.code !== s.code) return false;
    if (!!ev.ctrlKey !== !!s.ctrl) return false;
    if (!!ev.altKey !== !!s.alt) return false;
    if (!!ev.shiftKey !== !!s.shift) return false;
    if (!!ev.metaKey !== !!s.meta) return false;
    return true;
  }

  function bindingFromEvent(ev) {
    if (!ev.code || ev.code === "ControlLeft" || ev.code === "ControlRight" ||
        ev.code === "AltLeft" || ev.code === "AltRight" ||
        ev.code === "ShiftLeft" || ev.code === "ShiftRight" ||
        ev.code === "MetaLeft" || ev.code === "MetaRight") {
      return null;
    }
    return {
      ctrl: ev.ctrlKey,
      shift: ev.shiftKey,
      alt: ev.altKey,
      meta: ev.metaKey,
      code: ev.code,
      key: ev.key,
    };
  }

  function toCompanionBinding(s) {
    const vk = CODE_TO_VK[s.code] || 0;
    const altVks = [];
    if (s.code === "Period") altVks.push(0x6e);
    if (s.code === "NumpadDecimal") altVks.push(0xbe);
    return {
      ctrl: !!s.ctrl,
      shift: !!s.shift,
      alt: !!s.alt,
      key_vk: vk,
      alt_key_vks: altVks,
      code: s.code,
    };
  }

  async function load() {
    const defaults = {
      shortcuts: cloneDefaults(),
      shareXImageListen: false,
      shareXTextListen: false,
    };
    if (!chrome?.storage?.local) return defaults;
    try {
      const stored = await chrome.storage.local.get(STORAGE_KEY);
      const raw = stored?.[STORAGE_KEY];
      if (!raw || typeof raw !== "object") return defaults;
      const shortcuts = cloneDefaults();
      for (const id of Object.keys(DEFAULTS)) {
        shortcuts[id] = normalizeShortcut(raw.shortcuts?.[id], DEFAULTS[id]);
      }
      return {
        shortcuts,
        shareXImageListen: !!raw.shareXImageListen,
        shareXTextListen: !!raw.shareXTextListen,
      };
    } catch {
      return defaults;
    }
  }

  async function save(data) {
    if (!chrome?.storage?.local) return;
    const payload = {
      shortcuts: data.shortcuts || cloneDefaults(),
      shareXImageListen: !!data.shareXImageListen,
      shareXTextListen: !!data.shareXTextListen,
    };
    try {
      await chrome.storage.local.set({ [STORAGE_KEY]: payload });
    } catch {
      // ignore
    }
  }

  return {
    STORAGE_KEY,
    ACTION_LABELS,
    SHAREX_ACTIONS,
    PANEL_ACTIONS,
    DEFAULTS,
    load,
    save,
    formatCombo,
    formatKeyLabel,
    matchesEvent,
    bindingFromEvent,
    cloneShortcut,
    cloneDefaults,
    normalizeShortcut,
    toCompanionBinding,
  };
})();
