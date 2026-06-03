# Extension + Companion stack

```
Windows Live Captions
        ↓
InterviewAssistant.Companion.exe  (tray, :1212)
        ↓ HTTP / SSE
Chrome extension (30% panel on chatgpt.com)
        ↓ DOM
ChatGPT composer
```

## Folders

| Folder | Role |
|--------|------|
| `c#project/` | Original WPF app (WebView2 + full wizard) — still buildable |
| `companion/` | Tray backend — reuse caption logic from `c#project` |
| `chrome-extension/` | UI on real ChatGPT |

## Quick start

```powershell
# Terminal 1
cd companion
dotnet run --project src/InterviewAssistant.Companion

# Chrome: load unpacked extension from chrome-extension/
# Open https://chatgpt.com
```

## Publish companion (optional)

```powershell
cd companion/src/InterviewAssistant.Companion
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../../../publish/companion
```
