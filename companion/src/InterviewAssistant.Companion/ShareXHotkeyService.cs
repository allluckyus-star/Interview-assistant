using System.Runtime.InteropServices;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

/// <summary>
/// Detect ShareX shortcuts by polling physical key state.
/// Bindings are configurable from the extension settings tab.
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
    private readonly Dictionary<int, bool> _keyWasDown = new();
    private double _lastImage;
    private double _lastText;
    private double _lastTextShortcutAt;

    public double CooldownSeconds { get; set; } = 0.8;
    public double TextShortcutRecentSeconds { get; set; } = 4.0;

    public event Action? ImageShortcutPressed;
    public event Action? TextShortcutPressed;

    public ShareXHotkeyService()
    {
        _imageBinding = DefaultImageBinding();
        _textBinding = DefaultTextBinding();
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
    }

    public static ShareXShortcutBinding DefaultImageBinding() =>
        new(Ctrl: true, Alt: false, Shift: false, KeyVk: 0x2C);

    public static ShareXShortcutBinding DefaultTextBinding() =>
        new(Ctrl: false, Alt: true, Shift: false, KeyVk: 0xBE, AltKeyVks: [0x6E]);

    public void UpdateBindings(ShareXShortcutBinding? image, ShareXShortcutBinding? text)
    {
        lock (_bindGate)
        {
            if (image is not null && image.EffectiveKeyVks.Length > 0) _imageBinding = image;
            if (text is not null && text.EffectiveKeyVks.Length > 0) _textBinding = text;
            _keyWasDown.Clear();
        }

        StartupDiagnostics.Log(
            $"[IA ShareX] shortcuts updated image={DescribeBinding(_imageBinding)} text={DescribeBinding(_textBinding)}");
    }

    public (ShareXShortcutBinding Image, ShareXShortcutBinding Text) GetBindings()
    {
        lock (_bindGate)
            return (_imageBinding ?? DefaultImageBinding(), _textBinding ?? DefaultTextBinding());
    }

    public void Start()
    {
        StartupDiagnostics.Log("[IA ShareX] shortcut poller active (configurable bindings)");
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
            lock (_bindGate)
            {
                image = _imageBinding;
                text = _textBinding;
            }

            var now = Environment.TickCount64 / 1000.0;

            if (image is not null && TryFireEdge(image, now, ref _lastImage, "image"))
                ImageShortcutPressed?.Invoke();

            if (text is not null && TryFireEdge(text, now, ref _lastText, "text"))
            {
                _lastTextShortcutAt = now;
                TextShortcutPressed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA ShareX] shortcut poll: {ex.Message}");
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
                    StartupDiagnostics.Log($"[IA ShareX] {label} shortcut detected ({DescribeBinding(binding)})");
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
