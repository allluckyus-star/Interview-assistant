namespace InterviewAssistant.Companion;
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly CompanionSessionService _session;
    private readonly CompanionApiServer _api;
    private readonly ToolStripMenuItem _startupItem;

    public TrayApplicationContext(CompanionSessionService session, CompanionApiServer api)
    {
        _session = session;
        _api = api;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Restart captions", null, (_, _) => _session.RestartCaptions());
        menu.Items.Add(new ToolStripSeparator());

        _startupItem = new ToolStripMenuItem("Run at Windows startup")
        {
            CheckOnClick = true,
            Checked = CompanionStartupRegistration.ShouldRunAtLogin()
                    && CompanionStartupRegistration.IsEnabledInRegistry(),
        };
        _startupItem.Click += (_, _) => OnStartupToggle();
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Interview Assistant Companion",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => _session.RestartCaptions();
    }

    private void OnStartupToggle()
    {
        var wantOn = _startupItem.Checked;
        var ok = CompanionStartupRegistration.SetRunAtLogin(wantOn);
        if (!ok)
        {
            _startupItem.Checked = !wantOn;
            MessageBox.Show(
                "Could not update Windows startup setting.\n"
                + "Try running Companion once as your normal user, or check startup.log.",
                "Interview Assistant Companion",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _startupItem.Checked = wantOn && CompanionStartupRegistration.IsEnabledInRegistry();
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
