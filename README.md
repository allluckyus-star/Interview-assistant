# Interview Assistant

Windows live-interview assistant: **Windows Live Captions**, session modes, and ChatGPT.

| Stack | Folder | Description |
|-------|--------|-------------|
| **WPF desktop** | [c#project/](c#project/) | Full app with embedded WebView2 |
| **Chrome + tray** | [companion/](companion/) + [chrome-extension/](chrome-extension/) | Real ChatGPT in Chrome, 70/30 panel, tray captures captions |

See [EXTENSION-STACK.md](EXTENSION-STACK.md) for the extension workflow.

### WPF app

```powershell
cd c#project
dotnet run --project src/InterviewAssistant.App
```

### Extension + Companion

```powershell
cd companion
dotnet run --project src/InterviewAssistant.Companion
```

Then load `chrome-extension/` in Chrome and open [chatgpt.com](https://chatgpt.com).

**Requirements:** Windows 10/11, .NET 8 SDK, WebView2 (WPF app), Windows Live Captions.
