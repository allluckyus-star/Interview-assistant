using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using InterviewAssistant.App.Ui;
using InterviewAssistant.Bridge;

namespace InterviewAssistant.App;

public partial class App : Application
{
    private BridgeHttpServer? _bridge;

    public static PromptStore PromptStore { get; private set; } = null!;

    public static (string Host, int Port) BridgeEndpoint { get; private set; } = ("127.0.0.1", 8765);

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Trace.WriteLine($"[InterviewAssistant] UI unhandled: {args.Exception}");
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
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Trace.WriteLine($"[InterviewAssistant] AppDomain unhandled: {ex}");
        };

        var (host, port) = ReadBridgeConfig();
        BridgeEndpoint = (host, port);
        var store = new PromptStore();
        PromptStore = store;
        _bridge = new BridgeHttpServer(store, host, port);
        string? bridgeWarning = null;
        try
        {
            _bridge.Start();
        }
        catch (Exception ex)
        {
            bridgeWarning = $"Bridge blocked. Port {port} in use.";
            _ = ex;
        }

        var main = new MainWindow(store, host, port, bridgeWarning, ToastLevel.Warning);
        main.Show();
    }

    private static (string Host, int Port) ReadBridgeConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var b = doc.RootElement.GetProperty("Bridge");
            var host = b.GetProperty("Host").GetString() ?? "127.0.0.1";
            var port = b.TryGetProperty("Port", out var p) ? p.GetInt32() : 8765;
            return (host, port);
        }
        catch
        {
            return ("127.0.0.1", 8765);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _bridge?.Dispose();
        _bridge = null;
    }
}
