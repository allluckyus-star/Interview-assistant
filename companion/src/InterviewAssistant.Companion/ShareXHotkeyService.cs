using System.Runtime.InteropServices;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

/// <summary>
/// Detect ShareX shortcuts by polling physical key state (Ctrl+PrtSc, Alt+.).
/// Works even when ShareX owns the combo via RegisterHotKey — no region UI needed.
/// </summary>
internal sealed class ShareXHotkeyService : IDisposable
{
    private const int VkSnapshot = 0x2C;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkOemPeriod = 0xBE;
    private const int VkDecimal = 0x6E;

    private readonly System.Threading.Timer _timer;
    private bool _prtScDown;
    private bool _periodDown;
    private double _lastImage;
    private double _lastText;
    private double _lastTextShortcutAt;

    public double CooldownSeconds { get; set; } = 0.8;
    public double TextShortcutRecentSeconds { get; set; } = 4.0;

    public event Action? ImageShortcutPressed;
    public event Action? TextShortcutPressed;

    public ShareXHotkeyService()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
    }

    public void Start()
    {
        StartupDiagnostics.Log("[IA ShareX] shortcut poller active (Ctrl+PrtSc, Alt+.)");
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
            var ctrl = IsDown(VkControl);
            var alt = IsDown(VkMenu);
            var prtSc = IsDown(VkSnapshot);
            var period = IsDown(VkOemPeriod) || IsDown(VkDecimal);
            var now = Environment.TickCount64 / 1000.0;

            if (prtSc && !_prtScDown && ctrl)
            {
                if (now - _lastImage >= CooldownSeconds)
                {
                    _lastImage = now;
                    StartupDiagnostics.Log("[IA ShareX] Ctrl+PrtSc detected");
                    ImageShortcutPressed?.Invoke();
                }
            }

            if (period && !_periodDown && alt && !ctrl)
            {
                if (now - _lastText >= CooldownSeconds)
                {
                    _lastText = now;
                    _lastTextShortcutAt = now;
                    StartupDiagnostics.Log("[IA ShareX] Alt+. detected");
                    TextShortcutPressed?.Invoke();
                }
            }

            _prtScDown = prtSc;
            _periodDown = period;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA ShareX] shortcut poll: {ex.Message}");
        }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
