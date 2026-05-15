using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Hide from the taskbar (and Alt+Tab list) like live.py Qt.Tool — process still visible in Task Manager.
/// Does not add a notification-area tray icon.
/// </summary>
public static class WindowTaskbarHiding
{
    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;

    public static void Apply(Window window)
    {
        window.ShowInTaskbar = false;
        if (window.IsLoaded)
            ApplyToolWindowStyle(window);
        else
            window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.SourceInitialized -= OnSourceInitialized;
        ApplyToolWindowStyle(window);
    }

    private static void ApplyToolWindowStyle(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var style = IntPtr.Size == 8
            ? GetWindowLongPtr(hwnd, GwlExstyle)
            : new IntPtr(GetWindowLong32(hwnd, GwlExstyle));
        var updated = (style.ToInt64() | WsExToolwindow) & ~WsExAppwindow;
        if (IntPtr.Size == 8)
            SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(updated));
        else
            SetWindowLong32(hwnd, GwlExstyle, (int)updated);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
