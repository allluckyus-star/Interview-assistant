using System.Runtime.InteropServices;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

/// <summary>
/// Global shortcut detection via GetAsyncKeyState polling (works when Chrome is unfocused).
/// Bindings are synced from the extension Settings tab.
/// </summary>
internal sealed class ShareXHotkeyService : IDisposable
{
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;

    private readonly System.Threading.Timer _timer;
    private readonly object _bindGate = new();
    private ShareXShortcutBinding? _imageBinding;
    private ShareXShortcutBinding? _textBinding;
    private ShareXShortcutBinding? _sendBinding;
    private ShareXShortcutBinding? _copyGptBinding;
    private readonly Dictionary<int, bool> _keyWasDown = new();
    private double _lastImage;
    private double _lastText;
    private double _lastSend;
    private double _lastCopyGpt;
    private double _lastTextShortcutAt;

    public double CooldownSeconds { get; set; } = 0.8;
    public double TextShortcutRecentSeconds { get; set; } = 4.0;

    public event Action? ImageShortcutPressed;
    public event Action? TextShortcutPressed;
    public event Action? SendShortcutPressed;
    public event Action? CopyGptShortcutPressed;

    public ShareXHotkeyService()
    {
        _imageBinding = DefaultImageBinding();
        _textBinding = DefaultTextBinding();
        _sendBinding = DefaultSendBinding();
        _copyGptBinding = DefaultCopyGptBinding();
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
    }

    public static ShareXShortcutBinding DefaultImageBinding() =>
        new(Ctrl: true, Alt: false, Shift: false, KeyVk: 0x2C);

    public static ShareXShortcutBinding DefaultTextBinding() =>
        new(Ctrl: false, Alt: true, Shift: false, KeyVk: 0xBE, AltKeyVks: [0x6E]);

    public static ShareXShortcutBinding DefaultSendBinding() =>
        new(Ctrl: true, Alt: false, Shift: true, KeyVk: 0x0D);

    public static ShareXShortcutBinding DefaultCopyGptBinding() =>
        new(Ctrl: true, Alt: false, Shift: true, KeyVk: 0x47);

    public void UpdateBindings(
        ShareXShortcutBinding? image,
        ShareXShortcutBinding? text,
        ShareXShortcutBinding? send = null,
        ShareXShortcutBinding? copyGpt = null)
    {
        lock (_bindGate)
        {
            if (image is not null && image.EffectiveKeyVks.Length > 0) _imageBinding = image;
            if (text is not null && text.EffectiveKeyVks.Length > 0) _textBinding = text;
            if (send is not null && send.EffectiveKeyVks.Length > 0) _sendBinding = send;
            if (copyGpt is not null && copyGpt.EffectiveKeyVks.Length > 0) _copyGptBinding = copyGpt;
            _keyWasDown.Clear();
        }

        StartupDiagnostics.Log(
            "[IA shortcuts] updated " +
            $"image={DescribeBinding(_imageBinding)} " +
            $"ocr={DescribeBinding(_textBinding)} " +
            $"send={DescribeBinding(_sendBinding)} " +
            $"copy={DescribeBinding(_copyGptBinding)}");
    }

    public (ShareXShortcutBinding Image, ShareXShortcutBinding Text, ShareXShortcutBinding Send, ShareXShortcutBinding CopyGpt) GetBindings()
    {
        lock (_bindGate)
            return (
                _imageBinding ?? DefaultImageBinding(),
                _textBinding ?? DefaultTextBinding(),
                _sendBinding ?? DefaultSendBinding(),
                _copyGptBinding ?? DefaultCopyGptBinding());
    }

    public void Start()
    {
        StartupDiagnostics.Log("[IA shortcuts] global poller active (Companion, all windows)");
    }

    public bool WasTextShortcutRecent()
    {
        var now = Environment.TickCount64 / 1000.0;
        return now - _lastTextShortcutAt < TextShortcutRecentSeconds;
    }

    private void Poll()
    {
        try
        {
            ShareXShortcutBinding? image;
            ShareXShortcutBinding? text;
            ShareXShortcutBinding? send;
            ShareXShortcutBinding? copyGpt;
            lock (_bindGate)
            {
                image = _imageBinding;
                text = _textBinding;
                send = _sendBinding;
                copyGpt = _copyGptBinding;
            }

            var now = Environment.TickCount64 / 1000.0;

            if (send is not null && TryFireEdge(send, now, ref _lastSend, "send"))
                SendShortcutPressed?.Invoke();

            if (copyGpt is not null && TryFireEdge(copyGpt, now, ref _lastCopyGpt, "copy_gpt"))
                CopyGptShortcutPressed?.Invoke();

            if (image is not null && TryFireEdge(image, now, ref _lastImage, "image"))
                ImageShortcutPressed?.Invoke();

            if (text is not null && TryFireEdge(text, now, ref _lastText, "ocr"))
            {
                _lastTextShortcutAt = now;
                TextShortcutPressed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA shortcuts] poll: {ex.Message}");
        }
    }

    private bool TryFireEdge(ShareXShortcutBinding binding, double now, ref double lastFire, string label)
    {
        if (!MatchesModifiers(binding)) return false;

        foreach (var vk in binding.EffectiveKeyVks)
        {
            var down = IsDown(vk);
            _keyWasDown.TryGetValue(vk, out var wasDown);
            if (down && !wasDown)
            {
                _keyWasDown[vk] = true;
                if (now - lastFire >= CooldownSeconds)
                {
                    lastFire = now;
                    StartupDiagnostics.Log($"[IA shortcuts] {label} detected ({DescribeBinding(binding)})");
                    return true;
                }
            }
            else
            {
                _keyWasDown[vk] = down;
            }
        }

        return false;
    }

    private static bool MatchesModifiers(ShareXShortcutBinding binding)
    {
        var ctrl = IsDown(VkControl);
        var alt = IsDown(VkMenu);
        var shift = IsDown(VkShift);
        return ctrl == binding.Ctrl && alt == binding.Alt && shift == binding.Shift;
    }

    private static string DescribeBinding(ShareXShortcutBinding? b)
    {
        if (b is null) return "none";
        var mods = new List<string>();
        if (b.Ctrl) mods.Add("Ctrl");
        if (b.Alt) mods.Add("Alt");
        if (b.Shift) mods.Add("Shift");
        mods.Add($"VK=0x{b.KeyVk:X}");
        return string.Join("+", mods);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
