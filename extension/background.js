const BRIDGE_URL = "http://127.0.0.1:8765/next-prompt";
const PREP_JOB_URL = "http://127.0.0.1:8765/prep/job";
const POLL_INTERVAL_MS = 2000;

let lastRequestId = "";
let lastHandledPrepJobId = "";
let prepTabId = null;

async function fetchNextPrompt() {
  const res = await fetch(BRIDGE_URL, { method: "GET", cache: "no-store" });
  if (!res.ok) return null;
  const data = await res.json();
  if (!data || !data.request_id || !data.prompt) return null;
  return data;
}

async function pushToActiveTab(promptText, requestId, autoSubmit) {
  const tabs = await chrome.tabs.query({
    active: true,
    currentWindow: true,
    url: ["https://chat.openai.com/*", "https://chatgpt.com/*"]
  });

  if (!tabs.length) return;
  const tabId = tabs[0].id;
  await chrome.tabs.sendMessage(tabId, {
    type: "INSERT_PROMPT",
    prompt: promptText,
    requestId,
    autoSubmit
  });
}

async function postPrepComplete(jobId, result, error) {
  try {
    await fetch("http://127.0.0.1:8765/prep/complete", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        job_id: jobId,
        result: result || "",
        error: error || ""
      })
    });
  } catch (_e) {
    // ignore
  }
}

async function handlePrepJob() {
  let res;
  try {
    res = await fetch(PREP_JOB_URL, { cache: "no-store" });
  } catch (_e) {
    return;
  }
  if (!res.ok) return;
  const job = await res.json();
  if (!job || job.status !== "pending" || !job.job_id || !job.prompt) return;
  if (job.job_id === lastHandledPrepJobId) return;

  lastHandledPrepJobId = job.job_id;
  const openNewTab = job.open_new_tab !== false;

  try {
    let tabId = prepTabId;
    if (openNewTab) {
      const created = await chrome.tabs.create({ url: "https://chatgpt.com/", active: true });
      tabId = created.id;
      prepTabId = tabId;
      await new Promise((r) => setTimeout(r, 2500));
    } else {
      const existing = await chrome.tabs.query({
        url: ["https://chat.openai.com/*", "https://chatgpt.com/*"]
      });
      if (prepTabId) {
        const still = await chrome.tabs.get(prepTabId).catch(() => null);
        if (still) {
          tabId = prepTabId;
        } else if (existing.length) {
          tabId = existing[0].id;
          prepTabId = tabId;
        } else {
          const created = await chrome.tabs.create({ url: "https://chatgpt.com/", active: true });
          tabId = created.id;
          prepTabId = tabId;
          await new Promise((r) => setTimeout(r, 2500));
        }
      } else if (existing.length) {
        tabId = existing[0].id;
        prepTabId = tabId;
      } else {
        const created = await chrome.tabs.create({ url: "https://chatgpt.com/", active: true });
        tabId = created.id;
        prepTabId = tabId;
        await new Promise((r) => setTimeout(r, 2500));
      }
      await chrome.tabs.update(tabId, { active: true });
    }

    await new Promise((resolve) => {
      chrome.tabs.sendMessage(
        tabId,
        {
          type: "RUN_PREP_JOB",
          jobId: job.job_id,
          prompt: job.prompt,
          phase: job.phase,
          newChat: true
        },
        async (resp) => {
          if (chrome.runtime.lastError) {
            await postPrepComplete(job.job_id, "", chrome.runtime.lastError.message);
            lastHandledPrepJobId = "";
            resolve();
            return;
          }
          if (!resp || !resp.ok) {
            await postPrepComplete(job.job_id, "", resp?.error || "prep_failed");
            lastHandledPrepJobId = "";
          }
          resolve();
        }
      );
    });
  } catch (_e) {
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

    await pushToActiveTab(payload.prompt, payload.request_id, false);
  } catch (_err) {
    // Silent retries; polling continues.
  }
}

chrome.runtime.onInstalled.addListener(async () => {
  const saved = await chrome.storage.local.get({ lastRequestId: "" });
  lastRequestId = saved.lastRequestId || "";
  chrome.alarms.create("pollBridge", { periodInMinutes: POLL_INTERVAL_MS / 60000 });
});

chrome.runtime.onStartup.addListener(async () => {
  const saved = await chrome.storage.local.get({ lastRequestId: "" });
  lastRequestId = saved.lastRequestId || "";
  chrome.alarms.create("pollBridge", { periodInMinutes: POLL_INTERVAL_MS / 60000 });
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === "pollBridge") {
    pollBridgeAndInject();
    handlePrepJob();
  }
});
