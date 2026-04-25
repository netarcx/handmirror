using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

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
        MouseLeftButtonDown += (_, _) => DragMove();
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
