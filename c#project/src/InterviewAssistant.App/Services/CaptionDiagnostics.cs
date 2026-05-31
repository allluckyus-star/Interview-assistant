using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace InterviewAssistant.App.Services;

/// <summary>Opt-in caption boundary logging. Set env <c>IA_CAPTION_LOG=1</c>.</summary>
internal static class CaptionDiagnostics
{
    private const int AttachParentProcess = -1;

    private static readonly object Gate = new();
    private static bool sInstalled;
    private static bool? sEnabled;
    private static bool sConsoleAttached;

    public static string LogPath { get; } =
        Path.Combine(StartupDiagnostics.LogDirectory, "caption.log");

    public static bool IsEnabled =>
        sEnabled ??= string.Equals(
            Environment.GetEnvironmentVariable("IA_CAPTION_LOG"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Call once at startup so <c>dotnet run</c> can show lines in the terminal.</summary>
    public static void Install()
    {
        if (sInstalled)
            return;
        sInstalled = true;

        if (!IsEnabled)
            return;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                sConsoleAttached = AttachConsole(AttachParentProcess) || AllocConsole();
            }
            catch
            {
                sConsoleAttached = false;
            }
        }

        var banner =
            "[Caption] logging ON — file: " + LogPath +
            (sConsoleAttached ? " (console attached)" : " (console not attached; read the file)");
        WriteRaw(banner);
    }

    public static void Log(
        string reason,
        int nextChunkStartIndex,
        int fullLen,
        string anchorFull,
        int fixedLen,
        int liveWindowLen,
        string draftTail)
    {
        if (!IsEnabled)
            return;

        var anchorLen = anchorFull.Length;
        var draftLen = draftTail.Length;
        var anchorPart = anchorLen > 0
            ? $" anchorStart=\"{PreviewHead(anchorFull, 80)}\""
            : "";
        var line =
            $"[Caption][{reason}] index={nextChunkStartIndex} " +
            $"fullLen={fullLen} anchorLen={anchorLen} fixedLen={fixedLen} " +
            $"liveLen={liveWindowLen} draftLen={draftLen} " +
            $"draftTailStart=\"{PreviewHead(draftTail, 160)}\"{anchorPart}";

        WriteRaw(line);
    }

    public static void LogSkipCap(
        string path,
        int skippedLen,
        int liveLen,
        int tailLen,
        int boundaryInFull,
        int proposedIndex,
        int cappedIndex,
        string capReason)
    {
        if (!IsEnabled)
            return;

        var line =
            $"[Caption][skip-cap] path={path} skippedLen={skippedLen} liveLen={liveLen} " +
            $"tailLen={tailLen} boundary={boundaryInFull} proposedIndex={proposedIndex} " +
            $"cappedIndex={cappedIndex} reason={capReason}";

        WriteRaw(line);
    }

    private static void WriteRaw(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(StartupDiagnostics.LogDirectory);
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // ignore
        }

        if (sConsoleAttached)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
                // ignore
            }
        }

        Trace.WriteLine($"[InterviewAssistant] {line}");
    }

    /// <summary>First <paramref name="maxChars"/> of text (pending draft start), not the tail end.</summary>
    private static string PreviewHead(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length <= maxChars)
            return text;
        return text[..maxChars] + "…";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}
