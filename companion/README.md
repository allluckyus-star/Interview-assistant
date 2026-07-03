# Interview Assistant Companion (tray backend)

Windows tray app: **Live Captions** capture, caption state, hotkeys (End/Delete), resume/JD/prompt storage. Serves HTTP API for the Chrome extension.

## Run

```powershell
cd companion
dotnet run --project src/InterviewAssistant.Companion
```

Exe output: `src/InterviewAssistant.Companion/bin/Debug/net8.0-windows/InterviewAssistant.Companion.exe`

API: `http://127.0.0.1:1212/` (CORS enabled for the extension)

## Single-file exe (copy to another PC)

From the `companion` folder:

```powershell
.\publish.ps1
```

Output: `publish\win-x64\InterviewAssistant.Companion.exe` (self-contained, ~60–80 MB). No .NET SDK needed on the target machine.

ARM64 Windows: `.\publish.ps1 -Runtime win-arm64`

Manual equivalent:

```powershell
dotnet publish src/InterviewAssistant.Companion/InterviewAssistant.Companion.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeAllContentForSelfExtract=true `
  -o publish/win-x64
```

Prompt templates under `Assets\` are bundled into the exe and extracted next to it on first run.

## Requirements (target PC)

| Needed | Notes |
|--------|--------|
| **64-bit Windows 10 (2004+)** or **Windows 11** | For `win-x64` build. ARM PCs need `.\publish.ps1 -Runtime win-arm64` |
| **No .NET install** | Runtime is inside the exe |
| **No WebView2** | Unlike the main desktop app — companion does not use WebView2 |
| **Windows Live Captions** | Settings → Accessibility → Live captions. Usually **Windows 11 22H2+**; many **Windows 10** PCs do not have `LiveCaptions.exe` |
| **Desktop session** | Tray icon in notification area; may not appear on headless/remote-only setups |
| **Port 1212 free** | API is `http://127.0.0.1:1212/` |
| **Chrome extension** | Only for the ChatGPT panel — not required just to start the exe |

## Troubleshooting on another PC

1. Run the exe once, then open **`InterviewAssistant-startup.log`** next to the exe (or `%TEMP%\InterviewAssistant\startup.log`).
2. Right-click exe → **Properties → Unblock** if shown.
3. If antivirus blocks single-file extract, run in cmd:
   ```cmd
   set DOTNET_BUNDLE_EXTRACT_BASE_DIR=D:\InterviewAssistant-bundle
   InterviewAssistant.Companion.exe
   ```
4. Test API: open `http://127.0.0.1:1212/health` in a browser on the same machine.
5. Full checklist: `publish/READ-ME-on-other-PC.txt`

## Build requirements

- Windows 10/11
- .NET 8 SDK (to build)
- Windows Live Captions
- Chrome extension on [chatgpt.com](https://chatgpt.com)

## Tray menu

- **Restart captions** — restarts capture session
- **Run at Windows startup** — register in Windows login startup (on by default)
- **Quit** — exits the app (stops API and captions)

If you run with `dotnet run`, **Ctrl+C** in that terminal also stops the host. After tray **Quit**, the companion process should disappear from Task Manager within a few seconds.

## Extension reconnect

On ChatGPT, use **Reconnect** next to the status dot after you start or restart the companion — no need to refresh the whole page.

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
