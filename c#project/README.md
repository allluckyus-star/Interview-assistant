# Interview Assistant (C# / WPF mirror)

This folder is a **credible parallel** to the Python **Interview-assistant** desktop tool: **prep-style wizard** (resume/JD → login ChatGPT → summary steps → main layout with captions + WebView2), similar **shell colors** (`#4F46E5` primary, prep grays), local **HTTP bridge** on the same default address, **single persistent WebView2** (`%USERPROFILE%\.interview_assistant\webview2_gpt_profile`), **always on top**, thin shell border, round **close** icon, **no** Prep/Session nav tabs and **no** top-bar opacity slider (window stays opaque unless you add one back).

## Build and run

```powershell
cd "D:\AI\Auto Script\Interview-assistant\c#project"
dotnet build
dotnet run --project src/InterviewAssistant.App
```

The solution targets **.NET 8** (`net8.0` / `net8.0-windows`). If `dotnet` is not on your `PATH`, install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and reopen the terminal.

**Runtime:** **Windows** desktop and the **WebView2 Evergreen Runtime** (usually already present on up-to-date Windows).

## Single-file exe (copy to another PC)

On a machine **with** the .NET 8 SDK, build a self-contained one-file app (~75 MB):

```powershell
cd "D:\AI\Auto Script\Interview-assistant\c#project"
.\publish.ps1
```

Output: `publish\win-x64\InterviewAssistant.App.exe` — copy **only that file** to another Windows 10/11 x64 PC. No SDK or .NET install required there.

- First launch may take a few seconds (bundled files extract to a temp folder).
- Target PC still needs the **WebView2 Runtime** ([installer](https://go.microsoft.com/fwlink/p/?LinkId=2124703) if ChatGPT pane fails).
- For ARM laptops: `.\publish.ps1 -Runtime win-arm64`

## Configuration

- `src/InterviewAssistant.App/appsettings.json` — `Bridge:Host` and `Bridge:Port` (default `127.0.0.1:8765`, matching `PromptBridgeServer` in `bridge_server.py`). Change the port if the Python bridge is already running.

## WebView2 profile

ChatGPT uses a persistent profile directory (mirrors the idea of a dedicated browser profile):

`%USERPROFILE%\.interview_assistant\webview2_gpt_profile`

## Python → C# mapping

| Python | C# |
|--------|-----|
| `live.py` + `prep_wizard.py` — wizard steps, shell, embedded GPT | `MainWindow.xaml` / `MainWindow.xaml.cs`: step 1 resume/JD, steps 2–5 overlays, one `WebView2`, then main 30% captions / 70% GPT |
| `live.py` — session modes, snip, toasts, extension wiring | **Not ported** |
| `prep_wizard.py` — bridge jobs, LLM, extension status | **Not ported** (buttons are UI-only) |
| `bridge_server.py` — `ThreadingHTTPServer`, `PromptStore`, JSON routes | `src/InterviewAssistant.Bridge/BridgeHttpServer.cs`, `PromptStore.cs` |
| `bridge_server.py` — full route set (`/register-client`, `/answer`, `/prep/job`, `/registered-clients`, …) | **Subset** (see below). |
| `local_profile_store.py` / registry JSON | **Not implemented** in C# sample. |

## Implemented bridge routes (subset)

Aligned with `bridge_server.py` where noted; extras are clearly marked.

| Method | Path | Behavior |
|--------|------|----------|
| GET | `/ping`, `/health` | **`/health` is C#-only** convenience; Python has no dedicated health route. Returns `{ "ok": true, "service": "interview-assistant-bridge" }`. |
| GET | `/next-prompt` | Same shape as Python: `request_id`, `created_at`, `prompt` (in-memory; starts empty). |
| GET | `/latest-answer` | Same shape as Python: `request_id`, `created_at`, `answer`. |
| GET | `/context` | Same top-level shape as Python: `resume`, `job_description`, `templates` (`resume_summary`, `jd_summary`, `initial_interview`). No `client_id` query handling. |
| POST | `/ack` | Same as Python: `{ "status": "ok" }`. |

Requests are logged with `System.Diagnostics.Debug.WriteLine` only (quiet, similar in spirit to the Python “quiet” page / minimal console noise).

## Intentional gaps vs Python

- **No browser extension** and no Chrome MV3 messaging; no port of extension-side logic.
- **No** full `live.py`: session modes, OS snip, WebSocket/audio, toasts, extension UI, or PySide6/WebEngine tricks — **solid** WebView2 `DefaultBackgroundColor` near `#FCFCFF`.
- **No** most `bridge_server.py` POST/GET routes: `/answer`, `/context/resume`, `/context/jd`, `/register-client`, `/prep/*`, `/gpt-state`, `/interview-live`, `/registered-clients`, `/wait-register-event`, etc.
- **No** `prep_wizard.py` LLM calls, job queue, or extension status pipeline.
- **No** `bridge_server.py` session reset / `registered_clients.json` persistence.

## Window chrome

- `WindowStyle="None"`, **`WindowChrome`** resize border, **`Topmost="True"`**, thin **`ShellCard`** border (`#E2E8F0`), outer window **`AllowsTransparency="True"`** + **`Background="Transparent"`** so the margin outside the card can show desktop tint (same idea as Python tool window).
- Drag: title strip + empty chrome on step 1 (not on `TextBox` / `Button`).

## Solution layout

- `InterviewAssistant.sln`
- `src/InterviewAssistant.App` — WPF host, wizard UI, single WebView2, bridge lifetime (start at startup, dispose on exit).
- `src/InterviewAssistant.Bridge` — `HttpListener` server + in-memory store.
- `src/InterviewAssistant.Core` — shared DTOs for bridge JSON payloads.
