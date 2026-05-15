using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InterviewAssistant.App.Services;

/// <summary>Alt+Shift+1/2 via RegisterHotKey — reliable on Windows (WM_HOTKEY).</summary>
public sealed class OpacityWindowHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int IdOpacityFull = 0x8A01;
    private const int IdOpacityZero = 0x8A02;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint Vk1 = 0x31;
    private const uint Vk2 = 0x32;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _registered;

    public event Action? OpacityFullPressed;
    public event Action? OpacityZeroPressed;

    public bool IsRegistered => _registered;

    public OpacityWindowHotkeys(Window window) => _window = window;

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
        var ok1 = RegisterHotKey(hwnd, IdOpacityFull, mods, Vk1);
        var ok2 = RegisterHotKey(hwnd, IdOpacityZero, mods, Vk2);
        _registered = ok1 && ok2;
        if (!_registered)
        {
            var err = Marshal.GetLastWin32Error();
            Trace.WriteLine($"[InterviewAssistant] RegisterHotKey opacity failed (error {err}) ok1={ok1} ok2={ok2}");
        }
        else
        {
            Trace.WriteLine("[InterviewAssistant] Opacity hotkeys registered (Alt+Shift+1/2)");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
            return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case IdOpacityFull:
                OpacityFullPressed?.Invoke();
                handled = true;
                break;
            case IdOpacityZero:
                OpacityZeroPressed?.Invoke();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _window.SourceInitialized -= OnSourceInitialized;
        var hwnd = _window.IsLoaded ? new WindowInteropHelper(_window).Handle : IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, IdOpacityFull);
            UnregisterHotKey(hwnd, IdOpacityZero);
        }

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
