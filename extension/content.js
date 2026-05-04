(function () {
  const ANSWER_ENDPOINT = "http://127.0.0.1:8765/answer";
  const PREP_COMPLETE_ENDPOINT = "http://127.0.0.1:8765/prep/complete";
  const latestSentByRequest = new Map();

  function findComposer() {
    return (
      document.querySelector("textarea#prompt-textarea") ||
      document.querySelector("textarea[placeholder*='Message']") ||
      document.querySelector("textarea") ||
      document.querySelector("[contenteditable='true']")
    );
  }

  function setInputValue(el, text) {
    if (!el) return false;

    if (el.tagName === "TEXTAREA" || el.tagName === "INPUT") {
      el.focus();
      el.value = text;
      el.dispatchEvent(new Event("input", { bubbles: true }));
      return true;
    }

    if (el.getAttribute("contenteditable") === "true") {
      el.focus();
      el.textContent = text;
      el.dispatchEvent(new Event("input", { bubbles: true }));
      return true;
    }

    return false;
  }

  function trySubmit() {
    const button =
      document.querySelector("button[data-testid='send-button']") ||
      document.querySelector("button[aria-label*='Send']") ||
      document.querySelector("button svg[data-icon='paper-plane']")?.closest("button");

    if (button && !button.disabled) {
      button.click();
      return true;
    }
    return false;
  }

  function getLatestAssistantText() {
    const candidates = [
      ...document.querySelectorAll("[data-message-author-role='assistant']"),
      ...document.querySelectorAll("article")
    ];
    for (let i = candidates.length - 1; i >= 0; i -= 1) {
      const text = (candidates[i].innerText || "").trim();
      if (text) return text;
    }
    return "";
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

  async function postPrepComplete(jobId, result, error) {
    try {
      await fetch(PREP_COMPLETE_ENDPOINT, {
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

  async function postAnswer(requestId, answer) {
    if (!requestId || !answer) return;
    if (latestSentByRequest.get(requestId) === answer) return;
    latestSentByRequest.set(requestId, answer);

    try {
      await fetch(ANSWER_ENDPOINT, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          request_id: requestId,
          answer
        })
      });
    } catch (_err) {
      // Best effort; ignore network errors.
    }
  }

  function watchAndReportAnswer(requestId) {
    let tries = 0;
    const maxTries = 80;
    const timer = setInterval(() => {
      tries += 1;
      const answer = getLatestAssistantText();
      if (answer) {
        postAnswer(requestId, answer);
        if (answer.length > 60) {
          clearInterval(timer);
        }
      }
      if (tries >= maxTries) {
        clearInterval(timer);
      }
    }, 1500);
  }

  /** WebSocket lives on 8766 (HTTP bridge stays on 8765). */
  const WS_INTERVIEW_BASE = "ws://127.0.0.1:8766/ws";
  let interviewWs = null;
  let wsStreamTimer = null;

  function wsSend(obj) {
    if (interviewWs && interviewWs.readyState === 1) {
      interviewWs.send(JSON.stringify(obj));
    }
  }

  function clearWsStreamWatch() {
    if (wsStreamTimer) {
      clearInterval(wsStreamTimer);
      wsStreamTimer = null;
    }
  }

  function startWsResponseWatch(requestId) {
    clearWsStreamWatch();
    let lastSentLive = 0;
    let lastText = "";
    let stableSince = 0;
    let stableText = "";
    let finalSent = false;

    wsStreamTimer = setInterval(() => {
      if (!requestId) return;
      const t = getLatestAssistantText();
      const now = Date.now();
      if (t !== lastText) {
        lastText = t;
        stableSince = now;
        stableText = t;
        if (t && now - lastSentLive >= 500) {
          lastSentLive = now;
          wsSend({ type: "LIVE_ANSWER", request_id: requestId, text: t });
        }
      } else if (t && stableText === t && t.length > 0) {
        if (now - stableSince >= 2000 && !finalSent) {
          finalSent = true;
          clearWsStreamWatch();
          wsSend({ type: "FINAL_ANSWER", request_id: requestId, answer: t });
        }
      }
    }, 300);
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
    if (!requestId) return;
    if (mtype === "INITIAL_PROMPT" && !prompt) {
      prompt = await resolveInitialPromptFromStorage();
    }
    if (!prompt) {
      wsSend({ type: "STATUS", level: "error", message: "No prompt (paste resume/JD in extension or use Python-built prompt)." });
      return;
    }
    const composer = findComposer();
    if (!composer) {
      wsSend({ type: "STATUS", level: "error", message: "composer_not_found" });
      return;
    }
    if (!setInputValue(composer, prompt)) {
      wsSend({ type: "STATUS", level: "error", message: "could_not_insert" });
      return;
    }
    await wait(120);
    trySubmit();
    await wait(400);
    startWsResponseWatch(requestId);
  }

  function connectInterviewWebSocket() {
    chrome.storage.local.get(["clientId"], (data) => {
      const cid = (data.clientId || "").trim();
      if (!cid) return;
      const url = `${WS_INTERVIEW_BASE}?client_id=${encodeURIComponent(cid)}`;
      try {
        interviewWs = new WebSocket(url);
      } catch (_e) {
        return;
      }
      interviewWs.onopen = () => {
        chrome.storage.local.get(["clientId", "email", "clientLabel"], (d) => {
          wsSend({
            type: "HELLO",
            client_id: (d.clientId || cid || "").trim(),
            email: (d.email || "").trim(),
            label: (d.clientLabel || "").trim()
          });
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
          handleWsInterviewMessage(msg).catch(() => {});
        }
      };
      interviewWs.onerror = () => {};
      interviewWs.onclose = () => {
        interviewWs = null;
        clearWsStreamWatch();
        setTimeout(connectInterviewWebSocket, 4000);
      };
    });
  }

  connectInterviewWebSocket();

  async function runPrepJob(message) {
    const jobId = message.jobId || "";
    const prompt = message.prompt || "";
    try {
      if (message.newChat) {
        clickNewChat();
        await wait(1400);
      }

      const composer = findComposer();
      if (!composer) {
        await postPrepComplete(jobId, "", "composer_not_found");
        return { ok: false, error: "composer_not_found" };
      }

      const ok = setInputValue(composer, prompt);
      if (!ok) {
        await postPrepComplete(jobId, "", "could_not_insert");
        return { ok: false, error: "could_not_insert" };
      }

      await wait(150);
      trySubmit();
      await wait(400);

      let tries = 0;
      let answer = "";
      while (tries < 120) {
        tries += 1;
        answer = getLatestAssistantText();
        if (answer && answer.length > 40) {
          break;
        }
        await wait(900);
      }

      if (!answer || answer.length < 20) {
        await postPrepComplete(jobId, "", "no_assistant_reply");
        return { ok: false, error: "no_assistant_reply" };
      }

      await postPrepComplete(jobId, answer, "");
      return { ok: true };
    } catch (err) {
      await postPrepComplete(jobId, "", String(err && err.message ? err.message : err));
      return { ok: false, error: String(err) };
    }
  }

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type === "RUN_PREP_JOB") {
      runPrepJob(message).then(sendResponse);
      return true;
    }

    if (message?.type !== "INSERT_PROMPT") return false;

    clearWsStreamWatch();

    const composer = findComposer();
    if (!composer) {
      sendResponse({ ok: false, error: "Composer not found" });
      return false;
    }

    const ok = setInputValue(composer, message.prompt || "");
    if (!ok) {
      sendResponse({ ok: false, error: "Could not insert prompt" });
      return false;
    }

    if (message.autoSubmit) {
      setTimeout(() => {
        trySubmit();
      }, 120);
    }

    watchAndReportAnswer(message.requestId || "");

    sendResponse({ ok: true, requestId: message.requestId || "" });
    return false;
  });
})();
