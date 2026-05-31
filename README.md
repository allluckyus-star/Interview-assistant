# Interview Assistant

Windows desktop app for live interview support: prep wizard, ChatGPT WebView2 pane, Windows Live Captions, and session modes.

All source and build files live in **`c#project/`**. See [c#project/README.md](c#project/README.md) and [c#project/WORKFLOW.md](c#project/WORKFLOW.md).

```powershell
cd c#project
dotnet build
dotnet run --project src/InterviewAssistant.App
```

**Requirements:** Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), WebView2 Runtime, Windows Live Captions.
