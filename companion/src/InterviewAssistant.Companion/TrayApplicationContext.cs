namespace InterviewAssistant.Companion;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly CompanionSessionService _session;
    private readonly CompanionApiServer _api;

    public TrayApplicationContext(CompanionSessionService session, CompanionApiServer api)
    {
        _session = session;
        _api = api;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Restart captions", null, (_, _) =>
        {
            _session.Stop();
            _session.Start();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Interview Assistant Companion",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) =>
        {
            _session.Stop();
            _session.Start();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _api.Dispose();
            _session.Dispose();
        }

        base.Dispose(disposing);
    }
}
