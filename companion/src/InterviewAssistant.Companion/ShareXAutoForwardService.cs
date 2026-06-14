using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

/// <summary>Arm on ShareX shortcuts only when extension listen toggles are on.</summary>
internal sealed class ShareXAutoForwardService : IDisposable
{
    private readonly CompanionClipboardService _clipboard;
    private readonly Action<ShareXImageResponse> _onImageReady;
    private readonly Action<ShareXTextResponse> _onTextReady;
    private readonly ShareXHotkeyService _shortcuts;
    private readonly ShareXListenState _listenState;
    private readonly object _waitGate = new();
    private CancellationTokenSource? _waitCts;
    private int _armedKind;

    private const int ArmedNone = 0;
    private const int ArmedImage = 1;
    private const int ArmedText = 2;

    public ShareXAutoForwardService(
        CompanionClipboardService clipboard,
        ShareXListenState listenState,
        Action<ShareXImageResponse> onImageReady,
        Action<ShareXTextResponse> onTextReady)
    {
        _clipboard = clipboard;
        _listenState = listenState;
        _onImageReady = onImageReady;
        _onTextReady = onTextReady;
        _shortcuts = new ShareXHotkeyService();

        _shortcuts.ImageShortcutPressed += () => TryArmImage("shortcut");
        _shortcuts.TextShortcutPressed += () => TryArmText("shortcut");
    }

    public ShareXHotkeyService Shortcuts => _shortcuts;

    public void Start()
    {
        _shortcuts.Start();
        StartupDiagnostics.Log("[IA ShareX] armed-wait mode (listen toggles + shortcut → clipboard)");
    }

    private CancellationToken BeginWait(int kind)
    {
        lock (_waitGate)
        {
            _waitCts?.Cancel();
            _waitCts?.Dispose();
            _waitCts = new CancellationTokenSource();
            _armedKind = kind;
            return _waitCts.Token;
        }
    }

    private void EndWait(int kind)
    {
        lock (_waitGate)
        {
            if (_armedKind == kind)
                _armedKind = ArmedNone;
        }
    }

    private void TryArmImage(string reason)
    {
        if (!_listenState.ImageEnabled)
        {
            StartupDiagnostics.Log("[IA ShareX] image shortcut ignored — toggle ▣ ON in panel");
            return;
        }

        lock (_waitGate)
        {
            if (_armedKind == ArmedText)
                return;
        }

        BeginWait(ArmedImage);
        StartupDiagnostics.Log($"[IA ShareX] armed image ({reason}) — waiting for first clipboard image");
        _ = RunImageWaitAsync();
    }

    private void TryArmText(string reason)
    {
        if (!_listenState.TextEnabled)
        {
            StartupDiagnostics.Log("[IA ShareX] OCR shortcut ignored — toggle T ON in panel");
            return;
        }

        BeginWait(ArmedText);
        StartupDiagnostics.Log($"[IA ShareX] armed OCR ({reason}) — waiting for first clipboard text");
        _ = RunTextWaitAsync();
    }

    private async Task RunImageWaitAsync()
    {
        CancellationToken token;
        lock (_waitGate)
            token = _waitCts?.Token ?? CancellationToken.None;

        try
        {
            var result = await _clipboard.WaitForShareXImageAsync(token).ConfigureAwait(false);
            if (result.Ok)
            {
                StartupDiagnostics.Log($"[IA ShareX] image captured ({result.ImageId})");
                _onImageReady(result);
            }
            else if (!result.Cancelled && !string.Equals(result.Error, "clipboard_busy", StringComparison.Ordinal))
            {
                StartupDiagnostics.Log($"[IA ShareX] image wait ended: {result.Error ?? "cancelled"}");
            }
        }
        finally
        {
            EndWait(ArmedImage);
        }
    }

    private async Task RunTextWaitAsync()
    {
        CancellationToken token;
        lock (_waitGate)
            token = _waitCts?.Token ?? CancellationToken.None;

        try
        {
            var result = await _clipboard.WaitForShareXTextAsync(token).ConfigureAwait(false);
            if (result.Ok)
            {
                StartupDiagnostics.Log($"[IA ShareX] OCR text captured ({result.Text?.Length ?? 0} chars)");
                _onTextReady(result);
            }
            else if (!result.Cancelled && !string.Equals(result.Error, "clipboard_busy", StringComparison.Ordinal))
            {
                StartupDiagnostics.Log($"[IA ShareX] text wait ended: {result.Error ?? "cancelled"}");
            }
        }
        finally
        {
            EndWait(ArmedText);
        }
    }

    public void Dispose()
    {
        lock (_waitGate)
        {
            _waitCts?.Cancel();
            _waitCts?.Dispose();
            _waitCts = null;
        }

        _shortcuts.Dispose();
    }
}
