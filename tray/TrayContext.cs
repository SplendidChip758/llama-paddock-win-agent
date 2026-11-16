namespace LlamaPaddock.Tray;

/// <summary>The tray icon and its menu: the version, the helper's status, and About with updates.</summary>
sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem status = new() { Enabled = false };
    readonly Icon logo = LogoIcon(32);
    AboutForm? about;

    public TrayContext(HelperServer server)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"llama-paddock agent {AgentUpdates.Version}") { Enabled = false });
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("About and updates…", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        icon = new NotifyIcon
        {
            Icon = LogoIcon(SystemInformation.SmallIconSize.Width),
            ContextMenuStrip = menu,
            Visible = true,
        };
        void Show()
        {
            status.Text = $"Helper: {server.Status}";
            // the tooltip is limited to 63 characters
            icon.Text = $"llama-paddock agent: {server.Status}"[..Math.Min(63, 21 + server.Status.Length)];
        }
        Show();
        // the helper reports from its own thread; a control made here hands it to this one
        var invoker = new Control();
        invoker.CreateControl();
        server.StatusChanged += () => invoker.BeginInvoke(Show);
        _ = ReportUpdate();
    }

    void ShowAbout()
    {
        if (about is { IsDisposed: false })
        {
            about.Activate();
            return;
        }
        about = new AboutForm(logo);
        about.Show();
    }

    /// <summary>After an update started from About: say how it went.</summary>
    async Task ReportUpdate()
    {
        var done = await AgentUpdates.Pending(CancellationToken.None);
        if (done is not { } d) return;
        icon.ShowBalloonTip(10_000, "llama-paddock agent", d.Text, d.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    /// <summary>The logo at a size: the notification area's is 16 px at 100 % scaling, 24 at 150 %, 32 at 200 %.</summary>
    static Icon LogoIcon(int size)
    {
        using var ico = typeof(TrayContext).Assembly.GetManifestResourceStream("paddock-agent.ico")!;
        return new Icon(ico, size, size);
    }

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        icon.Dispose();
        base.ExitThreadCore();
    }
}
