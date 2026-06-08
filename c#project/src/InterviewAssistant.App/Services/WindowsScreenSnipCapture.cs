using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
#if COMPANION
using System.Drawing;
using System.Drawing.Imaging;
using WinFormsClipboard = System.Windows.Forms.Clipboard;
#endif

namespace InterviewAssistant.App.Services;

/// <summary>Win+Shift+S snip (same flow as live.py) — poll clipboard for a new image.</summary>
public static class WindowsScreenSnipCapture
{
    private const int MaxPollCount = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShareXWaitTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan NoSnipUiFallback = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SnipTriggerDelay = TimeSpan.FromMilliseconds(120);

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
                await Task.Delay(SnipTriggerDelay, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Wait for ShareX (or any tool) to put a new image on the clipboard — does not trigger Win+Shift+S.</summary>
    public static async Task<byte[]?> WaitForShareXImageAsync(
        Dispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        return await WaitForClipboardImageChangeAsync(dispatcher, cancellationToken, ShareXWaitTimeout)
            .ConfigureAwait(false);
    }

    /// <summary>Wait for ShareX OCR (Alt+.) or similar to put new text on the clipboard.</summary>
    public static async Task<string?> WaitForShareXTextAsync(
        Dispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        return await WaitForClipboardTextChangeAsync(dispatcher, cancellationToken, ShareXWaitTimeout)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]?> WaitForClipboardImageChangeAsync(
        Dispatcher dispatcher,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        byte[]? imageBefore = null;
        var seqBefore = 0u;

        await dispatcher.InvokeAsync(() =>
        {
            imageBefore = TryEncodeClipboardImageToPng();
            seqBefore = GetClipboardSequenceNumber();
        }, DispatcherPriority.Normal).Task.ConfigureAwait(false);

        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollCount = 0;
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
            if (pollCount > MaxPollCount * 4)
            {
                Complete(null);
                return;
            }

            var current = TryEncodeClipboardImageToPng();
            var seqNow = GetClipboardSequenceNumber();
            if (current is { Length: > 0 }
                && (seqNow != seqBefore
                    || imageBefore is null
                    || !imageBefore.AsSpan().SequenceEqual(current)))
            {
                Complete(current);
            }
        }

        await dispatcher.InvokeAsync(() =>
        {
            timer = new DispatcherTimer { Interval = PollInterval };
            timer.Tick += OnTick;
            timer.Start();
        }, DispatcherPriority.Normal).Task.ConfigureAwait(false);

        using var cancelReg = cancellationToken.Register(() => Complete(null));

        try
        {
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Complete(null);
            return null;
        }
        catch (OperationCanceledException)
        {
            Complete(null);
            return null;
        }
    }

    private static async Task<string?> WaitForClipboardTextChangeAsync(
        Dispatcher dispatcher,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        string? textBefore = null;
        var seqBefore = 0u;

        await dispatcher.InvokeAsync(() =>
        {
            textBefore = TryGetClipboardText();
            seqBefore = GetClipboardSequenceNumber();
        }, DispatcherPriority.Normal).Task.ConfigureAwait(false);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollCount = 0;
        DispatcherTimer? timer = null;
        var completed = 0;

        void Complete(string? result)
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
            if (pollCount > MaxPollCount * 4)
            {
                Complete(null);
                return;
            }

            var current = TryGetClipboardText();
            var seqNow = GetClipboardSequenceNumber();
            if (!string.IsNullOrWhiteSpace(current)
                && (seqNow != seqBefore
                    || !string.Equals(current, textBefore, StringComparison.Ordinal)))
            {
                Complete(current);
            }
        }

        await dispatcher.InvokeAsync(() =>
        {
            timer = new DispatcherTimer { Interval = PollInterval };
            timer.Tick += OnTick;
            timer.Start();
        }, DispatcherPriority.Normal).Task.ConfigureAwait(false);

        using var cancelReg = cancellationToken.Register(() => Complete(null));

        try
        {
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Complete(null);
            return null;
        }
        catch (OperationCanceledException)
        {
            Complete(null);
            return null;
        }
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                return (System.Windows.Clipboard.GetText() ?? "").Trim();
        }
        catch
        {
            // fall through
        }

#if COMPANION
        try
        {
            if (WinFormsClipboard.ContainsText())
                return (WinFormsClipboard.GetText() ?? "").Trim();
        }
        catch
        {
            // fall through
        }
#endif

        return null;
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
        var png = TryEncodeWpfClipboardImagePng(maxSide);
        if (png is not null)
            return png;

#if COMPANION
        return TryEncodeWinFormsClipboardImagePng(maxSide);
#else
        return null;
#endif
    }

    private static byte[]? TryEncodeWpfClipboardImagePng(int maxSide = 2048)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage())
                return null;

            var src = System.Windows.Clipboard.GetImage();
            if (src is null)
                return null;

            return EncodeBitmapSourceToPng(src, maxSide);
        }
        catch
        {
            return null;
        }
    }

#if COMPANION
    private static byte[]? TryEncodeWinFormsClipboardImagePng(int maxSide = 2048)
    {
        try
        {
            var data = WinFormsClipboard.GetDataObject();
            if (data is not null)
            {
                var fromPng = TryReadPngBytesFromDataObject(data, maxSide);
                if (fromPng is not null)
                    return fromPng;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            if (WinFormsClipboard.ContainsFileDropList())
            {
                foreach (var path in WinFormsClipboard.GetFileDropList())
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        continue;
                    if (!IsImageFilePath(path))
                        continue;

                    using var img = Image.FromFile(path);
                    using var scaled = ScaleDrawingImage(img, maxSide);
                    using var ms = new MemoryStream();
                    scaled.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            if (!WinFormsClipboard.ContainsImage())
                return null;

            using var img = WinFormsClipboard.GetImage();
            if (img is null)
                return null;

            using var scaled = ScaleDrawingImage(img, maxSide);
            using var ms = new MemoryStream();
            scaled.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsImageFilePath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? TryReadPngBytesFromDataObject(System.Windows.Forms.IDataObject data, int maxSide)
    {
        try
        {
            if (data.GetDataPresent("PNG"))
            {
                if (data.GetData("PNG") is MemoryStream ms)
                    return NormalizePngBytes(ms.ToArray(), maxSide);
                if (data.GetData("PNG") is byte[] raw)
                    return NormalizePngBytes(raw, maxSide);
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    private static byte[]? NormalizePngBytes(byte[] pngBytes, int maxSide)
    {
        if (pngBytes is not { Length: > 0 })
            return null;

        try
        {
            using var ms = new MemoryStream(pngBytes);
            using var img = Image.FromStream(ms);
            using var scaled = ScaleDrawingImage(img, maxSide);
            using var outMs = new MemoryStream();
            scaled.Save(outMs, ImageFormat.Png);
            return outMs.ToArray();
        }
        catch
        {
            return pngBytes;
        }
    }

    private static Image ScaleDrawingImage(Image src, int maxSide)
    {
        var w = src.Width;
        var h = src.Height;
        if (w < 1 || h < 1)
            return (Image)src.Clone();

        var longest = Math.Max(w, h);
        if (longest <= maxSide)
            return (Image)src.Clone();

        var scale = maxSide / (double)longest;
        var nw = Math.Max(1, (int)Math.Round(w * scale));
        var nh = Math.Max(1, (int)Math.Round(h * scale));
        var bmp = new Bitmap(nw, nh);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, nw, nh);
        }

        return bmp;
    }
#endif

    private static byte[]? EncodeBitmapSourceToPng(BitmapSource src, int maxSide)
    {
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

    public static string? ReadClipboardText() => TryGetClipboardText();

    public static uint ReadClipboardSequenceNumber() => GetClipboardSequenceNumber();

    public static byte[]? ReadClipboardImagePng(int maxSide = 2048) =>
        TryEncodeClipboardImageToPng(maxSide);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
