using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace HandMirror;

public sealed class WebcamCapture : IDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private WriteableBitmap? _bitmap;
    private Dispatcher? _dispatcher;
    private byte[]? _scratch;

    public event Action<WriteableBitmap>? FrameReady;

    public async Task StartAsync(Dispatcher uiDispatcher)
    {
        _dispatcher = uiDispatcher;

        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        if (devices.Count == 0)
            throw new InvalidOperationException("No camera found");

        _capture = new MediaCapture();
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = devices[0].Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        });

        var source = _capture.FrameSources.Values.FirstOrDefault(s =>
                         s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                     ?? _capture.FrameSources.Values.FirstOrDefault(s =>
                         s.Info.MediaStreamType == MediaStreamType.VideoRecord);
        if (source == null)
            throw new InvalidOperationException("No video frame source");

        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.FrameArrived += OnFrameArrived;
        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException("Frame reader failed to start: " + status);
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frameRef = sender.TryAcquireLatestFrame();
        var bitmap = frameRef?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap == null) return;

        var converted = bitmap;
        var ownsConverted = false;
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            ownsConverted = true;
        }

        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        int byteCount = w * h * 4;

        if (_scratch == null || _scratch.Length != byteCount)
            _scratch = new byte[byteCount];

        var buffer = new Windows.Storage.Streams.Buffer((uint)byteCount);
        converted.CopyToBuffer(buffer);
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(_scratch);
        }

        if (ownsConverted) converted.Dispose();

        var bytes = _scratch;
        _dispatcher?.BeginInvoke(() =>
        {
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32, null);
            }
            _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bytes, w * 4, 0);
            FrameReady?.Invoke(_bitmap);
        });
    }

    public void Dispose()
    {
        try
        {
            if (_reader != null)
            {
                _reader.FrameArrived -= OnFrameArrived;
                _reader.StopAsync().AsTask().Wait();
                _reader.Dispose();
                _reader = null;
            }
        }
        catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
    }
}
