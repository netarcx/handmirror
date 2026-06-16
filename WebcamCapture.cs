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
    public event Action<string>? CaptureFailed;

    public sealed record CameraInfo(string Id, string Name);

    /// <summary>Lists connected video capture devices (webcams, capture cards, etc.).</summary>
    public static async Task<IReadOnlyList<CameraInfo>> ListCamerasAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Select(d => new CameraInfo(d.Id, d.Name)).ToList();
    }

    /// <summary>
    /// Starts capture from the given device (or the first available one). Pass the
    /// id from <see cref="ListCamerasAsync"/> to pick a specific camera — important
    /// when several video-in devices exist (e.g. a webcam plus an HDMI capture card).
    /// </summary>
    public async Task StartAsync(Dispatcher uiDispatcher, string? deviceId = null)
    {
        _dispatcher = uiDispatcher;

        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        if (_disposed) return;
        if (devices.Count == 0)
            throw new InvalidOperationException("No camera found");

        var device = devices.FirstOrDefault(d => d.Id == deviceId) ?? devices[0];

        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            });
        }
        catch (Exception ex)
        {
            capture.Dispose();
            throw new InvalidOperationException($"Could not open '{device.Name}': {ex.Message}", ex);
        }
        if (_disposed)
        {
            capture.Dispose();
            return;
        }
        _capture = capture;

        var source = PickSource(_capture);
        if (source == null)
            throw new InvalidOperationException($"'{device.Name}' exposes no usable video stream");

        // Best-effort: switch the source to an uncompressed format. Ignored in shared
        // mode or on devices that don't allow it — the reader fallback below still copes.
        var format = PickFormat(source);
        if (format != null)
        {
            try { await source.SetFormatAsync(format); }
            catch { /* keep the source's current format */ }
            if (_disposed) return;
        }

        var reader = await CreateReaderAsync(_capture, source);
        if (reader == null)
            throw new InvalidOperationException(
                $"'{device.Name}' did not accept any supported frame format. " +
                "If this is an HDMI capture card, make sure a source is connected.");
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

    // Prefer a real color source over Infrared/Depth, and a preview stream over record.
    private static MediaFrameSource? PickSource(MediaCapture capture)
    {
        var color = capture.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();
        var pool = color.Count > 0 ? color : capture.FrameSources.Values.ToList();

        return pool.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
               ?? pool.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
               ?? pool.FirstOrDefault();
    }

    // Create a frame reader, trying several output subtypes. Forcing BGRA8 directly
    // fails on many cameras and on MJPG-only capture cards (MF_E_INVALIDMEDIATYPE);
    // requesting NV12 lets MediaFoundation decode MJPG/YUY2 for us. OnFrameArrived
    // then converts whatever arrives to BGRA8 in software.
    private static async Task<MediaFrameReader?> CreateReaderAsync(MediaCapture capture, MediaFrameSource source)
    {
        var current = source.CurrentFormat;
        bool nativeUsable = current != null
            && UncompressedSubtypes.Contains(current.Subtype.ToUpperInvariant());

        var strategies = nativeUsable
            ? new string?[] { null, MediaEncodingSubtypes.Nv12, MediaEncodingSubtypes.Bgra8 }
            : new string?[] { MediaEncodingSubtypes.Nv12, MediaEncodingSubtypes.Yuy2, MediaEncodingSubtypes.Bgra8, null };

        foreach (var subtype in strategies)
        {
            try
            {
                return subtype == null
                    ? await capture.CreateFrameReaderAsync(source)
                    : await capture.CreateFrameReaderAsync(source, subtype);
            }
            catch
            {
                // This subtype isn't supported by the device — try the next one.
            }
        }
        return null;
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
            if (w <= 0 || h <= 0) return;

            // MediaFoundation pads each row for alignment, so the real stride is usually
            // larger than w*4. Assuming w*4 shifts every row and gives green/magenta
            // diagonal tearing; under-sizing the copy buffer (the previous bug) does the
            // same. So: probe the real stride to size the buffer generously, let
            // CopyToBuffer write its natural layout, then read back EXACTLY what it wrote
            // and derive the stride from that byte count. This never over-reads, so it
            // can't throw regardless of whether CopyToBuffer packs or pads.
            int probeStride = w * 4;
            try
            {
                using var locked = converted.LockBuffer(BitmapBufferAccessMode.Read);
                probeStride = Math.Max(probeStride, locked.GetPlaneDescription(0).Stride);
            }
            catch { /* fall back to w*4 */ }

            var buffer = new Windows.Storage.Streams.Buffer((uint)(probeStride * h));
            converted.CopyToBuffer(buffer);
            if (ownsConverted) converted.Dispose();

            int stride;
            using (var dataReader = DataReader.FromBuffer(buffer))
            {
                int copied = (int)dataReader.UnconsumedBufferLength;
                if (copied < w * 4 * h) return; // incomplete frame; skip it
                stride = copied / h;
                if (_scratch == null || _scratch.Length != copied)
                    _scratch = new byte[copied];
                dataReader.ReadBytes(_scratch);
            }

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
                    _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bytes, stride, 0);
                    FrameReady?.Invoke(_bitmap);
                }
                finally
                {
                    Interlocked.Exchange(ref _updatePending, 0);
                }
            });
            dispatched = true;
        }
        catch (Exception ex)
        {
            // Surface a per-frame failure instead of silently stalling on "Starting…".
            var dispatcher = _dispatcher;
            var message = ex.Message;
            if (dispatcher != null && !_disposed)
                dispatcher.BeginInvoke(() => CaptureFailed?.Invoke(message));
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

        var reader = _reader;
        var capture = _capture;
        _reader = null;
        _capture = null;

        try
        {
            if (reader != null)
            {
                reader.FrameArrived -= OnFrameArrived;
                // Stop on a thread-pool thread (no UI SynchronizationContext) so the
                // StopAsync continuation can't deadlock against the UI thread we're on.
                // Bounded wait so the camera/LED is released before we tear down.
                Task.Run(async () => await reader.StopAsync()).Wait(TimeSpan.FromSeconds(2));
                reader.Dispose();
            }
        }
        catch { }
        try { capture?.Dispose(); } catch { }

        _scratch = null;
        _bitmap = null;
        _dispatcher = null;
        FrameReady = null;
        CaptureFailed = null;
    }
}
