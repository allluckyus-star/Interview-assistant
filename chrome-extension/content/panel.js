window.IaPanel = (function () {

  const SCRIPT_TOKEN = String(Date.now());

  const MODES = [

    { id: "read", label: "Read", short: "Read" },

    { id: "type", label: "Type", short: "Type" },

    { id: "behavioral", label: "Behavioral", short: "Beh" },

  ];



  const PROMPT_KEYS = [

    { id: "resume_summary", label: "Resume summary" },

    { id: "jd_summary", label: "JD summary" },

    { id: "initial_interview", label: "Initial interview" },

    { id: "read", label: "Read" },

    { id: "type", label: "Type" },

    { id: "behavioral", label: "Behavioral" },

  ];



  let state = {

    tab: "caption",

    mode: "read",

    promptKey: "read",

    draft: "",

    fullCaption: "",

    /** Send boundary — set on Send only. */
    endpoint: 0,

    /** Skip boundary — set on Skip (not Send); grays skipped draft without moving endpoint. */
    skipThrough: 0,

    /** Sync efficiency: only live-sync sentences after this index (~20 before edge). Not green. */
    pendingStart: 0,

    connected: false,

    sentenceEdits: {},

    editingKey: null,

    lastRenderedPendingStart: -1,

  };



  let els = {};
  let disconnectEvents = null;
  let healthPollId = null;
  let companionBootstrapped = false;
  let autoscrollEnabled = true;
  let autoscrollResumeTimer = null;
  let suppressFeedScroll = false;

  let sendInProgress = false;

  /** Live-sync tail size — fixed captions older than this do not need re-upload. */
  const LIVE_SYNC_SENTENCES = 20;

  const AUTOSCROLL_RESUME_MS = 2000;
  const HEALTH_POLL_MS = 3000;

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

  function computePendingStart(full) {
    return window.IaCaptionSentences?.pendingStartIndex(full, LIVE_SYNC_SENTENCES) ?? 0;
  }

  function applyCaptionSnapshot(data) {
    if (!data) return;
    if (data.draft !== undefined) state.draft = data.draft || "";
    if (data.full !== undefined) state.fullCaption = data.full || "";
    else if (!state.fullCaption && state.draft) state.fullCaption = state.draft;
    state.pendingStart = computePendingStart(getFeedCaption());
    prunePendingEdits();
  }

  function sentenceText(key, fallback) {
    if (Object.prototype.hasOwnProperty.call(state.sentenceEdits, key)) {
      return state.sentenceEdits[key];
    }
    return fallback;
  }

  /** Green zone starts after both last send and last skip. */
  function getGreenStart() {
    return Math.max(state.endpoint || 0, state.skipThrough || 0);
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

  async function getContextTexts() {
    const ctx = await IaApi.get("/context");
    const savedResume = ctx.ok ? (ctx.data?.resume || "").trim() : "";
    const savedJd = ctx.ok ? (ctx.data?.job_description || "").trim() : "";
    return {
      resume: (els.resume?.value || "").trim() || savedResume,
      jd: (els.jd?.value || "").trim() || savedJd,
    };
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
      await sendPromptToGpt(template, false);
    }
  }

  function buildPendingChunk() {
    const greenStart = getGreenStart();
    const items = getCaptionItems().filter((it) => it.end > greenStart);
    return joinSentences(items.map((it) => sentenceText(`l-${it.start}`, it.text)));
  }

  function clearPendingSentenceEdits() {
    Object.keys(state.sentenceEdits).forEach((k) => {
      if (k.startsWith("l-")) delete state.sentenceEdits[k];
    });
  }

  function prunePendingEdits() {
    const pendingStart = state.pendingStart || 0;
    Object.keys(state.sentenceEdits).forEach((k) => {
      if (!k.startsWith("l-")) {
        delete state.sentenceEdits[k];
        return;
      }
      const start = parseInt(k.slice(2), 10);
      if (Number.isNaN(start) || start < pendingStart) delete state.sentenceEdits[k];
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

      feedFixed: null,

      feedLive: null,

      captionView: root.getElementById("ia-caption-view"),

      settings: root.getElementById("ia-settings"),

      resume: root.getElementById("ia-resume"),

      jd: root.getElementById("ia-jd"),

      promptNav: root.getElementById("ia-prompt-nav"),

      promptEditor: root.getElementById("ia-prompt-editor"),

      modeSeg: root.getElementById("ia-mode-seg"),

      btnResumeSend: root.getElementById("ia-btn-resume-send"),

      btnJdSend: root.getElementById("ia-btn-jd-send"),

      btnInterviewStart: root.getElementById("ia-btn-interview-start"),

      btnSave: root.getElementById("ia-btn-save"),

      btnSend: root.getElementById("ia-btn-send"),

      btnReject: root.getElementById("ia-btn-reject"),

      btnImage: root.getElementById("ia-btn-image"),

      btnText: root.getElementById("ia-btn-text"),

    };

  }



  function wirePanelEvents(root) {

    root.querySelectorAll(".ia-tab").forEach((btn) => {

      btn.addEventListener("click", () => setTab(btn.dataset.tab));

    });

    els.btnSend.addEventListener("click", onSend);

    els.btnReject.addEventListener("click", onReject);

    els.btnResumeSend?.addEventListener("click", () => onPrepSend("resume_summary"));

    els.btnJdSend?.addEventListener("click", () => onPrepSend("jd_summary"));

    els.btnInterviewStart?.addEventListener("click", () => onPrepSend("initial_interview"));

    els.btnSave.addEventListener("click", onSaveEdit);

    els.btnText.addEventListener("click", () => onPasteDraft(false));

    els.btnImage.addEventListener("click", () => onPasteDraft(false));

    root.getElementById("ia-save-resume").addEventListener("click", saveResume);

    root.getElementById("ia-save-jd").addEventListener("click", saveJd);

    setupFileUpload("ia-upload-resume", "ia-resume-file", () => els.resume, saveResume);

    setupFileUpload("ia-upload-jd", "ia-jd-file", () => els.jd, saveJd);

    root.getElementById("ia-save-prompt").addEventListener("click", savePrompt);

    const collapseBtn = root.getElementById("ia-panel-collapse-btn");

    collapseBtn?.addEventListener("click", (e) => {

      e.stopPropagation();

      if (window.IaLayout?.toggleCollapsed) window.IaLayout.toggleCollapsed();

    });

    setupFeedAutoscroll();

  }



  function activate(hostEl) {

    const root = hostEl.shadowRoot;

    if (!root) return mount(hostEl);

    bindElements(root);

    renderModeButtons();

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

              <div class="ia-status connecting" id="ia-status" aria-live="polite">
                <span class="ia-status-dot" aria-hidden="true"></span>
                <span class="ia-status-label">Connecting…</span>
              </div>

            </div>

          </div>

          <button type="button" class="ia-rail-btn" id="ia-panel-collapse-btn" title="Collapse panel">${I.chevronRight || "›"}</button>

        </header>

        <div class="ia-main-chrome">

          <nav class="ia-seg-tabs" aria-label="Panel sections">

            <button type="button" class="ia-tab active" data-tab="caption">Caption</button>

            <button type="button" class="ia-tab" data-tab="settings">Settings</button>

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

                  <button type="button" class="ia-icon-btn ghost" id="ia-btn-save" title="Save edit" style="display:none">${I.check || "✓"}</button>

                  <button type="button" class="ia-icon-btn primary" id="ia-btn-send" title="Send to ChatGPT">${I.send || "→"}</button>

                  <button type="button" class="ia-icon-btn" id="ia-btn-reject" title="Skip draft">${I.close || "×"}</button>

                  <button type="button" class="ia-icon-btn" id="ia-btn-image" title="Paste draft">${I.image || "▣"}</button>

                  <button type="button" class="ia-icon-btn" id="ia-btn-text" title="Paste draft to composer">${I.text || "T"}</button>

                </div>

              </div>

              <p class="ia-hint">Double-click to edit · green = since last send</p>

              <div class="ia-body" id="ia-feed"></div>

            </div>

            <div class="ia-settings" id="ia-settings">

              <div class="ia-settings-card ia-settings-card--compact">

                <label>Resume</label>

                <textarea id="ia-resume" placeholder="Paste or type resume…"></textarea>

                <div class="ia-settings-actions">
                  <input type="file" id="ia-resume-file" class="ia-file-input" accept=".txt,.pdf,.docx,text/plain,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document" hidden />
                  <button type="button" class="ia-btn-upload" id="ia-upload-resume">${I.upload || "↑"} Upload</button>
                  <button type="button" class="ia-btn-save" id="ia-save-resume">Save resume</button>
                </div>

              </div>

              <div class="ia-settings-card ia-settings-card--compact">

                <label>Job description</label>

                <textarea id="ia-jd" placeholder="Paste or type JD…"></textarea>

                <div class="ia-settings-actions">
                  <input type="file" id="ia-jd-file" class="ia-file-input" accept=".txt,.pdf,.docx,text/plain,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document" hidden />
                  <button type="button" class="ia-btn-upload" id="ia-upload-jd">${I.upload || "↑"} Upload</button>
                  <button type="button" class="ia-btn-save" id="ia-save-jd">Save JD</button>
                </div>

              </div>

              <div class="ia-settings-card ia-settings-card--prompt">

                <label>Prompt template</label>

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

    `;

    root.appendChild(wrap.firstElementChild);

    bindElements(root);

    wirePanelEvents(root);

    renderModeButtons();

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



  function renderPromptNav() {

    els.promptNav.innerHTML = "";

    PROMPT_KEYS.forEach((p) => {

      const btn = document.createElement("button");

      btn.type = "button";

      btn.className = "ia-prompt-nav-btn";

      btn.dataset.key = p.id;

      btn.innerHTML = (window.IaIcons && window.IaIcons.forPromptKey(p.id)) || "";

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



  async function init() {

    if (window.IaLayout && !window.IaLayout.isChatGptPage()) return;

    setConnectionState("connecting");

    await checkCompanionHealth();

    if (disconnectEvents) disconnectEvents();

    disconnectEvents = IaApi.connectEvents(onSseMessage);

    startHealthPoll();

    renderFeed();

  }



  async function loadCompanionData() {

    await IaApi.post("/session/start");

    state.endpoint = 0;

    state.skipThrough = 0;

    state.lastRenderedPendingStart = -1;

    const draft = await IaApi.get("/draft");

    if (draft.ok && draft.data) {

      applyCaptionSnapshot(draft.data);

      if (draft.data.mode) {

        state.mode = normalizeMode(draft.data.mode);

        updateModeButtons();

      }

    }

    const modes = await IaApi.get("/modes");

    if (modes.ok && modes.data?.active) {

      state.mode = normalizeMode(modes.data.active);

      updateModeButtons();

    }

    const ctx = await IaApi.get("/context");

    if (ctx.ok && ctx.data) {

      els.resume.value = ctx.data.resume || "";

      els.jd.value = ctx.data.job_description || "";

    }

    if (PROMPT_KEYS.some((p) => p.id === state.mode)) {

      setPromptKey(state.mode);

    } else {

      void loadPromptEditor();

    }

  }



  async function refreshCaptionSnapshot() {
    const draft = await IaApi.get("/draft");
    if (draft.ok && draft.data) applyCaptionSnapshot(draft.data);
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



  function normalizeMode(id) {

    const m = MODES.find((x) => x.id === id);

    return m ? m.id : "read";

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

    state.tab = tab;

    els.root.querySelectorAll(".ia-tab").forEach((b) => {

      b.classList.toggle("active", b.dataset.tab === tab);

    });

    els.captionView.classList.toggle("hidden", tab !== "caption");

    els.settings.classList.toggle("visible", tab === "settings");

    if (tab === "settings") {

      if (PROMPT_KEYS.some((p) => p.id === state.mode)) setPromptKey(state.mode);

      else void loadPromptEditor();

    }

  }



  function onSseMessage(msg) {

    if (msg.type === "draft" && msg.payload) {

      applyCaptionSnapshot(msg.payload);

      setConnectionState("connected");

      renderFeed();

    }

    if (msg.type === "connection") {

      if (msg.payload?.ok) setConnectionState("connected");

      else void checkCompanionHealth();

    }

  }



  function renderFeed() {

    if (state.editingKey) return;

    const items = getCaptionItems();

    if (!items.length) {

      els.feed.innerHTML = "";

      els.feedFixed = null;

      els.feedLive = null;

      state.lastRenderedPendingStart = -1;

      if (state.connected) {
        els.feed.innerHTML = '<p class="ia-hint ia-feed-empty">Listening for live captions…</p>';
      }

      return;

    }

    if (els.feed.querySelector(".ia-feed-empty")) {
      els.feed.innerHTML = "";
      els.feedFixed = null;
      els.feedLive = null;
      state.lastRenderedPendingStart = -1;
    }

    ensureFeedZones();

    /* Fixed: append only when pending_start slides (new sentence leaves live-sync tail). */

    if (state.pendingStart !== state.lastRenderedPendingStart) {

      appendNewFixedSentences();

      state.lastRenderedPendingStart = state.pendingStart;

    }

    /* Live-sync tail (pending_start → now): gray if sent, green if after endpoint. */

    syncLiveZone();

    updateFixedZoneStyles();

    if (!els.feed.querySelector(".ia-sentence")) {
      rebuildFeedFromCaption();
    }

    scrollFeedToBottom();

  }



  /** Fallback when incremental zones fail to paint any boxes. */
  function rebuildFeedFromCaption() {
    const greenStart = getGreenStart();
    const pendingStart = state.pendingStart || 0;
    const items = getCaptionItems();
    els.feed.innerHTML = "";
    els.feedFixed = null;
    els.feedLive = null;
    state.lastRenderedPendingStart = -1;
    ensureFeedZones();
    for (const item of items) {
      const inLive = item.end > pendingStart;
      const key = `${inLive ? "l" : "s"}-${item.start}`;
      const box = renderSentenceBox({
        key,
        text: sentenceText(key, item.text),
        pending: item.end > greenStart,
        live: item.end > greenStart && item === items[items.length - 1],
      });
      (inLive ? els.feedLive : els.feedFixed).appendChild(box);
    }
    state.lastRenderedPendingStart = state.pendingStart;
  }



  function appendNewFixedSentences() {

    const pendingStart = state.pendingStart || 0;

    for (const item of getCaptionItems()) {

      if (item.end > pendingStart) continue;

      const key = `s-${item.start}`;

      if (els.feedFixed.querySelector(`.ia-sentence[data-sentence-key="${CSS.escape(key)}"]`)) continue;

      els.feedFixed.appendChild(

        renderSentenceBox({

          key,

          text: sentenceText(key, item.text),

          pending: item.end > getGreenStart(),

          live: false,

        })

      );

    }

  }



  function syncLiveZone() {

    const pendingStart = state.pendingStart || 0;

    const greenStart = getGreenStart();

    const liveItems = getCaptionItems().filter((it) => it.end > pendingStart);

    const desired = liveItems.map((item, i) => ({

      key: `l-${item.start}`,

      text: sentenceText(`l-${item.start}`, item.text),

      pending: item.end > greenStart,

      live: item.end > greenStart && i === liveItems.length - 1,

    }));

    syncSentenceList(els.feedLive, desired);

  }



  /** Fixed boxes are append-only; restyle green/gray when send/skip boundary moves. */

  function updateFixedZoneStyles() {

    if (!els.feedFixed) return;

    const greenStart = getGreenStart();

    const byKey = new Map(getCaptionItems().map((it) => [`s-${it.start}`, it]));

    els.feedFixed.querySelectorAll(".ia-sentence[data-sentence-key]").forEach((el) => {

      const item = byKey.get(el.dataset.sentenceKey);

      if (!item) return;

      updateSentenceBox(el, {

        text: sentenceText(el.dataset.sentenceKey, item.text),

        pending: item.end > greenStart,

        live: false,

      });

    });

  }



  function ensureFeedZones() {

    if (els.feedFixed?.isConnected) return;

    els.feed.innerHTML = "";

    els.feedFixed = document.createElement("div");

    els.feedFixed.id = "ia-feed-fixed";

    els.feedLive = document.createElement("div");

    els.feedLive.id = "ia-feed-live";

    els.feed.appendChild(els.feedFixed);

    els.feed.appendChild(els.feedLive);

    state.lastRenderedPendingStart = -1;

  }



  function syncSentenceList(container, desired) {

    if (!container) return;

    const existing = new Map();

    container.querySelectorAll(".ia-sentence").forEach((el) => {

      if (el.dataset.sentenceKey) existing.set(el.dataset.sentenceKey, el);

    });

    const desiredKeys = new Set(desired.map((d) => d.key));

    existing.forEach((el, key) => {

      if (!desiredKeys.has(key)) el.remove();

    });

    let prev = null;

    for (const d of desired) {

      let el = container.querySelector(`.ia-sentence[data-sentence-key="${CSS.escape(d.key)}"]`);

      if (!el) el = renderSentenceBox(d);

      else updateSentenceBox(el, d);

      if (el.previousElementSibling !== prev) {

        if (prev) prev.after(el);

        else container.prepend(el);

      }

      prev = el;

    }

  }



  function updateSentenceBox(el, { text, pending, live }) {

    el.className =

      "ia-sentence" +

      (pending ? " ia-sentence--pending" : "") +

      (live ? " ia-sentence--live" : "");

    const label = el.querySelector(".ia-sentence-text");

    if (label && label.textContent !== (text || "")) label.textContent = text || "";

  }



  function renderSentenceBox({ key, text, pending, live }) {

    const div = document.createElement("div");

    div.className = "ia-sentence" + (pending ? " ia-sentence--pending" : "") + (live ? " ia-sentence--live" : "");

    div.dataset.sentenceKey = key;

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

    const chunkOverride = buildPendingChunk();

    const body = chunkOverride ? { chunk: chunkOverride } : undefined;

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

    applyCaptionSnapshot(data);

    /* Endpoint = "now" at send time (end of full caption). */
    state.endpoint = (state.fullCaption || "").length;

    clearPendingSentenceEdits();

    renderFeed();

    if (!data.prompt) return;

    await sendPromptToGpt(data.prompt, true);

  }



  async function onReject() {

    const greenStart = getGreenStart();

    const chunk = buildPendingChunk();

    if (!chunk.trim()) {

      window.IaToast?.warning("Nothing to skip.");

      return;

    }

    await IaApi.post("/endpoint", { start_index: greenStart });

    const r = await IaApi.post("/delete");

    if (!r.ok) {

      window.IaToast?.error(r.error || "Companion not running");

      return;

    }

    if (r.data) applyCaptionSnapshot(r.data);

    const skipped = (r.data?.skipped || "").trim();

    if (!skipped) {

      window.IaToast?.warning(r.data?.message || "Nothing to skip.");

      return;

    }

    /* Skip grays pending text but does not move send endpoint. */
    state.skipThrough = (state.fullCaption || "").length;

    clearPendingSentenceEdits();

    renderFeed();

    window.IaToast?.info("Skipped.");

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

    if (!text) {
      window.IaToast?.warning("No live caption draft to paste.");
      return;
    }
    const ready = window.__iaExtensionGetGptReady?.();
    if (!ready?.ok) {
      window.IaToast?.warning(gptNotReadyMessage(ready));
      return;
    }
    if (window.__iaExtensionPasteDraft) {
      const result = await window.__iaExtensionPasteDraft(text, append !== false);
      if (result?.ok) window.IaToast?.success("Live caption pasted into prompt.");
      else window.IaToast?.warning(sendFailureMessage(result));
    }

  }



  async function saveResume() {

    await IaApi.post("/context/resume", { text: els.resume.value });

  }



  async function saveJd() {

    await IaApi.post("/context/jd", { text: els.jd.value });

  }



  function setupFileUpload(btnId, inputId, getTextarea, saveFn) {
    const btn = els.root.getElementById(btnId);
    const input = els.root.getElementById(inputId);
    if (!btn || !input) return;

    btn.addEventListener("click", () => input.click());
    input.addEventListener("change", async () => {
      const file = input.files?.[0];
      input.value = "";
      if (!file) return;
      const label = btn.textContent;
      btn.disabled = true;
      btn.textContent = "Reading…";
      try {
        const text = await window.IaFileImport.extractText(file);
        getTextarea().value = text;
        await saveFn();
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


