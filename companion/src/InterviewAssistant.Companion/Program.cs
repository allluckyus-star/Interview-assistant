using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

internal static class Program
{
    private const string SingleInstanceMutexName = "InterviewAssistant.Companion.SingleInstance";

    [STAThread]
    private static void Main()
    {
        StartupDiagnostics.Install();
        StartupDiagnostics.Log("Companion: Main entry");

        using var instanceMutex = new Mutex(false, SingleInstanceMutexName, out _);
        if (!instanceMutex.WaitOne(0, false))
        {
            StartupDiagnostics.Log("Companion: another instance is already running");
            MessageBox.Show(
                "Interview Assistant Companion is already running.\nCheck the notification area (system tray).",
                "Interview Assistant Companion",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            StartupDiagnostics.Log("Companion: WinForms initialized");

            using var session = new CompanionSessionService();
            StartupDiagnostics.Log("Companion: session created");

            var clipboard = new CompanionClipboardService();
            using var api = new CompanionApiServer(session, clipboard);
            StartupDiagnostics.Log("Companion: API object created");

            try
            {
                CompanionSnipDispatcher.Get();
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"Companion: clipboard dispatcher start failed: {ex}");
            }

            try
            {
                api.Start();
                StartupDiagnostics.Log("Companion: API listening on http://127.0.0.1:1212/");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"Companion: API start failed: {ex}");
                StartupDiagnostics.ShowFatalDialog(
                    "Companion could not start the API (port 1212 may be in use or blocked).",
                    ex);
                return;
            }

            try
            {
                session.Start();
                StartupDiagnostics.Log("Companion: Live Captions session started");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"Companion: Live Captions start failed (API still running): {ex}");
                MessageBox.Show(
                    $"Live Captions could not start:\n{ex.Message}\n\n"
                    + "The tray app and API are still running. Enable Live Captions in "
                    + "Settings → Accessibility, then use tray → Restart captions.",
                    "Interview Assistant Companion",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            StartupDiagnostics.Log("Companion: entering tray message loop");
            Application.Run(new TrayApplicationContext(session, api));
            StartupDiagnostics.Log("Companion: tray loop ended — shutting down");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"Companion: fatal startup: {ex}");
            StartupDiagnostics.ShowFatalDialog("Interview Assistant Companion could not start.", ex);
        }
    }
}
