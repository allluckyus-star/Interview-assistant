using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterviewAssistant.App.Services;

/// <summary>
/// One running app per exe path. A second launch within ~30s activates the existing window and exits
/// (avoids "open then auto close" from killing the running instance). Older instances are closed for restart.
/// Set <c>IA_REPLACE_INSTANCE=1</c> to always kill any older instance immediately.
/// </summary>
public static class SingleInstanceEnforcer
{
    private const string ProcessName = "InterviewAssistant.App";
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleInstanceAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplaceKillMinAge = TimeSpan.FromSeconds(1);

    public static void ClosePreviousInstances()
    {
        var current = Process.GetCurrentProcess();
        StartupDiagnostics.Log($"SingleInstance: current PID={current.Id}");

        List<Process> peers;
        try
        {
            peers = FindPeerProcesses(current).ToList();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"SingleInstance: enumerate failed: {ex.Message}");
            return;
        }

        if (peers.Count == 0)
        {
            StartupDiagnostics.Log("SingleInstance: no other instance");
            StartupDiagnostics.Log("SingleInstance: done");
            return;
        }

        var forceReplace = string.Equals(
            Environment.GetEnvironmentVariable("IA_REPLACE_INSTANCE"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        StartupDiagnostics.Log($"SingleInstance: found {peers.Count} peer(s), forceReplace={forceReplace}");

        foreach (var peer in peers)
        {
            try
            {
                var age = GetInstanceAge(peer, current);
                if (forceReplace && age >= ReplaceKillMinAge)
                {
                    StartupDiagnostics.Log($"SingleInstance: replace kill PID={peer.Id} (age {age.TotalSeconds:F0}s)");
                    TryShutdown(peer);
                    continue;
                }

                if (age >= StaleInstanceAge)
                {
                    StartupDiagnostics.Log($"SingleInstance: stale kill PID={peer.Id} (age {age.TotalSeconds:F0}s)");
                    TryShutdown(peer);
                    continue;
                }

                StartupDiagnostics.Log(
                    $"SingleInstance: already running PID={peer.Id} — activating it, exiting this launch");
                TryActivate(peer);
                StartupDiagnostics.Log("SingleInstance: exiting duplicate launch (exit 0)");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"SingleInstance: peer PID={peer.Id} error: {ex.Message}");
            }
        }

        Thread.Sleep(300);
        StartupDiagnostics.Log("SingleInstance: done");
    }

    private static TimeSpan GetInstanceAge(Process peer, Process current)
    {
        try
        {
            return current.StartTime - peer.StartTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static IEnumerable<Process> FindPeerProcesses(Process current)
    {
        var currentPath = TryGetProcessPath(current);
        if (string.IsNullOrEmpty(currentPath))
        {
            StartupDiagnostics.Log("SingleInstance: cannot read current exe path — skip peer scan");
            yield break;
        }

        foreach (var proc in Process.GetProcessesByName(ProcessName))
        {
            if (proc.Id == current.Id)
                continue;

            var peerPath = TryGetProcessPath(proc);
            if (peerPath is not null
                && string.Equals(currentPath, peerPath, StringComparison.OrdinalIgnoreCase))
                yield return proc;
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static void TryActivate(Process process)
    {
        try
        {
            process.Refresh();
            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return;
            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"SingleInstance: activate PID={process.Id} failed: {ex.Message}");
        }
    }

    private static void TryShutdown(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            if (process.CloseMainWindow())
            {
                if (process.WaitForExit((int)ShutdownWait.TotalMilliseconds))
                    return;
            }

            process.Kill();
            process.WaitForExit((int)ShutdownWait.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"SingleInstance: kill PID={process.Id} failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
