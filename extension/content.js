(function () {
  const ANSWER_ENDPOINT = "http://127.0.0.1:8765/answer";
  const PREP_COMPLETE_ENDPOINT = "http://127.0.0.1:8765/prep/complete";
  const GPT_STATE_ENDPOINT = "http://127.0.0.1:8765/gpt-state";
  const PREP_EXT_STATUS_ENDPOINT = "http://127.0.0.1:8765/prep/extension-status";
  const latestSentByRequest = new Map();
  let lastGptStatusSent = 0;
  let lastGptStateFailNotified = 0;
  let activeInterviewRequestId = "";
  let activeBaselineAssistantCount = -1;
  /** Must match live.py `MANUAL_GPT_REQUEST_ID` */
  const MANUAL_GPT_RID = "__manual_chatgpt_tab__";
  let prepCaptureBusy = false;
  let cachedClientId = "";
  let manualGptObserver = null;
  let manualGptInterval = null;
  let lastManualLiveEmitAt = 0;
  let lastManualLivePayload = "";
  let manualStableText = "";
  let manualStableSince = 0;
  let lastManualFinalPayload = "";

  /** DevTools on the ChatGPT tab — prep/GPT progress (not the Python terminal). */
  function iaLog(...args) {
    try {
      // console.log("[Interview Assistant]", ...args);
    } catch (_e) {}
  }

  function isComposerVisible(el) {
    if (!el || !(el instanceof Element)) return false;
    const st = window.getComputedStyle(el);
    if (st.display === "none" || st.visibility === "hidden" || Number(st.opacity) === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 2 && r.height > 2;
  }

  function findComposer() {
    const surface = document.querySelector('[data-composer-surface="true"]');
    if (surface) {
      const prefer =
        surface.querySelector('div#prompt-textarea[contenteditable="true"]') ||
        surface.querySelector("div.ProseMirror[contenteditable='true']") ||
        surface.querySelector('[contenteditable="true"][role="textbox"]');
      if (prefer && isComposerVisible(prefer)) return prefer;
    }
    const byId = document.querySelector('div#prompt-textarea[contenteditable="true"]');
    if (byId && isComposerVisible(byId)) return byId;

    const textareas = document.querySelectorAll("textarea");
    for (let i = 0; i < textareas.length; i += 1) {
      const t = textareas[i];
      if (!isComposerVisible(t)) continue;
      if (
        t.id === "prompt-textarea" ||
        t.name === "prompt-textarea" ||
        /message/i.test(t.getAttribute("placeholder") || "")
      ) {
        return t;
      }
    }

    const legacyTa = document.querySelector("textarea#prompt-textarea");
    if (legacyTa && isComposerVisible(legacyTa)) return legacyTa;

    const ce = document.querySelector("[contenteditable='true']");
    if (ce && isComposerVisible(ce)) return ce;
    return null;
  }

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function setContentEditablePlainText(el, text) {
    const plain = String(text ?? "");
    el.focus();
    try {
      const sel = window.getSelection();
      const range = document.createRange();
      range.selectNodeContents(el);
      sel.removeAllRanges();
      sel.addRange(range);
      if (document.execCommand("insertText", false, plain)) {
        el.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertText" }));
        return true;
      }
    } catch (_e) {
      /* fall through */
    }
    const parts = plain.split("\n");
    const inner = parts
      .map((line) => {
        const esc = escapeHtml(line);
        return esc === "" ? "<p><br></p>" : `<p>${esc}</p>`;
      })
      .join("");
    el.innerHTML = inner || "<p><br></p>";
    el.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertParagraph" }));
    return true;
  }

  function base64ToUint8Array(b64) {
    const bin = atob(String(b64 || ""));
    const len = bin.length;
    const out = new Uint8Array(len);
    for (let i = 0; i < len; i += 1) out[i] = bin.charCodeAt(i);
    return out;
  }

  /**
   * ChatGPT dims the UI on synthetic dragenter/dragover. Without dragleave/dragend (like dragging away
   * from the composer), the overlay can stay. Clear it after our programmatic drop.
   */
  function dismissComposerDragDropOverlay(primary, partner) {
    const emptyDt = new DataTransfer();
    const leaveOpts = { bubbles: true, composed: true, cancelable: false, dataTransfer: emptyDt };
    const nodes = [];
    if (primary instanceof HTMLElement) nodes.push(primary);
    if (partner instanceof HTMLElement && partner !== primary) nodes.push(partner);
    const surf = getComposerSurface();
    if (surf instanceof HTMLElement && !nodes.includes(surf)) nodes.push(surf);
    const ta = document.querySelector('div#prompt-textarea[contenteditable="true"]');
    if (ta instanceof HTMLElement && !nodes.includes(ta)) nodes.push(ta);
    for (let i = 0; i < nodes.length; i += 1) {
      try {
        nodes[i].dispatchEvent(new DragEvent("dragleave", leaveOpts));
      } catch (_e) {}
      try {
        nodes[i].dispatchEvent(new DragEvent("dragexit", leaveOpts));
      } catch (_e) {}
    }
    try {
      document.documentElement.dispatchEvent(new DragEvent("dragleave", leaveOpts));
    } catch (_e) {}
    try {
      if (document.body) document.body.dispatchEvent(new DragEvent("dragleave", leaveOpts));
    } catch (_e) {}
    try {
      window.dispatchEvent(new DragEvent("dragleave", leaveOpts));
    } catch (_e) {}
    try {
      window.dispatchEvent(new DragEvent("dragend", leaveOpts));
    } catch (_e) {}
  }

  function tryAttachPngDataToElement(el, base64Png, partnerForCleanup) {
    if (!(el instanceof HTMLElement) || !base64Png) return false;
    const trimmed = String(base64Png).trim();
    if (!trimmed) return false;
    let bytes;
    try {
      bytes = base64ToUint8Array(trimmed);
    } catch (_e) {
      return false;
    }
    const name = `ia-snip-${Date.now()}.png`;
    const file = new File([bytes], name, { type: "image/png" });
    const dt = new DataTransfer();
    try {
      dt.items.add(file);
    } catch (_e) {
      return false;
    }
    const opts = { bubbles: true, composed: true, cancelable: true, dataTransfer: dt };
    let ok = false;
    try {
      try {
        el.focus({ preventScroll: true });
      } catch (_e) {
        try {
          el.focus();
        } catch (_e2) {}
      }
      el.dispatchEvent(new DragEvent("dragenter", opts));
      el.dispatchEvent(new DragEvent("dragover", opts));
      el.dispatchEvent(new DragEvent("drop", opts));
      ok = true;
    } catch (_e) {
      ok = false;
    } finally {
      dismissComposerDragDropOverlay(el, partnerForCleanup);
    }
    return ok;
  }

  /** Drop on outer composer surface (preferred for file chips). */
  function tryAttachPngToComposer(composer, base64Png) {
    if (!composer || !base64Png) return false;
    const surf = getComposerSurface();
    const el = surf instanceof HTMLElement ? surf : composer;
    const partner = el === composer ? null : composer;
    return tryAttachPngDataToElement(el, base64Png, partner);
  }

  /** Fallback: some builds accept drops only on the inner editor. */
  function tryAttachPngToInnerComposer(composer, base64Png) {
    if (!(composer instanceof HTMLElement) || !base64Png) return false;
    const surf = getComposerSurface();
    const partner = surf instanceof HTMLElement && !surf.isSameNode(composer) ? surf : null;
    return tryAttachPngDataToElement(composer, base64Png, partner);
  }

  /** Heuristic: ChatGPT staged a file/image on the composer (not just empty "drop here" copy). */
  function composerHasVisibleAttachment(root) {
    if (!(root instanceof HTMLElement)) return false;
    const hints = [
      '[data-testid*="attachment"]',
      'button[aria-label*="Remove"]',
      'button[aria-label*="remove"]',
    ];
    for (let i = 0; i < hints.length; i += 1) {
      try {
        if (root.querySelector(hints[i])) return true;
      } catch (_e) {}
    }
    const imgs = root.querySelectorAll("img");
    for (let i = 0; i < imgs.length; i += 1) {
      const img = imgs[i];
      const src = (img.getAttribute("src") || "").trim();
      if (src.startsWith("blob:") || src.startsWith("data:image")) return true;
    }
    return false;
  }

  async function waitForComposerAttachment(root, maxMs) {
    const limit = Date.now() + (maxMs || 12000);
    while (Date.now() < limit) {
      if (composerHasVisibleAttachment(root)) return true;
      await wait(90);
    }
    return false;
  }

  /** Prefer Send only after attachment UI appears; last ~3.5s allow Send anyway (invisible attach edge case). */
  async function trySubmitWithRetryAfterAttachment(maxMs, attachRoot, needsAttachment, composerEl) {
    const deadline = Date.now() + (maxMs || 22000);
    const graceAt = deadline - 3500;
    while (Date.now() < deadline) {
      const attached = !needsAttachment || composerHasVisibleAttachment(attachRoot);
      const button = findSendButton();
      const force = !!(needsAttachment && !attached && Date.now() >= graceAt);
      if (button && !button.disabled && (attached || force)) {
        if (force) iaLog("[ws] send without detected attachment chip (grace)");
        button.click();
        dismissComposerDragDropOverlay(attachRoot, composerEl || attachRoot);
        return true;
      }
      await wait(110);
    }
    return false;
  }

  function setInputValue(el, text) {
    if (!el) return false;

    if (el.tagName === "TEXTAREA" || el.tagName === "INPUT") {
      el.focus();
      el.value = text;
      el.dispatchEvent(new Event("input", { bubbles: true }));
      el.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true }));
      return true;
    }

    if (el.getAttribute("contenteditable") === "true") {
      return setContentEditablePlainText(el, text);
    }

    return false;
  }

  function getComposerSurface() {
    return document.querySelector('[data-composer-surface="true"]');
  }

  /** Prefer the real ChatGPT composer send control (avoid unrelated Send buttons on page). */
  function findSendButton() {
    const surface = getComposerSurface();
    const scope = surface || document;
    return (
      scope.querySelector("#composer-submit-button[data-testid='send-button']") ||
      scope.querySelector("button#composer-submit-button[data-testid='send-button']") ||
      scope.querySelector("#composer-submit-button") ||
      scope.querySelector("button[data-testid='send-button']") ||
      scope.querySelector('button[aria-label="Send prompt"]') ||
      document.querySelector("button svg[data-icon='paper-plane']")?.closest("button")
    );
  }

  function trySubmit() {
    const button = findSendButton();
    if (button && !button.disabled) {
      button.click();
      return true;
    }
    return false;
  }

  async function trySubmitWithRetry(maxMs) {
    const deadline = Date.now() + (maxMs || 15000);
    while (Date.now() < deadline) {
      const button = findSendButton();
      if (button && !button.disabled) {
        button.click();
        return true;
      }
      await wait(120);
    }
    return false;
  }

  async function waitForComposer(maxMs) {
    const deadline = Date.now() + (maxMs || 20000);
    while (Date.now() < deadline) {
      const c = findComposer();
      if (c) return c;
      await wait(150);
    }
    return null;
  }

  /** All assistant turns in document order (last index = latest reply). */
  function getAssistantMessageList() {
    return document.querySelectorAll('[data-message-author-role="assistant"]');
  }

  function isElementVisible(el) {
    if (!el || !(el instanceof HTMLElement)) return false;
    if (el.offsetParent === null && el.getClientRects().length === 0) return false;
    const st = window.getComputedStyle(el);
    if (st.display === "none" || st.visibility === "hidden" || Number(st.opacity) === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 1 && r.height > 1;
  }

  function sanitizeAssistantText(s) {
    return String(s || "")
      .replace(/\r\n/g, "\n")
      .replace(/\u00a0/g, " ")
      .trim();
  }

  /**
   * Reject placeholders / noise; tuned for prose (prep) and short interview replies.
   * @param {{ minLen?: number, minAlphaRatio?: number }} [o]
   */
  function isUsableAssistantText(text, o) {
    const opts = o || {};
    const minLen = opts.minLen != null ? opts.minLen : 25;
    const minRatio = opts.minAlphaRatio != null ? opts.minAlphaRatio : 0.12;
    const t = sanitizeAssistantText(text);
    if (/^(WAITING|READY)$/i.test(t)) return true;
    if (/^[^.!?\n]{1,120}[.!?]?$/.test(t) && t.length >= 2) return true;
    if (t.length < minLen) return false;
    const letters = t.replace(/[^0-9A-Za-z]+/g, "").length;
    if (letters / Math.max(t.length, 1) < minRatio) return false;
    if (/^(thinking([….]|\.*)?|\.{3,})$/i.test(t)) return false;
    return true;
  }

  /**
   * Latest visible assistant bubble (scan from DOM end). Matches multiple ChatGPT DOM variants.
   * @param {{ rejectPromptEcho?: string, usableMinChars?: number, minAlphaRatio?: number }} [o]
   */
  function getLatestAssistantMessageElement(o) {
    const opts = o || {};
    const minU = opts.usableMinChars != null ? opts.usableMinChars : 25;
    const nodes = Array.from(
      document.querySelectorAll(
        "[data-message-author-role='assistant'], [data-testid*='conversation-turn-assistant'], article[data-testid*='conversation-turn']"
      )
    );
    for (let i = nodes.length - 1; i >= 0; i -= 1) {
      const el = nodes[i];
      if (!(el instanceof HTMLElement)) continue;
      if (!isElementVisible(el)) continue;
      const raw = sanitizeAssistantText(extractAssistantTurnText(el));
      const text = applyRejectEcho(raw, opts.rejectPromptEcho || "");
      if (isUsableAssistantText(text, { minLen: minU, minAlphaRatio: opts.minAlphaRatio })) return el;
    }
    return null;
  }

  function getLastAssistantElement() {
    const el = getLatestAssistantMessageElement({ usableMinChars: 12, minAlphaRatio: 0.08 });
    if (el) return el;
    const messages = getAssistantMessageList();
    if (!messages.length) return null;
    return messages[messages.length - 1];
  }

  function extractAssistantTurnText(el) {
    if (!el) return "";
    const md = el.querySelector(".markdown, [class*='markdown-new-styling'], [class*='prose']");
    const fromMd = md ? (md.innerText || "").trim() : "";
    const fromWhole = (el.innerText || "").trim();
    // Keep markdown quality when available, but prefer the full turn text when it is richer
    // (cards/boxes/buttons/tool outputs often live outside the markdown subtree).
    if (!fromMd) return fromWhole;
    if (!fromWhole) return fromMd;
    return fromWhole.length >= fromMd.length ? fromWhole : fromMd;
  }

  function applyRejectEcho(text, promptEcho) {
    const reject = (promptEcho || "").trim();
    const t = (text || "").trim();
    if (reject.length >= 10 && t.length > 0 && t === reject) {
      return "";
    }
    const rejectHead = reject.slice(0, 180);
    if (rejectHead.length >= 50 && t.length >= rejectHead.length - 10 && t.slice(0, rejectHead.length) === rejectHead) {
      return "";
    }
    return t;
  }

  /** True while the model is streaming (Stop control visible). Narrow selectors — avoid generic "Stop" matches. */
  function isStopGeneratingControlVisible() {
    return !!(
      document.querySelector('[data-testid="stop-button"]') ||
      document.querySelector('button[aria-label="Stop generating"]') ||
      document.querySelector('button[aria-label*="Stop generating" i]')
    );
  }

  function isAssistantLikelyGenerating() {
    return isStopGeneratingControlVisible();
  }

  /**
   * After a turn: Stop is gone and composer is back to idle — "Start Voice" and/or enabled Send
   * in the same bar you described (trailing ms-auto cluster).
   */
  function isComposerIdleAfterAssistantTurn() {
    if (isStopGeneratingControlVisible()) return false;
    const surface = getComposerSurface();
    if (!surface) return true;
    const voice =
      surface.querySelector('button[aria-label="Start Voice"]') ||
      surface.querySelector('button[aria-label^="Start Voice"]');
    if (voice && isElementVisible(voice)) return true;
    const send =
      surface.querySelector("#composer-submit-button[data-testid='send-button']") ||
      surface.querySelector("button#composer-submit-button[data-testid='send-button']") ||
      surface.querySelector("button[data-testid='send-button']");
    if (send && !send.disabled && isElementVisible(send)) return true;
    return false;
  }

  function isGenerating() {
    return isAssistantLikelyGenerating();
  }

  async function waitUntilDone(maxMs) {
    const deadline = Date.now() + (maxMs || 300000);
    while (isAssistantLikelyGenerating() && Date.now() < deadline) {
      await wait(400);
    }
  }

  /**
   * Wait until Stop is gone and the composer shows an enabled Send (generation truly finished).
   * Avoids returning a mid-stream snapshot while the model is still writing.
   */
  async function waitUntilGenerationUiIdle(maxMs) {
    const deadline = Date.now() + (maxMs != null ? maxMs : 180000);
    while (Date.now() < deadline) {
      if (!isStopGeneratingControlVisible() && isComposerIdleAfterAssistantTurn()) {
        await wait(200);
        if (!isStopGeneratingControlVisible() && isComposerIdleAfterAssistantTurn()) {
          return;
        }
      }
      await wait(100);
    }
  }

  function refreshAssistantReplyText(o, usableMin) {
    const el = getLatestAssistantMessageElement({
      rejectPromptEcho: o.rejectPromptEcho || "",
      usableMinChars: usableMin,
      minAlphaRatio: o.minAlphaRatio,
    });
    if (!el) return "";
    const raw = sanitizeAssistantText(extractAssistantTurnText(el));
    return applyRejectEcho(raw, o.rejectPromptEcho || "").trim();
  }

  /**
   * Resume-sender style: MutationObserver + ~700ms polling, finish when text is usable,
   * unchanged for stableElapsedMs, and generation heuristics say idle. Hard wall (default 180s).
   * @param {{ rejectPromptEcho?: string, minLen?: number, captureWallMs?: number, stableElapsedMs?: number, compositePollMs?: number }} [opts]
   */
  async function waitForCompositeAssistantFinish(opts) {
    const o = opts || {};
    const wall = Date.now() + (o.captureWallMs != null ? o.captureWallMs : 180000);
    const stableNeed = o.stableElapsedMs != null ? o.stableElapsedMs : 700;
    const pollMs = o.compositePollMs != null ? o.compositePollMs : 220;
    const usableMin = o.minLen != null ? o.minLen : 25;
    const statusExtra = () => ({
      rejectEcho: o.rejectPromptEcho || "",
      phase: o.statusPhase || "",
      job_id: (o.statusExtras && o.statusExtras.job_id) || "",
      stable_ms: lastText ? Date.now() - stableSince : 0,
      captured_len: lastText.length,
    });

    let lastText = "";
    let stableSince = 0;
    let domDirty = true;

    const observer = new MutationObserver(() => {
      domDirty = true;
    });
    observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true });

    try {
      while (Date.now() < wall) {
        const latest = getLatestAssistantMessageElement({
          rejectPromptEcho: o.rejectPromptEcho,
          usableMinChars: usableMin,
          minAlphaRatio: o.minAlphaRatio,
        });
        if (latest) {
          const raw = sanitizeAssistantText(extractAssistantTurnText(latest));
          const text = applyRejectEcho(raw, o.rejectPromptEcho || "");
          if (isUsableAssistantText(text, { minLen: usableMin, minAlphaRatio: o.minAlphaRatio })) {
            const now = Date.now();
            if (lastText !== text) {
              lastText = text;
              stableSince = now;
            } else if (!isStopGeneratingControlVisible() && now - stableSince >= stableNeed) {
              const idleUi = isComposerIdleAfterAssistantTurn();
              const longSettled = now - stableSince >= stableNeed * 3;
              if (idleUi || longSettled) {
                await waitUntilGenerationUiIdle(180000);
                const refreshed = refreshAssistantReplyText(o, usableMin);
                const out = refreshed.length >= text.length ? refreshed : text;
                await reportGptStatus(
                  "capture_done",
                  {
                    ...statusExtra(),
                    assistant_chars: out.length,
                    preview: out.slice(0, 48).replace(/\s+/g, " ").trim(),
                    stop: false,
                    composer_idle: true,
                  },
                  true
                );
                return out;
              }
            }
          }
        }
        await reportGptStatus("capture", statusExtra(), false);
        const delay = domDirty ? Math.min(120, pollMs) : pollMs;
        domDirty = false;
        await wait(delay);
      }
      await waitUntilGenerationUiIdle(90000);
      const tailFresh = refreshAssistantReplyText(o, usableMin);
      const merged = (tailFresh && tailFresh.length >= (lastText || "").length * 0.7) ? tailFresh : lastText;
      const tail = applyRejectEcho(merged, o.rejectPromptEcho || "").trim();
      await reportGptStatus("capture_wall", { ...statusExtra(), assistant_chars: tail.length, preview: tail.slice(0, 48) }, true);
      return tail;
    } finally {
      try {
        observer.disconnect();
      } catch (_e) {}
    }
  }

  /**
   * Wait until a new assistant turn appears OR generation has visibly started.
   * Call with assistant count captured immediately before clicking Send.
   */
  async function waitForAssistantReplyStarted(baselineCount, timeoutMs) {
    const limit = timeoutMs || 120000;
    const deadline = Date.now() + limit;

    const shouldStop = () => {
      if (Date.now() >= deadline) return true;
      if (isGenerating()) return true;
      if (getAssistantMessageList().length > baselineCount) return true;
      const snap = getLatestAssistantTextSnapshot({
        baselineAssistantCount: baselineCount,
        minRawLen: 1,
      });
      return !!(snap && snap.trim());
    };

    if (shouldStop()) return;

    await new Promise((resolve) => {
      let settled = false;
      const observer = new MutationObserver(tick);
      let poll;
      let to;

      const done = () => {
        if (settled) return;
        settled = true;
        try {
          observer.disconnect();
        } catch (_e) {}
        if (poll) clearInterval(poll);
        if (to) clearTimeout(to);
        resolve();
      };

      function tick() {
        if (shouldStop()) done();
      }

      observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true });
      poll = setInterval(tick, 200);
      to = setTimeout(done, limit);
      tick();
    });
  }

  /**
   * Full pipeline: post-send delay → wait for reply start → composite finish (observer + ~700ms poll).
   */
  async function captureAssistantResponseAfterSend(baselineCount, opts) {
    const o = opts || {};
    const postDelay = o.postSubmitDelayMs != null ? o.postSubmitDelayMs : 350;
    await wait(postDelay);
    await waitForAssistantReplyStarted(baselineCount, o.startTimeoutMs != null ? o.startTimeoutMs : 120000);
    await reportGptStatus(
      "reply_started",
      {
        rejectEcho: o.rejectPromptEcho || "",
        phase: o.statusPhase || "",
        job_id: (o.statusExtras && o.statusExtras.job_id) || "",
        request_id: (o.statusExtras && o.statusExtras.request_id) || "",
      },
      true
    );
    return waitForCompositeAssistantFinish({
      rejectPromptEcho: o.rejectPromptEcho,
      minLen: o.minLen != null ? o.minLen : 25,
      minAlphaRatio: o.minAlphaRatio,
      captureWallMs: o.captureWallMs != null ? o.captureWallMs : 180000,
      stableElapsedMs: o.stableElapsedMs,
      compositePollMs: o.compositePollMs,
      statusPhase: o.statusPhase,
      statusExtras: o.statusExtras,
    });
  }

  async function captureAssistantResponseWithRetries(baselineCount, opts) {
    const o = opts || {};
    const minLen = o.minLen != null ? o.minLen : 25;
    const retries = o.retryAttempts != null ? o.retryAttempts : 3;
    const retryDelay = o.retryDelayMs != null ? o.retryDelayMs : 450;

    let text = (await captureAssistantResponseAfterSend(baselineCount, o)).trim();
    if (text.length >= minLen) return text;

    for (let i = 1; i < retries; i += 1) {
      await wait(retryDelay);
      text = (
        await waitForCompositeAssistantFinish({
          rejectPromptEcho: o.rejectPromptEcho,
          minLen,
          minAlphaRatio: o.minAlphaRatio,
          captureWallMs: o.retryWallMs != null ? o.retryWallMs : 90000,
          stableElapsedMs: o.stableElapsedMs,
          compositePollMs: o.compositePollMs,
          statusPhase: o.statusPhase,
          statusExtras: o.statusExtras,
        })
      ).trim();
      if (text.length >= minLen) return text;
    }
    return text || "";
  }

  /**
   * Snapshot for LIVE relay only: same baseline scoping as getLatestAssistantTextSnapshot, but while
   * Stop/generating is visible we accept very short snippets so pre-results stream from the first token.
   */
  function getLiveRelayAssistantSnapshot(opts) {
    const o = opts || {};
    const b = o.baselineAssistantCount;
    if (typeof b !== "number" || b < 0) {
      return getLatestAssistantTextSnapshot(o);
    }
    const nodes = getAssistantMessageList();
    if (nodes.length <= b) {
      return "";
    }
    const streaming = isStopGeneratingControlVisible();
    const minRaw = streaming ? 1 : o.minRawLen != null ? o.minRawLen : 1;
    for (let i = nodes.length - 1; i >= b; i -= 1) {
      const el = nodes[i];
      if (!(el instanceof HTMLElement)) continue;
      if (!isElementVisible(el)) continue;
      let text = extractAssistantTurnText(el);
      text = applyRejectEcho(text, o.rejectPromptEcho || "");
      const t = text.trim();
      if (!t) continue;
      if (/^(thinking([….]|\.*)?)$/i.test(t)) continue;
      if (t.length < minRaw) continue;
      return text;
    }
    return "";
  }

  /**
   * Non-blocking snapshot (no streaming wait) — last assistant only.
   * When baselineAssistantCount is set, only assistant bubbles at index >= baseline are considered
   * (avoids reading the previous reply or non-output UI while the new turn is still "thinking").
   */
  function getLatestAssistantTextSnapshot(opts) {
    const o = opts || {};
    const minRaw = o.minRawLen != null ? o.minRawLen : 1;

    if (typeof o.baselineAssistantCount === "number" && o.baselineAssistantCount >= 0) {
      const nodes = getAssistantMessageList();
      const b = o.baselineAssistantCount;
      if (nodes.length <= b) {
        return "";
      }
      for (let i = nodes.length - 1; i >= b; i -= 1) {
        const el = nodes[i];
        if (!(el instanceof HTMLElement)) continue;
        if (!isElementVisible(el)) continue;
        let text = extractAssistantTurnText(el);
        text = applyRejectEcho(text, o.rejectPromptEcho);
        if (text.length < minRaw) continue;
        if (/^(thinking([….]|\.*)?)$/i.test(text)) continue;
        return text;
      }
      return "";
    }

    const minUsable = o.usableMinChars != null ? o.usableMinChars : 10;
    const minRatio = o.minAlphaRatio != null ? o.minAlphaRatio : 0.06;
    const el =
      getLatestAssistantMessageElement({
        rejectPromptEcho: o.rejectPromptEcho,
        usableMinChars: minUsable,
        minAlphaRatio: minRatio,
      }) || getLastAssistantElement();
    if (!el) return "";
    let text = extractAssistantTurnText(el);
    text = applyRejectEcho(text, o.rejectPromptEcho);
    if (text.length < minRaw) return "";
    if (/^(thinking([….]|\.*)?)$/i.test(text)) return "";
    return text;
  }

  /** Mirrors bridge_server._derive_gpt_ui_stage for consistent terminal labels. */
  function deriveGptUiStage(o) {
    if (o.stop) return "STREAMING";
    if (o.composer_idle) return "IDLE_AFTER_TURN";
    if (o.send_present && !o.send_disabled) return "READY_TO_SEND";
    const ac = Number(o.assistant_chars) || 0;
    if (ac > 30) return "HAS_ASSISTANT_TEXT";
    return "WAITING_OR_LOADING";
  }

  function collectGptUiSnapshot(extra) {
    const rej = (extra && extra.rejectEcho) || "";
    const surface = getComposerSurface();
    const send = findSendButton();
    const voice =
      surface &&
      (surface.querySelector('button[aria-label="Start Voice"]') ||
        surface.querySelector('button[aria-label^="Start Voice"]'));
    const snap = getLatestAssistantTextSnapshot({ rejectPromptEcho: rej });
    const preview = (snap || "").slice(0, 48).replace(/\s+/g, " ").trim();
    const assistant_chars = (snap || "").length;
    const stop = isStopGeneratingControlVisible();
    const composer_idle = isComposerIdleAfterAssistantTurn();
    const send_present = !!send;
    const send_disabled = !!(send && send.disabled);
    const base = {
      stop,
      composer_idle,
      send_present,
      send_disabled,
      start_voice: !!(voice && isElementVisible(voice)),
      has_surface: !!surface,
      assistant_chars,
      preview,
      ui_stage: deriveGptUiStage({
        stop,
        composer_idle,
        send_present,
        send_disabled,
        assistant_chars,
      }),
    };
    if (extra && typeof extra === "object") {
      const copy = { ...extra };
      delete copy.rejectEcho;
      Object.assign(base, copy);
      if (!copy.ui_stage) {
        base.ui_stage = deriveGptUiStage({
          stop: base.stop,
          composer_idle: base.composer_idle,
          send_present: base.send_present,
          send_disabled: base.send_disabled,
          assistant_chars: base.assistant_chars,
        });
      }
    }
    return base;
  }

  async function reportGptStatus(reason, extra, force) {
    const gap = 900;
    const now = Date.now();
    if (!force && now - lastGptStatusSent < gap) return;
    lastGptStatusSent = now;
    const payload = { reason: String(reason || "tick"), source: "content", ...collectGptUiSnapshot(extra || {}) };
    iaLog("[gpt]", payload.reason, "ui_stage=", payload.ui_stage, {
      job_id: payload.job_id || "",
      stop: payload.stop,
      composer_idle: payload.composer_idle,
      send_disabled: payload.send_disabled,
      assistant_chars: payload.assistant_chars,
      preview: (payload.preview || "").slice(0, 60),
    });
    try {
      const res = await fetchJsonWithTimeout(
        GPT_STATE_ENDPOINT,
        { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) },
        4000
      );
      if (!res.ok && now - lastGptStateFailNotified > 8000) {
        lastGptStateFailNotified = now;
        try {
          await fetchJsonWithTimeout(
            PREP_EXT_STATUS_ENDPOINT,
            {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({
                source: "content",
                event: "gpt_state_http_error",
                detail: `status=${res.status} reason=${String(reason || "").slice(0, 40)}`,
                job_id: payload.job_id || "",
              }),
            },
            3500
          );
        } catch (_e2) {}
      }
    } catch (e) {
      if (now - lastGptStateFailNotified > 8000) {
        lastGptStateFailNotified = now;
        const msg = e && e.message ? String(e.message) : String(e);
        try {
          await fetchJsonWithTimeout(
            PREP_EXT_STATUS_ENDPOINT,
            {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({
                source: "content",
                event: "gpt_state_post_failed",
                detail: msg.slice(0, 140),
                job_id: payload.job_id || "",
              }),
            },
            3500
          );
        } catch (_e2) {}
      }
    }
  }

  function clickNewChat() {
    const selectors = [
      '[data-testid="create-new-chat-button"]',
      'button[aria-label="New chat"]',
      'a[href="/"]'
    ];
    for (let i = 0; i < selectors.length; i += 1) {
      const el = document.querySelector(selectors[i]);
      if (el && el.offsetParent !== null) {
        el.click();
        return true;
      }
    }
    return false;
  }

  function wait(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  /** Yield a few display frames so ChatGPT can paint attachment UI after synthetic drop. */
  function waitAnimationFrames(count) {
    const n = Math.max(1, Math.floor(Number(count) || 2));
    return new Promise((resolve) => {
      let i = 0;
      const step = () => {
        i += 1;
        if (i >= n) {
          resolve();
          return;
        }
        requestAnimationFrame(step);
      };
      requestAnimationFrame(step);
    });
  }

  /** Loopback HTTP from https://chatgpt.com is blocked (Chrome local network access); service worker proxies. */
  function iaBridgeFetch(url, options, timeoutMs) {
    return new Promise((resolve) => {
      chrome.runtime.sendMessage(
        {
          type: "IA_BRIDGE_FETCH",
          url: String(url),
          method: (options && options.method) || "GET",
          headers: (options && options.headers) || {},
          body: options && options.body != null ? String(options.body) : undefined,
          timeoutMs: timeoutMs || 12000,
        },
        (answer) => {
          void chrome.runtime.lastError;
          const textBody = answer && answer.bodyText != null ? String(answer.bodyText) : "";
          resolve({
            ok: !!(answer && answer.ok),
            status: answer && answer.status != null ? Number(answer.status) : 0,
            text: () => Promise.resolve(textBody),
          });
        }
      );
    });
  }

  async function fetchJsonWithTimeout(url, options, timeoutMs) {
    const u = String(url || "");
    if (u.startsWith("http://127.0.0.1:8765/") || u.startsWith("http://localhost:8765/")) {
      return iaBridgeFetch(u, options || {}, timeoutMs);
    }
    const ms = timeoutMs || 12000;
    const ctrl = new AbortController();
    const tid = setTimeout(() => ctrl.abort(), ms);
    try {
      return await fetch(url, { ...options, signal: ctrl.signal });
    } finally {
      clearTimeout(tid);
    }
  }

  function readClientIdFromStorage() {
    return new Promise((resolve) => {
      try {
        chrome.storage.local.get(["clientId"], (d) => {
          resolve(String((d && d.clientId) || "").trim());
        });
      } catch (_e) {
        resolve("");
      }
    });
  }

  async function postPrepComplete(jobId, result, error) {
    try {
      const cid = await readClientIdFromStorage();
      const res = await fetchJsonWithTimeout(
        PREP_COMPLETE_ENDPOINT,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            job_id: jobId,
            result: result || "",
            error: error || "",
            client_id: cid,
          }),
        },
        12000
      );
      if (!res.ok) {
        console.warn("[Interview Assistant] prep/complete HTTP", res.status, await res.text().catch(() => ""));
      }
    } catch (e) {
      console.warn("[Interview Assistant] prep/complete failed", e && e.message ? e.message : e);
    }
  }

  async function postAnswer(requestId, answer) {
    if (!requestId || !answer) return;
    if (latestSentByRequest.get(requestId) === answer) return;
    latestSentByRequest.set(requestId, answer);

    try {
      await fetchJsonWithTimeout(
        ANSWER_ENDPOINT,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            request_id: requestId,
            answer
          })
        },
        12000
      );
    } catch (_err) {
      // Best effort; ignore network errors.
    }
  }

  function watchAndReportAnswer(requestId, baselineAssistantCount, rejectEcho) {
    const baseline =
      typeof baselineAssistantCount === "number"
        ? baselineAssistantCount
        : getAssistantMessageList().length;
    activeInterviewRequestId = String(requestId || "").trim();
    activeBaselineAssistantCount = baseline;
    runLiveAnswerRelayTicker(requestId, rejectEcho || "", undefined, undefined, baseline);
    (async () => {
      try {
        const answer = await captureAssistantResponseWithRetries(baseline, {
          rejectPromptEcho: rejectEcho || "",
          minLen: 1,
          retryAttempts: 3,
          retryDelayMs: 400,
          statusPhase: "insert_prompt",
          statusExtras: { request_id: requestId },
        });
        if (answer) {
          await postAnswer(requestId, answer);
        }
      } finally {
        clearWsStreamWatch();
      }
    })();
  }

  /** Interview WebSocket runs in the background service worker; we only send frames over runtime messaging. */
  let wsStreamTimer = null;

  function wsSend(obj) {
    try {
      chrome.runtime.sendMessage({ type: "IA_INTERVIEW_WS_SEND", payload: obj }, () => {
        void chrome.runtime.lastError;
      });
    } catch (_e) {}
  }

  function clearWsStreamWatch() {
    if (wsStreamTimer) {
      clearInterval(wsStreamTimer);
      wsStreamTimer = null;
    }
  }

  /** Poll DOM every tickMs; emit LIVE_ANSWER at most once per minEmitMs while text grows (WS or HTTP relay to live.py). */
  function runLiveAnswerRelayTicker(requestId, promptEcho, tickMs, minEmitMs, baselineAssistantCount) {
    const tick = tickMs != null ? tickMs : 100;
    const minGap = minEmitMs != null ? minEmitMs : 100;
    clearWsStreamWatch();
    let lastSentLive = 0;
    let lastLiveSnap = "";
    wsStreamTimer = setInterval(() => {
      if (!requestId) return;
      if (activeInterviewRequestId && requestId !== activeInterviewRequestId) return;
      const now = Date.now();
      const snap = getLiveRelayAssistantSnapshot({
        rejectPromptEcho: promptEcho || "",
        minRawLen: 4,
        baselineAssistantCount:
          typeof baselineAssistantCount === "number" && baselineAssistantCount >= 0
            ? baselineAssistantCount
            : undefined,
      });
      if (snap && snap !== lastLiveSnap && now - lastSentLive >= minGap) {
        lastLiveSnap = snap;
        lastSentLive = now;
        console.log("[Interview Assistant] LIVE_ANSWER pre-result", {
          request_id: requestId,
          length: snap.length,
        });
        if (!activeInterviewRequestId || requestId === activeInterviewRequestId) {
          wsSend({ type: "LIVE_ANSWER", request_id: requestId, text: snap });
        }
      }
    }, tick);
  }

  function startWsResponseWatch(requestId, baselineAssistantCount, promptEcho) {
    const baseline =
      typeof baselineAssistantCount === "number"
        ? baselineAssistantCount
        : getAssistantMessageList().length;
    activeInterviewRequestId = String(requestId || "").trim();
    activeBaselineAssistantCount = baseline;
    runLiveAnswerRelayTicker(requestId, promptEcho, undefined, undefined, baseline);

    (async () => {
      try {
        const answer = await captureAssistantResponseWithRetries(baseline, {
          rejectPromptEcho: promptEcho || "",
          minLen: 1,
          retryAttempts: 3,
          retryDelayMs: 180,
          stableElapsedMs: 450,
          compositePollMs: 120,
          statusPhase: "ws_interview",
          statusExtras: { request_id: requestId },
        });
        clearWsStreamWatch();
        if (answer && (!activeInterviewRequestId || requestId === activeInterviewRequestId)) {
          wsSend({ type: "FINAL_ANSWER", request_id: requestId, answer });
        } else {
          wsSend({ type: "STATUS", level: "error", message: "no_assistant_reply" });
        }
      } catch (_e) {
        clearWsStreamWatch();
        wsSend({ type: "STATUS", level: "error", message: "capture_failed" });
      }
    })();
  }

  async function resolveInitialPromptFromStorage() {
    return new Promise((resolve) => {
      chrome.storage.local.get(["resumeSummary", "jdSummary", "clientLabel"], (data) => {
        const rs = (data.resumeSummary || "").trim();
        const jd = (data.jdSummary || "").trim();
        const label = (data.clientLabel || "").trim();
        if (!rs && !jd) {
          resolve("");
          return;
        }
        resolve(
          `Interview assist (${label || "candidate"}).\nResume summary:\n${rs}\n\nJD summary:\n${jd}\n\nReply with one short ready sentence, then stop.`
        );
      });
    });
  }

  async function handleWsInterviewMessage(msg) {
    let prompt = (msg.prompt || "").trim();
    const requestId = (msg.request_id || "").trim();
    const mtype = msg.type || "";
    const imgB64 = String(msg.image_png_base64 || "").trim();
    if (!requestId) return;
    if (mtype === "INITIAL_PROMPT" && !prompt) {
      prompt = await resolveInitialPromptFromStorage();
    }
    if (!prompt && !imgB64) {
      wsSend({ type: "STATUS", level: "error", message: "No prompt (paste resume/JD in extension or use Python-built prompt)." });
      return;
    }
    const composer = await waitForComposer(15000);
    if (!composer) {
      wsSend({ type: "STATUS", level: "error", message: "composer_not_found" });
      return;
    }
    if (prompt) {
      if (!setInputValue(composer, prompt)) {
        wsSend({ type: "STATUS", level: "error", message: "could_not_insert" });
        return;
      }
    } else if (imgB64) {
      if (!setInputValue(composer, " ")) {
        wsSend({ type: "STATUS", level: "error", message: "could_not_insert" });
        return;
      }
    }
    const surf = getComposerSurface();
    const attachRoot = surf instanceof HTMLElement ? surf : composer;

    if (imgB64) {
      await wait(220);
      await waitAnimationFrames(3);
      tryAttachPngToComposer(composer, imgB64);
      await waitAnimationFrames(3);
      let chip = await waitForComposerAttachment(attachRoot, 8000);
      if (!chip) {
        iaLog("[ws] attachment chip not seen; retry surface drop");
        await wait(350);
        tryAttachPngToComposer(composer, imgB64);
        await waitAnimationFrames(3);
        chip = await waitForComposerAttachment(attachRoot, 6000);
      }
      if (
        !chip &&
        surf instanceof HTMLElement &&
        composer instanceof HTMLElement &&
        !surf.isSameNode(composer)
      ) {
        iaLog("[ws] try inner composer drop once");
        tryAttachPngToInnerComposer(composer, imgB64);
        await waitAnimationFrames(3);
        await waitForComposerAttachment(attachRoot, 6000);
      }
      await wait(180);
      dismissComposerDragDropOverlay(attachRoot, composer);
      await waitAnimationFrames(2);
    }
    const baselineAi = getAssistantMessageList().length;
    await waitAnimationFrames(2);
    await wait(imgB64 ? 120 : 200);
    if (imgB64) {
      await trySubmitWithRetryAfterAttachment(22000, attachRoot, true, composer);
    } else {
      await trySubmitWithRetry(15000);
    }
    await wait(350);
    const rejectEcho = prompt || "(screenshot)";
    await reportGptStatus(
      "ws_sent",
      { phase: "ws_interview", request_id: requestId, rejectEcho },
      true
    );
    startWsResponseWatch(requestId, baselineAi, rejectEcho);
  }

  async function runPrepJob(message) {
    prepCaptureBusy = true;
    const jobId = message.jobId || "";
    const prompt = message.prompt || "";
    iaLog("[prep] RUN_PREP_JOB start", { jobId, phase: message.phase, promptChars: (prompt || "").length });
    try {
      if (message.newChat) {
        iaLog("[prep] new chat click + wait");
        clickNewChat();
        await wait(2200);
      }

      const composer = await waitForComposer(22000);
      if (!composer) {
        iaLog("[prep] FAIL composer_not_found");
        await postPrepComplete(jobId, "", "composer_not_found");
        return { ok: false, error: "composer_not_found" };
      }

      const baselineAi = getAssistantMessageList().length;

      const ok = setInputValue(composer, prompt);
      if (!ok) {
        iaLog("[prep] FAIL could_not_insert");
        await postPrepComplete(jobId, "", "could_not_insert");
        return { ok: false, error: "could_not_insert" };
      }

      await wait(280);
      const sent = await trySubmitWithRetry(20000);
      if (!sent) {
        iaLog("[prep] FAIL send_button_disabled");
        await postPrepComplete(jobId, "", "send_button_disabled");
        return { ok: false, error: "send_button_disabled" };
      }

      const prepPhase = String(message.phase || "prep_capture").replace(/[^a-z0-9_-]/gi, "_");
      iaLog("[prep] submitted; capturing assistant reply", { prepPhase, jobId });
      await reportGptStatus("prep_sent", { phase: prepPhase, job_id: jobId, rejectEcho: prompt }, true);
      if (prepPhase === "interview_gpt_setup") {
        // Step 4 only: do not wait for assistant capture.
        await postPrepComplete(jobId, "", "");
        return { ok: true };
      }

      // Step 2/3: keep capturing result text.
      const answer = await captureAssistantResponseWithRetries(baselineAi, {
        rejectPromptEcho: prompt,
        minLen: 1,
        retryAttempts: 3,
        retryDelayMs: 450,
        postSubmitDelayMs: 350,
        genTimeoutMs: 300000,
        startTimeoutMs: 120000,
        statusPhase: prepPhase,
        statusExtras: { job_id: jobId },
      });
      await postPrepComplete(jobId, answer || "", "");
      return { ok: true };
    } catch (err) {
      iaLog("[prep] FAIL exception", err && err.message ? err.message : err);
      await postPrepComplete(jobId, "", String(err && err.message ? err.message : err));
      return { ok: false, error: String(err) };
    } finally {
      prepCaptureBusy = false;
    }
  }

  function getManualLatestAssistantText() {
    // For manual mirroring, anchor to DOM order (latest turn), not visibility.
    // ChatGPT virtualized viewport/scroll can hide the newest bubble and make visible-only
    // selectors jump back to an older answer.
    const nodes = getAssistantMessageList();
    for (let i = nodes.length - 1; i >= 0; i -= 1) {
      const el = nodes[i];
      if (!(el instanceof HTMLElement)) continue;
      const t = sanitizeAssistantText(extractAssistantTurnText(el)).trim();
      if (!t) continue;
      if (/^(thinking([….]|\.*)?)$/i.test(t)) continue;
      return t;
    }
    return "";
  }

  function manualCaptureAllowed() {
    if (prepCaptureBusy) return false;
    if (wsStreamTimer) return false;
    return true;
  }

  function pulseManualGptTabRelay() {
    if (!manualCaptureAllowed() || !cachedClientId) return;
    const trimmed = getManualLatestAssistantText();
    const now = Date.now();
    if (isStopGeneratingControlVisible()) {
      lastManualFinalPayload = "";
    }
    if (trimmed && trimmed !== lastManualLivePayload && now - lastManualLiveEmitAt >= 400) {
      lastManualLiveEmitAt = now;
      lastManualLivePayload = trimmed;
      wsSend({ type: "MANUAL_GPT_LIVE", request_id: MANUAL_GPT_RID, text: trimmed });
    }
    if (trimmed !== manualStableText) {
      manualStableText = trimmed;
      manualStableSince = now;
    }
    const thinking = /^(thinking([….]|\.*)?)$/i.test(trimmed);
    if (
      trimmed &&
      !thinking &&
      !isStopGeneratingControlVisible() &&
      isComposerIdleAfterAssistantTurn() &&
      now - manualStableSince >= 2000 &&
      trimmed === manualStableText &&
      trimmed !== lastManualFinalPayload
    ) {
      lastManualFinalPayload = trimmed;
      wsSend({ type: "MANUAL_GPT_FINAL", request_id: MANUAL_GPT_RID, answer: trimmed });
    }
  }

  function startManualGptTabWatcher() {
    if (!cachedClientId) return;
    if (manualGptInterval) {
      clearInterval(manualGptInterval);
      manualGptInterval = null;
    }
    if (manualGptObserver) {
      try {
        manualGptObserver.disconnect();
      } catch (_e) {}
      manualGptObserver = null;
    }
    manualGptObserver = new MutationObserver(() => pulseManualGptTabRelay());
    try {
      manualGptObserver.observe(document.body, {
        subtree: true,
        childList: true,
        characterData: true,
        attributes: true,
      });
    } catch (_e) {}
    manualGptInterval = setInterval(pulseManualGptTabRelay, 400);
  }

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type === "IA_INTERVIEW_WS_PUSH") {
      const msg = message.payload;
      if (msg && (msg.type === "INITIAL_PROMPT" || msg.type === "INTERVIEWER_CHUNK")) {
        iaLog("interview WS (background) →", msg.type);
        handleWsInterviewMessage(msg).catch(() => {});
      }
      sendResponse({ ok: true });
      return false;
    }
    if (message?.type === "IA_INTERVIEW_WS_DISCONNECTED") {
      clearWsStreamWatch();
      iaLog("interview WebSocket disconnected (background); cleared live stream timer");
      sendResponse({ ok: true });
      return false;
    }
    if (message?.type === "RUN_PREP_JOB") {
      runPrepJob(message).then(sendResponse);
      return true;
    }

    if (message?.type !== "INSERT_PROMPT") return false;

    clearWsStreamWatch();

    (async () => {
      const composer = await waitForComposer(15000);
      if (!composer) {
        sendResponse({ ok: false, error: "Composer not found" });
        return;
      }
      const baselineAi = getAssistantMessageList().length;
      const ok = setInputValue(composer, message.prompt || "");
      if (!ok) {
        sendResponse({ ok: false, error: "Could not insert prompt" });
        return;
      }
      if (message.autoSubmit) {
        await wait(200);
        await trySubmitWithRetry(15000);
        await reportGptStatus(
          "insert_sent",
          {
            phase: "insert_prompt",
            request_id: message.requestId || "",
            rejectEcho: message.prompt || "",
          },
          true
        );
        watchAndReportAnswer(message.requestId || "", baselineAi, message.prompt || "");
      }
      sendResponse({ ok: true, requestId: message.requestId || "" });
    })();

    return true;
  });

  try {
    chrome.storage.local.get(["clientId"], (d) => {
      cachedClientId = String((d && d.clientId) || "").trim();
      if (cachedClientId) startManualGptTabWatcher();
    });
  } catch (_e) {}
  try {
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area !== "local" || !changes.clientId) return;
      const nv = changes.clientId.newValue;
      cachedClientId = nv != null ? String(nv).trim() : "";
      if (cachedClientId) startManualGptTabWatcher();
    });
  } catch (_e) {}

})();
