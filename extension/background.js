const BRIDGE_URL = "http://127.0.0.1:8765/next-prompt";
const PREP_JOB_BASE = "http://127.0.0.1:8765/prep/job";
const PREP_EXT_STATUS_URL = "http://127.0.0.1:8765/prep/extension-status";
const WS_INTERVIEW_BASE = "ws://127.0.0.1:8766/ws";
const INTERVIEW_LIVE_URL = "http://127.0.0.1:8765/interview-live";
const ANSWER_ENDPOINT_BG = "http://127.0.0.1:8765/answer";
/** One-shot alarm name; rescheduled each tick (Chrome recurring alarms are min 1 minute). */
const BRIDGE_TICK_ALARM = "bridgeTick";
const TICK_MS = 2000;

let lastRequestId = "";
let lastHandledPrepJobId = "";
let prepTabId = null;

const BRIDGED_PREP_TAB_KEY = "bridgedPrepTabId";

function isChatGptPageUrl(url) {
  if (!url) return false;
  return url.includes("chatgpt.com") || url.includes("chat.openai.com");
}

/** Remember which ChatGPT tab prep (or first bridge) used — survives service worker sleep; avoids targeting random ChatGPT tabs. */
function rememberPrepTabId(tabId) {
  prepTabId = tabId;
  if (tabId != null) {
    chrome.storage.local.set({ [BRIDGED_PREP_TAB_KEY]: tabId });
  }
}

/**
 * Same tab the HTTP /next-prompt path uses: bridged prep tab first, then storage, then focused-window ChatGPT only
 * (never first match across all Chrome windows).
 */
async function resolveInterviewTargetTabId() {
  const tryId = async (id) => {
    if (id == null) return null;
    const t = await chrome.tabs.get(id).catch(() => null);
    if (t && t.id != null && isChatGptPageUrl(t.url)) return t.id;
    return null;
  };

  let id = await tryId(prepTabId);
  if (id != null) return id;

  const data = await chrome.storage.local.get([BRIDGED_PREP_TAB_KEY]);
  id = await tryId(data[BRIDGED_PREP_TAB_KEY]);
  if (id != null) {
    prepTabId = id;
    return id;
  }

  const chatgptUrls = [
    "https://chat.openai.com/*",
    "https://chatgpt.com/*",
    "https://www.chatgpt.com/*",
  ];

  let tabs = await chrome.tabs.query({ active: true, currentWindow: true, url: chatgptUrls });
  if (tabs.length && tabs[0].id != null) return tabs[0].id;

  tabs = await chrome.tabs.query({ active: true, lastFocusedWindow: true, url: chatgptUrls });
  if (tabs.length && tabs[0].id != null) return tabs[0].id;

  tabs = await chrome.tabs.query({ currentWindow: true, url: chatgptUrls });
  if (tabs.length) {
    const sorted = [...tabs].sort((a, b) => (b.lastAccessed || 0) - (a.lastAccessed || 0));
    if (sorted[0].id != null) return sorted[0].id;
  }

  return null;
}

/** Interview WS in the service worker — content scripts cannot reach loopback from https://chatgpt.com (Chrome PNA). */
let interviewWs = null;
let wsReconnectMs = 4000;
let wsReconnectTimer = null;

function isAllowedBridgeHttpUrl(url) {
  try {
    const u = new URL(String(url));
    if (u.protocol !== "http:") return false;
    if (u.port !== "8765") return false;
    return u.hostname === "127.0.0.1" || u.hostname === "localhost";
  } catch (_e) {
    return false;
  }
}

async function forwardInterviewWsToTab(msg) {
  const tabId = await resolveInterviewTargetTabId();
  if (tabId == null) {
    console.warn("[Interview Assistant bg] interview WS: no ChatGPT tab for", msg && msg.type);
    return;
  }
  try {
    await chrome.tabs.sendMessage(tabId, { type: "IA_INTERVIEW_WS_PUSH", payload: msg });
  } catch (e) {
    console.warn("[Interview Assistant bg] forward interview WS to tab failed", e && e.message ? e.message : e);
  }
}

async function notifyInterviewWsDisconnectedBridgedTab() {
  const tabId = await resolveInterviewTargetTabId();
  if (!tabId) return;
  try {
    await chrome.tabs.sendMessage(tabId, { type: "IA_INTERVIEW_WS_DISCONNECTED" });
  } catch (_e) {}
}

function scheduleInterviewWsReconnect(delayMs) {
  if (wsReconnectTimer) clearTimeout(wsReconnectTimer);
  wsReconnectTimer = setTimeout(() => {
    wsReconnectTimer = null;
    ensureInterviewWebSocket();
  }, delayMs);
}

function ensureInterviewWebSocket() {
  chrome.storage.local.get(["clientId"], (data) => {
    const cid = (String(data.clientId || "").trim());
    if (!cid) {
      if (wsReconnectTimer) {
        clearTimeout(wsReconnectTimer);
        wsReconnectTimer = null;
      }
      if (interviewWs) {
        try {
          interviewWs.close();
        } catch (_e) {}
        interviewWs = null;
      }
      return;
    }
    if (interviewWs && (interviewWs.readyState === WebSocket.OPEN || interviewWs.readyState === WebSocket.CONNECTING)) {
      return;
    }
    if (wsReconnectTimer) {
      clearTimeout(wsReconnectTimer);
      wsReconnectTimer = null;
    }

    const url = `${WS_INTERVIEW_BASE}?client_id=${encodeURIComponent(cid)}`;
    try {
      interviewWs = new WebSocket(url);
    } catch (e) {
      console.warn("[Interview Assistant bg] WebSocket ctor failed", e);
      const delay = wsReconnectMs;
      wsReconnectMs = Math.min(90000, Math.max(4000, wsReconnectMs * 2));
      scheduleInterviewWsReconnect(delay);
      return;
    }

    interviewWs.onopen = () => {
      wsReconnectMs = 4000;
      // console.log("[Interview Assistant bg] interview WebSocket open");
      chrome.storage.local.get(["clientId", "email", "clientLabel"], (d) => {
        if (!interviewWs || interviewWs.readyState !== WebSocket.OPEN) return;
        try {
          interviewWs.send(
            JSON.stringify({
              type: "HELLO",
              client_id: (String(d.clientId || cid || "").trim()),
              email: (String(d.email || "").trim()),
              label: (String(d.clientLabel || "").trim()),
            })
          );
        } catch (_e) {}
      });
    };

    interviewWs.onmessage = (ev) => {
      let msg;
      try {
        msg = JSON.parse(ev.data);
      } catch (_e) {
        return;
      }
      if (!msg || typeof msg !== "object") return;
      if (msg.type === "INITIAL_PROMPT" || msg.type === "INTERVIEWER_CHUNK") {
        forwardInterviewWsToTab(msg).catch(() => {});
      }
    };

    interviewWs.onerror = () => {
      console.warn("[Interview Assistant bg] interview WebSocket error (is live.py WS on 8766 running?)");
    };

    interviewWs.onclose = (ev) => {
      interviewWs = null;
      notifyInterviewWsDisconnectedBridgedTab().catch(() => {});
      const delay = ev.wasClean ? 4000 : wsReconnectMs;
      if (!ev.wasClean) {
        wsReconnectMs = Math.min(90000, Math.max(4000, wsReconnectMs * 2));
      } else {
        wsReconnectMs = 4000;
      }
      // console.log(
      //   "[Interview Assistant bg] interview WebSocket closed",
      //   { code: ev.code, wasClean: ev.wasClean },
      //   "reconnect in",
      //   delay / 1000,
      //   "s"
      // );
      scheduleInterviewWsReconnect(delay);
    };
  });
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

/** Content scripts inject at document_idle; message too early → "Receiving end does not exist". */
async function waitUntilTabComplete(tabId, timeoutMs) {
  const limit = timeoutMs ?? 60000;
  const deadline = Date.now() + limit;
  while (Date.now() < deadline) {
    const tab = await chrome.tabs.get(tabId).catch(() => null);
    if (!tab) return false;
    if (tab.status === "complete") return true;
    await sleep(120);
  }
  return false;
}

function isNoReceiverError(msg) {
  const m = String(msg || "");
  return (
    m.includes("Receiving end does not exist") ||
    m.includes("Could not establish connection") ||
    m.includes("The message port closed")
  );
}

/**
 * @returns {Promise<object|null>} response object from content script, or null if posted error to bridge
 */
async function sendRunPrepJobWithRetry(tabId, job) {
  const payload = {
    type: "RUN_PREP_JOB",
    jobId: job.job_id,
    prompt: job.prompt,
    phase: job.phase,
    newChat: job.open_new_tab !== false,
  };
  const maxAttempts = 30;
  await postPrepExtensionStatus({
    event: "prep_sendmessage_begin",
    job_id: job.job_id,
    tab_id: tabId,
    detail: "Posting RUN_PREP_JOB to content script",
  });
  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    if (attempt === 0 || attempt % 6 === 0) {
      await postPrepExtensionStatus({
        event: "prep_sendmessage_try",
        job_id: job.job_id,
        tab_id: tabId,
        attempt: attempt + 1,
        detail: `sendMessage attempt ${attempt + 1}/${maxAttempts}`,
      });
    }
    const outcome = await new Promise((resolve) => {
      chrome.tabs.sendMessage(tabId, payload, (resp) => {
        const err = chrome.runtime.lastError;
        if (err) {
          resolve({ ok: false, error: err.message });
        } else {
          resolve({ ok: true, resp });
        }
      });
    });
    if (outcome.ok) {
      await postPrepExtensionStatus({
        event: "prep_sendmessage_ok",
        job_id: job.job_id,
        tab_id: tabId,
        attempt: attempt + 1,
      });
      return outcome.resp;
    }
    if (isNoReceiverError(outcome.error)) {
      if (attempt === 0 || attempt % 6 === 0) {
        await postPrepExtensionStatus({
          event: "prep_no_receiver_yet",
          job_id: job.job_id,
          tab_id: tabId,
          attempt: attempt + 1,
          detail: String(outcome.error || "").slice(0, 120),
        });
      }
      await sleep(350 + Math.min(attempt, 10) * 80);
      continue;
    }
    await postPrepExtensionStatus({
      event: "prep_sendmessage_fatal",
      job_id: job.job_id,
      tab_id: tabId,
      detail: String(outcome.error || ""),
    });
    await postPrepComplete(job.job_id, "", outcome.error || "sendMessage_failed");
    lastHandledPrepJobId = "";
    return null;
  }
  await postPrepExtensionStatus({
    event: "prep_sendmessage_gave_up",
    job_id: job.job_id,
    tab_id: tabId,
    detail: "content script never attached after retries",
  });
  await postPrepComplete(job.job_id, "", "content_script_unreachable_after_retries");
  lastHandledPrepJobId = "";
  return null;
}

async function fetchNextPrompt() {
  const res = await fetch(BRIDGE_URL, { method: "GET", cache: "no-store" });
  if (!res.ok) return null;
  const data = await res.json();
  if (!data || !data.request_id || !data.prompt) return null;
  return data;
}

async function pushToActiveTab(promptText, requestId, autoSubmit) {
  const tabId = await resolveInterviewTargetTabId();
  if (tabId == null) return;
  await chrome.tabs.update(tabId, { active: true }).catch(() => {});
  await chrome.tabs.sendMessage(tabId, {
    type: "INSERT_PROMPT",
    prompt: promptText,
    requestId,
    autoSubmit,
  });
}

async function postPrepExtensionStatus(payload) {
  try {
    await fetch(PREP_EXT_STATUS_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ source: "background", ...payload }),
    });
  } catch (_e) {
    /* bridge may be down */
  }
}

async function postPrepComplete(jobId, result, error) {
  try {
    const data = await chrome.storage.local.get(["clientId"]);
    const cid = String(data.clientId || "").trim();
    await fetch("http://127.0.0.1:8765/prep/complete", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        job_id: jobId,
        result: result || "",
        error: error || "",
        client_id: cid,
      }),
    });
  } catch (_e) {
    // ignore
  }
}

async function prepJobPollUrl() {
  const data = await chrome.storage.local.get(["clientId"]);
  const cid = encodeURIComponent(String(data.clientId || "").trim());
  if (!cid) return PREP_JOB_BASE;
  return `${PREP_JOB_BASE}?client_id=${cid}`;
}

async function handlePrepJob() {
  let res;
  try {
    res = await fetch(await prepJobPollUrl(), { cache: "no-store" });
  } catch (_e) {
    return;
  }
  if (!res.ok) return;
  const job = await res.json();
  if (!job || job.status !== "pending" || !job.job_id || !job.prompt) return;
  if (job.job_id === lastHandledPrepJobId) return;

  lastHandledPrepJobId = job.job_id;
  const openNewTab = job.open_new_tab !== false;

  await postPrepExtensionStatus({
    event: "prep_claimed",
    job_id: job.job_id,
    open_new_tab: openNewTab,
    detail: "Background picked pending job from /prep/job",
  });

  try {
    let tabId = prepTabId;
    if (openNewTab) {
      await postPrepExtensionStatus({
        event: "prep_opening_tab",
        job_id: job.job_id,
        detail: "Creating https://chatgpt.com/ tab",
      });
      const created = await chrome.tabs.create({ url: "https://chatgpt.com/", active: true });
      tabId = created.id;
      rememberPrepTabId(tabId);
      await postPrepExtensionStatus({
        event: "prep_tab_loading",
        job_id: job.job_id,
        tab_id: tabId,
        detail: "Waiting for tab status=complete (content script injects at document_idle)",
      });
      const okComplete = await waitUntilTabComplete(tabId, 90000);
      await postPrepExtensionStatus({
        event: "prep_tab_loaded",
        job_id: job.job_id,
        tab_id: tabId,
        tab_complete: okComplete,
      });
      await sleep(500);
    } else {
      const existing = await chrome.tabs.query({
        url: ["https://chat.openai.com/*", "https://chatgpt.com/*", "https://www.chatgpt.com/*"]
      });
      if (prepTabId) {
        const still = await chrome.tabs.get(prepTabId).catch(() => null);
        if (still) {
          tabId = prepTabId;
        } else if (existing.length) {
          tabId = existing[0].id;
          rememberPrepTabId(tabId);
        } else {
          const created = await chrome.tabs.create({ url: "https://chatgpt.com/", active: true });
          tabId = created.id;
          rememberPrepTabId(tabId);
          await postPrepExtensionStatus({
            event: "prep_tab_loading",
            job_id: job.job_id,
            tab_id: tabId,
          });
          const okC = await waitUntilTabComplete(tabId, 90000);
          await postPrepExtensionStatus({ event: "prep_tab_loaded", job_id: job.job_id, tab_id: tabId, tab_complete: okC });
          await sleep(500);
        }
      } else if (existing.length) {
        tabId = existing[0].id;
        rememberPrepTabId(tabId);
      } else {
        const created = await chrome.tabs.create({ url: "https://chatgpt.com/", active: true });
        tabId = created.id;
        rememberPrepTabId(tabId);
        await postPrepExtensionStatus({ event: "prep_tab_loading", job_id: job.job_id, tab_id: tabId });
        const okC2 = await waitUntilTabComplete(tabId, 90000);
        await postPrepExtensionStatus({ event: "prep_tab_loaded", job_id: job.job_id, tab_id: tabId, tab_complete: okC2 });
        await sleep(500);
      }
      await chrome.tabs.update(tabId, { active: true });
      await postPrepExtensionStatus({ event: "prep_focus_wait", job_id: job.job_id, tab_id: tabId });
      const okF = await waitUntilTabComplete(tabId, 20000);
      await postPrepExtensionStatus({ event: "prep_focus_ready", job_id: job.job_id, tab_id: tabId, tab_complete: okF });
      await sleep(400);
    }

    const resp = await sendRunPrepJobWithRetry(tabId, job);
    if (resp == null) return;
    if (!resp.ok) {
      await postPrepExtensionStatus({
        event: "prep_content_reported_error",
        job_id: job.job_id,
        detail: String(resp.error || "unknown").slice(0, 120),
      });
      // Content script already POSTed /prep/complete with the failure reason.
      lastHandledPrepJobId = "";
      return;
    }
    await postPrepExtensionStatus({
      event: "prep_run_finished_ok",
      job_id: job.job_id,
      detail: "RUN_PREP_JOB finished; content script posted prep/complete",
    });
  } catch (e) {
    await postPrepExtensionStatus({
      event: "prep_background_exception",
      job_id: job.job_id,
      detail: String(e && e.message ? e.message : e).slice(0, 120),
    });
    await postPrepComplete(job.job_id, "", "prep_exception");
    lastHandledPrepJobId = "";
  }
}

async function pollBridgeAndInject() {
  try {
    const payload = await fetchNextPrompt();
    if (!payload) return;

    if (payload.request_id === lastRequestId) return;
    lastRequestId = payload.request_id;
    await chrome.storage.local.set({ lastRequestId });

    await pushToActiveTab(payload.prompt, payload.request_id, true);
  } catch (_err) {
    // Silent retries; polling continues.
  }
}

function scheduleBridgeTick() {
  chrome.alarms.create(BRIDGE_TICK_ALARM, { when: Date.now() + TICK_MS });
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "IA_BRIDGE_FETCH") {
    (async () => {
      if (!isAllowedBridgeHttpUrl(message.url)) {
        sendResponse({ ok: false, status: 0, bodyText: "", error: "URL not allowed" });
        return;
      }
      const tmo = Math.min(120000, Math.max(2000, Number(message.timeoutMs) || 12000));
      const ctrl = new AbortController();
      const tid = setTimeout(() => ctrl.abort(), tmo);
      try {
        const init = {
          method: message.method || "GET",
          headers: message.headers || {},
          signal: ctrl.signal,
        };
        if (message.body != null && message.body !== "") init.body = message.body;
        const res = await fetch(message.url, init);
        const bodyText = await res.text();
        sendResponse({ ok: res.ok, status: res.status, bodyText });
      } catch (e) {
        sendResponse({ ok: false, status: 0, bodyText: "", error: String(e.message || e) });
      } finally {
        clearTimeout(tid);
      }
    })();
    return true;
  }
  if (message?.type === "IA_INTERVIEW_WS_SEND") {
    (async () => {
      const p = message.payload || {};
      try {
        if (interviewWs && interviewWs.readyState === WebSocket.OPEN) {
          interviewWs.send(JSON.stringify(p));
        } else {
          const pt = String(p.type || "");
          if (pt === "LIVE_ANSWER" || pt === "MANUAL_GPT_LIVE") {
            const rid = String(p.request_id || "").trim();
            if (!rid) {
              sendResponse({ ok: true });
              return;
            }
            const text = p.text != null ? String(p.text) : "";
            await fetch(INTERVIEW_LIVE_URL, {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ request_id: rid, text }),
            });
          } else if (pt === "MANUAL_GPT_FINAL") {
            const rid = String(p.request_id || "").trim();
            const answer = p.answer != null ? String(p.answer).trim() : "";
            if (rid && answer) {
              await fetch(ANSWER_ENDPOINT_BG, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ request_id: rid, answer }),
              });
            }
          }
        }
      } catch (e) {
        console.warn("[Interview Assistant bg] interview WS / interview-live send", e);
      }
      sendResponse({ ok: true });
    })();
    return true;
  }
  return false;
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "local" || !changes.clientId) return;
  if (interviewWs) {
    try {
      interviewWs.close();
    } catch (_e) {}
    interviewWs = null;
  }
  wsReconnectMs = 4000;
  ensureInterviewWebSocket();
});

chrome.runtime.onInstalled.addListener(async () => {
  const saved = await chrome.storage.local.get({ lastRequestId: "", [BRIDGED_PREP_TAB_KEY]: null });
  lastRequestId = saved.lastRequestId || "";
  if (saved[BRIDGED_PREP_TAB_KEY] != null) prepTabId = saved[BRIDGED_PREP_TAB_KEY];
  await chrome.alarms.clear("pollBridge");
  chrome.alarms.create(BRIDGE_TICK_ALARM, { when: Date.now() + 400 });
  ensureInterviewWebSocket();
});

chrome.runtime.onStartup.addListener(async () => {
  const saved = await chrome.storage.local.get({ lastRequestId: "", [BRIDGED_PREP_TAB_KEY]: null });
  lastRequestId = saved.lastRequestId || "";
  if (saved[BRIDGED_PREP_TAB_KEY] != null) prepTabId = saved[BRIDGED_PREP_TAB_KEY];
  chrome.alarms.create(BRIDGE_TICK_ALARM, { when: Date.now() + 400 });
  ensureInterviewWebSocket();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name !== BRIDGE_TICK_ALARM) return;
  (async () => {
    ensureInterviewWebSocket();
    await pollBridgeAndInject();
    await handlePrepJob();
  })();
  scheduleBridgeTick();
});
