using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InterviewAssistant.App.Services;

/// <summary>Win32 SetWindowDisplayAffinity entry points (see <see cref="CaptureStealthMonitor"/>).</summary>
public static class WindowCaptureStealth
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool SetEnabled(Window window, bool excludeFromCapture)
    {
        if (!IsSupported)
            return false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return false;

        var affinity = excludeFromCapture ? WdaExcludeFromCapture : WdaNone;
        return SetWindowDisplayAffinity(hwnd, affinity);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
}
