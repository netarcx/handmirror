using System;
using System.Linq;
using System.Threading;
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
    private int _updatePending;
    private volatile bool _disposed;

    public event Action<WriteableBitmap>? FrameReady;

    public async Task StartAsync(Dispatcher uiDispatcher)
    {
        _dispatcher = uiDispatcher;

        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        if (_disposed) return;
        if (devices.Count == 0)
            throw new InvalidOperationException("No camera found");

        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = devices[0].Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            });
        }
        catch
        {
            capture.Dispose();
            throw;
        }
        if (_disposed)
        {
            capture.Dispose();
            return;
        }
        _capture = capture;

        var source = _capture.FrameSources.Values.FirstOrDefault(s =>
                         s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                     ?? _capture.FrameSources.Values.FirstOrDefault(s =>
                         s.Info.MediaStreamType == MediaStreamType.VideoRecord);
        if (source == null)
            throw new InvalidOperationException("No video frame source");

        // Most webcams only deliver NV12/YUY2/MJPG natively, not BGRA8. Asking the
        // frame reader to output BGRA8 directly fails with MF_E_INVALIDMEDIATYPE
        // ("data specified for the media type is invalid...") when MediaFoundation
        // can't build a converter for that device. Instead, switch the source to a
        // supported uncompressed format and let OnFrameArrived convert to BGRA8 in
        // software (which it already does for any input format).
        var format = PickFormat(source);
        if (format != null)
        {
            try { await source.SetFormatAsync(format); }
            catch { /* keep the source's current format as a fallback */ }
            if (_disposed) return;
        }

        var reader = await _capture.CreateFrameReaderAsync(source);
        if (_disposed)
        {
            reader.Dispose();
            return;
        }
        _reader = reader;
        _reader.FrameArrived += OnFrameArrived;
        var status = await _reader.StartAsync();
        if (_disposed) return;
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException("Frame reader failed to start: " + status);
    }

    // Uncompressed subtypes whose frames expose a usable SoftwareBitmap on the CPU.
    // Compressed formats (MJPG/H264/HEVC) yield no SoftwareBitmap, so we avoid them.
    private static readonly string[] UncompressedSubtypes =
    {
        "NV12", "YUY2", "UYVY", "YV12", "IYUV", "I420", "RGB24", "RGB32", "ARGB32", "BGRA8",
    };

    private static MediaFrameFormat? PickFormat(MediaFrameSource source)
    {
        const long target = 1280L * 720; // prefer ~720p, then the highest frame rate

        return source.SupportedFormats
            .Where(f => string.Equals(f.MajorType, "Video", StringComparison.OrdinalIgnoreCase)
                        && UncompressedSubtypes.Contains(f.Subtype.ToUpperInvariant()))
            .OrderBy(f => Math.Abs((long)f.VideoFormat.Width * f.VideoFormat.Height - target))
            .ThenByDescending(f => f.FrameRate.Denominator == 0
                ? 0d
                : f.FrameRate.Numerator / (double)f.FrameRate.Denominator)
            .FirstOrDefault();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.CompareExchange(ref _updatePending, 1, 0) != 0)
        {
            using var skipped = sender.TryAcquireLatestFrame();
            return;
        }

        bool dispatched = false;
        try
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
            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_disposed) return;
                    if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                    {
                        _bitmap = new WriteableBitmap(w, h, 96, 96,
                            System.Windows.Media.PixelFormats.Pbgra32, null);
                    }
                    _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bytes, w * 4, 0);
                    FrameReady?.Invoke(_bitmap);
                }
                finally
                {
                    Interlocked.Exchange(ref _updatePending, 0);
                }
            });
            dispatched = true;
        }
        finally
        {
            if (!dispatched)
                Interlocked.Exchange(ref _updatePending, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
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
