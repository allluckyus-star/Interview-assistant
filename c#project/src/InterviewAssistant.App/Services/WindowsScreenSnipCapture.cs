using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace InterviewAssistant.App.Services;

/// <summary>Win+Shift+S snip (same flow as live.py) — poll clipboard for a new image.</summary>
public static class WindowsScreenSnipCapture
{
    private const int MaxPollCount = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NoSnipUiFallback = TimeSpan.FromSeconds(12);

    private static readonly string[] SnipProcessNames = ["ScreenSnipping", "SnippingTool"];

    private const byte VkLWin = 0x5B;
    private const byte VkShift = 0x10;
    private const byte VkS = 0x53;
    private const uint KeyeventfKeyup = 0x0002;

    public static async Task<byte[]?> CaptureViaOsSnipAsync(
        Dispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[]? imageBefore = null;

        await dispatcher.InvokeAsync(() =>
        {
            imageBefore = TryEncodeClipboardImageToPng();
        }, DispatcherPriority.Normal).Task.ConfigureAwait(false);

        var pollCount = 0;
        var snipTriggered = false;
        var snipTriggeredAt = DateTime.MinValue;
        var snipUiWasVisible = false;
        DispatcherTimer? timer = null;
        var completed = 0;

        void Complete(byte[]? result)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
                return;

            if (timer is not null)
            {
                timer.Stop();
                timer.Tick -= OnTick;
            }

            tcs.TrySetResult(result);
        }

        void OnTick(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref completed) != 0)
                return;

            pollCount++;
            if (pollCount > MaxPollCount)
            {
                Complete(null);
                return;
            }

            var current = TryEncodeClipboardImageToPng();
            if (current is not null && current.Length > 0
                && (imageBefore is null || !imageBefore.AsSpan().SequenceEqual(current)))
            {
                Complete(current);
                return;
            }

            var snipUiVisible = IsWindowsSnipUiVisible();
            if (snipUiVisible)
                snipUiWasVisible = true;

            if (!snipTriggered)
                return;

            var sinceTrigger = DateTime.UtcNow - snipTriggeredAt;
            if (snipUiWasVisible && !snipUiVisible)
            {
                Complete(null);
                return;
            }

            if (!snipUiWasVisible && sinceTrigger > NoSnipUiFallback)
                Complete(null);
        }

        await dispatcher.InvokeAsync(() =>
        {
            timer = new DispatcherTimer { Interval = PollInterval };
            timer.Tick += OnTick;
            timer.Start();
        }, DispatcherPriority.Normal).Task.ConfigureAwait(false);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                if (!TriggerWindowsSnip())
                {
                    await dispatcher.InvokeAsync(() => Complete(null)).Task.ConfigureAwait(false);
                    return;
                }

                snipTriggered = true;
                snipTriggeredAt = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                await dispatcher.InvokeAsync(() => Complete(null)).Task.ConfigureAwait(false);
            }
        }, cancellationToken);

        try
        {
            return await tcs.Task.WaitAsync(OverallTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await dispatcher.InvokeAsync(() => Complete(null)).Task.ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            await dispatcher.InvokeAsync(() => Complete(null)).Task.ConfigureAwait(false);
            return null;
        }
    }

    private static bool IsWindowsSnipUiVisible()
    {
        foreach (var name in SnipProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var proc in processes)
            {
                using (proc)
                {
                    if (proc.HasExited)
                        continue;
                    if (ProcessHasVisibleWindow(proc.Id))
                        return true;
                }
            }
        }

        return IsSnipTitleWindowVisible();
    }

    private static bool IsSnipTitleWindowVisible()
    {
        var found = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var len = GetWindowTextLength(hwnd);
            if (len <= 0)
                return true;

            var sb = new StringBuilder(len + 1);
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Contains("snip", StringComparison.OrdinalIgnoreCase)
                || title.Contains("screen clip", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool ProcessHasVisibleWindow(int processId)
    {
        var found = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if ((int)pid != processId)
                return true;

            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool TriggerWindowsSnip()
    {
        try
        {
            keybd_event(VkLWin, 0, 0, UIntPtr.Zero);
            keybd_event(VkShift, 0, 0, UIntPtr.Zero);
            keybd_event(VkS, 0, 0, UIntPtr.Zero);
            keybd_event(VkS, 0, KeyeventfKeyup, UIntPtr.Zero);
            keybd_event(VkShift, 0, KeyeventfKeyup, UIntPtr.Zero);
            keybd_event(VkLWin, 0, KeyeventfKeyup, UIntPtr.Zero);
            return true;
        }
        catch
        {
            // fall through
        }

        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? TryEncodeClipboardImageToPng(int maxSide = 2048)
    {
        try
        {
            if (!Clipboard.ContainsImage())
                return null;

            var src = Clipboard.GetImage();
            if (src is null)
                return null;

            var w = src.PixelWidth;
            var h = src.PixelHeight;
            if (w < 1 || h < 1)
                return null;

            BitmapSource scaled = src;
            var longest = Math.Max(w, h);
            if (longest > maxSide)
            {
                var scale = maxSide / (double)longest;
                scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                scaled.Freeze();
            }

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(scaled));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
