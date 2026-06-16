using System;
using System.Drawing;
using System.Windows.Forms;
using WpfApp = System.Windows.Application;

namespace HandMirror;

public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _cameraMenu;
    private MirrorWindow? _window;
    private string _cameraMenuSignature = "";

    public TrayApp()
    {
        if (StartupRegistration.IsRunningFromStableExe() && !StartupRegistration.IsEnabled())
            StartupRegistration.Enable();
        StartupRegistration.RefreshIfEnabled();

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

        _cameraMenu = new ToolStripMenuItem("Camera");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => Toggle());
        menu.Items.Add(_cameraMenu);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => WpfApp.Current.Shutdown());

        // Refresh the camera list each time the menu opens, so newly plugged-in
        // devices (or an unplugged capture card) are reflected.
        menu.Opening += (_, _) => _ = PopulateCamerasAsync();

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

        _ = PopulateCamerasAsync();
    }

    private async Task PopulateCamerasAsync()
    {
        IReadOnlyList<WebcamCapture.CameraInfo> cameras;
        try { cameras = await WebcamCapture.ListCamerasAsync(); }
        catch { return; }

        var selected = Settings.CameraId;
        if (selected == null || cameras.All(c => c.Id != selected))
            selected = cameras.Count > 0 ? cameras[0].Id : null;

        // Skip the rebuild when nothing changed — avoids flicker and dropping a click
        // if the async refresh lands while the submenu is open.
        var signature = string.Join("|", cameras.Select(c => c.Id + "=" + c.Name)) + "#" + selected;
        if (signature == _cameraMenuSignature) return;

        void Rebuild()
        {
            _cameraMenuSignature = signature;
            _cameraMenu.DropDownItems.Clear();

            if (cameras.Count == 0)
            {
                _cameraMenu.DropDownItems.Add(new ToolStripMenuItem("(no cameras found)") { Enabled = false });
                return;
            }

            foreach (var cam in cameras)
            {
                var item = new ToolStripMenuItem(cam.Name) { Checked = cam.Id == selected };
                var id = cam.Id;
                item.Click += (_, _) => SelectCamera(id);
                _cameraMenu.DropDownItems.Add(item);
            }
        }

        // The tray menu has WPF-UI-thread affinity; marshal the rebuild there in case
        // the WinRT await resumed on a thread-pool thread.
        var dispatcher = WpfApp.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(Rebuild);
        else
            Rebuild();
    }

    private void SelectCamera(string id)
    {
        if (Settings.CameraId == id) return;
        Settings.CameraId = id;

        // Restart the preview with the newly chosen camera if it's open.
        if (_window != null)
        {
            _window.Close();
            Toggle();
        }
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
