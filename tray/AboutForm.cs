using System.Diagnostics;

namespace LlamaPaddock.Tray;

/// <summary>About the agent: its version and channel, and checking for and installing updates.</summary>
sealed class AboutForm : Form
{
    const string Releases = "https://github.com/SplendidChip758/llama-paddock-win-agent/releases";
    const string Docs = "https://github.com/SplendidChip758/llama-paddock/blob/main/docs/agent-windows.md";

    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(360, 0), Text = "" };
    readonly LinkLabel notes = new() { AutoSize = true, Text = "What's new", Visible = false };
    readonly Button check = new() { Text = "Check for updates", AutoSize = true };
    readonly Button update = new() { Text = "Update", AutoSize = true, Visible = false, FlatStyle = FlatStyle.System };
    string page = Releases;

    public AboutForm(Icon logo)
    {
        Text = "About llama-paddock agent";
        Icon = logo;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        var picture = new PictureBox { Image = new Icon(logo, 48, 48).ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 16, 0) };
        layout.Controls.Add(picture, 0, 0);
        layout.SetRowSpan(picture, 7);

        var name = new Label { AutoSize = true, Text = "llama-paddock agent", Font = new Font(Font.FontFamily, Font.Size * 1.4f, FontStyle.Bold) };
        var version = new Label { AutoSize = true, Text = $"Version {AgentUpdates.Version}" };
        var channel = new Label
        {
            AutoSize = true, ForeColor = SystemColors.GrayText,
            Text = AgentUpdates.Channel == "dev" ? "Update channel: dev (prereleases)" : "Update channel: stable",
        };
        var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
        links.Controls.Add(Link("Releases", () => Open(Releases)));
        links.Controls.Add(Link("Documentation", () => Open(Docs)));
        links.Controls.Add(Link("Update log", () => Open(AgentUpdates.UpdateLog), visible: File.Exists(AgentUpdates.UpdateLog)));

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        buttons.Controls.Add(check);
        buttons.Controls.Add(update);
        // the shield: this asks for an administrator
        update.HandleCreated += (_, _) => SendMessage(update.Handle, 0x160C /* BCM_SETSHIELD */, IntPtr.Zero, 1);

        layout.Controls.Add(name, 1, 0);
        layout.Controls.Add(version, 1, 1);
        layout.Controls.Add(channel, 1, 2);
        layout.Controls.Add(links, 1, 3);
        layout.Controls.Add(status, 1, 4);
        layout.Controls.Add(notes, 1, 5);
        layout.Controls.Add(buttons, 1, 6);
        Controls.Add(layout);

        notes.LinkClicked += (_, _) => Open(page);
        check.Click += async (_, _) => await Check();
        update.Click += async (_, _) => await Install();
    }

    static LinkLabel Link(string text, Action open, bool visible = true)
    {
        var link = new LinkLabel { AutoSize = true, Text = text, Visible = visible, Margin = new Padding(0, 0, 12, 0) };
        link.LinkClicked += (_, _) => open();
        return link;
    }

    void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Say($"Can't open {target}: {e.Message}"); }
    }

    void Say(string text) => status.Text = text;

    async Task Check()
    {
        check.Enabled = false;
        update.Visible = notes.Visible = false;
        Say("Checking…");
        try
        {
            var r = await AgentUpdates.Check();
            if (r.Latest == "") Say(AgentUpdates.Channel == "stable" ? "There is no stable release yet." : "There is no release yet.");
            else if (!r.Newer) Say($"You have the newest version ({AgentUpdates.Version}).");
            else
            {
                Say($"Version {r.Latest} is available.");
                page = r.Page;
                notes.Visible = update.Visible = true;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            Say($"Couldn't check for updates: {e.Message}");
        }
        check.Enabled = true;
    }

    async Task Install()
    {
        check.Enabled = update.Enabled = false;
        Say("Downloading and checking the update…");
        try
        {
            switch (await AgentUpdates.Update())
            {
                case null: Say("The update wasn't started: it needs an administrator."); break;
                case "current": Say($"You have the newest version ({AgentUpdates.Version})."); update.Visible = false; break;
                default:
                    // the setup closes this app in a moment; the one the service starts says how it went
                    Say("Updating. The agent restarts, and this app with it: it says when the update is done.");
                    return;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Say($"The update failed: {e.Message}");
        }
        check.Enabled = update.Enabled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    static void SendMessage(IntPtr hwnd, int msg, IntPtr wParam, int lParam) => SendMessage(hwnd, msg, wParam, (IntPtr)lParam);
}
