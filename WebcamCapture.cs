using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace HandMirror;

// Lets us read the raw bytes (and the real row stride) of a WinRT BitmapBuffer.
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

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
            int rowBytes = w * 4;
            int byteCount = rowBytes * h;

            if (_scratch == null || _scratch.Length != byteCount)
                _scratch = new byte[byteCount];

            // Copy out the pixels honoring the bitmap's REAL row stride. MediaFoundation
            // pads each row for alignment, so the source stride is usually larger than
            // w*4. Assuming w*4 shifts every row and produces green/magenta diagonal
            // tearing — so we read plane.Stride and repack tightly into _scratch.
            using (var locked = converted.LockBuffer(BitmapBufferAccessMode.Read))
            using (var reference = locked.CreateReference())
            {
                var plane = locked.GetPlaneDescription(0);
                int srcStride = plane.Stride;
                unsafe
                {
                    ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* src, out uint capacity);
                    byte* start = src + plane.StartIndex;
                    fixed (byte* dst = _scratch)
                    {
                        for (int y = 0; y < h; y++)
                            System.Buffer.MemoryCopy(
                                start + (long)y * srcStride,
                                dst + (long)y * rowBytes,
                                rowBytes, rowBytes);
                    }
                }
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
