using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InterviewAssistant.App.Services;

/// <summary>Alt+Shift+3 toggles exclude-from-capture stealth (see <see cref="CaptureStealthMonitor"/>).</summary>
public sealed class CaptureStealthHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int IdStealthToggle = 0x8A04;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint Vk3 = 0x33;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _registered;

    public event Action? StealthTogglePressed;

    public bool IsRegistered => _registered;

    public CaptureStealthHotkeys(Window window) => _window = window;

    public void Attach()
    {
        if (_source is not null)
            return;

        if (_window.IsLoaded)
            AttachToHandle();
        else
            _window.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        AttachToHandle();
    }

    private void AttachToHandle()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        _source = HwndSource.FromHwnd(hwnd);
        if (_source is null)
            return;

        _source.AddHook(WndProc);
        var mods = ModAlt | ModShift | ModNoRepeat;
        var ok = RegisterHotKey(hwnd, IdStealthToggle, mods, Vk3);
        _registered = ok;
        if (!_registered)
        {
            var err = Marshal.GetLastWin32Error();
            Trace.WriteLine($"[InterviewAssistant] RegisterHotKey stealth toggle failed (error {err})");
        }
        else
            Trace.WriteLine("[InterviewAssistant] Stealth hotkey registered (Alt+Shift+3)");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt32() != IdStealthToggle)
            return IntPtr.Zero;

        StealthTogglePressed?.Invoke();
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _window.SourceInitialized -= OnSourceInitialized;
        var hwnd = _window.IsLoaded ? new WindowInteropHelper(_window).Handle : IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
            UnregisterHotKey(hwnd, IdStealthToggle);

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _registered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
