using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace InterviewAssistant.App.Services;

/// <summary>Logs startup failures to disk and shows a message box (for machines without a debugger).</summary>
public static class StartupDiagnostics
{
    public static string LogDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "InterviewAssistant");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "startup.log");

    /// <summary>Same log next to the .exe — easier to find on another PC.</summary>
    public static string? ExeSideLogPath { get; private set; }

    private static bool sInstalled;

    public static void Install()
    {
        if (sInstalled)
            return;
        sInstalled = true;

        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            // ignore
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            WriteFatal("Unhandled (AppDomain)", ex);
            ShowFatalDialog("Interview Assistant crashed on startup.", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log($"Unobserved task: {args.Exception}");
            args.SetObserved();
        };

        LogEnvironment();

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Log("ProcessExit (application exiting)");
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
        TryAppend(LogPath, line);
        if (ExeSideLogPath is not null)
            TryAppend(ExeSideLogPath, line);

        Trace.WriteLine($"[InterviewAssistant] {message}");
    }

    private static readonly object LogLock = new();

    private static void TryAppend(string path, string line)
    {
        try
        {
            lock (LogLock)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static void ShowFatalDialog(string title, Exception? ex)
    {
        try
        {
            var body = new StringBuilder();
            body.AppendLine(ex?.Message ?? "Unknown error.");
            if (ex?.InnerException is not null)
                body.AppendLine().AppendLine(ex.InnerException.Message);
            body.AppendLine().AppendLine($"Log (temp): {LogPath}");
            if (ExeSideLogPath is not null)
                body.AppendLine($"Log (next to exe): {ExeSideLogPath}");
            body.AppendLine().AppendLine(
                "Common fixes: use the exe from publish\\win-x64 (self-contained), "
                + "Windows 10 version 2004 or newer, 64-bit Windows for win-x64 build, "
                + "install WebView2 Runtime, unblock the exe in Windows Security.");
            MessageBox.Show(body.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // ignore UI failure
        }
    }

    private static void WriteFatal(string context, Exception? ex)
    {
        Log($"FATAL [{context}] {ex}");
    }

    private static void InitExeSideLog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir))
                return;
            ExeSideLogPath = Path.Combine(dir, "InterviewAssistant-startup.log");
        }
        catch
        {
            // ignore
        }
    }

    private static void LogEnvironment()
    {
        try
        {
            InitExeSideLog();
            var exe = Environment.ProcessPath ?? "(unknown)";
            Log($"Starting {exe}");
            if (ExeSideLogPath is not null)
                Log($"Exe-side log: {ExeSideLogPath}");
            Log($"Temp log: {LogPath}");
            Log($"OS: {RuntimeInformation.OSDescription}");
            Log($"Arch: {RuntimeInformation.OSArchitecture}, 64-bit OS: {Environment.Is64BitOperatingSystem}");
            Log($".NET: {RuntimeInformation.FrameworkDescription}");
            Log($"BaseDirectory: {AppContext.BaseDirectory}");
            Log($"Assembly: {Assembly.GetExecutingAssembly().GetName().FullName}");
        }
        catch (Exception ex)
        {
            Log($"LogEnvironment failed: {ex.Message}");
        }
    }
}
