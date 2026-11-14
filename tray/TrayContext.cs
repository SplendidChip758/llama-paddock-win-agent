namespace LlamaPaddock.Tray;

/// <summary>The tray icon and its menu. The base for more (status, links to the dashboard, updates).</summary>
sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem status = new() { Enabled = false };

    public TrayContext(HelperServer server)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("llama-paddock agent") { Enabled = false });
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        icon = new NotifyIcon
        {
            // a placeholder until the logo is made into an .ico
            Icon = SystemIcons.Application,
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
    }

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        icon.Dispose();
        base.ExitThreadCore();
    }
}
