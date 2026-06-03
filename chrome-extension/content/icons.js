/** Inline SVG icons — aligned with c#project TopBarIcons.cs */
window.IaIcons = {
  chevronRight: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 6l6 6-6 6"/></svg>',
  chevronLeft: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 6l-6 6 6 6"/></svg>',
  send: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M22 2L11 13"/><path d="M22 2l-7 20-4-9-9-4 20-7z"/></svg>',
  close: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6L6 18M6 6l12 12"/></svg>',
  check: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 6L9 17l-5-5"/></svg>',
  image: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8.5" cy="8.5" r="1.5"/><path d="M21 15l-5-5L5 21"/></svg>',
  text: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7V4h16v3M9 20h6M12 4v16"/></svg>',
  layers: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5"/></svg>',
  nudge: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 12H5M12 19l-7-7 7-7"/></svg>',
  save: '<svg viewBox="0 0 16 16" aria-hidden="true"><path fill="currentColor" d="M8 1 4 6h2.5v5h3V6H12L8 1zM2 12.5h12V15H2v-2.5z"/></svg>',
  folder: '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z"/></svg>',
  upload: '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M9 16h6v-6h4l-7-7-7 7h4v6zm-4 2h14v2H5v-2z"/></svg>',
  briefcase: '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M20 6h-4V4c0-1.11-.89-2-2-2h-4c-1.11 0-2 .89-2 2v2H4c-1.11 0-2 .89-2 2v11c0 1.11.89 2 2 2h16c1.11 0 2-.89 2-2V8c0-1.11-.89-2-2-2zm-6 0h-4V4h4v2z"/></svg>',
  info: '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z"/></svg>',
  modeRead:
    '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M8 2.5h10.5c.83 0 1.5.67 1.5 1.5v16c0 .83-.67 1.5-1.5 1.5H8A2.5 2.5 0 0 1 5.5 19V5A2.5 2.5 0 0 1 8 2.5zm1.5 5.25h7v1.5h-7zm0 3.5h7v1.5h-7zm0 3.5h4.5v1.5H9.5z"/></svg>',
  modeType:
    '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M3 17.46V20.5c0 .28.22.5.5.5h3.04c.13 0 .26-.05.35-.15L17.81 9.94l-3.59-3.59L3.35 17.11c-.09.09-.15.22-.15.35zM20.71 7.04a1.003 1.003 0 0 0 0-1.41l-2.34-2.34a1.003 1.003 0 0 0-1.41 0l-1.83 1.83 3.59 3.59 1.99-1.67z"/></svg>',
  modeBehavioral:
    '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M5 4.5h8.5A2.25 2.25 0 0 1 15.75 6.75v3.5A2.25 2.25 0 0 1 13.5 12.5h-1.9L9.25 15.5V12.5H5A2.25 2.25 0 0 1 2.75 10.25v-3.5A2.25 2.25 0 0 1 5 4.5z"/></svg>',
};

window.IaIcons.forPromptKey = function (key) {
  const map = {
    resume_summary: "folder",
    jd_summary: "briefcase",
    initial_interview: "info",
    read: "modeRead",
    type: "modeType",
    behavioral: "modeBehavioral",
  };
  const k = map[key] || "info";
  return window.IaIcons[k] || "";
};

window.IaIcons.forMode = function (modeId) {
  const map = { read: "modeRead", type: "modeType", behavioral: "modeBehavioral" };
  const k = map[modeId] || "modeRead";
  return window.IaIcons[k] || "";
};
