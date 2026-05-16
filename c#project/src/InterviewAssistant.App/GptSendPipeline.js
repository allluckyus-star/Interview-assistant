/* Injected into ChatGPT WebView2. Send button uses __iaWpfSendOnly (no answer wait). */
function __iaWait(ms) {
  return new Promise(function (r) {
    setTimeout(r, ms);
  });
}

function __iaIsComposerVisible(el) {
  if (!el || !(el instanceof Element)) return false;
  var st = window.getComputedStyle(el);
  if (st.display === "none" || st.visibility === "hidden" || Number(st.opacity) === 0) return false;
  var r = el.getBoundingClientRect();
  return r.width > 2 && r.height > 2;
}

function __iaGetComposerSurface() {
  return document.querySelector('[data-composer-surface="true"]');
}

function __iaFindComposer() {
  var surface = document.querySelector('[data-composer-surface="true"]');
  if (surface) {
    var prefer =
      surface.querySelector('div#prompt-textarea[contenteditable="true"]') ||
      surface.querySelector("div.ProseMirror[contenteditable='true']") ||
      surface.querySelector('[contenteditable="true"][role="textbox"]');
    if (prefer && __iaIsComposerVisible(prefer)) return prefer;
  }
  var byId = document.querySelector('div#prompt-textarea[contenteditable="true"]');
  if (byId && __iaIsComposerVisible(byId)) return byId;
  var textareas = document.querySelectorAll("textarea");
  for (var i = 0; i < textareas.length; i++) {
    var t = textareas[i];
    if (!__iaIsComposerVisible(t)) continue;
    if (
      t.id === "prompt-textarea" ||
      t.name === "prompt-textarea" ||
      /message/i.test(t.getAttribute("placeholder") || "")
    ) {
      return t;
    }
  }
  var legacyTa = document.querySelector("textarea#prompt-textarea");
  if (legacyTa && __iaIsComposerVisible(legacyTa)) return legacyTa;
  var ce = document.querySelector("[contenteditable='true']");
  if (ce && __iaIsComposerVisible(ce)) return ce;
  return null;
}

function __iaEscapeHtml(s) {
  return String(s)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function __iaSetContentEditablePlainText(el, text) {
  var plain = String(text ?? "");
  el.focus();
  try {
    var sel = window.getSelection();
    var range = document.createRange();
    range.selectNodeContents(el);
    sel.removeAllRanges();
    sel.addRange(range);
    if (document.execCommand("insertText", false, plain)) {
      el.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertText" }));
      return true;
    }
  } catch (_e) {}
  var parts = plain.split("\n");
  var inner = parts
    .map(function (line) {
      var esc = __iaEscapeHtml(line);
      return esc === "" ? "<p><br></p>" : "<p>" + esc + "</p>";
    })
    .join("");
  el.innerHTML = inner || "<p><br></p>";
  el.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertParagraph" }));
  return true;
}

function __iaSetInputValue(el, text) {
  if (!el) return false;
  if (el.tagName === "TEXTAREA" || el.tagName === "INPUT") {
    el.focus();
    el.value = text;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true }));
    return true;
  }
  if (el.getAttribute("contenteditable") === "true") {
    return __iaSetContentEditablePlainText(el, text);
  }
  return false;
}

function __iaFindSendButton() {
  var surface = __iaGetComposerSurface();
  var scope = surface || document;
  return (
    scope.querySelector("#composer-submit-button[data-testid='send-button']") ||
    scope.querySelector("button#composer-submit-button[data-testid='send-button']") ||
    scope.querySelector("#composer-submit-button") ||
    scope.querySelector("button[data-testid='send-button']") ||
    scope.querySelector('button[aria-label="Send prompt"]') ||
    (document.querySelector("button svg[data-icon='paper-plane']") &&
      document.querySelector("button svg[data-icon='paper-plane']").closest("button"))
  );
}

function __iaTrySubmit() {
  var button = __iaFindSendButton();
  if (button && !button.disabled) {
    button.click();
    return true;
  }
  return false;
}

async function __iaTrySubmitWithRetry(maxMs) {
  var deadline = Date.now() + (maxMs || 15000);
  while (Date.now() < deadline) {
    var button = __iaFindSendButton();
    if (button && !button.disabled) {
      button.click();
      return true;
    }
    await __iaWait(120);
  }
  return false;
}

async function __iaWaitForComposer(maxMs) {
  var deadline = Date.now() + (maxMs || 20000);
  while (Date.now() < deadline) {
    var c = __iaFindComposer();
    if (c) return c;
    await __iaWait(150);
  }
  return null;
}

function __iaGetAssistantMessageList() {
  return document.querySelectorAll('[data-message-author-role="assistant"]');
}

function __iaIsStopGeneratingControlVisible() {
  return !!(
    document.querySelector('[data-testid="stop-button"]') ||
    document.querySelector('button[aria-label="Stop generating"]') ||
    document.querySelector('button[aria-label*="Stop generating" i]') ||
    document.querySelector('button[aria-label*="Stop streaming" i]')
  );
}

function __iaDebugLog(requestId, stage, detail) {
  var payload = {
    type: "ia_send_debug",
    requestId: requestId || "",
    stage: stage || "",
    detail: detail || {},
    t: Date.now(),
  };
  try {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(payload);
    }
  } catch (_e) {}
  try {
    console.log("[InterviewAssistant]", stage, detail);
  } catch (_e2) {}
}

function __iaDescribeSubmitControl(btn) {
  if (!btn) return null;
  return {
    testid: btn.getAttribute("data-testid") || "",
    aria: btn.getAttribute("aria-label") || "",
    disabled: !!btn.disabled,
    visible: __iaIsElementVisible(btn),
  };
}

function __iaGetGenerationUiSnapshot() {
  var send = __iaFindSendButton();
  var submit =
    document.querySelector("#composer-submit-button") ||
    document.querySelector("button#composer-submit-button");
  return {
    stop: __iaIsStopGeneratingControlVisible(),
    assistantCount: __iaGetAssistantMessageList().length,
    send: __iaDescribeSubmitControl(send),
    submit: __iaDescribeSubmitControl(submit),
    latestAnswerLen: (__iaRefreshLatestAnswer("", 1) || "").length,
  };
}

function __iaIsElementVisible(el) {
  if (!el || !(el instanceof HTMLElement)) return false;
  if (el.offsetParent === null && el.getClientRects().length === 0) return false;
  var st = window.getComputedStyle(el);
  if (st.display === "none" || st.visibility === "hidden" || Number(st.opacity) === 0) return false;
  var r = el.getBoundingClientRect();
  return r.width > 1 && r.height > 1;
}

function __iaSanitizeAssistantText(s) {
  return String(s || "")
    .replace(/\r\n/g, "\n")
    .replace(/\u00a0/g, " ")
    .trim();
}

function __iaExtractAssistantTurnText(el) {
  if (!el) return "";
  var md = el.querySelector(".markdown, [class*='markdown-new-styling'], [class*='prose']");
  var fromMd = md ? (md.innerText || "").trim() : "";
  var fromWhole = (el.innerText || "").trim();
  if (!fromMd) return fromWhole;
  if (!fromWhole) return fromMd;
  return fromWhole.length >= fromMd.length ? fromWhole : fromMd;
}

function __iaApplyRejectEcho(text, promptEcho) {
  var reject = (promptEcho || "").trim();
  var t = (text || "").trim();
  if (reject.length >= 10 && t.length > 0 && t === reject) {
    return "";
  }
  var rejectHead = reject.slice(0, 180);
  if (rejectHead.length >= 50 && t.length >= rejectHead.length - 10 && t.slice(0, rejectHead.length) === rejectHead) {
    return "";
  }
  return t;
}

function __iaIsUsableAssistantText(text, minLen) {
  var m = minLen != null ? minLen : 25;
  var t = __iaSanitizeAssistantText(text);
  if (/^(thinking([….]|\.*)?)$/i.test(t)) return false;
  if (t.length < m) return false;
  var letters = t.replace(/[^0-9A-Za-z]+/g, "").length;
  if (letters / Math.max(t.length, 1) < 0.08) return false;
  return true;
}

function __iaRefreshLatestAnswer(rejectEcho, minLen) {
  var nodes = document.querySelectorAll("[data-message-author-role='assistant']");
  for (var i = nodes.length - 1; i >= 0; i--) {
    var el = nodes[i];
    if (!(el instanceof HTMLElement)) continue;
    if (!__iaIsElementVisible(el)) continue;
    var raw = __iaSanitizeAssistantText(__iaExtractAssistantTurnText(el));
    var text = __iaApplyRejectEcho(raw, rejectEcho || "").trim();
    if (__iaIsUsableAssistantText(text, minLen)) return text;
  }
  return "";
}

function __iaIsComposerIdleAfterAssistantTurn() {
  // ChatGPT idle = no Stop control. Composer may show Send OR "Use voice" — not only send-button.
  return !__iaIsStopGeneratingControlVisible();
}

async function __iaWaitUntilGenerationUiIdle(maxMs, requestId) {
  var deadline = Date.now() + (maxMs != null ? maxMs : 180000);
  var stableNeed = 450;
  var stableSince = 0;
  var lastLog = 0;
  __iaDebugLog(requestId, "idle_wait_start", __iaGetGenerationUiSnapshot());
  while (Date.now() < deadline) {
    var snap = __iaGetGenerationUiSnapshot();
    var now = Date.now();
    if (now - lastLog >= 800) {
      __iaDebugLog(requestId, "idle_poll", snap);
      lastLog = now;
    }
    if (__iaIsComposerIdleAfterAssistantTurn()) {
      if (!stableSince) stableSince = now;
      if (now - stableSince >= stableNeed) {
        __iaDebugLog(requestId, "idle_ok", snap);
        return;
      }
    } else {
      stableSince = 0;
    }
    await __iaWait(120);
  }
  __iaDebugLog(requestId, "idle_timeout", __iaGetGenerationUiSnapshot());
}

async function __iaWaitForAssistantReplyStarted(baselineCount, timeoutMs, requestId) {
  var limit = timeoutMs || 120000;
  var deadline = Date.now() + limit;
  var lastLog = 0;
  __iaDebugLog(requestId, "reply_wait_start", { baseline: baselineCount });
  var shouldStop = function () {
    if (Date.now() >= deadline) return true;
    if (__iaIsStopGeneratingControlVisible()) return true;
    if (__iaGetAssistantMessageList().length > baselineCount) return true;
    if ((__iaRefreshLatestAnswer("", 1) || "").length > 0) return true;
    return false;
  };
  if (shouldStop()) {
    __iaDebugLog(requestId, "reply_wait_done_immediate", __iaGetGenerationUiSnapshot());
    return;
  }
  await new Promise(function (resolve) {
    var settled = false;
    var observer = new MutationObserver(function () {
      if (shouldStop()) done();
    });
    function done() {
      if (settled) return;
      settled = true;
      try {
        observer.disconnect();
      } catch (_e) {}
      if (poll) clearInterval(poll);
      if (to) clearTimeout(to);
      resolve();
    }
    var poll = setInterval(function () {
      var now = Date.now();
      if (now - lastLog >= 800) {
        __iaDebugLog(requestId, "reply_poll", __iaGetGenerationUiSnapshot());
        lastLog = now;
      }
      if (shouldStop()) done();
    }, 200);
    var to = setTimeout(done, limit);
    try {
      observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true });
    } catch (_e) {
      done();
    }
  });
  __iaDebugLog(requestId, "reply_wait_done", __iaGetGenerationUiSnapshot());
}

async function __iaWaitForCompositeFinish(rejectEcho, minLen) {
  var wall = Date.now() + 300000;
  var stableNeed = 700;
  var lastText = "";
  var stableSince = 0;
  var usableMin = minLen != null ? minLen : 1;
  while (Date.now() < wall) {
    var text = __iaRefreshLatestAnswer(rejectEcho, usableMin);
    if (text) {
      var now = Date.now();
      if (lastText !== text) {
        lastText = text;
        stableSince = now;
      } else if (!__iaIsStopGeneratingControlVisible() && now - stableSince >= stableNeed) {
        var idleUi = __iaIsComposerIdleAfterAssistantTurn();
        if (idleUi || now - stableSince >= stableNeed * 3) {
          await __iaWaitUntilGenerationUiIdle(120000);
          var refreshed = __iaRefreshLatestAnswer(rejectEcho, usableMin);
          return refreshed.length >= text.length ? refreshed : text;
        }
      }
    }
    await __iaWait(220);
  }
  await __iaWaitUntilGenerationUiIdle(90000);
  return __iaRefreshLatestAnswer(rejectEcho, usableMin) || lastText || "";
}

async function __iaCaptureAfterSend(baselineCount, rejectEcho, minLen) {
  await __iaWait(350);
  await __iaWaitForAssistantReplyStarted(baselineCount, 120000);
  return __iaWaitForCompositeFinish(rejectEcho, minLen);
}

function __iaBase64ToUint8Array(b64) {
  var bin = atob(String(b64 || ""));
  var len = bin.length;
  var out = new Uint8Array(len);
  for (var i = 0; i < len; i += 1) out[i] = bin.charCodeAt(i);
  return out;
}

function __iaDismissComposerDragDropOverlay(primary, partner) {
  var emptyDt = new DataTransfer();
  var leaveOpts = { bubbles: true, composed: true, cancelable: false, dataTransfer: emptyDt };
  var nodes = [];
  if (primary instanceof HTMLElement) nodes.push(primary);
  if (partner instanceof HTMLElement && partner !== primary) nodes.push(partner);
  var surf = __iaGetComposerSurface();
  if (surf instanceof HTMLElement && nodes.indexOf(surf) < 0) nodes.push(surf);
  var ta = document.querySelector('div#prompt-textarea[contenteditable="true"]');
  if (!ta) ta = document.querySelector('div#prompt-textarea[contenteditable="true"]');
  if (ta instanceof HTMLElement && nodes.indexOf(ta) < 0) nodes.push(ta);
  for (var i = 0; i < nodes.length; i += 1) {
    try {
      nodes[i].dispatchEvent(new DragEvent("dragleave", leaveOpts));
    } catch (_e) {}
    try {
      nodes[i].dispatchEvent(new DragEvent("dragexit", leaveOpts));
    } catch (_e) {}
  }
  try {
    window.dispatchEvent(new DragEvent("dragend", leaveOpts));
  } catch (_e2) {}
}

function __iaTryAttachFileToElement(el, file, partnerForCleanup) {
  if (!(el instanceof HTMLElement) || !file) return false;
  var dt = new DataTransfer();
  try {
    dt.items.add(file);
  } catch (_e2) {
    return false;
  }
  var opts = { bubbles: true, composed: true, cancelable: true, dataTransfer: dt };
  var ok = false;
  try {
    try {
      el.focus({ preventScroll: true });
    } catch (_e3) {
      try {
        el.focus();
      } catch (_e4) {}
    }
    el.dispatchEvent(new DragEvent("dragenter", opts));
    el.dispatchEvent(new DragEvent("dragover", opts));
    el.dispatchEvent(new DragEvent("drop", opts));
    ok = true;
  } catch (_e5) {
    ok = false;
  } finally {
    __iaDismissComposerDragDropOverlay(el, partnerForCleanup);
  }
  return ok;
}

function __iaTryAttachPngDataToElement(el, base64Png, partnerForCleanup) {
  if (!(el instanceof HTMLElement) || !base64Png) return false;
  var trimmed = String(base64Png).trim();
  if (!trimmed) return false;
  var bytes;
  try {
    bytes = __iaBase64ToUint8Array(trimmed);
  } catch (_e) {
    return false;
  }
  var name = "ia-snip-" + Date.now() + ".png";
  var file = new File([bytes], name, { type: "image/png" });
  return __iaTryAttachFileToElement(el, file, partnerForCleanup);
}

function __iaTryAttachPngToComposer(composer, base64Png) {
  if (!composer || !base64Png) return false;
  var surf = __iaGetComposerSurface();
  var el = surf instanceof HTMLElement ? surf : composer;
  var partner = el === composer ? null : composer;
  return __iaTryAttachPngDataToElement(el, base64Png, partner);
}

function __iaTryAttachFileToComposer(composer, file) {
  if (!composer || !file) return false;
  var surf = __iaGetComposerSurface();
  var el = surf instanceof HTMLElement ? surf : composer;
  var partner = el === composer ? null : composer;
  return __iaTryAttachFileToElement(el, file, partner);
}

function __iaTryAttachPngToInnerComposer(composer, base64Png) {
  if (!(composer instanceof HTMLElement) || !base64Png) return false;
  var trimmed = String(base64Png).trim();
  if (!trimmed) return false;
  var bytes;
  try {
    bytes = __iaBase64ToUint8Array(trimmed);
  } catch (_e) {
    return false;
  }
  var file = new File([bytes], "ia-snip-" + Date.now() + ".png", { type: "image/png" });
  return __iaTryAttachFileToInnerComposer(composer, file);
}

function __iaTryAttachFileToInnerComposer(composer, file) {
  if (!(composer instanceof HTMLElement) || !file) return false;
  var surf = __iaGetComposerSurface();
  var partner = surf instanceof HTMLElement && !surf.isSameNode(composer) ? surf : null;
  return __iaTryAttachFileToElement(composer, file, partner);
}

function __iaBasenameHint(p) {
  var s = String(p || "").replace(/\\/g, "/");
  var i = s.lastIndexOf("/");
  return i >= 0 ? s.slice(i + 1) : s;
}

function __iaIsImageFile(file) {
  if (!file) return false;
  var t = String(file.type || "").toLowerCase();
  if (t.indexOf("image/") === 0) return true;
  var n = String(file.name || "").toLowerCase();
  return /\.(png|jpe?g|gif|webp|bmp|heic|heif|svg)$/.test(n);
}

function __iaComposerHasVisibleAttachment(root, fileNameHint) {
  if (!(root instanceof HTMLElement)) return false;
  var hints = [
    '[data-testid*="attachment"]',
    '[data-testid*="file"]',
    '[data-testid*="upload"]',
    'button[aria-label*="Remove"]',
    'button[aria-label*="remove"]',
  ];
  for (var i = 0; i < hints.length; i += 1) {
    try {
      if (root.querySelector(hints[i])) return true;
    } catch (_e) {}
  }
  var imgs = root.querySelectorAll("img");
  for (var j = 0; j < imgs.length; j += 1) {
    var src = (imgs[j].getAttribute("src") || "").trim();
    if (src.indexOf("blob:") === 0 || src.indexOf("data:image") === 0) return true;
  }
  var base = __iaBasenameHint(fileNameHint);
  if (base.length >= 3) {
    try {
      var txt = (root.innerText || root.textContent || "").trim();
      if (txt.indexOf(base) >= 0) return true;
    } catch (_e2) {}
  }
  return false;
}

async function __iaWaitForComposerAttachment(root, maxMs, fileNameHint) {
  var limit = Date.now() + (maxMs || 12000);
  while (Date.now() < limit) {
    if (__iaComposerHasVisibleAttachment(root, fileNameHint)) return true;
    await __iaWait(90);
  }
  return false;
}

async function __iaWaitAnimationFrames(count) {
  var n = Math.max(1, Math.floor(Number(count) || 2));
  return new Promise(function (resolve) {
    var i = 0;
    var step = function () {
      i += 1;
      if (i >= n) resolve();
      else requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  });
}

async function __iaTrySubmitWithRetryAfterAttachment(maxMs, attachRoot, needsAttachment, composerEl) {
  var deadline = Date.now() + (maxMs || 22000);
  var graceAt = deadline - 3500;
  while (Date.now() < deadline) {
    var attached = !needsAttachment || __iaComposerHasVisibleAttachment(attachRoot);
    var button = __iaFindSendButton();
    var force = !!(needsAttachment && !attached && Date.now() >= graceAt);
    if (button && !button.disabled && (attached || force)) {
      button.click();
      __iaDismissComposerDragDropOverlay(attachRoot, composerEl || attachRoot);
      return true;
    }
    await __iaWait(110);
  }
  return false;
}

async function __iaInsertPromptIntoComposer(composer, prompt, appendToExisting) {
  var p = String(prompt ?? "");
  if (!p.trim()) return false;
  if (appendToExisting === false) {
    return __iaSetInputValue(composer, p);
  }
  var existing = (composer.innerText || composer.textContent || composer.value || "").trim();
  if (!existing) {
    return __iaSetInputValue(composer, p);
  }
  if (__iaInsertTextAtComposerEnd(composer, "\n\n" + p)) {
    return true;
  }
  return __iaSetInputValue(composer, existing + "\n\n" + p);
}

async function __iaWpfSendOnly(userPrompt, appendToExisting) {
  if (window.__iaWpfPrepInFlight) {
    return { ok: false, error: "prep_already_in_flight" };
  }
  window.__iaWpfPrepInFlight = true;
  try {
    var prompt = String(userPrompt || "");
    var composer = await __iaWaitForComposer(28000);
    if (!composer) {
      return { ok: false, error: "composer_not_found" };
    }
    if (!(await __iaInsertPromptIntoComposer(composer, prompt, appendToExisting === true))) {
      return { ok: false, error: "could_not_insert" };
    }
    await __iaWait(280);
    var sent = await __iaTrySubmitWithRetry(22000);
    if (!sent) {
      return { ok: false, error: "send_button_disabled" };
    }
    return { ok: true, phase: "sent" };
  } catch (e) {
    return { ok: false, error: String((e && e.message) || e) };
  } finally {
    window.__iaWpfPrepInFlight = false;
  }
}

async function __iaWpfRunSendAndWait(userPrompt, appendToExisting) {
  if (window.__iaWpfPrepInFlight) {
    return { ok: false, error: "prep_already_in_flight" };
  }
  window.__iaWpfPrepInFlight = true;
  try {
    var prompt = String(userPrompt || "");
    var composer = await __iaWaitForComposer(28000);
    if (!composer) {
      return { ok: false, error: "composer_not_found" };
    }
    var baseline = __iaGetAssistantMessageList().length;
    if (!(await __iaInsertPromptIntoComposer(composer, prompt, appendToExisting === true))) {
      return { ok: false, error: "could_not_insert" };
    }
    await __iaWait(280);
    var sent = await __iaTrySubmitWithRetry(22000);
    if (!sent) {
      return { ok: false, error: "send_button_disabled" };
    }
    var answer = (await __iaCaptureAfterSend(baseline, prompt, 1)).trim();
    // Keep the return payload tiny: full "answer" breaks WebView2 JSON / C# parsing for long replies.
    return { ok: true, answer_len: answer.length };
  } catch (e) {
    return { ok: false, error: String((e && e.message) || e) };
  } finally {
    window.__iaWpfPrepInFlight = false;
  }
}

/** Wizard prep: send then advance when ChatGPT UI is idle (stop hidden, composer ready) — not full answer stability wait. */
async function __iaWpfRunSendAndWaitUiIdle(userPrompt, requestId, appendToExisting) {
  if (window.__iaWpfPrepInFlight) {
    return { ok: false, error: "prep_already_in_flight" };
  }
  window.__iaWpfPrepInFlight = true;
  try {
    var prompt = String(userPrompt || "");
    __iaDebugLog(requestId, "send_begin", { promptLen: prompt.length, append: appendToExisting === true });
    var composer = await __iaWaitForComposer(28000);
    if (!composer) {
      __iaDebugLog(requestId, "send_fail", { error: "composer_not_found" });
      return { ok: false, error: "composer_not_found" };
    }
    var baseline = __iaGetAssistantMessageList().length;
    if (!(await __iaInsertPromptIntoComposer(composer, prompt, appendToExisting === true))) {
      __iaDebugLog(requestId, "send_fail", { error: "could_not_insert" });
      return { ok: false, error: "could_not_insert" };
    }
    await __iaWait(280);
    var sent = await __iaTrySubmitWithRetry(22000);
    if (!sent) {
      __iaDebugLog(requestId, "send_fail", __iaGetGenerationUiSnapshot());
      return { ok: false, error: "send_button_disabled" };
    }
    __iaDebugLog(requestId, "send_submitted", __iaGetGenerationUiSnapshot());
    await __iaWait(350);
    await __iaWaitForAssistantReplyStarted(baseline, 120000, requestId);
    await __iaWaitUntilGenerationUiIdle(300000, requestId);
    __iaDebugLog(requestId, "send_complete", __iaGetGenerationUiSnapshot());
    return { ok: true };
  } catch (e) {
    __iaDebugLog(requestId, "send_exception", { error: String((e && e.message) || e) });
    return { ok: false, error: String((e && e.message) || e) };
  } finally {
    window.__iaWpfPrepInFlight = false;
  }
}

/** WPF: read the latest visible assistant turn from the ChatGPT page. */
window.__iaWpfGetLatestAssistantText = function () {
  try {
    return __iaRefreshLatestAnswer("", 1) || "";
  } catch (_e) {
    return "";
  }
};

/** WPF starts prep send via ExecuteScriptAsync; final result arrives through chrome.webview.postMessage after GPT finishes generating. */
window.__iaWpfStartSendOnly = function (requestId, userPrompt, appendToExisting) {
  __iaDebugLog(requestId, "starter_called", { append: appendToExisting === true });
  __iaWpfRunSendAndWaitUiIdle(userPrompt, requestId, appendToExisting === true)
    .then(function (result) {
      var payload = result && typeof result === "object" ? result : { ok: false, error: "empty_result" };
      if (payload.ok) {
        payload.phase = "sent";
      }
      window.chrome.webview.postMessage({
        type: "ia_send_result",
        requestId: requestId,
        result: payload,
      });
    })
    .catch(function (e) {
      window.chrome.webview.postMessage({
        type: "ia_send_result",
        requestId: requestId,
        result: {
          ok: false,
          error: String((e && e.message) || e),
        },
      });
    });

  return { ok: true, phase: "started", requestId: requestId };
};

function __iaClickComposerAttachButton() {
  var btn =
    document.querySelector('button[data-testid="composer-plus-btn"]') ||
    document.querySelector('button[aria-label*="Attach" i]') ||
    document.querySelector('button[aria-label*="Upload" i]') ||
    document.querySelector('button[aria-label*="Add photos" i]');
  if (btn instanceof HTMLElement && __iaIsElementVisible(btn)) {
    btn.click();
    return true;
  }
  return false;
}

/** After + is open, pick a menu row that leads to local file upload (documents + images). */
function __iaTryClickAddPhotosAndFilesOrSimilar() {
  var candidates = document.querySelectorAll('[role="menuitem"], [role="option"], button[type="button"], a[role="menuitem"]');
  for (var i = 0; i < candidates.length; i += 1) {
    var el = candidates[i];
    if (!(el instanceof HTMLElement) || !__iaIsElementVisible(el)) continue;
    var t = (el.textContent || "").replace(/\s+/g, " ").trim();
    if (!t || t.length > 120) continue;
    if (/connect|drive|dropbox|onedrive|link only|paste url|microphone(?!.*file)/i.test(t)) continue;
    if (/\bupload\b|\bfile\b|\bphoto\b|\bimage\b|\battach\b|\bcomputer\b|\bbrowse\b|\bdocument\b/i.test(t)) {
      try {
        el.click();
        return true;
      } catch (_e) {}
    }
  }
  return false;
}

function __iaInputAcceptScore(input, file) {
  var acc = (input.getAttribute("accept") || "").trim().toLowerCase();
  var ftype = (file.type || "").toLowerCase();
  var fname = (file.name || "").toLowerCase();
  var dot = fname.lastIndexOf(".");
  var ext = dot >= 0 ? fname.slice(dot) : "";

  if (!acc) return 80;
  if ((acc === "image/*" || acc.split(",").every(function (p) { return p.trim() === "image/*"; })) && ftype.indexOf("image/") !== 0)
    return -1;

  var score = 10;
  if (acc.indexOf("*/*") >= 0 || acc === "*") score = 90;
  if (acc.indexOf(ftype) >= 0) score += 60;
  if (ext && acc.indexOf(ext) >= 0) score += 50;
  if (ftype.indexOf("text/") === 0 && (acc.indexOf("text") >= 0 || acc.indexOf(".txt") >= 0)) score += 55;
  return score;
}

function __iaTryAttachViaFileInputWithFile(file) {
  if (!file) return false;
  var list = Array.prototype.slice.call(document.querySelectorAll('input[type="file"]'), 0);
  list.sort(function (a, b) {
    return __iaInputAcceptScore(b, file) - __iaInputAcceptScore(a, file);
  });
  for (var i = 0; i < list.length; i += 1) {
    var input = list[i];
    if (!(input instanceof HTMLInputElement)) continue;
    if (__iaInputAcceptScore(input, file) < 0) continue;
    try {
      var dt = new DataTransfer();
      dt.items.add(file);
      input.files = dt.files;
      input.dispatchEvent(new Event("input", { bubbles: true }));
      input.dispatchEvent(new Event("change", { bubbles: true }));
      return true;
    } catch (_e2) {}
  }
  return false;
}

function __iaTryAttachViaFileInput(base64Png) {
  var trimmed = String(base64Png || "").trim();
  if (!trimmed) return false;
  var bytes;
  try {
    bytes = __iaBase64ToUint8Array(trimmed);
  } catch (_e) {
    return false;
  }
  var file = new File([bytes], "ia-snip-" + Date.now() + ".png", { type: "image/png" });
  return __iaTryAttachViaFileInputWithFile(file);
}

/** Attach one File to ChatGPT composer only — does not click Send. */
async function __iaWpfAttachPreparedFileToComposer(file) {
  if (!file) {
    return { ok: false, error: "file_missing" };
  }
  var fileHint = file.name || "";
  var isImg = __iaIsImageFile(file);
  var composer = await __iaWaitForComposer(28000);
  if (!composer) {
    return { ok: false, error: "composer_not_found" };
  }
  try {
    composer.focus({ preventScroll: true });
  } catch (_e) {
    try {
      composer.focus();
    } catch (_e2) {}
  }
  var surf = __iaGetComposerSurface();
  var attachRoot = surf instanceof HTMLElement ? surf : composer;
  var existing =
    (composer.innerText || composer.textContent || composer.value || "").replace(/\s+/g, " ").trim();
  if (!existing) {
    if (!__iaSetInputValue(composer, " ")) {
      return { ok: false, error: "could_not_focus_composer" };
    }
    await __iaWait(160);
  }
  await __iaWait(220);
  await __iaWaitAnimationFrames(3);

  async function tryMenuAndFileInput() {
    if (!__iaClickComposerAttachButton()) return false;
    await __iaWait(360);
    __iaTryClickAddPhotosAndFilesOrSimilar();
    await __iaWait(280);
    if (!__iaTryAttachViaFileInputWithFile(file)) return false;
    await __iaWait(400);
    return true;
  }

  var chip = false;

  // Documents (.txt, etc.): ChatGPT usually needs + → upload menu + real file input (not image-only or drag alone).
  if (!isImg) {
    await tryMenuAndFileInput();
    chip = await __iaWaitForComposerAttachment(attachRoot, 11000, fileHint);
  }

  if (!chip) {
    var dropped = __iaTryAttachFileToComposer(composer, file);
    await __iaWaitAnimationFrames(3);
    chip = await __iaWaitForComposerAttachment(attachRoot, 8000, fileHint);
    if (!chip) {
      await __iaWait(350);
      dropped = __iaTryAttachFileToComposer(composer, file) || dropped;
      await __iaWaitAnimationFrames(3);
      chip = await __iaWaitForComposerAttachment(attachRoot, 6000, fileHint);
    }
  }

  if (!chip && surf instanceof HTMLElement && composer instanceof HTMLElement && !surf.isSameNode(composer)) {
    __iaTryAttachFileToInnerComposer(composer, file);
    await __iaWaitAnimationFrames(3);
    chip = await __iaWaitForComposerAttachment(attachRoot, 6000, fileHint);
  }

  if (!chip) {
    await tryMenuAndFileInput();
    chip = await __iaWaitForComposerAttachment(attachRoot, 10000, fileHint);
  }

  await __iaWait(120);
  __iaDismissComposerDragDropOverlay(attachRoot, composer);
  await __iaWaitAnimationFrames(2);
  try {
    composer.focus();
  } catch (_e3) {}

  if (__iaComposerHasVisibleAttachment(attachRoot, fileHint)) {
    return { ok: true, phase: "attached" };
  }
  return { ok: false, error: "attachment_not_visible" };
}

/** Attach PNG to ChatGPT composer only — does not click Send. */
async function __iaWpfAttachPngToComposer(imagePngBase64) {
  var imgB64 = String(imagePngBase64 || "").trim();
  if (!imgB64) {
    return { ok: false, error: "image_missing" };
  }
  var bytes;
  try {
    bytes = __iaBase64ToUint8Array(imgB64);
  } catch (_e0) {
    return { ok: false, error: "image_missing" };
  }
  var pngFile = new File([bytes], "ia-snip-" + Date.now() + ".png", { type: "image/png" });
  return __iaWpfAttachPreparedFileToComposer(pngFile);
}

window.__iaWpfAttachBinaryFileToComposer = function (base64, fileName, mimeType) {
  try {
    var trimmed = String(base64 || "").trim();
    if (!trimmed) return Promise.resolve({ ok: false, error: "file_missing" });
    var bytes = __iaBase64ToUint8Array(trimmed);
    var name = String(fileName || "attachment.txt").trim() || "attachment.txt";
    var mime = String(mimeType || "text/plain").trim() || "text/plain";
    var f = new File([bytes], name, { type: mime });
    return __iaWpfAttachPreparedFileToComposer(f);
  } catch (e) {
    return Promise.resolve({ ok: false, error: String((e && e.message) || e) });
  }
};

/** Load bytes from WebView2 virtual-host mapped folder (https://ia-export-host/...) then attach — avoids huge host JSON. */
window.__iaWpfAttachFileFromVirtualUrl = async function (fileUrl, fileName, mimeType) {
  try {
    var url = String(fileUrl || "").trim();
    if (!url) return { ok: false, error: "url_missing" };
    var name = String(fileName || "snapshot.txt").trim() || "snapshot.txt";
    var mime = String(mimeType || "text/plain").trim() || "text/plain";
    var r = await fetch(url, { credentials: "omit", cache: "no-store" });
    if (!r.ok) return { ok: false, error: "fetch_failed_" + r.status };
    var buf = await r.arrayBuffer();
    var f = new File([buf], name, { type: mime });
    return await __iaWpfAttachPreparedFileToComposer(f);
  } catch (e) {
    return { ok: false, error: String((e && e.message) || e) };
  }
};

window.__iaWpfAttachImageToComposer = function (imagePngBase64) {
  return __iaWpfAttachPngToComposer(imagePngBase64);
};

function __iaInsertTextAtComposerEnd(composer, chunk) {
  if (!(composer instanceof HTMLElement) || !chunk) return false;
  try {
    composer.focus({ preventScroll: true });
  } catch (_e) {
    try {
      composer.focus();
    } catch (_e2) {}
  }
  try {
    var sel = window.getSelection();
    if (!sel) return false;
    var range = document.createRange();
    range.selectNodeContents(composer);
    range.collapse(false);
    sel.removeAllRanges();
    sel.addRange(range);
    if (document.execCommand("insertText", false, chunk)) {
      composer.dispatchEvent(
        new InputEvent("input", { bubbles: true, composed: true, inputType: "insertText" })
      );
      return true;
    }
  } catch (_e3) {}
  return false;
}

async function __iaPasteTextToComposerCore(text, append) {
  var t = String(text ?? "");
  if (!t.trim()) {
    return { ok: false, error: "text_missing" };
  }
  var composer = await __iaWaitForComposer(28000);
  if (!composer) {
    return { ok: false, error: "composer_not_found" };
  }
  var surf = __iaGetComposerSurface();
  if (surf instanceof HTMLElement) {
    try {
      surf.click();
    } catch (_e) {}
  }
  try {
    composer.focus({ preventScroll: true });
  } catch (_e2) {
    try {
      composer.focus();
    } catch (_e3) {}
  }
  await __iaWait(120);
  var existing = (composer.innerText || composer.textContent || composer.value || "").trim();
  if (append !== false && existing) {
    if (__iaInsertTextAtComposerEnd(composer, "\n\n" + t)) {
      return { ok: true, phase: "pasted" };
    }
    t = existing + "\n\n" + t;
  }
  if (__iaSetInputValue(composer, t)) {
    await __iaWait(80);
    return { ok: true, phase: "pasted" };
  }
  if (__iaInsertTextAtComposerEnd(composer, t)) {
    return { ok: true, phase: "pasted" };
  }
  return { ok: false, error: "could_not_insert" };
}

function __iaNormalizeMatchText(s) {
  return String(s ?? "")
    .replace(/\s+/g, " ")
    .trim()
    .toLowerCase();
}

function __iaComposerContainsNeedle(needle) {
  var n = __iaNormalizeMatchText(needle);
  if (!n) return false;
  var composer = __iaFindComposer();
  if (!composer) return false;
  var body = __iaNormalizeMatchText(
    composer.innerText || composer.textContent || composer.value || ""
  );
  if (!body) return false;
  if (body.indexOf(n) >= 0) return true;
  var probe = n.length > 80 ? n.slice(0, 80) : n;
  return body.indexOf(probe) >= 0;
}

window.__iaWpfComposerContainsNeedle = __iaComposerContainsNeedle;
window.__iaWpfPasteTextToComposer = function (text, append) {
  return __iaPasteTextToComposerCore(text, append);
};

(function __iaInstallWebViewMessageBridge() {
  if (window.__iaWpfMessageBridgeInstalled) return;
  if (!window.chrome || !window.chrome.webview || !window.chrome.webview.addEventListener) return;
  window.__iaWpfMessageBridgeInstalled = true;
  window.chrome.webview.addEventListener("message", function (event) {
    var msg = event.data;
    if (typeof msg === "string") {
      try {
        msg = JSON.parse(msg);
      } catch (_e) {
        return;
      }
    }
    if (!msg || !msg.type) return;
    var requestId = msg.requestId || "";
    if (msg.type === "ia_attach_image") {
      __iaWpfAttachPngToComposer(msg.imagePngBase64 || "")
        .then(function (result) {
          window.chrome.webview.postMessage({
            type: "ia_attach_result",
            requestId: requestId,
            result: result && typeof result === "object" ? result : { ok: false, error: "empty_result" },
          });
        })
        .catch(function (e) {
          window.chrome.webview.postMessage({
            type: "ia_attach_result",
            requestId: requestId,
            result: { ok: false, error: String((e && e.message) || e) },
          });
        });
      return;
    }
    if (msg.type === "ia_attach_file") {
      window
        .__iaWpfAttachBinaryFileToComposer(msg.fileBase64 || "", msg.fileName || "", msg.mimeType || "text/plain")
        .then(function (result) {
          window.chrome.webview.postMessage({
            type: "ia_attach_result",
            requestId: requestId,
            result: result && typeof result === "object" ? result : { ok: false, error: "empty_result" },
          });
        })
        .catch(function (e) {
          window.chrome.webview.postMessage({
            type: "ia_attach_result",
            requestId: requestId,
            result: { ok: false, error: String((e && e.message) || e) },
          });
        });
      return;
    }
    if (msg.type === "ia_paste_text") {
      __iaPasteTextToComposerCore(msg.text || "", msg.append !== false)
        .then(function (result) {
          window.chrome.webview.postMessage({
            type: "ia_paste_result",
            requestId: requestId,
            result: result && typeof result === "object" ? result : { ok: false, error: "empty_result" },
          });
        })
        .catch(function (e) {
          window.chrome.webview.postMessage({
            type: "ia_paste_result",
            requestId: requestId,
            result: { ok: false, error: String((e && e.message) || e) },
          });
        });
    }
  });
})();
