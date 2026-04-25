using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HandMirror;

public partial class MirrorWindow : Window
{
    private readonly WebcamCapture _capture = new();
    private bool _sizedToFrame;

    public MirrorWindow()
    {
        InitializeComponent();
        PositionAtTopCenter();
        Loaded += async (_, _) => await StartAsync();
        MouseLeftButtonDown += OnWindowMouseLeftButtonDown;
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
        _capture.FrameReady += bmp =>
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
        try
        {
            await _capture.StartAsync(Dispatcher);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Camera error: " + ex.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _capture.Dispose();
        base.OnClosed(e);
    }

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
}
