using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InterviewAssistant.App.Services;

/// <summary>Ctrl+Insert / Ctrl+Home — snip image or OCR text into ChatGPT composer (main interview).</summary>
public sealed class SnipComposerHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int IdImageSnip = 0x8A05;
    private const int IdTextSnip = 0x8A06;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkInsert = 0x2D;
    private const uint VkHome = 0x24;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _imageRegistered;
    private bool _textRegistered;

    public event Action? ImageSnipPressed;
    public event Action? TextSnipPressed;

    public bool IsImageRegistered => _imageRegistered;
    public bool IsTextRegistered => _textRegistered;

    public SnipComposerHotkeys(Window window) => _window = window;

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
        var mods = ModControl | ModNoRepeat;
        _imageRegistered = RegisterHotKey(hwnd, IdImageSnip, mods, VkInsert);
        _textRegistered = RegisterHotKey(hwnd, IdTextSnip, mods, VkHome);
        if (!_imageRegistered)
        {
            var err = Marshal.GetLastWin32Error();
            Trace.WriteLine($"[InterviewAssistant] RegisterHotKey Ctrl+Insert failed (error {err})");
        }

        if (!_textRegistered)
        {
            var err = Marshal.GetLastWin32Error();
            Trace.WriteLine($"[InterviewAssistant] RegisterHotKey Ctrl+Home failed (error {err})");
        }

        if (_imageRegistered || _textRegistered)
        {
            Trace.WriteLine(
                $"[InterviewAssistant] Snip hotkeys: Ctrl+Insert={_imageRegistered}, Ctrl+Home={_textRegistered}");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
            return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case IdImageSnip:
                ImageSnipPressed?.Invoke();
                handled = true;
                break;
            case IdTextSnip:
                TextSnipPressed?.Invoke();
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
            UnregisterHotKey(hwnd, IdImageSnip);
            UnregisterHotKey(hwnd, IdTextSnip);
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _imageRegistered = false;
        _textRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
