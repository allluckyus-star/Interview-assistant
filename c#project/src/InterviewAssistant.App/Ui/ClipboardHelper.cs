using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace InterviewAssistant.App.Ui;

/// <summary>Clipboard set with retries off the UI thread (avoids freezing / re-entrancy).</summary>
internal static class ClipboardHelper
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int HresultClipbrdCantOpen = unchecked((int)0x800401D0);
    private static readonly object CopyGate = new();
    private static bool _copyInProgress;

    public static Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var payload = text ?? "";
        if (string.IsNullOrEmpty(payload))
            return Task.FromResult(false);

        lock (CopyGate)
        {
            if (_copyInProgress)
                return Task.FromResult(false);
            _copyInProgress = true;
        }

        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OperatingSystem.IsWindows())
                    return TrySetTextWindows(payload, cancellationToken);

                return TrySetTextWpfOnce(payload);
            }
            finally
            {
                lock (CopyGate)
                    _copyInProgress = false;
            }
        }, cancellationToken);
    }

    private static bool TrySetTextWindows(string text, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Win32SetClipboardUnicode(text);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 7)
                    Thread.Sleep(50);
            }
        }

        Trace.WriteLine($"[InterviewAssistant] clipboard failed: {last?.Message}");
        return false;
    }

    private static bool TrySetTextWpfOnce(string text)
    {
        var done = false;
        var ok = false;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return false;

        dispatcher.Invoke(() =>
        {
            try
            {
                Clipboard.SetText(text);
                ok = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[InterviewAssistant] clipboard failed: {ex.Message}");
            }
            finally
            {
                done = true;
            }
        });

        return done && ok;
    }

    private static void Win32SetClipboardUnicode(string text)
    {
        var opened = false;
        for (var i = 0; i < 8; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                opened = true;
                break;
            }

            Thread.Sleep(50);
        }

        if (!opened)
            throw new COMException("OpenClipboard failed", HresultClipbrdCantOpen);

        try
        {
            if (!EmptyClipboard())
                throw new ExternalException("EmptyClipboard failed");

            var raw = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            var bytes = System.Text.Encoding.Unicode.GetBytes(raw + "\0");
            var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (hGlobal == IntPtr.Zero)
                throw new ExternalException("GlobalAlloc failed");

            var target = GlobalLock(hGlobal);
            if (target == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                throw new ExternalException("GlobalLock failed");
            }

            try
            {
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                throw new ExternalException("SetClipboardData failed");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
