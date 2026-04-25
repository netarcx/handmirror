using System;
using System.Drawing;
using System.Windows.Forms;
using WpfApp = System.Windows.Application;

namespace HandMirror;

public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private MirrorWindow? _window;

    public TrayApp()
    {
        if (StartupRegistration.IsRunningFromStableExe() && !StartupRegistration.IsEnabled())
            StartupRegistration.Enable();

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupRegistration.IsEnabled(),
            CheckOnClick = true,
            Enabled = StartupRegistration.IsRunningFromStableExe(),
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            if (startupItem.Checked) StartupRegistration.Enable();
            else StartupRegistration.Disable();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => Toggle());
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => WpfApp.Current.Shutdown());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Hand Mirror",
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };
    }

    private void Toggle()
    {
        var existing = _window;
        if (existing != null)
        {
            existing.Close();
            return;
        }

        var w = new MirrorWindow();
        w.Closed += (_, _) => { if (ReferenceEquals(_window, w)) _window = null; };
        _window = w;
        w.Show();
        w.Activate();
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("icon.ico")
            ?? throw new InvalidOperationException("Embedded icon.ico resource missing");
        return new Icon(stream);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _window?.Close();
    }
}
