using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace InterviewAssistant.App.Services;

/// <summary>Win+Shift+S snip (same flow as live.py) — poll clipboard for a new image.</summary>
public static class WindowsScreenSnipCapture
{
    private const int MaxPollCount = 600;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

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
        DispatcherTimer? timer = null;

        void Complete(byte[]? result)
        {
            if (timer is not null)
            {
                timer.Stop();
                timer.Tick -= OnTick;
            }

            tcs.TrySetResult(result);
        }

        void OnTick(object? sender, EventArgs e)
        {
            pollCount++;
            if (pollCount > MaxPollCount)
            {
                Complete(null);
                return;
            }

            var current = TryEncodeClipboardImageToPng();
            if (current is null || current.Length == 0)
                return;

            if (imageBefore is not null && imageBefore.AsSpan().SequenceEqual(current))
                return;

            Complete(current);
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
                    await dispatcher.InvokeAsync(() => Complete(null)).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await dispatcher.InvokeAsync(() => Complete(null)).Task.ConfigureAwait(false);
            }
        }, cancellationToken);

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await dispatcher.InvokeAsync(() => Complete(null)).Task.ConfigureAwait(false);
            return null;
        }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
