# Interview Assistant — Chrome extension

Docked **30% right panel** on ChatGPT (`chatgpt.com`). **70% left** remains the real ChatGPT UI. Requires **Companion** tray app running on `http://127.0.0.1:1212`.

## Install (developer)

1. Build and run Companion (see `companion/README.md`).
2. Chrome → `chrome://extensions` → **Developer mode** → **Load unpacked**.
3. Select this folder: `chrome-extension/`.
4. Open [https://chatgpt.com](https://chatgpt.com) (extension does **not** run on other sites).

5. **Reload extension** after updates: `chrome://extensions` → refresh icon on Interview Assistant.

## Layout (70% / 30%)

ChatGPT is **compressed to 70%** of the window width on the left. The extension panel uses the **right 30%** — side by side, **not overlapping** the chat.

## Collapse / expand

- **▶** in the panel header — collapse (ChatGPT uses full width; slim tab on the right edge).
- Edge tab — expand again.
- **Extension toolbar icon** on ChatGPT — same toggle.
- Animated width transition (~0.28s).

## UI

- **Caption** tab — live draft, history bubbles, toolbar (Mode, Save, Send, Reject, Image, Text).
- **Settings** tab — resume, JD, prompt templates.

| Button | Action |
|--------|--------|
| Send | Same as **End** — send chunk + mode prompt to ChatGPT |
| Reject | Same as **Delete** — skip pending draft |
| Mode | Cycle Read / Type / Error / Behavioral / Closing |
| Text / Image | Paste live draft into composer (Shift+click = append) |

Global **End** / **Delete** keys work when Companion is running (handled by tray, extension reacts via SSE).

## Styling

Panel CSS is **embedded** in `content/panel-styles.js` (ChatGPT CSP blocks `fetch()` of extension CSS inside the page). After editing `content/panel.css`, regenerate:

```bash
node -e "const fs=require('fs');const p='content/panel.css';fs.writeFileSync('content/panel-styles.js','window.__IA_PANEL_CSS = '+JSON.stringify(fs.readFileSync(p,'utf8'))+';\\n');"
```

(Run from `chrome-extension/`.)

## Files

- `content/layout.js` — 70/30 split + panel host
- `content/panel.css` — source of truth for panel styles
- `content/panel-styles.js` — embedded CSS (loaded before `panel.js`)
- `content/panel.js` — sidebar UI
- `content/gpt-send.js` — ChatGPT composer send (from `c#project` pipeline)
- `content/api.js` — Companion HTTP client
- `background.js` — fetch proxy (CORS)
