window.IaPanel = (function () {

  const SCRIPT_TOKEN = String(Date.now());

  const MODES = [

    { id: "read", label: "Read", short: "Read" },

    { id: "type", label: "Type", short: "Type" },

    { id: "behavioral", label: "Behavioral", short: "Beh" },

  ];

  const LANGUAGES = [

    { id: "english", label: "English", short: "EN" },

    { id: "chinese", label: "中文", short: "中" },

  ];



  const PROMPT_KEYS = [

    { id: "resume_summary", label: "Resume summary" },

    { id: "jd_summary", label: "JD summary" },

    { id: "initial_interview", label: "Initial interview" },

    { id: "english", label: "Language: English" },

    { id: "chinese", label: "Language: 中文" },

    { id: "read", label: "Read" },

    { id: "type", label: "Type" },

    { id: "behavioral", label: "Behavioral" },

  ];



  let state = {

    tab: "caption",

    mode: "read",

    language: "english",

    promptKey: "read",

    draft: "",

    fullCaption: "",

    /** Send boundary — set on Send only. */
    endpoint: 0,

    /** Skip boundary — set on Skip (not Send); grays skipped draft without moving endpoint. */
    skipThrough: 0,

    connected: false,

    contextResumes: [],

    contextJds: [],

    activeResumeName: "",

    activeJdName: "",

    /** Settings accordion: resume | jd | prompt — only one expanded. */
    settingsSection: "resume",
    selectedResumeName: "",

    selectedJdName: "",

    /** Interview recording state */
    recording: false,
    recordingPairs: [],        // [{caption, result, ts_utc}]
    recordingCreatedUtc: null,

    /** History tab */
    historyMode: "list",       // "list" | "detail"
    historyFiles: [],          // [{name, created_utc, pair_count}]
    historySelectedFile: null, // loaded session data
    historySelectedFileName: "",
    /** Filename currently being renamed inline (blocks list re-render). */
    historyEditingName: null,

    sentenceEdits: {},

    editingKey: null,

    /** User drag selection: sentence indices in getCaptionItems(). */
    selection: null,

  };



  let els = {};
  let dragSelect = null;
  let disconnectEvents = null;
  let healthPollId = null;
  let companionBootstrapped = false;
  let autoscrollEnabled = true;
  let autoscrollResumeTimer = null;
  let suppressFeedScroll = false;

  let sendInProgress = false;
  let copyGptInProgress = false;
  let quoteSendInProgress = false;
  let companionSessionGeneration = -1;

  const AUTOSCROLL_RESUME_MS = 2000;
  const HEALTH_POLL_MS = 3000;
  const DRAFT_POLL_MS = 16;
  let draftPollId = null;
  let renderFeedScheduled = false;
  let lastRenderedCaption = "";
  let captionResyncPending = false;

  function splitSentences(text) {
    return window.IaCaptionSentences?.splitIntoSentences(text) || [(text || "").trim()].filter(Boolean);
  }

  function getFeedCaption() {
    return state.fullCaption || state.draft || "";
  }

  function getCaptionItems() {
    return window.IaCaptionSentences?.splitWithRanges(getFeedCaption()) || [];
  }

  function joinSentences(sentences) {
    return window.IaCaptionSentences?.joinSentences(sentences) || (sentences || []).join(" ");
  }

  function applyCaptionSnapshot(data, source = "unknown") {
    if (!data) return;
    if (data.changed === false) return;

    if (typeof data.session_generation === "number" && data.session_generation !== companionSessionGeneration) {
      companionSessionGeneration = data.session_generation;
      state.endpoint = 0;
      state.skipThrough = 0;
      state.selection = null;
      state.sentenceEdits = {};
      lastRenderedCaption = "";
    }
    if (data.draft !== undefined) state.draft = data.draft || "";

    if (data.full !== null && data.full !== undefined) {
      // Full replace (initial connect, session reset, or forced full).
      state.fullCaption = data.full || "";
      captionResyncPending = false;
    } else if (
      typeof data.patch_from === "number" &&
      data.patch_tail !== null &&
      data.patch_tail !== undefined
    ) {
      // Incremental patch: keep the stable prefix, splice in the new tail.
      const current = state.fullCaption || "";
      if (data.patch_from <= current.length) {
        if (state.skipThrough > data.patch_from) {
          state.skipThrough = data.patch_from;
        }
        state.fullCaption = current.slice(0, data.patch_from) + (data.patch_tail || "");
        captionResyncPending = false;
      } else if (data.patch_from === 0) {
        // Full replace via patch (first content or backend fallback).
        state.fullCaption = data.patch_tail || "";
        captionResyncPending = false;
      } else {
        // Client behind backend — request a full snapshot on next poll.
        captionResyncPending = true;
      }
    } else if (!state.fullCaption && data.draft) {
      state.fullCaption = state.draft;
    }

    if (typeof data.pending_start === "number" && data.pending_start >= 0) {
      state.endpoint = data.pending_start;
    }
    if (data.language) {
      state.language = normalizeLanguage(data.language);
    }
    prunePendingEdits();
  }

  function sentenceText(key, fallback) {
    if (Object.prototype.hasOwnProperty.call(state.sentenceEdits, key)) {
      return state.sentenceEdits[key];
    }
    return fallback;
  }

  /** Character index: text at or before this is sent/skipped (gray); after is pending (green). */
  function getGreenStart() {
    return Math.max(state.endpoint || 0, state.skipThrough || 0);
  }

  /** Unsent in caption model (endpoint boundary). */
  function isSentencePending(item) {
    if (!item) return false;
    return item.end > getGreenStart();
  }

  /** Default (no drag): unsent sentences green. After drag: green = selected range only. */
  function isGreenSendTarget(index, item) {
    if (state.selection) return isSentenceSelected(index);
    return isSentencePending(item);
  }

  /** Drag uses selection as-is; otherwise all unsent sentences. */
  function getPendingSendRange() {
    const items = getCaptionItems();
    if (!items.length) return null;

    const sel = getSelectionRange();
    if (sel) return sel;

    let first = -1;
    let last = -1;
    for (let i = 0; i < items.length; i++) {
      if (!isSentencePending(items[i])) continue;
      if (first < 0) first = i;
      last = i;
    }
    if (first < 0) return null;
    return { anchorIdx: first, endIdx: last };
  }

  async function setEndpointToNow() {
    const now = getFeedCaption().length;
    state.endpoint = now;
    await IaApi.post("/endpoint", { start_index: now });
  }

  function getSelectionRange() {
    if (!state.selection) return null;
    const items = getCaptionItems();
    if (!items.length) return null;
    let a = state.selection.anchorIdx;
    let e = state.selection.endIdx;
    if (state.selection.anchoredToLive) e = items.length - 1;
    else e = Math.min(e, items.length - 1);
    a = Math.max(0, Math.min(a, items.length - 1));
    if (a > e) return null;
    return { anchorIdx: a, endIdx: e };
  }

  function isSentenceSelected(index) {
    const r = getSelectionRange();
    if (!r || index < 0) return false;
    return index >= r.anchorIdx && index <= r.endIdx;
  }

  function itemKeyForItem(item) {
    return `s-${item.start}`;
  }

  function sentenceBoxProps(items, index) {
    const item = items[index];
    const key = itemKeyForItem(item);
    return {
      key,
      text: sentenceText(key, item.text),
      pending: isGreenSendTarget(index, item),
      selected: !!state.selection && isSentenceSelected(index),
      live:
        isSentenceSelected(index) &&
        !!state.selection?.anchoredToLive &&
        index === items.length - 1,
      index,
    };
  }

  function sentenceIndexFromEl(el) {
    const box = el?.closest?.(".ia-sentence");
    if (!box) return -1;
    const idx = parseInt(box.dataset.sentenceIndex, 10);
    return Number.isNaN(idx) ? -1 : idx;
  }

  /** Hit-test in shadow feed — document mousemove target does not reach shadow children. */
  function sentenceIndexAtClient(clientX, clientY) {
    if (!els.feed) return -1;
    const feedR = els.feed.getBoundingClientRect();
    if (clientX < feedR.left || clientX > feedR.right || clientY < feedR.top || clientY > feedR.bottom) {
      return -1;
    }

    const boxes = els.feed.querySelectorAll(".ia-sentence[data-sentence-index]");
    let bestIdx = -1;
    let bestDist = Infinity;

    for (const box of boxes) {
      const r = box.getBoundingClientRect();
      if (clientX >= r.left && clientX <= r.right && clientY >= r.top && clientY <= r.bottom) {
        const idx = parseInt(box.dataset.sentenceIndex, 10);
        return Number.isNaN(idx) ? -1 : idx;
      }
      const cx = Math.max(r.left, Math.min(clientX, r.right));
      const cy = Math.max(r.top, Math.min(clientY, r.bottom));
      const d = (clientX - cx) ** 2 + (clientY - cy) ** 2;
      if (d < bestDist) {
        const idx = parseInt(box.dataset.sentenceIndex, 10);
        if (!Number.isNaN(idx)) {
          bestDist = d;
          bestIdx = idx;
        }
      }
    }

    return bestIdx;
  }

  function sentenceIndexFromEvent(e) {
    if (e.composedPath) {
      for (const el of e.composedPath()) {
        if (el instanceof Element) {
          const idx = sentenceIndexFromEl(el);
          if (idx >= 0) return idx;
        }
      }
    }
    return sentenceIndexAtClient(e.clientX, e.clientY);
  }

  function commitSelection(anchorIdx, endIdx) {
    const items = getCaptionItems();
    if (!items.length) return;
    const a = Math.max(0, Math.min(anchorIdx, endIdx));
    const e = Math.max(0, Math.min(Math.max(anchorIdx, endIdx), items.length - 1));
    state.selection = {
      anchorIdx: a,
      endIdx: e,
      anchoredToLive: e === items.length - 1,
    };
    applySelectionStyles();
  }

  function applySelectionStyles() {
    const items = getCaptionItems();
    els.feed?.querySelectorAll(".ia-sentence[data-sentence-index]").forEach((el) => {
      const idx = sentenceIndexFromEl(el);
      if (idx < 0 || !items[idx]) return;
      const p = sentenceBoxProps(items, idx);
      updateSentenceBox(el, p);
    });
  }

  function finishDragSelect(e) {
    if (!dragSelect) return;
    const items = getCaptionItems();
    if (state.selection && items.length) {
      state.selection.anchoredToLive = state.selection.endIdx === items.length - 1;
    }
    dragSelect = null;
    els.feed.removeEventListener("pointermove", onFeedSelectMove);
    els.feed.removeEventListener("pointerup", onFeedSelectEnd);
    els.feed.removeEventListener("pointercancel", onFeedSelectEnd);
    try {
      if (e?.pointerId != null) els.feed.releasePointerCapture(e.pointerId);
    } catch (_) {
      /* ignore */
    }
    applySelectionStyles();
  }

  function onFeedSelectStart(e) {
    if (state.editingKey) return;
    if (e.button !== 0) return;
    const idx = sentenceIndexFromEvent(e);
    if (idx < 0) return;
    e.preventDefault();
    /* New drag replaces prior green send range; selection ignores endpoint. */
    state.selection = null;
    applySelectionStyles();
    dragSelect = { anchorIdx: idx };
    try {
      els.feed.setPointerCapture(e.pointerId);
    } catch (_) {
      /* ignore */
    }
    commitSelection(idx, idx);
    els.feed.addEventListener("pointermove", onFeedSelectMove);
    els.feed.addEventListener("pointerup", onFeedSelectEnd);
    els.feed.addEventListener("pointercancel", onFeedSelectEnd);
  }

  function onFeedSelectMove(e) {
    if (!dragSelect) return;
    const idx = sentenceIndexFromEvent(e);
    if (idx < 0) return;
    commitSelection(dragSelect.anchorIdx, idx);
  }

  function onFeedSelectEnd(e) {
    finishDragSelect(e);
  }

  function setupFeedSelection() {
    els.feed.addEventListener("pointerdown", onFeedSelectStart);
    els.feed.style.userSelect = "none";
    els.feed.style.touchAction = "none";
  }

  function gptNotReadyMessage(status) {
    switch (status?.reason) {
      case "busy":
        return "Send failed. Busy.";
      case "generating":
        return "Send failed. GPT is still generating.";
      case "composer_not_found":
        return "Send failed. GPT not ready.";
      case "pipeline_missing":
        return "Send failed. Pipeline missing.";
      default:
        return "Send failed. GPT not ready.";
    }
  }

  function sendFailureMessage(result) {
    const err = (result?.error || "").trim();
    if (err.includes("send_button_disabled")) return "Send failed. Send button disabled.";
    if (err.includes("composer_not_found")) return "Send failed. GPT not ready.";
    if (err.includes("could_not_insert")) return "Send failed. Could not insert prompt.";
    if (err.includes("prep_already_in_flight")) return "Send failed. Busy.";
    if (err) return `Send failed. ${err.split(":")[0]}`;
    if (result?.ok && result?.phase !== "sent") return `Send failed. Bad phase (${result?.phase || "?"}).`;
    return "Send failed. Not confirmed.";
  }

  function resolveLatestGptResultText() {
    try {
      if (typeof window.__iaExtensionGetLatestAssistantText === "function") {
        return (window.__iaExtensionGetLatestAssistantText() || "").trim();
      }
      if (typeof window.__iaRefreshLatestAnswer === "function") {
        return (window.__iaRefreshLatestAnswer("", 1) || "").trim();
      }
    } catch (_) {
      /* ignore */
    }
    return "";
  }

  async function copyTextToClipboard(text) {
    const payload = String(text || "");
    if (!payload) return false;
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(payload);
        return true;
      }
    } catch (_) {
      /* fallback */
    }
    try {
      const ta = document.createElement("textarea");
      ta.value = payload;
      ta.setAttribute("readonly", "");
      ta.style.position = "fixed";
      ta.style.left = "-9999px";
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand("copy");
      ta.remove();
      return ok;
    } catch (_) {
      return false;
    }
  }

  async function onCopyGptResult() {
    if (copyGptInProgress) return;
    copyGptInProgress = true;
    try {
      const text = resolveLatestGptResultText();
      if (!text) {
        window.IaToast?.warning("No GPT reply to copy yet.");
        return;
      }
      if (await copyTextToClipboard(text)) {
        window.IaToast?.success("Copied.");
      } else {
        window.IaToast?.warning("Copy failed. Clipboard busy — try again.");
      }
    } catch (e) {
      window.IaToast?.error(`Failed. ${String(e.message || e)}`);
    } finally {
      copyGptInProgress = false;
    }
  }

  async function pasteTextToComposer(text, append) {
    const t = String(text || "").trim();
    if (!t) {
      window.IaToast?.warning("Nothing to paste.");
      return { ok: false };
    }
    const ready = window.__iaExtensionGetGptReady?.();
    if (!ready?.ok) {
      window.IaToast?.warning(gptNotReadyMessage(ready));
      return { ok: false };
    }
    if (!window.__iaExtensionPasteDraft) {
      window.IaToast?.warning("Paste failed. Pipeline missing.");
      return { ok: false };
    }
    const result = await window.__iaExtensionPasteDraft(t, append !== false);
    if (result?.ok) return result;
    window.IaToast?.warning(sendFailureMessage(result));
    return result || { ok: false };
  }

  async function sendPromptToGpt(prompt, append) {
    if (sendInProgress) {
      window.IaToast?.warning("Send failed. Busy.");
      return { ok: false };
    }
    if (!String(prompt || "").trim()) {
      window.IaToast?.warning("Send failed. Empty prompt.");
      return { ok: false };
    }
    const ready = window.__iaExtensionGetGptReady?.();
    if (!ready?.ok) {
      window.IaToast?.warning(gptNotReadyMessage(ready));
      return { ok: false };
    }
    if (!window.__iaExtensionSendPrompt) {
      window.IaToast?.warning("Send failed. Pipeline missing.");
      return { ok: false };
    }

    sendInProgress = true;
    window.IaToast?.info("Sending…");
    try {
      const result = await window.__iaExtensionSendPrompt(prompt, append === true);
      if (result?.ok) {
        window.IaToast?.success(append ? "Sent (appended to prompt)." : "Sent.");
        return result;
      }
      window.IaToast?.warning(sendFailureMessage(result));
      return result || { ok: false };
    } catch (e) {
      window.IaToast?.error(`Failed. ${String(e.message || e)}`);
      return { ok: false };
    } finally {
      sendInProgress = false;
    }
  }

  function contextNamesEqual(a, b) {
    return String(a || "").trim().toLowerCase() === String(b || "").trim().toLowerCase();
  }

  function findContextEntry(kind, name) {
    const items = kind === "resume" ? state.contextResumes : state.contextJds;
    return items.find((e) => contextNamesEqual(e.name, name));
  }

  function getContextTextForKind(kind) {
    const isResume = kind === "resume";
    const selected = isResume ? state.selectedResumeName : state.selectedJdName;
    const textarea = isResume ? els.resume : els.jd;
    const editor = (textarea?.value || "").trim();

    if (selected) {
      const item = findContextEntry(kind, selected);
      if (item) return editor || (item.text || "").trim();
    }
    return editor;
  }

  async function getContextTexts() {
    let resume = getContextTextForKind("resume");
    let jd = getContextTextForKind("jd");
    if (resume && jd) return { resume, jd };

    const ctx = await IaApi.get("/context");
    if (!ctx.ok) return { resume, jd };
    if (!resume) resume = (ctx.data?.resume || "").trim();
    if (!jd) jd = (ctx.data?.job_description || "").trim();
    return { resume, jd };
  }

  async function buildPrepPrompt(kind) {
    const { resume, jd } = await getContextTexts();
    const pr = await IaApi.get(`/prompts/${encodeURIComponent(kind)}`);
    const template = pr.ok ? pr.data?.text || "" : "";
    const t = template.trim();
    if (kind === "resume_summary") {
      if (!t || !resume) return "";
      return t.replace(/\{resume_text\}/g, resume);
    }
    if (kind === "jd_summary") {
      if (!t || !jd) return "";
      return t.replace(/\{jd_text\}/g, jd);
    }
    return t;
  }

  async function onPrepSend(kind) {
    const { resume, jd } = await getContextTexts();
    const pr = await IaApi.get(`/prompts/${encodeURIComponent(kind)}`);
    const template = (pr.ok ? pr.data?.text || "" : "").trim();

    if (kind === "resume_summary") {
      if (!resume) {
        window.IaToast?.warning("Send failed. No resume — add it in Settings.");
        return;
      }
      if (!template) {
        window.IaToast?.warning("Send failed. Resume summary template is empty.");
        return;
      }
      await sendPromptToGpt(template.replace(/\{resume_text\}/g, resume), false);
      return;
    }

    if (kind === "jd_summary") {
      if (!jd) {
        window.IaToast?.warning("Send failed. No job description — add it in Settings.");
        return;
      }
      if (!template) {
        window.IaToast?.warning("Send failed. JD summary template is empty.");
        return;
      }
      await sendPromptToGpt(template.replace(/\{jd_text\}/g, jd), false);
      return;
    }

    if (kind === "initial_interview") {
      if (!template) {
        window.IaToast?.warning("Send failed. Initial interview template is empty.");
        return;
      }
      const result = await sendPromptToGpt(template, false);
      if (result?.ok) {
        await setEndpointToNow();
        state.selection = null;
        renderFeed();
      }
    }
  }

  function formatInterviewerQuote(text) {
    const inner = (text || "").trim().replace(/\\/g, "\\\\").replace(/"/g, '\\"');
    return `Interviewer said "${inner}"`;
  }

  function buildPendingChunk() {
    const range = getPendingSendRange();
    if (!range) return "";
    const items = getCaptionItems();
    const texts = [];
    for (let i = range.anchorIdx; i <= range.endIdx; i++) {
      const item = items[i];
      if (!item) continue;
      texts.push(sentenceText(itemKeyForItem(item), item.text));
    }
    return joinSentences(texts);
  }

  function clearPendingSentenceEdits() {
    Object.keys(state.sentenceEdits).forEach((k) => {
      if (k.startsWith("l-")) delete state.sentenceEdits[k];
    });
  }

  function prunePendingEdits() {
    const valid = new Set(getCaptionItems().map((item) => `s-${item.start}`));
    Object.keys(state.sentenceEdits).forEach((k) => {
      if (!valid.has(k)) delete state.sentenceEdits[k];
    });
  }

  function injectStyles(root) {

    const style = document.createElement("style");

    style.textContent = window.__IA_PANEL_CSS || "";

    root.appendChild(style);

  }



  function bindElements(root) {

    els = {

      root,

      status: root.getElementById("ia-status"),
      statusLabel: root.querySelector("#ia-status .ia-status-label"),

      feed: root.getElementById("ia-feed"),

      captionView: root.getElementById("ia-caption-view"),

      settings: root.getElementById("ia-settings"),

      resume: root.getElementById("ia-resume"),

      resumeName: root.getElementById("ia-resume-name"),

      resumeHistory: root.getElementById("ia-resume-history"),

      jd: root.getElementById("ia-jd"),

      jdName: root.getElementById("ia-jd-name"),

      jdHistory: root.getElementById("ia-jd-history"),

      promptNav: root.getElementById("ia-prompt-nav"),

      promptEditor: root.getElementById("ia-prompt-editor"),

      modeSeg: root.getElementById("ia-mode-seg"),

      langSeg: root.getElementById("ia-lang-seg"),

      btnResumeSend: root.getElementById("ia-btn-resume-send"),

      btnJdSend: root.getElementById("ia-btn-jd-send"),

      btnInterviewStart: root.getElementById("ia-btn-interview-start"),

      btnSave: root.getElementById("ia-btn-save"),

      btnSend: root.getElementById("ia-btn-send"),

      btnQuoteCaption: root.getElementById("ia-btn-quote-caption"),

      btnCopyGpt: root.getElementById("ia-btn-copy-gpt"),

      btnReject: root.getElementById("ia-btn-reject"),

      btnImage: root.getElementById("ia-btn-image"),

      btnText: root.getElementById("ia-btn-text"),

      btnReconnect: root.getElementById("ia-btn-reconnect"),

      btnRecord: root.getElementById("ia-btn-record"),

      historyTab: root.getElementById("ia-history-tab"),
      historyContent: root.getElementById("ia-history-content"),
      historyListPanel: root.getElementById("ia-history-list-panel"),
      historyFiles: root.getElementById("ia-history-files"),
      historyPairs: root.getElementById("ia-history-pairs"),
      historyExpandBtn: root.getElementById("ia-history-expand-btn"),
      historyListCollapseBtn: root.getElementById("ia-history-list-collapse-btn"),

    };

  }



  function wirePanelEvents(root) {

    root.querySelectorAll(".ia-tab").forEach((btn) => {

      btn.addEventListener("click", () => setTab(btn.dataset.tab));

    });

    els.btnSend.addEventListener("click", onSend);

    els.btnQuoteCaption?.addEventListener("click", onQuoteCaption);

    els.btnCopyGpt?.addEventListener("click", onCopyGptResult);

    els.btnReject.addEventListener("click", onReject);

    els.btnResumeSend?.addEventListener("click", () => onPrepSend("resume_summary"));

    els.btnJdSend?.addEventListener("click", () => onPrepSend("jd_summary"));

    els.btnInterviewStart?.addEventListener("click", () => onPrepSend("initial_interview"));

    els.btnSave.addEventListener("click", onSaveEdit);

    els.btnText.addEventListener("click", () => onPasteDraft(false));

    els.btnImage.addEventListener("click", () => onPasteDraft(false));

    els.btnReconnect?.addEventListener("click", () => void reconnectCompanion());

    els.btnRecord?.addEventListener("click", () => void onToggleRecord());

    // Left > : 1:9 → 9:1
    els.historyExpandBtn?.addEventListener("click", (e) => {
      e.stopPropagation();
      void expandHistoryPanel();
    });
    // Right < : 9:1 → 1:9
    els.historyListCollapseBtn?.addEventListener("click", (e) => {
      e.stopPropagation();
      collapseHistoryPanel();
    });

    root.getElementById("ia-save-resume").addEventListener("click", saveResume);

    root.getElementById("ia-save-jd").addEventListener("click", saveJd);

    setupFileUpload("ia-upload-resume", "ia-resume-file", {
      getTextarea: () => els.resume,
      getNameInput: () => els.resumeName,
    });

    setupFileUpload("ia-upload-jd", "ia-jd-file", {
      getTextarea: () => els.jd,
      getNameInput: () => els.jdName,
    });

    root.getElementById("ia-save-prompt").addEventListener("click", savePrompt);

    const collapseBtn = root.getElementById("ia-panel-collapse-btn");

    collapseBtn?.addEventListener("click", (e) => {

      e.stopPropagation();

      if (window.IaLayout?.toggleCollapsed) window.IaLayout.toggleCollapsed();

    });

    setupFeedAutoscroll();

    setupFeedSelection();

    setupSettingsAccordion(root);

  }

  function setupSettingsAccordion(root) {
    const accordion = root.getElementById("ia-settings-accordion");
    if (!accordion) return;

    accordion.querySelectorAll(".ia-settings-section-toggle").forEach((btn) => {
      btn.addEventListener("click", () => {
        const section = btn.closest(".ia-settings-section");
        const id = section?.dataset.section;
        if (id) setSettingsAccordionSection(id);
      });
    });

    setSettingsAccordionSection(state.settingsSection || "resume");
  }

  function setSettingsAccordionSection(sectionId) {
    state.settingsSection = sectionId;
    const accordion = els.settings?.querySelector("#ia-settings-accordion");
    if (!accordion) return;

    accordion.querySelectorAll(".ia-settings-section").forEach((sec) => {
      const isExpanded = sec.dataset.section === sectionId;
      sec.classList.toggle("is-expanded", isExpanded);
      sec.classList.toggle("is-collapsed", !isExpanded);
      const btn = sec.querySelector(".ia-settings-section-toggle");
      if (btn) btn.setAttribute("aria-expanded", isExpanded ? "true" : "false");
    });
  }



  function activate(hostEl) {

    const root = hostEl.shadowRoot;

    if (!root) return mount(hostEl);

    bindElements(root);

    renderModeButtons();

    renderLanguageButtons();

    renderPromptNav();

    if (disconnectEvents) disconnectEvents();

    void init();

  }



  function mount(hostEl) {

    if (hostEl.shadowRoot) return activate(hostEl);

    const root = hostEl.attachShadow({ mode: "open" });

    injectStyles(root);



    const I = window.IaIcons || {};

    const wrap = document.createElement("div");

    wrap.innerHTML = `

      <div class="ia-panel">

        <header class="ia-top">

          <div class="ia-brand">

            <span class="ia-logo" aria-hidden="true">IA</span>

            <div>

              <div class="ia-title">Interview</div>

              <div class="ia-status-row">
                <div class="ia-status connecting" id="ia-status" aria-live="polite">
                  <span class="ia-status-dot" aria-hidden="true"></span>
                  <span class="ia-status-label">Connecting…</span>
                </div>
                <button type="button" class="ia-link-btn" id="ia-btn-reconnect" title="Reconnect after starting or restarting the companion">Reconnect</button>
              </div>

            </div>

          </div>

          <button type="button" class="ia-rail-btn" id="ia-panel-collapse-btn" title="Collapse panel">${I.chevronRight || "›"}</button>

        </header>

        <div class="ia-main-chrome">

          <nav class="ia-seg-tabs" aria-label="Panel sections">

            <button type="button" class="ia-tab active" data-tab="caption">Caption</button>

            <button type="button" class="ia-tab" data-tab="settings">Settings</button>

            <button type="button" class="ia-tab" data-tab="history">History</button>

          </nav>

          <div class="ia-chrome-body">

            <div class="ia-caption-view" id="ia-caption-view">

              <div class="ia-toolbar-card">

                <div class="ia-toolbar">

                  <div class="ia-prep-seg" id="ia-prep-seg" role="group" aria-label="Prep sends">

                    <button type="button" class="ia-icon-btn" id="ia-btn-resume-send" title="Send resume summary">${I.folder || "R"}</button>

                    <button type="button" class="ia-icon-btn" id="ia-btn-jd-send" title="Send JD summary">${I.briefcase || "J"}</button>

                    <button type="button" class="ia-icon-btn" id="ia-btn-interview-start" title="Start initial interview">${I.info || "i"}</button>

                  </div>

                  <div class="ia-mode-seg" id="ia-mode-seg" role="group" aria-label="Session mode"></div>

                  <div class="ia-lang-seg" id="ia-lang-seg" role="group" aria-label="Output language"></div>

                  <button type="button" class="ia-icon-btn ghost" id="ia-btn-save" title="Save edit" style="display:none">${I.check || "✓"}</button>

                  <button type="button" class="ia-icon-btn primary" id="ia-btn-send" title="Send to ChatGPT (mode prompt)">${I.send || "→"}</button>

                  <button type="button" class="ia-icon-btn" id="ia-btn-quote-caption" title="Paste selected caption as: Interviewer said &quot;…&quot; (does not send)">${I.quote || '"'} </button>

                  <button type="button" class="ia-icon-btn" id="ia-btn-copy-gpt" title="Copy latest ChatGPT reply">${I.copy || "⧉"}</button>

                  <button type="button" class="ia-icon-btn" id="ia-btn-reject" title="Skip draft">${I.close || "×"}</button>

                  <button type="button" class="ia-icon-btn" id="ia-btn-image" title="Paste draft">${I.image || "▣"}</button>

                  <button type="button" class="ia-icon-btn" id="ia-btn-text" title="Paste draft to composer">${I.text || "T"}</button>

                  <button type="button" class="ia-icon-btn ia-btn-record" id="ia-btn-record" title="Start recording interview Q+A pairs">${I.play || "▶"}</button>

                </div>

              </div>

              <div class="ia-body" id="ia-feed"></div>

            </div>

            <div class="ia-settings" id="ia-settings">

              <div class="ia-settings-accordion" id="ia-settings-accordion">

              <div class="ia-settings-section ia-settings-card ia-settings-card--compact is-expanded" data-section="resume">

                <button type="button" class="ia-settings-section-toggle" aria-expanded="true" aria-controls="ia-settings-body-resume">
                  <span>Resume</span>
                  <span class="ia-settings-chevron" aria-hidden="true">${I.chevronRight || "›"}</span>
                </button>

                <div class="ia-settings-section-body" id="ia-settings-body-resume">
                <div class="ia-settings-section-inner">

                <input type="text" id="ia-resume-name" class="ia-context-name" placeholder="Name (required to save)" autocomplete="off" />

                <textarea id="ia-resume" placeholder="Paste or type resume…"></textarea>

                <div class="ia-settings-actions">
                  <input type="file" id="ia-resume-file" class="ia-file-input" accept=".txt,.pdf,.docx,text/plain,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document" hidden />
                  <button type="button" class="ia-btn-upload" id="ia-upload-resume">${I.upload || "↑"} Upload</button>
                  <button type="button" class="ia-btn-save" id="ia-save-resume">Save resume</button>
                </div>

                <div class="ia-context-history" id="ia-resume-history" aria-label="Saved resumes"></div>

                </div>
                </div>

              </div>

              <div class="ia-settings-section ia-settings-card ia-settings-card--compact is-collapsed" data-section="jd">

                <button type="button" class="ia-settings-section-toggle" aria-expanded="false" aria-controls="ia-settings-body-jd">
                  <span>Job description</span>
                  <span class="ia-settings-chevron" aria-hidden="true">${I.chevronRight || "›"}</span>
                </button>

                <div class="ia-settings-section-body" id="ia-settings-body-jd">
                <div class="ia-settings-section-inner">

                <input type="text" id="ia-jd-name" class="ia-context-name" placeholder="Name (required to save)" autocomplete="off" />

                <textarea id="ia-jd" placeholder="Paste or type JD…"></textarea>

                <div class="ia-settings-actions">
                  <input type="file" id="ia-jd-file" class="ia-file-input" accept=".txt,.pdf,.docx,text/plain,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document" hidden />
                  <button type="button" class="ia-btn-upload" id="ia-upload-jd">${I.upload || "↑"} Upload</button>
                  <button type="button" class="ia-btn-save" id="ia-save-jd">Save JD</button>
                </div>

                <div class="ia-context-history" id="ia-jd-history" aria-label="Saved job descriptions"></div>

                </div>
                </div>

              </div>

              <div class="ia-settings-section ia-settings-card ia-settings-card--prompt is-collapsed" data-section="prompt">

                <button type="button" class="ia-settings-section-toggle" aria-expanded="false" aria-controls="ia-settings-body-prompt">
                  <span>Prompt template</span>
                  <span class="ia-settings-chevron" aria-hidden="true">${I.chevronRight || "›"}</span>
                </button>

                <div class="ia-settings-section-body" id="ia-settings-body-prompt">
                <div class="ia-settings-section-inner">

                <div class="ia-prompt-nav" id="ia-prompt-nav" role="group" aria-label="Prompt template"></div>

                <div class="ia-prompt-editor-wrap">
                  <textarea id="ia-prompt-editor" placeholder="Loading prompt…"></textarea>
                </div>

                <button type="button" class="ia-btn-save" id="ia-save-prompt">Save prompt</button>

                </div>
                </div>

              </div>

              </div>

            </div>

            <div class="ia-history-tab" id="ia-history-tab">

              <div class="ia-history-split">

                <div class="ia-history-content ia-history-panel--collapsed" id="ia-history-content" style="flex:1">
                  <div class="ia-history-content-placeholder">
                    <button type="button" class="ia-history-expand-btn" id="ia-history-expand-btn" title="Expand content">${I.chevronRight || "›"}</button>
                  </div>
                  <div class="ia-history-pairs" id="ia-history-pairs"></div>
                </div>

                <div class="ia-history-list-panel" id="ia-history-list-panel" style="flex:9">
                  <div class="ia-history-files" id="ia-history-files"></div>
                  <div class="ia-history-list-collapse-btn-wrap" id="ia-history-list-collapse-wrap">
                    <button type="button" class="ia-history-expand-btn" id="ia-history-list-collapse-btn" title="Back to file list">${I.chevronLeft || "‹"}</button>
                  </div>
                </div>

              </div>

            </div>

          </div>

        </div>

      </div>

    `;

    root.appendChild(wrap.firstElementChild);

    bindElements(root);

    wirePanelEvents(root);

    renderModeButtons();

    renderLanguageButtons();

    renderPromptNav();

    init();

  }



  function setupFeedAutoscroll() {
    els.feed.addEventListener("scroll", onFeedScroll, { passive: true });
  }

  function onFeedScroll() {
    if (suppressFeedScroll) return;
    autoscrollEnabled = false;
    if (autoscrollResumeTimer) clearTimeout(autoscrollResumeTimer);
    autoscrollResumeTimer = setTimeout(() => {
      autoscrollEnabled = true;
      autoscrollResumeTimer = null;
    }, AUTOSCROLL_RESUME_MS);
  }

  function scrollFeedToBottom() {
    if (!autoscrollEnabled) return;
    suppressFeedScroll = true;
    els.feed.scrollTop = els.feed.scrollHeight;
    requestAnimationFrame(() => {
      suppressFeedScroll = false;
    });
  }



  function renderModeButtons() {

    els.modeSeg.innerHTML = "";

    MODES.forEach((m) => {

      const btn = document.createElement("button");

      btn.type = "button";

      btn.className = "ia-mode-btn";

      btn.dataset.mode = m.id;

      btn.innerHTML = (window.IaIcons && window.IaIcons.forMode(m.id)) || m.short;

      btn.title = m.label;

      btn.setAttribute("aria-label", m.label);

      btn.addEventListener("click", () => setMode(m.id));

      els.modeSeg.appendChild(btn);

    });

    updateModeButtons();

  }

  function renderLanguageButtons() {
    if (!els.langSeg) return;
    els.langSeg.innerHTML = "";
    LANGUAGES.forEach((lang) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "ia-lang-btn";
      btn.dataset.language = lang.id;
      btn.textContent = lang.short;
      btn.title = lang.label;
      btn.setAttribute("aria-label", lang.label);
      btn.addEventListener("click", () => setLanguage(lang.id));
      els.langSeg.appendChild(btn);
    });
    updateLanguageButtons();
  }

  function updateLanguageButtons() {
    els.langSeg?.querySelectorAll(".ia-lang-btn").forEach((btn) => {
      const on = btn.dataset.language === state.language;
      btn.classList.toggle("active", on);
      btn.setAttribute("aria-pressed", on ? "true" : "false");
    });
  }



  const PROMPT_NAV_TEXT = {
    english: "En",
    chinese: "中",
  };

  function renderPromptNav() {

    els.promptNav.innerHTML = "";

    PROMPT_KEYS.forEach((p) => {

      const btn = document.createElement("button");

      btn.type = "button";

      const navText = PROMPT_NAV_TEXT[p.id];

      btn.className = "ia-prompt-nav-btn" + (navText ? " ia-prompt-nav-btn--text" : "");

      btn.dataset.key = p.id;

      btn.innerHTML = navText || (window.IaIcons && window.IaIcons.forPromptKey(p.id)) || "";

      btn.title = p.label;

      btn.setAttribute("aria-label", p.label);

      btn.addEventListener("click", () => setPromptKey(p.id));

      els.promptNav.appendChild(btn);

    });

    updatePromptNav();

  }



  function updateModeButtons() {

    els.modeSeg.querySelectorAll(".ia-mode-btn").forEach((btn) => {

      const on = btn.dataset.mode === state.mode;

      btn.classList.toggle("active", on);

      btn.setAttribute("aria-pressed", on ? "true" : "false");

    });

  }



  function updatePromptNav() {

    els.promptNav.querySelectorAll(".ia-prompt-nav-btn").forEach((btn) => {

      btn.classList.toggle("active", btn.dataset.key === state.promptKey);

    });

  }



  function setPromptKey(key) {

    state.promptKey = key;

    updatePromptNav();

    void loadPromptEditor();

  }



  // ── Recording ──────────────────────────────────────────────────────────────

  async function onToggleRecord() {
    if (!state.recording) {
      state.recording = true;
      state.recordingPairs = [];
      state.recordingCreatedUtc = new Date().toISOString();
      updateRecordBadge();
      window.IaToast?.info("Recording started — Send captions to capture Q+A pairs.");
    } else {
      state.recording = false;
      updateRecordBadge();
      if (!state.recordingPairs.length) {
        window.IaToast?.warning("Recording stopped — no pairs captured.");
        return;
      }
      const r = await IaApi.post("/interview-history", {
        created_utc: state.recordingCreatedUtc,
        pairs: state.recordingPairs,
      });
      state.recordingPairs = [];
      state.recordingCreatedUtc = null;
      if (r.ok) {
        window.IaToast?.success(`Interview saved as "${r.data?.name || "file"}".`);
        if (state.tab === "history") void loadHistoryFileList();
      } else {
        window.IaToast?.error("Could not save interview history.");
      }
    }
  }

  function updateRecordBadge() {
    const btn = els.btnRecord;
    if (!btn) return;
    const I = window.IaIcons || {};
    if (state.recording) {
      btn.innerHTML = I.stop || "■";
      btn.title = `Stop recording (${state.recordingPairs.length} pair${state.recordingPairs.length === 1 ? "" : "s"} captured)`;
      btn.classList.add("ia-record-active");
    } else {
      btn.innerHTML = I.play || "▶";
      btn.title = "Start recording interview Q+A pairs";
      btn.classList.remove("ia-record-active");
    }
  }

  // ── History tab ─────────────────────────────────────────────────────────────

  async function loadHistoryFileList() {
    if (state.historyEditingName) return;
    const r = await IaApi.get("/interview-history");
    if (!r.ok) return;
    state.historyFiles = Array.isArray(r.data?.files) ? r.data.files : [];
    renderHistoryFileList();
  }

  function setHistoryMode(mode) {
    state.historyMode = mode;
    const content = els.historyContent;
    const listPanel = els.historyListPanel;
    if (!content || !listPanel) return;

    if (mode === "detail") {
      content.style.flex = "9";
      listPanel.style.flex = "1";
      listPanel.classList.add("ia-history-panel--collapsed");
      content.classList.remove("ia-history-panel--collapsed");
    } else {
      content.style.flex = "1";
      listPanel.style.flex = "9";
      content.classList.add("ia-history-panel--collapsed");
      listPanel.classList.remove("ia-history-panel--collapsed");
    }
  }

  async function expandHistoryPanel() {
    if (state.historySelectedFile) {
      setHistoryMode("detail");
      return;
    }
    if (state.historySelectedFileName) {
      await openHistoryFile(state.historySelectedFileName);
      return;
    }
    if (state.historyFiles.length > 0) {
      await openHistoryFile(state.historyFiles[0].name);
      return;
    }
    window.IaToast?.warning("No saved interviews yet.");
  }

  function collapseHistoryPanel() {
    setHistoryMode("list");
  }

  function renderHistoryFileList() {
    if (state.historyEditingName) return;
    const list = els.historyFiles;
    if (!list) return;

    list.replaceChildren();

    if (!state.historyFiles.length) {
      const empty = document.createElement("p");
      empty.className = "ia-hint";
      empty.style.padding = "16px";
      empty.textContent = "No saved interviews yet. Use the record button to capture one.";
      list.appendChild(empty);
      return;
    }

    const I = window.IaIcons || {};
    for (const file of state.historyFiles) {
      const item = document.createElement("div");
      item.className = "ia-hist-file-item";
      item.dataset.name = file.name;

      const nameDisplay = document.createElement("div");
      nameDisplay.className = "ia-hist-file-name";
      nameDisplay.textContent = fileDisplayName(file.name);

      const timeEl = document.createElement("div");
      timeEl.className = "ia-hist-file-time";
      timeEl.textContent = formatHistoryDate(file.created_utc) +
        (file.pair_count ? ` · ${file.pair_count} Q&A` : "");

      const actions = document.createElement("div");
      actions.className = "ia-hist-file-actions";

      const dotsBtn = document.createElement("button");
      dotsBtn.type = "button";
      dotsBtn.className = "ia-hist-dots-btn";
      dotsBtn.title = "Rename";
      dotsBtn.setAttribute("aria-label", "Rename");
      dotsBtn.innerHTML = I.dotsH || "…";
      dotsBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        startInlineRename(item, file);
      });

      const delBtn = document.createElement("button");
      delBtn.type = "button";
      delBtn.className = "ia-hist-del-btn";
      delBtn.title = "Delete";
      delBtn.setAttribute("aria-label", "Delete");
      delBtn.innerHTML = I.trash || "×";
      delBtn.addEventListener("click", async (e) => {
        e.stopPropagation();
        const r = await IaApi.del(`/interview-history/${encodeURIComponent(file.name)}`);
        if (r.ok) {
          window.IaToast?.success("Deleted.");
          void loadHistoryFileList();
        } else {
          window.IaToast?.error("Could not delete.");
        }
      });

      actions.appendChild(dotsBtn);
      actions.appendChild(delBtn);

      const meta = document.createElement("div");
      meta.className = "ia-hist-file-meta";
      meta.appendChild(nameDisplay);
      meta.appendChild(timeEl);

      item.appendChild(meta);
      item.appendChild(actions);

      item.addEventListener("dblclick", (e) => {
        // Don't open when rename input is focused inside this item
        if (item.querySelector(".ia-hist-rename-input")) return;
        void openHistoryFile(file.name);
      });
      list.appendChild(item);
    }
  }

  function startInlineRename(itemEl, file) {
    if (state.historyEditingName) return;
    const meta = itemEl.querySelector(".ia-hist-file-meta");
    if (!meta) return;
    const nameEl = itemEl.querySelector(".ia-hist-file-name");
    if (!nameEl) return;

    state.historyEditingName = file.name;

    const input = document.createElement("input");
    input.type = "text";
    input.className = "ia-hist-rename-input";
    input.value = fileDisplayName(file.name);
    input.title = "Press Enter to save, Escape to cancel";

    nameEl.replaceWith(input);
    input.focus();
    input.select();

    input.addEventListener("click", (e) => e.stopPropagation());
    input.addEventListener("dblclick", (e) => e.stopPropagation());
    input.addEventListener("mousedown", (e) => e.stopPropagation());

    let finished = false;

    const onDocMouseDown = (e) => {
      if (itemEl.contains(e.target)) return;
      cancel();
    };

    const detachOutsideListener = () => {
      document.removeEventListener("mousedown", onDocMouseDown, true);
    };

    const endEdit = () => {
      state.historyEditingName = null;
      detachOutsideListener();
    };

    const cancel = () => {
      if (finished) return;
      finished = true;
      endEdit();
      if (input.isConnected) input.replaceWith(nameEl);
    };

    const save = async () => {
      if (finished) return;
      const newName = input.value.trim();
      if (!newName || newName === fileDisplayName(file.name)) {
        cancel();
        return;
      }
      finished = true;
      detachOutsideListener();
      const r = await IaApi.patch(`/interview-history/${encodeURIComponent(file.name)}`, { new_name: newName });
      endEdit();
      if (r.ok) {
        if (state.historySelectedFileName === file.name) {
          state.historySelectedFileName = newName.endsWith(".json") ? newName : `${newName}.json`;
        }
        window.IaToast?.success("Renamed.");
        void loadHistoryFileList();
      } else {
        window.IaToast?.error("Could not rename.");
        if (input.isConnected) input.replaceWith(nameEl);
      }
    };

    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        e.stopPropagation();
        void save();
      } else if (e.key === "Escape") {
        e.preventDefault();
        e.stopPropagation();
        cancel();
      }
    });

    setTimeout(() => {
      document.addEventListener("mousedown", onDocMouseDown, true);
    }, 0);
  }

  async function openHistoryFile(filename) {
    const r = await IaApi.get(`/interview-history/${encodeURIComponent(filename)}`);
    if (!r.ok) { window.IaToast?.error("Could not load file."); return; }
    state.historySelectedFile = r.data;
    state.historySelectedFileName = filename;
    renderHistoryPairs();
    setHistoryMode("detail");
  }

  function renderHistoryPairs() {
    const container = els.historyPairs;
    if (!container) return;
    container.replaceChildren();

    const session = state.historySelectedFile;
    if (!session?.pairs?.length) {
      const empty = document.createElement("p");
      empty.className = "ia-hint";
      empty.textContent = "No Q&A pairs in this session.";
      container.appendChild(empty);
      return;
    }

    for (const pair of session.pairs) {
      const block = document.createElement("div");
      block.className = "ia-hist-pair";

      const qRow = document.createElement("div");
      qRow.className = "ia-hist-pair-caption";
      const qLabel = document.createElement("span");
      qLabel.className = "ia-hist-pair-label";
      qLabel.textContent = "Interviewer";
      const qText = document.createElement("p");
      qText.className = "ia-hist-pair-text";
      qText.textContent = pair.caption || "";
      qRow.appendChild(qLabel);
      qRow.appendChild(qText);

      const aRow = document.createElement("div");
      aRow.className = "ia-hist-pair-result";
      const aLabel = document.createElement("span");
      aLabel.className = "ia-hist-pair-label";
      aLabel.textContent = "GPT";
      const aText = document.createElement("p");
      aText.className = "ia-hist-pair-text";
      aText.textContent = pair.result || "";
      aRow.appendChild(aLabel);
      aRow.appendChild(aText);

      block.appendChild(qRow);
      block.appendChild(aRow);
      container.appendChild(block);
    }
  }

  function fileDisplayName(filename) {
    return (filename || "").replace(/\.json$/, "");
  }

  function formatHistoryDate(isoStr) {
    try {
      const d = new Date(isoStr);
      return d.toLocaleString(undefined, {
        month: "short", day: "numeric",
        hour: "2-digit", minute: "2-digit",
      });
    } catch {
      return isoStr || "";
    }
  }

  async function reconnectCompanion() {
    setConnectionState("connecting");
    stopDraftPoll();
    if (disconnectEvents) {
      disconnectEvents();
      disconnectEvents = null;
    }
    companionBootstrapped = false;
    disconnectEvents = IaApi.connectEvents(onSseMessage);
    const ok = await checkCompanionHealth();
    startDraftPoll();
    if (ok) {
      window.IaToast?.success("Connected to companion.");
    } else {
      window.IaToast?.warning(
        "Companion not reachable. Start the tray app (or run dotnet), then click Reconnect."
      );
    }
  }

  async function init() {

    if (window.IaLayout && !window.IaLayout.isChatGptPage()) return;

    setConnectionState("connecting");

    await checkCompanionHealth();

    if (disconnectEvents) disconnectEvents();

    disconnectEvents = IaApi.connectEvents(onSseMessage);

    startHealthPoll();
    startDraftPoll();

    renderFeed();

  }



  async function loadCompanionData() {

    await IaApi.post("/session/start");

    state.endpoint = 0;

    state.skipThrough = 0;

    state.selection = null;

    const draft = await IaApi.get("/draft?full=1");

    if (draft.ok && draft.data) {

      applyCaptionSnapshot(draft.data, "load");

      if (draft.data.mode) {

        state.mode = normalizeMode(draft.data.mode);

        updateModeButtons();

      }

      if (draft.data.language) {

        state.language = normalizeLanguage(draft.data.language);

        updateLanguageButtons();

      }

    }

    const modes = await IaApi.get("/modes");

    if (modes.ok && modes.data?.active) {

      state.mode = normalizeMode(modes.data.active);

      updateModeButtons();

    }

    const languages = await IaApi.get("/languages");

    if (languages.ok && languages.data?.active) {

      state.language = normalizeLanguage(languages.data.active);

      updateLanguageButtons();

    }

    const ctx = await IaApi.get("/context");

    if (ctx.ok && ctx.data) {
      applyContextPayload(ctx.data);
    }

    if (PROMPT_KEYS.some((p) => p.id === state.mode)) {

      setPromptKey(state.mode);

    } else {

      void loadPromptEditor();

    }

  }



  async function refreshCaptionSnapshot() {
    const draft = await IaApi.get("/draft?full=1");
    if (draft.ok && draft.data) applyCaptionSnapshot(draft.data, "refresh");
  }

  async function checkCompanionHealth() {

    const health = await IaApi.get("/health");

    setConnectionState(health.ok ? "connected" : "offline");

    if (health.ok) {
      if (!companionBootstrapped) {
        companionBootstrapped = true;
        await loadCompanionData();
      } else {
        await refreshCaptionSnapshot();
      }
      renderFeed();
    } else {
      companionBootstrapped = false;
    }

    return health.ok;

  }



  function startHealthPoll() {

    stopHealthPoll();

    healthPollId = setInterval(() => void checkCompanionHealth(), HEALTH_POLL_MS);

  }



  function stopHealthPoll() {

    if (healthPollId) {

      clearInterval(healthPollId);

      healthPollId = null;

    }

  }

  function startDraftPoll() {
    stopDraftPoll();
    draftPollId = setInterval(() => void pollDraftFast(), DRAFT_POLL_MS);
  }

  function stopDraftPoll() {
    if (draftPollId) {
      clearInterval(draftPollId);
      draftPollId = null;
    }
  }

  async function pollDraftFast() {
    if (!state.connected) return;
    const path = captionResyncPending ? "/draft?full=1" : "/draft";
    const draft = await IaApi.get(path);
    if (!draft.ok || !draft.data || draft.data.changed === false) return;
    const prevFull = state.fullCaption || "";
    applyCaptionSnapshot(draft.data, "poll");
    const nextFull = state.fullCaption || "";
    if (nextFull !== prevFull || captionResyncPending) renderFeedImmediate();
  }

  function scheduleRenderFeed() {
    if (renderFeedScheduled) return;
    renderFeedScheduled = true;
    requestAnimationFrame(() => {
      renderFeedScheduled = false;
      renderFeed();
    });
  }

  /** SSE caption path — paint immediately, no rAF frame wait. */
  function renderFeedImmediate() {
    renderFeedScheduled = false;
    renderFeed();
  }



  function normalizeMode(id) {

    const m = MODES.find((x) => x.id === id);

    return m ? m.id : "read";

  }

  function normalizeLanguage(id) {

    const lang = LANGUAGES.find((x) => x.id === id);

    return lang ? lang.id : "english";

  }



  function setConnectionState(mode) {

    const connected = mode === "connected";

    state.connected = connected;

    els.status.classList.remove("connecting", "connected", "offline");

    els.status.classList.add(mode);

    const labels = {

      connecting: "Connecting…",

      connected: "Connected",

      offline: "Companion offline",

    };

    const titles = {

      connecting: "Checking companion at http://127.0.0.1:1212",

      connected: "Companion is running",

      offline: "Start Interview Assistant Companion tray app",

    };

    if (els.statusLabel) {

      els.statusLabel.textContent = labels[mode] || labels.offline;

    }

    els.status.title = titles[mode] || titles.offline;

  }



  function setTab(tab) {
    const prevTab = state.tab;
    state.tab = tab;

    els.root.querySelectorAll(".ia-tab").forEach((b) => {

      b.classList.toggle("active", b.dataset.tab === tab);

    });

    els.captionView.classList.toggle("hidden", tab !== "caption");

    els.settings.classList.toggle("visible", tab === "settings");

    if (els.historyTab) {
      els.historyTab.classList.toggle("visible", tab === "history");
    }

    if (tab === "settings") {

      setSettingsAccordionSection(state.settingsSection || "resume");

      if (PROMPT_KEYS.some((p) => p.id === state.mode)) setPromptKey(state.mode);

      else void loadPromptEditor();

    }

    if (tab === "history" && prevTab !== "history") {
      setHistoryMode("list");
      void loadHistoryFileList();
    }

  }



  function onSseMessage(msg) {

    if (msg.type === "draft" && msg.payload) {

      applyCaptionSnapshot(msg.payload, "sse");

      setConnectionState("connected");

      renderFeedImmediate();

    }

    if (msg.type === "connection") {

      if (msg.payload?.ok) setConnectionState("connected");

      else void checkCompanionHealth();

    }

  }



  function renderFeed() {
    if (state.editingKey) return;

    const items = getCaptionItems();
    const captionKey = `${state.fullCaption}|${state.endpoint}|${state.skipThrough}|${state.selection ? state.selection.anchorIdx : ""}:${state.selection ? state.selection.endIdx : ""}`;

    if (!items.length) {
      lastRenderedCaption = "";
      els.feed.innerHTML = state.connected
        ? '<p class="ia-hint ia-feed-empty">Listening for live captions…</p>'
        : "";
      return;
    }

    const existing = els.feed.querySelectorAll(".ia-sentence");
    const canPatch =
      captionKey !== lastRenderedCaption &&
      existing.length > 0 &&
      items.length >= existing.length &&
      items.length - existing.length <= 3;

    if (canPatch) {
      let patchFrom = 0;
      if (items.length === existing.length) {
        for (let i = 0; i < items.length; i++) {
          const label = existing[i]?.querySelector(".ia-sentence-text");
          const props = sentenceBoxProps(items, i);
          if (!label || label.textContent !== (props.text || "")) {
            patchFrom = i;
            break;
          }
        }
        if (patchFrom === 0 && items.length > 0) {
          const lastProps = sentenceBoxProps(items, items.length - 1);
          const lastLabel = existing[items.length - 1]?.querySelector(".ia-sentence-text");
          if (lastLabel && lastLabel.textContent === (lastProps.text || "")) {
            patchFrom = items.length;
          }
        }
      } else {
        patchFrom = Math.max(0, existing.length - 1);
      }

      for (let i = patchFrom; i < items.length; i++) {
        const props = sentenceBoxProps(items, i);
        if (i < existing.length) {
          updateSentenceBox(existing[i], props);
        } else {
          els.feed.appendChild(renderSentenceBox(props));
        }
      }
      if (patchFrom >= items.length) {
        for (let i = 0; i < items.length; i++) {
          updateSentenceBox(existing[i], sentenceBoxProps(items, i));
        }
      }
      while (els.feed.children.length > items.length) {
        els.feed.lastChild?.remove();
      }
    } else if (captionKey !== lastRenderedCaption || existing.length !== items.length) {
      els.feed.innerHTML = "";
      items.forEach((item, i) => {
        els.feed.appendChild(renderSentenceBox(sentenceBoxProps(items, i)));
      });
    }

    lastRenderedCaption = captionKey;
    applySelectionStyles();
    scrollFeedToBottom();
  }

  function updateSentenceBox(el, { text, pending, selected, live, index }) {
    let cls = "ia-sentence";
    if (pending || selected) cls += pending ? " ia-sentence--pending" : " ia-sentence--selected";
    else if (getGreenStart() > 0) cls += " ia-sentence--sent";
    if (live) cls += " ia-sentence--live";
    el.className = cls;

    if (index !== undefined && index >= 0) el.dataset.sentenceIndex = String(index);

    const label = el.querySelector(".ia-sentence-text");

    if (label && label.textContent !== (text || "")) label.textContent = text || "";

  }



  function renderSentenceBox({ key, text, pending, selected, live, index }) {
    const div = document.createElement("div");
    let cls = "ia-sentence";
    if (pending || selected) cls += pending ? " ia-sentence--pending" : " ia-sentence--selected";
    else if (getGreenStart() > 0) cls += " ia-sentence--sent";
    if (live) cls += " ia-sentence--live";
    div.className = cls;

    div.dataset.sentenceKey = key;

    if (index !== undefined && index >= 0) div.dataset.sentenceIndex = String(index);

    const label = document.createElement("div");

    label.className = "ia-sentence-text";

    label.textContent = text || "";

    div.appendChild(label);

    div.addEventListener("dblclick", (e) => {

      if (e.target.closest("button")) return;

      const current = div.querySelector(".ia-sentence-text")?.textContent || "";

      startSentenceEdit(key, sentenceText(key, current), div);

    });

    return div;

  }



  function startSentenceEdit(key, text, boxEl) {

    if (state.editingKey) return;

    state.editingKey = key;

    els.btnSave.style.display = "";

    const ta = document.createElement("textarea");

    ta.className = "ia-sentence-editor";

    ta.value = text || "";

    boxEl.innerHTML = "";

    boxEl.appendChild(ta);

    ta.focus();

    ta.select();

  }



  function onSaveEdit() {

    const ta = els.feed.querySelector(".ia-sentence-editor");

    if (!ta || !state.editingKey) return;

    state.sentenceEdits[state.editingKey] = ta.value.trim();

    state.editingKey = null;

    els.btnSave.style.display = "none";

    renderFeed();

  }



  async function onSend() {
    const range = getPendingSendRange();
    const chunkOverride = buildPendingChunk();

    if (!range || !chunkOverride.trim()) {
      window.IaToast?.warning("Nothing to send — green sentences are sent.");
      return;
    }

    const items = getCaptionItems();

    const startChar = items[range.anchorIdx] ? items[range.anchorIdx].start : 0;

    await IaApi.post("/endpoint", { start_index: startChar });

    const body = { chunk: chunkOverride };

    const r = await IaApi.post("/end", body);

    if (!r.ok) {

      window.IaToast?.error(r.error || "Companion not running");

      return;

    }

    const data = r.data;

    if (!data.ok) {

      window.IaToast?.warning(data.message || "Nothing to send");

      return;

    }

    applyCaptionSnapshot(data, "send-end");

    await setEndpointToNow();

    state.selection = null;

    clearPendingSentenceEdits();

    renderFeed();

    if (!data.prompt) return;

    const captionForRecord = chunkOverride.trim();
    const sentResult = await sendPromptToGpt(data.prompt, true);

    if (state.recording && sentResult?.ok && captionForRecord) {
      // Wait a short moment then grab GPT result (generation already finished by sendPromptToGpt)
      await new Promise((r) => setTimeout(r, 400));
      const gptText = resolveLatestGptResultText();
      if (gptText) {
        state.recordingPairs.push({
          caption: captionForRecord,
          result: gptText,
          ts_utc: new Date().toISOString(),
        });
        updateRecordBadge();
      }
    }

  }

  async function onQuoteCaption() {
    if (quoteSendInProgress || sendInProgress) return;
    const range = getPendingSendRange();
    const chunk = buildPendingChunk();
    if (!range || !chunk.trim()) {
      window.IaToast?.warning("Nothing to quote — select green captions first.");
      return;
    }

    quoteSendInProgress = true;
    try {
      const items = getCaptionItems();
      const startChar = items[range.anchorIdx] ? items[range.anchorIdx].start : 0;
      await IaApi.post("/endpoint", { start_index: startChar });
      const r = await IaApi.post("/end", { chunk });
      if (!r.ok) {
        window.IaToast?.error(r.error || "Companion not running");
        return;
      }
      const data = r.data;
      if (!data?.ok) {
        window.IaToast?.warning(data?.message || "Nothing to quote");
        return;
      }

      applyCaptionSnapshot(data, "quote-send");
      await refreshCaptionSnapshot();
      await setEndpointToNow();
      state.selection = null;
      clearPendingSentenceEdits();
      renderFeed();

      const quote = formatInterviewerQuote(chunk);
      const pasted = await pasteTextToComposer(quote, true);
      if (pasted?.ok) {
        window.IaToast?.success("Interviewer quote pasted into prompt (not sent).");
      }
    } finally {
      quoteSendInProgress = false;
    }
  }



  async function onReject() {
    const range = getPendingSendRange();
    const chunk = buildPendingChunk();

    if (!range || !chunk.trim()) {
      window.IaToast?.warning("Nothing to skip — green sentences are skipped.");
      return;
    }

    const items = getCaptionItems();

    const startChar = items[range.anchorIdx] ? items[range.anchorIdx].start : 0;

    await IaApi.post("/endpoint", { start_index: startChar });

    const r = await IaApi.post("/delete");

    if (!r.ok) {

      window.IaToast?.error(r.error || "Companion not running");

      return;

    }

    if (r.data) applyCaptionSnapshot(r.data, "skip");

    const skipped = (r.data?.skipped || "").trim();

    if (!skipped) {

      window.IaToast?.warning(r.data?.message || "Nothing to skip.");

      return;

    }

    /* Skip grays pending text but does not move send endpoint. */
    state.skipThrough = (state.fullCaption || "").length;

    state.selection = null;

    clearPendingSentenceEdits();

    renderFeed();

    window.IaToast?.info("Skipped.");

  }



  async function setLanguage(id) {

    const language = normalizeLanguage(id);

    state.language = language;

    updateLanguageButtons();

    await IaApi.post("/language", { language });

    if (state.tab === "settings" && PROMPT_KEYS.some((p) => p.id === language)) {

      setPromptKey(language);

    }

  }



  async function setMode(id) {

    const mode = normalizeMode(id);

    state.mode = mode;

    updateModeButtons();

    await IaApi.post("/mode", { mode });

    if (state.tab === "settings" && PROMPT_KEYS.some((p) => p.id === mode)) {

      setPromptKey(mode);

    }

  }



  async function onPasteDraft(append) {
    const text = buildPendingChunk() || (state.draft || "").trim();
    const result = await pasteTextToComposer(text, append !== false);
    if (result?.ok) {
      window.IaToast?.success("Live caption pasted into prompt (not sent).");
    }
  }



  async function saveResume() {
    const name = (els.resumeName?.value || "").trim();
    const text = (els.resume?.value || "").trim();
    if (!name || !text) {
      window.IaToast?.warning("Name and content are both required to save a resume.");
      return;
    }
    const r = await IaApi.post("/context/resume", { name, text });
    if (r.ok) {
      applyContextPayload(r.data?.context || r.data);
      window.IaToast?.success("Resume saved.");
    } else {
      window.IaToast?.error(r.data?.error || "Could not save resume.");
    }
  }

  async function saveJd() {
    const name = (els.jdName?.value || "").trim();
    const text = (els.jd?.value || "").trim();
    if (!name || !text) {
      window.IaToast?.warning("Name and content are both required to save a JD.");
      return;
    }
    const r = await IaApi.post("/context/jd", { name, text });
    if (r.ok) {
      applyContextPayload(r.data?.context || r.data);
      window.IaToast?.success("Job description saved.");
    } else {
      window.IaToast?.error(r.data?.error || "Could not save JD.");
    }
  }

  function selectDocHistoryItem(kind, item) {
    const isResume = kind === "resume";
    if (isResume) {
      state.selectedResumeName = item?.name || "";
      if (els.resumeName) els.resumeName.value = item?.name || "";
      if (els.resume) els.resume.value = item?.text || "";
    } else {
      state.selectedJdName = item?.name || "";
      if (els.jdName) els.jdName.value = item?.name || "";
      if (els.jd) els.jd.value = item?.text || "";
    }
    renderDocHistory(kind);
  }

  function applyContextPayload(data) {
    if (!data) return;
    state.contextResumes = Array.isArray(data.resumes) ? data.resumes : [];
    state.contextJds = Array.isArray(data.jds) ? data.jds : [];
    state.activeResumeName = data.active_resume || "";
    state.activeJdName = data.active_jd || "";
    state.selectedResumeName = state.activeResumeName;
    state.selectedJdName = state.activeJdName;
    if (els.resumeName) els.resumeName.value = state.activeResumeName;
    if (els.resume) els.resume.value = data.resume || "";
    if (els.jdName) els.jdName.value = state.activeJdName;
    if (els.jd) els.jd.value = data.job_description || "";
    renderDocHistory("resume");
    renderDocHistory("jd");
  }

  function renderDocHistory(kind) {
    const isResume = kind === "resume";
    const listEl = isResume ? els.resumeHistory : els.jdHistory;
    const items = isResume ? state.contextResumes : state.contextJds;
    const selectedName = isResume ? state.selectedResumeName : state.selectedJdName;
    if (!listEl) return;

    listEl.replaceChildren();
    if (!items.length) {
      const empty = document.createElement("p");
      empty.className = "ia-context-history-empty";
      empty.textContent = "No saved items yet.";
      listEl.appendChild(empty);
      return;
    }

    const iconTrash = window.IaIcons?.trash || "";
    for (const item of items) {
      const row = document.createElement("div");
      row.className = "ia-context-history-item";
      if (item.name && contextNamesEqual(item.name, selectedName)) {
        row.classList.add("ia-context-history-item--active");
      }

      const selectBtn = document.createElement("button");
      selectBtn.type = "button";
      selectBtn.className = "ia-context-history-select";
      selectBtn.title = "Use for prompts";
      selectBtn.textContent = item.name || "(unnamed)";
      selectBtn.addEventListener("click", () => selectDocHistoryItem(kind, item));

      const deleteBtn = document.createElement("button");
      deleteBtn.type = "button";
      deleteBtn.className = "ia-context-history-delete";
      deleteBtn.title = "Delete from history";
      deleteBtn.setAttribute("aria-label", `Delete ${item.name || "item"}`);
      deleteBtn.innerHTML = iconTrash;
      deleteBtn.addEventListener("click", async (e) => {
        e.stopPropagation();
        const path = isResume
          ? `/context/resume/${encodeURIComponent(item.name)}`
          : `/context/jd/${encodeURIComponent(item.name)}`;
        const r = await IaApi.del(path);
        if (r.ok) {
          applyContextPayload(r.data);
          window.IaToast?.success(isResume ? "Resume removed." : "JD removed.");
        } else {
          window.IaToast?.error("Could not delete.");
        }
      });

      row.appendChild(selectBtn);
      row.appendChild(deleteBtn);
      listEl.appendChild(row);
    }
  }

  function fileBaseName(file) {
    const n = file?.name || "document";
    const dot = n.lastIndexOf(".");
    return dot > 0 ? n.slice(0, dot) : n;
  }

  function setupFileUpload(btnId, inputId, opts) {
    const btn = els.root.getElementById(btnId);
    const input = els.root.getElementById(inputId);
    const getTextarea = opts?.getTextarea;
    const getNameInput = opts?.getNameInput;
    if (!btn || !input || !getTextarea) return;

    btn.addEventListener("click", () => input.click());
    input.addEventListener("change", async () => {
      const file = input.files?.[0];
      input.value = "";
      if (!file) return;
      btn.disabled = true;
      btn.textContent = "Reading…";
      try {
        const text = await window.IaFileImport.extractText(file);
        getTextarea().value = text;
        if (getNameInput) getNameInput().value = fileBaseName(file);
        if (btnId === "ia-upload-resume") state.selectedResumeName = "";
        else state.selectedJdName = "";
        renderDocHistory(btnId === "ia-upload-resume" ? "resume" : "jd");
        window.IaToast?.success("File loaded — set the name if needed, then Save.");
      } catch (e) {
        window.IaToast?.error("Could not read file: " + (e.message || e));
      } finally {
        btn.disabled = false;
        btn.innerHTML = `${window.IaIcons?.upload || "↑"} Upload`;
      }
    });
  }



  async function loadPromptEditor() {

    const key = state.promptKey;

    els.promptEditor.placeholder = "Loading…";

    const r = await IaApi.get(`/prompts/${encodeURIComponent(key)}`);

    if (r.ok && r.data) {

      els.promptEditor.value = r.data.text ?? "";

      els.promptEditor.placeholder = "";

    } else {

      els.promptEditor.value = "";

      els.promptEditor.placeholder = state.connected

        ? "No prompt text for this template."

        : "Start companion tray app to load prompts.";

    }

  }



  async function savePrompt() {

    const key = state.promptKey;

    const r = await IaApi.post(`/prompts/${encodeURIComponent(key)}`, { text: els.promptEditor.value });

    if (!r.ok) window.IaToast?.error(r.error || "Could not save prompt");
    else window.IaToast?.success("Saved.");

  }



  return { mount, activate, scriptToken: SCRIPT_TOKEN };

})();


