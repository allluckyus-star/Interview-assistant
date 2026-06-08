using System.Windows.Threading;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

/// <summary>WPF dispatcher on a dedicated STA thread for clipboard access.</summary>
internal static class CompanionSnipDispatcher
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;
    private static ManualResetEventSlim? _ready;

    public static Dispatcher Get()
    {
        lock (Gate)
        {
            if (_dispatcher is not null && _thread is { IsAlive: true })
                return _dispatcher;

            _ready = new ManualResetEventSlim(false);
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "IA-Clipboard-WPF",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(15)))
                throw new InvalidOperationException("Companion clipboard dispatcher failed to start.");

            return _dispatcher ?? throw new InvalidOperationException("Companion clipboard dispatcher is null.");
        }
    }

    private static void ThreadMain()
    {
        try
        {
            _ = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
            };
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready?.Set();
            StartupDiagnostics.Log("[IA ShareX] clipboard dispatcher ready");
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA ShareX] clipboard dispatcher fatal: {ex}");
            _ready?.Set();
        }
    }
}
