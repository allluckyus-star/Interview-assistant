# Interview Assistant (C# / WPF)

Desktop tool: **prep wizard** (resume/JD → login ChatGPT → summary steps → main layout with captions + WebView2), session modes, snip/OCR, and optional local HTTP bridge.

## Build and run

```powershell
dotnet build
dotnet run --project src/InterviewAssistant.App
```

The solution targets **.NET 8** (`net8.0-windows10.0.19041.0`). Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) if `dotnet` is not on your PATH.

**Runtime:** Windows desktop + **WebView2 Evergreen Runtime** (usually preinstalled).

## Single-file exe (copy to another PC)

```powershell
.\publish.ps1
```

Output: `publish\win-x64\InterviewAssistant.App.exe` (~75 MB, self-contained). Target PC still needs **WebView2 Runtime**. ARM: `.\publish.ps1 -Runtime win-arm64`.

## Configuration

- `src/InterviewAssistant.App/appsettings.json` — optional bridge (`Bridge:Host`, `Bridge:Port`, default `127.0.0.1:1212`, off at launch).
- Prep + mode prompt templates: `src/InterviewAssistant.App/Assets/` (copied next to the exe on build).

## User data (runtime)

| Path | Purpose |
|------|---------|
| `%USERPROFILE%\.interview_assistant\webview2_gpt_profile` | ChatGPT login/session |
| `%USERPROFILE%\.interview_assistant\mode_prompts.json` | Edited mode prompts |
| `%USERPROFILE%\.interview_assistant\saved_resume_jd.json` | Saved resume/JD |
| `%TEMP%\InterviewAssistant\` | Startup + caption logs |

Full workflow: [WORKFLOW.md](WORKFLOW.md).

## Solution layout

```
c#project/
├── InterviewAssistant.sln
├── publish.ps1
├── WORKFLOW.md
└── src/
    ├── InterviewAssistant.App/     WPF UI, wizard, WebView2, interview loop
    ├── InterviewAssistant.Bridge/    Optional local HTTP bridge
    └── InterviewAssistant.Core/      Shared JSON DTOs
```
