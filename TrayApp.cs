using System;
using System.Drawing;
using System.Windows.Forms;
using WpfApp = System.Windows.Application;

namespace HandMirror;

public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _cameraMenu;
    private readonly ToolStripMenuItem _updateItem;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer;
    private MirrorWindow? _window;
    private string _cameraMenuSignature = "";
    private UpdateService.UpdateInfo? _pendingUpdate;
    private Version? _notifiedVersion;
    private bool _installing;

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
        _updateItem = new ToolStripMenuItem("Check for updates…", null, (_, _) => OnUpdateItemClicked());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => Toggle());
        menu.Items.Add("Refresh (Ctrl+Shift+R)", null, (_, _) => RefreshCamera());
        menu.Items.Add(_cameraMenu);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updateItem);
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
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_pendingUpdate != null) _ = InstallUpdateAsync(_pendingUpdate);
        };

        _ = PopulateCamerasAsync();

        // Check for updates on launch, then on a recurring timer. DispatcherTimer
        // ticks on the WPF UI thread, so the async continuations are UI-thread safe.
        _updateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30),
        };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(announce: true);
        _updateTimer.Start();
        _ = CheckForUpdatesAsync(announce: true);
    }

    private async Task CheckForUpdatesAsync(bool announce)
    {
        if (_installing) return;

        try
        {
            var info = await UpdateService.CheckAsync();
            if (info == null)
            {
                _pendingUpdate = null;
                _updateItem.Text = "Check for updates…";
                return;
            }

            _pendingUpdate = info;
            _updateItem.Text = $"Install update {info.Version}…";

            // Only pop the balloon once per discovered version, so the recurring check
            // doesn't nag every 30 minutes.
            if (announce && info.Version != _notifiedVersion)
            {
                _notifiedVersion = info.Version;
                _icon.ShowBalloonTip(7000, "Hand Mirror update available",
                    $"Version {info.Version} is ready. Click here to install.", ToolTipIcon.Info);
            }
        }
        catch
        {
            // Never let an update check take down the tray app.
        }
    }

    private async void OnUpdateItemClicked()
    {
        if (_installing) return;

        if (_pendingUpdate != null)
        {
            await InstallUpdateAsync(_pendingUpdate);
            return;
        }

        _updateItem.Text = "Checking…";
        _updateItem.Enabled = false;
        try
        {
            await CheckForUpdatesAsync(announce: false);
            if (_pendingUpdate == null)
                _updateItem.Text = $"Up to date ({UpdateService.CurrentVersion})";
        }
        finally
        {
            _updateItem.Enabled = true;
        }
    }

    private async Task InstallUpdateAsync(UpdateService.UpdateInfo info)
    {
        if (_installing) return;
        _installing = true;
        _updateItem.Enabled = false;
        _updateItem.Text = $"Downloading {info.Version}…";

        string installerPath;
        try
        {
            installerPath = await UpdateService.DownloadAsync(info);
        }
        catch (Exception ex)
        {
            _installing = false;
            _updateItem.Enabled = true;
            _updateItem.Text = $"Install update {info.Version}…";
            _icon.ShowBalloonTip(7000, "Update failed",
                "Could not download the update: " + ex.Message, ToolTipIcon.Error);
            return;
        }

        try
        {
            // Runs the silent installer and shuts us down; the installer relaunches us.
            UpdateService.ApplyAndRestart(installerPath);
        }
        catch (Exception ex)
        {
            _installing = false;
            _updateItem.Enabled = true;
            _updateItem.Text = $"Install update {info.Version}…";
            _icon.ShowBalloonTip(7000, "Update failed",
                "Could not start the installer: " + ex.Message, ToolTipIcon.Error);
        }
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
        try
        {
            var dispatcher = WpfApp.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.Invoke(Rebuild);
            else
                Rebuild();
        }
        catch
        {
            // Don't let a menu rebuild failure crash the tray.
        }
    }

    private void SelectCamera(string id)
    {
        if (Settings.CameraId == id) return;
        Settings.CameraId = id;

        // Restart the preview with the newly chosen camera if it's open.
        _window?.Refresh();
    }

    // Refresh the open preview, or open it if it's hidden.
    private void RefreshCamera()
    {
        if (_window != null) _window.Refresh();
        else Toggle();
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
        _updateTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _window?.Close();
    }
}
