using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HandMirror;

public partial class MirrorWindow : Window
{
    private WebcamCapture _capture = new();
    private bool _sizedToFrame;

    public MirrorWindow()
    {
        InitializeComponent();
        PositionAtTopCenter();
        Loaded += async (_, _) => await StartAsync();
        MouseLeftButtonDown += OnWindowMouseLeftButtonDown;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+Shift+R force-refreshes the camera feed.
        if (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            Refresh();
        }
    }

    /// <summary>Tears down the current capture and starts a fresh one.</summary>
    public void Refresh()
    {
        var old = _capture;
        _capture = new WebcamCapture();
        old.Dispose();
        _ = StartAsync();
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsDescendantOf(src, CloseButton))
            return;
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void PositionAtTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 12;
    }

    private async Task StartAsync()
    {
        // Reset the overlay state so a refresh re-shows "Starting…" and re-fits.
        _sizedToFrame = false;
        StatusText.Text = "Starting camera…";
        StatusText.Visibility = Visibility.Visible;
        DiagText.Visibility = Visibility.Collapsed;

        var capture = _capture;
        capture.FrameReady += bmp =>
        {
            PreviewBrush.ImageSource = bmp;
            if (StatusText.Visibility != Visibility.Collapsed)
                StatusText.Visibility = Visibility.Collapsed;
            if (!_sizedToFrame && bmp.PixelWidth > 0)
            {
                _sizedToFrame = true;
                Height = Width * ((double)bmp.PixelHeight / bmp.PixelWidth);
                PositionAtTopCenter();
            }
        };
        capture.DiagnosticsReady += info =>
        {
            DiagText.Text = info;
            DiagText.Visibility = Visibility.Visible;
        };
        capture.CaptureFailed += message =>
        {
            var text = "Camera error: " + message +
                "\nTry another camera from the tray icon's Camera menu.";
            if (StatusText.Text == text && StatusText.Visibility == Visibility.Visible)
                return; // avoid re-laying out the UI on every failing frame
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = text;
        };
        try
        {
            await capture.StartAsync(Dispatcher, Settings.CameraId);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Camera error: " + ex.Message +
                "\nPick a different camera from the tray icon's Camera menu.";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _capture.Dispose();
        base.OnClosed(e);
    }

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
}
