using System.Diagnostics;
using System.Runtime.InteropServices;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

/// <summary>
/// Global shortcuts: LL hook for Send/Copy; GetAsyncKeyState polling for ShareX image/OCR
/// (PrintScreen and Alt+. are often invisible to WH_KEYBOARD_LL when ShareX owns the hotkey).
/// </summary>
internal sealed class ShareXHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;
    private const int LlkhfAltdown = 0x20;
    private const int LlkhfUp = unchecked((int)0x80000000);

    private readonly LowLevelKeyboardProc _proc;
    private readonly System.Threading.Timer _shareXPollTimer;
    private IntPtr _hook = IntPtr.Zero;
    private GCHandle _gcHandle;
    private readonly object _bindGate = new();
    private ShareXShortcutBinding? _imageBinding;
    private ShareXShortcutBinding? _textBinding;
    private ShareXShortcutBinding? _sendBinding;
    private ShareXShortcutBinding? _copyGptBinding;
    private readonly Dictionary<int, bool> _pollKeyWasDown = new();
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
        _proc = HookCallback;
        _gcHandle = GCHandle.Alloc(_proc);
        _imageBinding = DefaultImageBinding();
        _textBinding = DefaultTextBinding();
        _sendBinding = DefaultSendBinding();
        _copyGptBinding = DefaultCopyGptBinding();
        _shareXPollTimer = new System.Threading.Timer(_ => PollShareXBindings(), null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
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
            _pollKeyWasDown.Clear();
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
        StartupDiagnostics.Log("[IA shortcuts] ShareX poll active (Ctrl+PrtSc, Alt+.)");

        if (_hook != IntPtr.Zero)
            return;

        try
        {
            var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
            var module = string.IsNullOrEmpty(moduleName)
                ? GetModuleHandle(null)
                : GetModuleHandle(moduleName);
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, module, 0);
            if (_hook == IntPtr.Zero)
            {
                StartupDiagnostics.Log($"[IA shortcuts] panel LL hook failed (error {Marshal.GetLastWin32Error()}) — Send/Copy may not work globally");
                return;
            }

            StartupDiagnostics.Log("[IA shortcuts] panel LL hook active (Send/Copy)");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA shortcuts] panel LL hook start failed: {ex.Message}");
            _hook = IntPtr.Zero;
        }
    }

    public bool WasTextShortcutRecent()
    {
        var now = Environment.TickCount64 / 1000.0;
        return now - _lastTextShortcutAt < TextShortcutRecentSeconds;
    }

    private void PollShareXBindings()
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

            if (image is not null && TryPollEdge(image, now, ref _lastImage, "image"))
                ImageShortcutPressed?.Invoke();

            if (text is not null && TryPollEdge(text, now, ref _lastText, "ocr"))
            {
                _lastTextShortcutAt = now;
                TextShortcutPressed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA shortcuts] ShareX poll: {ex.Message}");
        }
    }

    private bool TryPollEdge(ShareXShortcutBinding binding, double now, ref double lastFire, string label)
    {
        if (!MatchesModifiers(binding)) return false;

        foreach (var vk in binding.EffectiveKeyVks)
        {
            var down = IsVkDown(vk);
            _pollKeyWasDown.TryGetValue(vk, out var wasDown);
            if (down && !wasDown)
            {
                _pollKeyWasDown[vk] = true;
                if (now - lastFire >= CooldownSeconds)
                {
                    lastFire = now;
                    StartupDiagnostics.Log($"[IA shortcuts] {label} detected via poll ({DescribeBinding(binding)})");
                    return true;
                }
            }
            else
            {
                _pollKeyWasDown[vk] = down;
            }
        }

        return false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hook != IntPtr.Zero && IsKeyDownMessage(wParam))
        {
            try
            {
                var vk = Marshal.ReadInt32(lParam, 0);
                var flags = Marshal.ReadInt32(lParam, 8);
                if ((flags & LlkhfUp) != 0)
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                HandlePanelKeyDown(vk, flags);
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"[IA shortcuts] hook callback: {ex.Message}");
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandlePanelKeyDown(int vk, int flags)
    {
        var ctrl = IsVkDown(VkControl);
        var alt = IsVkDown(VkMenu) || (flags & LlkhfAltdown) != 0;
        var shift = IsVkDown(VkShift);
        var now = Environment.TickCount64 / 1000.0;

        ShareXShortcutBinding? send;
        ShareXShortcutBinding? copyGpt;
        lock (_bindGate)
        {
            send = _sendBinding;
            copyGpt = _copyGptBinding;
        }

        if (send is not null && MatchesBinding(send, vk, ctrl, alt, shift) && now - _lastSend >= CooldownSeconds)
        {
            _lastSend = now;
            StartupDiagnostics.Log($"[IA shortcuts] send detected ({DescribeBinding(send)})");
            SendShortcutPressed?.Invoke();
            return;
        }

        if (copyGpt is not null && MatchesBinding(copyGpt, vk, ctrl, alt, shift) && now - _lastCopyGpt >= CooldownSeconds)
        {
            _lastCopyGpt = now;
            StartupDiagnostics.Log($"[IA shortcuts] copy_gpt detected ({DescribeBinding(copyGpt)})");
            CopyGptShortcutPressed?.Invoke();
        }
    }

    private static bool MatchesBinding(ShareXShortcutBinding binding, int vk, bool ctrl, bool alt, bool shift) =>
        ctrl == binding.Ctrl &&
        alt == binding.Alt &&
        shift == binding.Shift &&
        binding.EffectiveKeyVks.Contains(vk);

    private static bool MatchesModifiers(ShareXShortcutBinding binding)
    {
        var ctrl = IsVkDown(VkControl);
        var alt = IsVkDown(VkMenu);
        var shift = IsVkDown(VkShift);
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

    private static bool IsVkDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsKeyDownMessage(IntPtr wParam) =>
        wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown;

    public void Dispose()
    {
        _shareXPollTimer.Dispose();

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        if (_gcHandle.IsAllocated)
            _gcHandle.Free();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
