const BRIDGE = "http://127.0.0.1:8765";

const shell = document.getElementById("shell");
const panelResume = document.getElementById("panelResume");
const panelJD = document.getElementById("panelJD");
const btnResume = document.getElementById("btnResume");
const btnJD = document.getElementById("btnJD");
const resumeText = document.getElementById("resumeText");
const jdText = document.getElementById("jdText");
const bridgeErr = document.getElementById("bridgeErr");
const registerStatus = document.getElementById("registerStatus");
const selectedClientStatus = document.getElementById("selectedClientStatus");

const W_MENU = 248;
const W_PANEL = 280;

let resumeOpen = false;
let jdOpen = false;
let saveResumeTimer = null;
let saveJdTimer = null;

function syncShellWidth() {
  let w = W_MENU;
  if (resumeOpen) w += W_PANEL;
  if (jdOpen) w += W_PANEL;
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

async function fetchContext() {
  try {
    const res = await fetch(`${BRIDGE}/context`, { cache: "no-store" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    resumeText.value = data.resume ?? "";
    jdText.value = data.job_description ?? "";
    setBridgeError("");
  } catch (e) {
    setBridgeError("Bridge offline — start live.py");
  }
}

async function postResume(text) {
  try {
    await fetch(`${BRIDGE}/context/resume`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text })
    });
    setBridgeError("");
  } catch (_e) {
    setBridgeError("Could not save resume");
  }
}

async function postJd(text) {
  try {
    await fetch(`${BRIDGE}/context/jd`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text })
    });
    setBridgeError("");
  } catch (_e) {
    setBridgeError("Could not save JD");
  }
}

function scheduleSaveResume() {
  clearTimeout(saveResumeTimer);
  saveResumeTimer = setTimeout(() => postResume(resumeText.value), 400);
}

function scheduleSaveJd() {
  clearTimeout(saveJdTimer);
  saveJdTimer = setTimeout(() => postJd(jdText.value), 400);
}

btnResume.addEventListener("click", () => {
  resumeOpen = !resumeOpen;
  panelResume.classList.toggle("open", resumeOpen);
  btnResume.classList.toggle("toggle-on", resumeOpen);
  panelResume.setAttribute("aria-hidden", resumeOpen ? "false" : "true");
  syncShellWidth();
  if (resumeOpen) resumeText.focus();
});

btnJD.addEventListener("click", () => {
  jdOpen = !jdOpen;
  panelJD.classList.toggle("open", jdOpen);
  btnJD.classList.toggle("toggle-on", jdOpen);
  panelJD.setAttribute("aria-hidden", jdOpen ? "false" : "true");
  syncShellWidth();
  if (jdOpen) jdText.focus();
});

resumeText.addEventListener("input", scheduleSaveResume);
resumeText.addEventListener("blur", () => postResume(resumeText.value));
jdText.addEventListener("input", scheduleSaveJd);
jdText.addEventListener("blur", () => postJd(jdText.value));

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
      profile_id: (info.id || "").trim()
    };
  } catch (_e) {
    return { email: "", profile_id: "" };
  }
}

async function refreshRegistryStatus() {
  try {
    const res = await fetch(`${BRIDGE}/registered-clients`, { cache: "no-store" });
    if (!res.ok) throw new Error("unreachable");
    const data = await res.json();
    const sel = data.selected_client_id;
    const clients = Array.isArray(data.clients) ? data.clients : [];
    const selObj = clients.find((c) => c && c.client_id === sel);
    if (sel == null || sel === "") {
      selectedClientStatus.textContent = "Selected on server: (none)";
    } else {
      const label = selObj && (selObj.label || selObj.email) ? selObj.label || selObj.email : sel;
      selectedClientStatus.textContent = `Selected on server: ${label}`;
    }
  } catch (_e) {
    selectedClientStatus.textContent = "";
  }
}

async function registerClient() {
  registerStatus.textContent = "Registering…";
  const clientId = await getOrCreateClientId();
  const { email, profile_id } = await getChromeProfileInfo();
  const label = email || `Chrome client ${clientId.slice(0, 6)}`;
  try {
    const res = await fetch(`${BRIDGE}/register-client`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        client_id: clientId,
        email,
        profile_id,
        label
      })
    });
    let body = {};
    try {
      body = await res.json();
    } catch (_e) {
      body = {};
    }
    if (!res.ok) {
      registerStatus.textContent = "Python app not reachable. Start live.py first.";
      return;
    }
    if (!body.ok) {
      registerStatus.textContent = body.error || "Registration failed";
      return;
    }
    const client = body.client || {};
    await chrome.storage.local.set({
      clientLabel: client.label || label,
      email: client.email ?? email,
      profileId: client.profile_id ?? profile_id
    });
    registerStatus.textContent = `Registered: ${client.label || label}`;
    await refreshRegistryStatus();
  } catch (_e) {
    registerStatus.textContent = "Python app not reachable. Start live.py first.";
  }
}

document.getElementById("btnRegister").addEventListener("click", () => {
  registerClient();
});

async function initPopupLabels() {
  const local = await chrome.storage.local.get(["clientLabel"]);
  if (local.clientLabel) {
    registerStatus.textContent = `This profile: ${local.clientLabel}`;
  }
}

initPopupLabels();
fetchContext();
refreshRegistryStatus();
syncShellWidth();
