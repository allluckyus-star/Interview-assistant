using System.Diagnostics;
using System.IO;

namespace InterviewAssistant.App.Services;

public static class LiveCaptionsRestarter
{
    private const string WindowName = "Live Captions";

    public static string WindowTitle => WindowName;

    public static void Restart()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var exePath = Environment.GetEnvironmentVariable("LIVE_CAPTION_EXE");
        if (string.IsNullOrWhiteSpace(exePath))
            exePath = Path.Combine(windir, "System32", "LiveCaptions.exe");
        exePath = Path.GetFullPath(exePath);
        if (!File.Exists(exePath))
        {
            Trace.WriteLine($"[InterviewAssistant] LiveCaptions.exe not found at {exePath}");
            return;
        }

        var exeName = Path.GetFileName(exePath);
        try
        {
            using var kill = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /IM {exeName}",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            kill?.WaitForExit(15_000);
        }
        catch
        {
            // ignore
        }

        Thread.Sleep(400);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? windir,
                UseShellExecute = true,
            });
            Trace.WriteLine($"[InterviewAssistant] Started {exePath}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InterviewAssistant] Failed to start LiveCaptions: {ex.Message}");
        }

        Thread.Sleep(1000);
    }
}
