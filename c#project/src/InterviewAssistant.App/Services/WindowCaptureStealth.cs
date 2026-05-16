using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InterviewAssistant.App.Services;

/// <summary>Win32 SetWindowDisplayAffinity entry points (see <see cref="CaptureStealthMonitor"/>).</summary>
public static class WindowCaptureStealth
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaMonitor = 0x00000001;
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool SetEnabled(Window window, bool excludeFromCapture)
    {
        if (!IsSupported)
            return false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return false;

        return SetHwndExcludeFromCapture(hwnd, excludeFromCapture);
    }

    /// <summary>Exclude an arbitrary HWND from screen capture (e.g. Windows Live Captions).</summary>
    public static bool SetHwndExcludeFromCapture(IntPtr hwnd, bool excludeFromCapture)
    {
        if (!IsSupported || hwnd == IntPtr.Zero)
            return false;

        if (!excludeFromCapture)
            return SetAffinity(hwnd, WdaNone);

        if (SetAffinity(hwnd, WdaExcludeFromCapture))
            return true;

        if (SetAffinity(hwnd, WdaMonitor))
            return true;

        var err = Marshal.GetLastWin32Error();
        Trace.WriteLine(
            $"[InterviewAssistant] SetWindowDisplayAffinity failed hwnd=0x{hwnd.ToInt64():X} err={err}");
        return false;
    }

    private static bool SetAffinity(IntPtr hwnd, uint affinity) =>
        SetWindowDisplayAffinity(hwnd, affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
}
