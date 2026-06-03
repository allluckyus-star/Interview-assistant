/** Runs at document_start — dock + collapsed state before ChatGPT paints. */
(function () {
  const h = location.hostname.replace(/^www\./, "");
  if (h !== "chatgpt.com" && h !== "chat.openai.com") return;

  const root = document.documentElement;
  root.classList.add("ia-docked");

  try {
    if (localStorage.getItem("iaPanelCollapsed") === "1") {
      root.classList.add("ia-panel-collapsed");
    }
  } catch {
    // ignore
  }
})();
