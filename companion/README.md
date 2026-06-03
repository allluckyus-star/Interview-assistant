# Interview Assistant Companion (tray backend)

Windows tray app: **Live Captions** capture, caption state, hotkeys (End/Delete), resume/JD/prompt storage. Serves HTTP API for the Chrome extension.

## Run

```powershell
cd companion
dotnet run --project src/InterviewAssistant.Companion
```

Exe output: `src/InterviewAssistant.Companion/bin/Debug/net8.0-windows/InterviewAssistant.Companion.exe`

API: `http://127.0.0.1:1212/` (CORS enabled for the extension)

## Requirements

- Windows 10/11
- .NET 8 SDK (to build)
- Windows Live Captions
- Chrome extension on [chatgpt.com](https://chatgpt.com)

## Tray menu

- **Restart captions** — restarts capture session
- **Quit** — exit

## API (subset)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Companion alive |
| GET | `/draft` | Current draft + mode |
| GET | `/history` | Caption/GPT events |
| GET | `/events` | SSE stream (draft, history, hotkeys) |
| POST | `/end` | Snapshot chunk → build prompt |
| POST | `/delete` | Skip pending draft |
| GET | `/endpoint-words?count=20` | Word picker |
| POST | `/endpoint` | `{ "start_index": N }` |
| GET/POST | `/context`, `/context/resume`, `/context/jd` | Resume/JD |
| GET/POST | `/prompts/{key}` | Mode + prep templates |
| GET | `/languages` | Active language (`english` / `chinese`) |
| POST | `/language` | `{ "language": "chinese" }` |
| POST | `/mode` | `{ "mode": "read" }` |
| POST | `/gpt-answer` | Extension reports GPT reply text |
