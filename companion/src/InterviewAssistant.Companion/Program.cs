using System.Diagnostics;
using System.Windows.Threading;

namespace InterviewAssistant.Companion;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        StartWpfDispatcherThread();
        using var session = new CompanionSessionService();
        using var api = new CompanionApiServer(session);
        try
        {
            api.Start();
            session.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start companion API:\n{ex.Message}\n\nPort 1212 may be in use.",
                "Interview Assistant Companion",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.Run(new TrayApplicationContext(session, api));
    }

    private static void StartWpfDispatcherThread()
    {
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            WpfDispatcherHolder.Dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "CompanionWpfDispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }
}

internal static class WpfDispatcherHolder
{
    public static Dispatcher? Dispatcher { get; set; }
}
