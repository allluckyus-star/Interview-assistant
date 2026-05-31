using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.Ui;
using InterviewAssistant.Bridge;

namespace InterviewAssistant.App;

public partial class App : Application
{
    static App() => StartupDiagnostics.Install();

    public App() => StartupDiagnostics.Log("App() constructor — loading XAML resources");

    private BridgeHttpServer? _bridge;

    public static PromptStore PromptStore { get; private set; } = null!;

    public static (string Host, int Port) BridgeEndpoint { get; private set; } = ("127.0.0.1", 1212);

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            CaptionDiagnostics.Install();
            StartupDiagnostics.Log("Application_Startup: begin");

            if (!OperatingSystem.IsWindows())
            {
                StartupDiagnostics.ShowFatalDialog(
                    "Interview Assistant requires Windows.",
                    new PlatformNotSupportedException("Windows desktop only."));
                Shutdown(1);
                return;
            }

            if (!Environment.Is64BitOperatingSystem)
            {
                StartupDiagnostics.ShowFatalDialog(
                    "Interview Assistant requires 64-bit Windows.",
                    new PlatformNotSupportedException("Use the win-x64 build on 64-bit Windows."));
                Shutdown(1);
                return;
            }

            StartupDiagnostics.Log("Application_Startup: closing other instances");
            SingleInstanceEnforcer.ClosePreviousInstances();
            StartupDiagnostics.Log("Application_Startup: after single instance");

            DispatcherUnhandledException += (_, args) =>
            {
                StartupDiagnostics.Log($"UI unhandled: {args.Exception}");
                try
                {
                    ToastService.Show(ToastMessages.ForException(args.Exception), ToastLevel.Error);
                }
                catch
                {
                    // ignore toast failure
                }

                args.Handled = true;
            };
            StartupDiagnostics.Log("Application_Startup: dispatcher hook registered");

            var (host, port, startBridge) = ReadBridgeConfig();
            BridgeEndpoint = (host, port);
            StartupDiagnostics.Log(
                $"Application_Startup: bridge config {host}:{port}, startAtLaunch={startBridge}");

            var store = new PromptStore();
            PromptStore = store;
            StartupDiagnostics.Log("Application_Startup: prompt store ready");

            _bridge = new BridgeHttpServer(store, host, port);
            var bridgeAtLaunch = startBridge
                && !string.Equals(
                    Environment.GetEnvironmentVariable("IA_DISABLE_BRIDGE"),
                    "1",
                    StringComparison.OrdinalIgnoreCase);
            if (string.Equals(
                    Environment.GetEnvironmentVariable("IA_ENABLE_BRIDGE"),
                    "1",
                    StringComparison.OrdinalIgnoreCase))
                bridgeAtLaunch = true;
            StartupDiagnostics.Log("Application_Startup: creating MainWindow");
            var main = new MainWindow(store, host, port, startupToastMessage: null, ToastLevel.Warning);
            StartupDiagnostics.Log("Application_Startup: showing MainWindow");
            main.Show();
            StartupDiagnostics.Log("Main window shown.");

            if (bridgeAtLaunch)
                StartBridgeInBackground(main, port);
            else
                StartupDiagnostics.Log("Application_Startup: bridge skipped at launch (set Bridge:StartAtLaunch true or IA_ENABLE_BRIDGE=1)");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.ShowFatalDialog("Interview Assistant could not start.", ex);
            Shutdown(1);
        }
    }

    private void StartBridgeInBackground(MainWindow main, int port)
    {
        if (_bridge is null)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                StartupDiagnostics.Log("Bridge: Start() on background thread");
                var startTask = Task.Run(() => _bridge.Start());
                if (!startTask.Wait(TimeSpan.FromSeconds(8)))
                {
                    StartupDiagnostics.Log("Bridge: Start() timed out after 8s — continuing without bridge");
                    return;
                }

                if (startTask.IsFaulted)
                    throw startTask.Exception!.GetBaseException();

                StartupDiagnostics.Log("Application_Startup: bridge listening");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"Bridge start failed: {ex}");
                var msg = $"Bridge blocked. Port {port} in use.";
                _ = main.Dispatcher.BeginInvoke(() =>
                    ToastService.Show(msg, ToastLevel.Warning));
            }
            finally
            {
                StartupDiagnostics.Log("Bridge: background start finished");
            }
        });
    }

    private static (string Host, int Port, bool StartAtLaunch) ReadBridgeConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var b = doc.RootElement.GetProperty("Bridge");
            var host = b.GetProperty("Host").GetString() ?? "127.0.0.1";
            var port = b.TryGetProperty("Port", out var p) ? p.GetInt32() : 1212;
            var start = !b.TryGetProperty("StartAtLaunch", out var s) || s.GetBoolean();
            return (host, port, start);
        }
        catch
        {
            return ("127.0.0.1", 1212, false);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        StartupDiagnostics.Log($"Application_Exit: code={e.ApplicationExitCode}");
        _bridge?.Dispose();
        _bridge = null;
    }
}
