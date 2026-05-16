using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Applies exclude-from-capture to Windows Live Captions (<c>LiveCaptions.exe</c>) and related host HWNDs.
/// </summary>
public static class LiveCaptionsStealth
{
    private static readonly HashSet<IntPtr> sTrackedHwnds = new();
    private static DispatcherTimer? sMaintainerTimer;
    private static bool sMaintainerExclude;

    public static void Sync(bool excludeFromCapture)
    {
        if (!WindowCaptureStealth.IsSupported)
            return;

        sMaintainerExclude = excludeFromCapture;

        if (!excludeFromCapture)
        {
            StopMaintainer();
            foreach (var hwnd in sTrackedHwnds.ToArray())
                WindowCaptureStealth.SetHwndExcludeFromCapture(hwnd, false);
            sTrackedHwnds.Clear();
            return;
        }

        var found = ApplyToAllKnownWindows();
        Trace.WriteLine(
            $"[InterviewAssistant] LiveCaptions stealth sync: applied={found}, tracked={sTrackedHwnds.Count}");
    }

    /// <summary>Called when UI Automation locates the Live Captions surface (most reliable HWND).</summary>
    public static void ReportAutomationWindow(AutomationElement? window)
    {
        if (!sMaintainerExclude || window is null)
            return;

        try
        {
            var hwnd = (IntPtr)window.Current.NativeWindowHandle;
            if (hwnd != IntPtr.Zero)
                ApplyToHwndTree(hwnd, exclude: true);
        }
        catch (ElementNotAvailableException)
        {
            // window closed
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InterviewAssistant] LiveCaptions UIA hwnd: {ex.Message}");
        }
    }

    public static void ScheduleRetries(Dispatcher dispatcher, bool excludeFromCapture, int attempts = 14)
    {
        if (!excludeFromCapture || !WindowCaptureStealth.IsSupported)
            return;

        Sync(true);
        StartMaintainer(dispatcher);

        // Extra burst right after LiveCaptions.exe restart (HWNDs appear late).
        var burst = 0;
        var burstTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        burstTimer.Tick += (_, _) =>
        {
            Sync(true);
            burst++;
            if (burst >= attempts)
                burstTimer.Stop();
        };
        burstTimer.Start();
    }

    private static void StartMaintainer(Dispatcher dispatcher)
    {
        if (sMaintainerTimer is not null)
            return;

        sMaintainerTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        sMaintainerTimer.Tick += (_, _) =>
        {
            if (sMaintainerExclude)
                ApplyToAllKnownWindows();
            else
                StopMaintainer();
        };
        sMaintainerTimer.Start();
    }

    private static void StopMaintainer()
    {
        if (sMaintainerTimer is null)
            return;
        sMaintainerTimer.Stop();
        sMaintainerTimer = null;
    }

    private static int ApplyToAllKnownWindows()
    {
        var pids = CollectLiveCaptionsProcessIds();
        var titleBuf = new StringBuilder(512);
        var applied = 0;

        EnumWindows(
            (hwnd, _) =>
            {
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                    return true;

                GetWindowThreadProcessIdNative(hwnd, out uint processId);
                var matchesPid = pids.Contains(processId);
                var matchesTitle = !matchesPid && HasLiveCaptionsTitle(hwnd, titleBuf);

                if (!matchesPid && !matchesTitle)
                    return true;

                applied += ApplyToHwndTree(hwnd, exclude: true);
                return true;
            },
            IntPtr.Zero);

        return applied;
    }

    private static HashSet<uint> CollectLiveCaptionsProcessIds()
    {
        var pids = new HashSet<uint>();
        try
        {
            foreach (var proc in Process.GetProcessesByName("LiveCaptions"))
            {
                try
                {
                    pids.Add((uint)proc.Id);
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // ignore
        }

        return pids;
    }

    private static bool HasLiveCaptionsTitle(IntPtr hwnd, StringBuilder titleBuf)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return false;

        titleBuf.Clear();
        titleBuf.EnsureCapacity(len + 1);
        _ = GetWindowText(hwnd, titleBuf, titleBuf.Capacity);
        var title = titleBuf.ToString();
        if (title.Length == 0)
            return false;

        return title.Equals(LiveCaptionsRestarter.WindowTitle, StringComparison.OrdinalIgnoreCase)
            || title.Contains("Live Captions", StringComparison.OrdinalIgnoreCase);
    }

    private static int ApplyToHwndTree(IntPtr hwnd, bool exclude)
    {
        var applied = 0;
        if (TryApplyOne(hwnd, exclude))
            applied++;

        EnumChildWindows(
            hwnd,
            (child, _) =>
            {
                if (TryApplyOne(child, exclude))
                    applied++;
                return true;
            },
            IntPtr.Zero);

        return applied;
    }

    private static bool TryApplyOne(IntPtr hwnd, bool exclude)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;

        if (!WindowCaptureStealth.SetHwndExcludeFromCapture(hwnd, exclude))
            return false;

        if (exclude)
            sTrackedHwnds.Add(hwnd);
        else
            sTrackedHwnds.Remove(hwnd);

        return true;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessIdNative(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

}
