const BRIDGE = "http://127.0.0.1:8765";
/** Logical "{profile}_resume.txt" / "{profile}_jd.txt" — Chrome extensions cannot write the package folder; keys live in chrome.storage.local. */
const IA_STORE = "ia_profile_docs_v1";
const LEGACY_RESUME_DRAFT = "resumeDraft";
const LEGACY_JD_DRAFT = "jdDraft";

const shell = document.getElementById("shell");
const panelResume = document.getElementById("panelResume");
const panelJD = document.getElementById("panelJD");
const panelPrompts = document.getElementById("panelPrompts");
const btnResume = document.getElementById("btnResume");
const btnJD = document.getElementById("btnJD");
const btnPrompts = document.getElementById("btnPrompts");
const resumeText = document.getElementById("resumeText");
const jdText = document.getElementById("jdText");
const bridgeErr = document.getElementById("bridgeErr");
const registerStatus = document.getElementById("registerStatus");

/** All four prompt templates the app can override from the extension. */
const TEMPLATE_KEYS = ["resume_summary", "jd_summary", "initial_interview", "chunk_interview"];
const TEMPLATE_FIELDS = {
  resume_summary: document.getElementById("tplResumeSummary"),
  jd_summary: document.getElementById("tplJdSummary"),
  initial_interview: document.getElementById("tplInitialInterview"),
  chunk_interview: document.getElementById("tplChunkInterview"),
};

const W_MENU = 248;
const W_PANEL = 280;
const W_PANEL_PROMPTS = 420;

/** Defaults shown the first time a profile opens the Prompts panel; identical to what live.py used as fixed prompts. */
const DEFAULT_TEMPLATES = {
  resume_summary: `You are an expert technical interviewer assistant.

Task:
Summarize the candidate's resume into a concise, structured briefing for real-time interview support.

Rules:

* Output ONLY the final summary (no explanation)
* Keep it concise but information-dense
* Focus on signal, not fluff
* No generic phrases

---

Structure:

1. CORE PROFILE (2–3 lines)

* Years of experience
* Primary role (AI / backend / full-stack / etc.)
* Key domains

2. KEY SKILLS

* List top technical strengths
* Prioritize production-level skills

3. SYSTEM EXPERIENCE

* What kinds of systems they built (e.g., ML pipelines, APIs, distributed systems)

4. STRENGTH SIGNALS

* What makes them strong (e.g., scalability, ownership, optimization)

5. WEAK / MISSING AREAS (IMPORTANT)

* Gaps relative to modern expectations

---

Tone:

* Direct
* Technical
* Interview-ready

---

INPUT RESUME:
{resume_text}`,
  jd_summary: `You are an expert hiring manager.

Task:
Analyze the job description and extract what the interviewer ACTUALLY cares about.

Rules:

* Output ONLY the structured summary
* Be precise and realistic
* Avoid repeating the JD verbatim

---

Structure:

1. ROLE CORE

* What this role really is (not title, actual function)

2. MUST-HAVE SKILLS (CRITICAL)

* Non-negotiable requirements

3. IMPORTANT SKILLS

* Strongly preferred but flexible

4. NICE-TO-HAVE

* Bonus skills

5. SYSTEM EXPECTATIONS

* What systems candidate should be able to build

6. INTERVIEW FOCUS

* What interviewer will likely test (VERY IMPORTANT)

7. RED FLAGS

* What will cause rejection

---

Tone:

* Hiring manager perspective
* Practical, not theoretical

---

INPUT JOB DESCRIPTION:
{jd_text}`,
  initial_interview: `You are helping a candidate during a live technical interview.

You will receive short excerpts of what the interviewer said (as captions). When sent, reply with only the exact words the candidate should say next — concise, first person, no meta commentary.

Static resume and job context were already provided in earlier messages in this chat. Do not ask for them again.

Reply with one short sentence confirming you are ready, then stop.`,
  chunk_interview: `You are generating what the candidate should say in an interview.

Hard rules (non-negotiable):
- Output only the final answer text the candidate should speak.
- No explanation, no analysis, no headings, no labels, no bullet list unless interviewer explicitly asked for a list.
- Use first person.
- Keep concise and natural, interview-ready tone.
- If multiple questions are present, answer them in asked order in compact paragraph form.
- If there is no clear question, output one short clarification question only.

Interviewer intent (cleaned):
{cleaned_interviewer_intent}
`,
};

let resumeOpen = false;
let jdOpen = false;
let promptsOpen = false;
let saveResumeTimer = null;
let saveJdTimer = null;
const saveTplTimers = { resume_summary: null, jd_summary: null, initial_interview: null, chunk_interview: null };
/** Cached profile stem for beforeunload (sync API only). */
let cachedProfileStem = "";

function profileResumeKey(stem) {
  return `${IA_STORE}:${stem}:resume`;
}

function profileJdKey(stem) {
  return `${IA_STORE}:${stem}:jd`;
}

function profileTemplateKey(stem, key) {
  return `${IA_STORE}:${stem}:tpl:${key}`;
}

function sanitizeStemFromEmail(email) {
  const e = String(email || "").trim().toLowerCase();
  if (!e) return "";
  return e.replace(/[^a-z0-9._-]+/g, "_").slice(0, 96);
}

window.addEventListener("beforeunload", () => {
  if (!cachedProfileStem) return;
  const patch = {
    [profileResumeKey(cachedProfileStem)]: resumeText.value,
    [profileJdKey(cachedProfileStem)]: jdText.value,
  };
  for (const k of TEMPLATE_KEYS) {
    const el = TEMPLATE_FIELDS[k];
    if (el) patch[profileTemplateKey(cachedProfileStem, k)] = el.value;
  }
  chrome.storage.local.set(patch);
});

function syncShellWidth() {
  let w = W_MENU;
  if (resumeOpen) w += W_PANEL;
  if (jdOpen) w += W_PANEL;
  if (promptsOpen) w += W_PANEL_PROMPTS;
  shell.style.width = `${w}px`;
}

function setBridgeError(msg) {
  if (!msg) {
    bridgeErr.hidden = true;
    bridgeErr.textContent = "";
    return;
  }
  bridgeErr.hidden = false;
  bridgeErr.textContent = msg;
}

async function getOrCreateClientId() {
  const data = await chrome.storage.local.get("clientId");
  let id = data.clientId;
  if (!id || typeof id !== "string") {
    id = crypto.randomUUID();
    await chrome.storage.local.set({ clientId: id });
  }
  return id;
}

async function getChromeProfileInfo() {
  try {
    const info = await new Promise((resolve, reject) => {
      chrome.identity.getProfileUserInfo({ accountStatus: "ANY" }, (result) => {
        if (chrome.runtime.lastError) {
          reject(chrome.runtime.lastError);
          return;
        }
        resolve(result || {});
      });
    });
    return {
      email: (info.email || "").trim(),
      profile_id: (info.id || "").trim(),
    };
  } catch (_e) {
    return { email: "", profile_id: "" };
  }
}

async function profileDocStem() {
  const { clientId, email: storedEmail } = await chrome.storage.local.get(["clientId", "email"]);
  const cid = typeof clientId === "string" && clientId ? clientId : await getOrCreateClientId();
  let email = (storedEmail && String(storedEmail).trim()) || "";
  if (!email) {
    const info = await getChromeProfileInfo();
    email = info.email || "";
  }
  const fromMail = sanitizeStemFromEmail(email);
  if (fromMail) return fromMail;
  const raw = String(cid).replace(/-/g, "");
  return `cid_${raw.slice(0, 20)}`;
}

async function refreshProfileStemCache() {
  cachedProfileStem = await profileDocStem();
}

async function migrateLegacyDraftsIfNeeded(stem) {
  const rKey = profileResumeKey(stem);
  const jKey = profileJdKey(stem);
  const loc = await chrome.storage.local.get([rKey, jKey, LEGACY_RESUME_DRAFT, LEGACY_JD_DRAFT]);
  const patch = {};
  let remove = [];
  if (loc[rKey] == null && loc[LEGACY_RESUME_DRAFT] != null) {
    patch[rKey] = String(loc[LEGACY_RESUME_DRAFT]);
    remove.push(LEGACY_RESUME_DRAFT);
  }
  if (loc[jKey] == null && loc[LEGACY_JD_DRAFT] != null) {
    patch[jKey] = String(loc[LEGACY_JD_DRAFT]);
    remove.push(LEGACY_JD_DRAFT);
  }
  if (Object.keys(patch).length) await chrome.storage.local.set(patch);
  if (remove.length) await chrome.storage.local.remove(remove);
}

async function migrateStemDocs(fromStem, toStem) {
  if (!fromStem || !toStem || fromStem === toStem) return;
  const oldR = profileResumeKey(fromStem);
  const oldJ = profileJdKey(fromStem);
  const oldTpls = TEMPLATE_KEYS.map((k) => profileTemplateKey(fromStem, k));
  const data = await chrome.storage.local.get([oldR, oldJ, ...oldTpls]);
  const patch = {};
  if (data[oldR] != null && String(data[oldR]).length) patch[profileResumeKey(toStem)] = data[oldR];
  if (data[oldJ] != null && String(data[oldJ]).length) patch[profileJdKey(toStem)] = data[oldJ];
  for (const k of TEMPLATE_KEYS) {
    const oldKey = profileTemplateKey(fromStem, k);
    if (data[oldKey] != null && String(data[oldKey]).length) {
      patch[profileTemplateKey(toStem, k)] = data[oldKey];
    }
  }
  if (Object.keys(patch).length) await chrome.storage.local.set(patch);
  await chrome.storage.local.remove([oldR, oldJ, ...oldTpls]);
}

async function loadPersistedIntoTextareas() {
  const stem = cachedProfileStem || (await profileDocStem());
  await migrateLegacyDraftsIfNeeded(stem);
  const rKey = profileResumeKey(stem);
  const jKey = profileJdKey(stem);
  const tplKeys = TEMPLATE_KEYS.map((k) => profileTemplateKey(stem, k));
  const data = await chrome.storage.local.get([rKey, jKey, ...tplKeys]);
  if (data[rKey] != null) resumeText.value = String(data[rKey]);
  if (data[jKey] != null) jdText.value = String(data[jKey]);
  const seedPatch = {};
  for (const k of TEMPLATE_KEYS) {
    const el = TEMPLATE_FIELDS[k];
    if (!el) continue;
    const stored = data[profileTemplateKey(stem, k)];
    const hasStored = stored != null && String(stored).trim().length > 0;
    if (hasStored) {
      el.value = String(stored);
    } else if (DEFAULT_TEMPLATES[k]) {
      el.value = DEFAULT_TEMPLATES[k];
      seedPatch[profileTemplateKey(stem, k)] = el.value;
    }
  }
  if (Object.keys(seedPatch).length) await chrome.storage.local.set(seedPatch);
}

/** Bridge reachability only — resume/JD text live in the extension profile store. */
async function pingBridge() {
  try {
    const clientId = await getOrCreateClientId();
    const res = await fetch(
      `${BRIDGE}/context?client_id=${encodeURIComponent(clientId)}`,
      { cache: "no-store" }
    );
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    setBridgeError("");
  } catch (_e) {
    setBridgeError("Bridge offline — start live.py");
  }
}

async function postResume(text) {
  try {
    const clientId = await getOrCreateClientId();
    await fetch(`${BRIDGE}/context/resume`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text, client_id: clientId }),
    });
    setBridgeError("");
  } catch (_e) {
    setBridgeError("Could not push resume to bridge (saved locally in extension)");
  }
}

async function postJd(text) {
  try {
    const clientId = await getOrCreateClientId();
    await fetch(`${BRIDGE}/context/jd`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text, client_id: clientId }),
    });
    setBridgeError("");
  } catch (_e) {
    setBridgeError("Could not push JD to bridge (saved locally in extension)");
  }
}

async function postTemplate(key, text) {
  if (!TEMPLATE_KEYS.includes(key)) return;
  try {
    const clientId = await getOrCreateClientId();
    await fetch(`${BRIDGE}/context/template/${encodeURIComponent(key)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text, client_id: clientId }),
    });
    setBridgeError("");
  } catch (_e) {
    setBridgeError(`Could not push ${key} prompt to bridge (saved locally in extension)`);
  }
}

async function persistProfileTemplateNow(key) {
  if (!TEMPLATE_KEYS.includes(key)) return;
  if (!cachedProfileStem) await refreshProfileStemCache();
  if (!cachedProfileStem) return;
  const el = TEMPLATE_FIELDS[key];
  if (!el) return;
  await chrome.storage.local.set({ [profileTemplateKey(cachedProfileStem, key)]: el.value });
}

async function pushTemplateToBridgeSession(key) {
  if (!TEMPLATE_KEYS.includes(key)) return;
  await persistProfileTemplateNow(key);
  const el = TEMPLATE_FIELDS[key];
  await postTemplate(key, el ? el.value : "");
}

function scheduleSaveTemplateDraft(key) {
  if (!TEMPLATE_KEYS.includes(key)) return;
  clearTimeout(saveTplTimers[key]);
  saveTplTimers[key] = setTimeout(() => {
    void pushTemplateToBridgeSession(key);
  }, 400);
}

async function persistProfileResumeNow() {
  if (!cachedProfileStem) await refreshProfileStemCache();
  if (!cachedProfileStem) return;
  await chrome.storage.local.set({ [profileResumeKey(cachedProfileStem)]: resumeText.value });
}

async function persistProfileJdNow() {
  if (!cachedProfileStem) await refreshProfileStemCache();
  if (!cachedProfileStem) return;
  await chrome.storage.local.set({ [profileJdKey(cachedProfileStem)]: jdText.value });
}

/** Persist locally + mirror to bridge (empty string clears bridge so prep waits). */
function scheduleSaveResumeDraft() {
  clearTimeout(saveResumeTimer);
  saveResumeTimer = setTimeout(() => {
    void pushResumeToBridgeSession();
  }, 400);
}

function scheduleSaveJdDraft() {
  clearTimeout(saveJdTimer);
  saveJdTimer = setTimeout(() => {
    void pushJdToBridgeSession();
  }, 400);
}

async function pushResumeToBridgeSession() {
  await persistProfileResumeNow();
  await postResume(resumeText.value);
}

async function pushJdToBridgeSession() {
  await persistProfileJdNow();
  await postJd(jdText.value);
}

/** Close any currently open panel except the one named, persisting + pushing its content. */
async function closeOtherPanels(except) {
  if (except !== "resume" && resumeOpen) {
    resumeOpen = false;
    panelResume.classList.remove("open");
    btnResume.classList.remove("toggle-on");
    panelResume.setAttribute("aria-hidden", "true");
    await pushResumeToBridgeSession();
  }
  if (except !== "jd" && jdOpen) {
    jdOpen = false;
    panelJD.classList.remove("open");
    btnJD.classList.remove("toggle-on");
    panelJD.setAttribute("aria-hidden", "true");
    await pushJdToBridgeSession();
  }
  if (except !== "prompts" && promptsOpen) {
    promptsOpen = false;
    panelPrompts.classList.remove("open");
    btnPrompts.classList.remove("toggle-on");
    panelPrompts.setAttribute("aria-hidden", "true");
    for (const k of TEMPLATE_KEYS) await pushTemplateToBridgeSession(k);
  }
}

btnResume.addEventListener("click", async () => {
  const wasOpen = resumeOpen;
  resumeOpen = !resumeOpen;
  panelResume.classList.toggle("open", resumeOpen);
  btnResume.classList.toggle("toggle-on", resumeOpen);
  panelResume.setAttribute("aria-hidden", resumeOpen ? "false" : "true");
  if (resumeOpen) await closeOtherPanels("resume");
  syncShellWidth();
  if (resumeOpen) {
    resumeText.focus();
    if (resumeText.value.trim()) {
      await pushResumeToBridgeSession();
    }
    return;
  }
  if (wasOpen) {
    await pushResumeToBridgeSession();
  }
});

btnJD.addEventListener("click", async () => {
  const wasOpen = jdOpen;
  jdOpen = !jdOpen;
  panelJD.classList.toggle("open", jdOpen);
  btnJD.classList.toggle("toggle-on", jdOpen);
  panelJD.setAttribute("aria-hidden", jdOpen ? "false" : "true");
  if (jdOpen) await closeOtherPanels("jd");
  syncShellWidth();
  if (jdOpen) {
    jdText.focus();
    if (jdText.value.trim()) {
      await pushJdToBridgeSession();
    }
    return;
  }
  if (wasOpen) {
    await pushJdToBridgeSession();
  }
});

resumeText.addEventListener("input", scheduleSaveResumeDraft);
resumeText.addEventListener("blur", () => {
  void pushResumeToBridgeSession();
});
jdText.addEventListener("input", scheduleSaveJdDraft);
jdText.addEventListener("blur", () => {
  void pushJdToBridgeSession();
});

btnPrompts.addEventListener("click", async () => {
  const wasOpen = promptsOpen;
  promptsOpen = !promptsOpen;
  panelPrompts.classList.toggle("open", promptsOpen);
  btnPrompts.classList.toggle("toggle-on", promptsOpen);
  panelPrompts.setAttribute("aria-hidden", promptsOpen ? "false" : "true");
  if (promptsOpen) await closeOtherPanels("prompts");
  syncShellWidth();
  if (promptsOpen) {
    for (const k of TEMPLATE_KEYS) {
      const el = TEMPLATE_FIELDS[k];
      if (el && el.value.trim()) await pushTemplateToBridgeSession(k);
    }
    return;
  }
  if (wasOpen) {
    for (const k of TEMPLATE_KEYS) await pushTemplateToBridgeSession(k);
  }
});

for (const k of TEMPLATE_KEYS) {
  const el = TEMPLATE_FIELDS[k];
  if (!el) continue;
  el.addEventListener("input", () => scheduleSaveTemplateDraft(k));
  el.addEventListener("blur", () => {
    void pushTemplateToBridgeSession(k);
  });
}

async function refreshRegistryStatus() {
  try {
    const clientId = await getOrCreateClientId();
    const res = await fetch(`${BRIDGE}/registered-clients`, { cache: "no-store" });
    if (!res.ok) throw new Error("unreachable");
    const data = await res.json();
    const sessionRegs = Array.isArray(data.session_registered_client_ids)
      ? data.session_registered_client_ids
      : [];
    const bridgedThisRun = sessionRegs.includes(clientId);
    registerStatus.textContent = bridgedThisRun ? "Status: Registered" : "Status: Please register";
    setBridgeError("");
  } catch (_e) {
    registerStatus.textContent = "Status: Please register";
    setBridgeError("Bridge offline — start live.py");
  }
}

async function registerClient() {
  registerStatus.textContent = "Status: Registering…";
  const clientId = await getOrCreateClientId();
  const { email, profile_id } = await getChromeProfileInfo();
  const label = email || `Chrome client ${clientId.slice(0, 6)}`;
  const prevStem = cachedProfileStem;
  try {
    const res = await fetch(`${BRIDGE}/register-client`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        client_id: clientId,
        email,
        profile_id,
        label,
      }),
    });
    let body = {};
    try {
      body = await res.json();
    } catch (_e) {
      body = {};
    }
    if (!res.ok) {
      registerStatus.textContent = "Status: Please register";
      setBridgeError("Bridge offline — start live.py");
      return;
    }
    if (!body.ok) {
      registerStatus.textContent = "Status: Please register";
      setBridgeError(body.error || "Registration failed");
      return;
    }
    const client = body.client || {};
    await chrome.storage.local.set({
      clientLabel: client.label || label,
      email: client.email ?? email,
      profileId: client.profile_id ?? profile_id,
    });
    registerStatus.textContent = "Status: Registered";
    setBridgeError("");
    await refreshProfileStemCache();
    if (prevStem && prevStem !== cachedProfileStem) {
      await migrateStemDocs(prevStem, cachedProfileStem);
    }
    await loadPersistedIntoTextareas();
    await pushResumeToBridgeSession();
    await pushJdToBridgeSession();
    for (const k of TEMPLATE_KEYS) await pushTemplateToBridgeSession(k);
    await refreshRegistryStatus();
  } catch (_e) {
    registerStatus.textContent = "Status: Please register";
    setBridgeError("Bridge offline — start live.py");
  }
}

document.getElementById("btnRegister").addEventListener("click", () => {
  registerClient();
});

async function bootstrap() {
  await refreshProfileStemCache();
  await migrateLegacyDraftsIfNeeded(cachedProfileStem);
  await loadPersistedIntoTextareas();
  await pushResumeToBridgeSession();
  await pushJdToBridgeSession();
  for (const k of TEMPLATE_KEYS) await pushTemplateToBridgeSession(k);
  await pingBridge();
  await refreshRegistryStatus();
  setInterval(refreshRegistryStatus, 2000);
  syncShellWidth();
}

bootstrap();
