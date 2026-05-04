# Interview Assistant Browser Extension

## What it does
- Polls `http://127.0.0.1:8765/next-prompt` from your local Python bridge.
- Detects new `request_id`.
- Inserts the prompt into an open ChatGPT tab.
- Optional auto-submit.

## Load in Chrome/Edge
1. Open extensions page (`chrome://extensions` or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked**.
4. Select this folder: `d:\AI\Auto Script\extension`.

## Usage
1. Start your Python app (`live.py`) so bridge server is running.
2. Open ChatGPT website in active tab.
3. Press `End` in your interview assistant app.
4. Extension injects latest prompt.

## Notes
- Keep ChatGPT tab active in the current window for injection.
- Use popup button **Pull now** to test fetch/insert instantly.
