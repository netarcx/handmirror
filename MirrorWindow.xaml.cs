using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HandMirror;

public partial class MirrorWindow : Window
{
    private const double DefaultWidth = 640;
    private WebcamCapture _capture = new();
    private bool _sizedToFrame;
    private double _aspect = 480.0 / 640.0; // updated from the first frame

    public MirrorWindow()
    {
        InitializeComponent();
        Width = DefaultWidth; // always open at the default size (never persisted)
        PositionAtTopCenter();
        Loaded += async (_, _) => await StartAsync();
        MouseLeftButtonDown += OnWindowMouseLeftButtonDown;
        PreviewKeyDown += OnPreviewKeyDown;
        MouseWheel += OnMouseWheel;
    }

    // Scroll the wheel over the window to scale it up/down (aspect-locked). The size
    // isn't saved, so each time the window is opened it returns to the default size.
    private void OnMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        const double step = 80;
        var area = SystemParameters.WorkArea;

        // Cap width by both the screen width and the screen height (via aspect), so a
        // portrait source can't grow the window taller than the display.
        double maxByHeight = _aspect > 0 ? (area.Height - 24) / _aspect : area.Width;
        double maxWidth = Math.Min(area.Width, maxByHeight);

        double newWidth = Width + (e.Delta > 0 ? step : -step);
        newWidth = Math.Max(240, Math.Min(maxWidth, newWidth));

        Width = newWidth;
        Height = newWidth * _aspect;
        PositionAtTopCenter();
        e.Handled = true;
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
        // Reset state so a refresh re-shows "Starting…" and re-fits to the frame.
        _sizedToFrame = false;
        StatusText.Text = "Starting camera…";
        StatusText.Visibility = Visibility.Visible;

        var capture = _capture;
        capture.FrameReady += bmp =>
        {
            PreviewBrush.ImageSource = bmp;
            if (StatusText.Visibility != Visibility.Collapsed)
                StatusText.Visibility = Visibility.Collapsed;
            if (bmp.PixelWidth > 0)
            {
                _aspect = (double)bmp.PixelHeight / bmp.PixelWidth;
                if (!_sizedToFrame)
                {
                    _sizedToFrame = true;
                    Height = Width * _aspect;
                    PositionAtTopCenter();
                }
            }
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
