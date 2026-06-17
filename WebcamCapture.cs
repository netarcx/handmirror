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

    // Create a frame reader, preferring BGRA8 output so MediaFoundation's (correct)
    // color converter does the YUV->RGB work. We deliberately do NOT take YUY2 or the
    // native YUV frame and convert it ourselves: SoftwareBitmap.Convert mishandles
    // YUY2 (green/magenta duotone) on some hardware. NV12 via MF is the fallback;
    // native is last (and only yields a SoftwareBitmap for already-RGB sources).
    private async Task<MediaFrameReader?> CreateReaderAsync(MediaCapture capture, MediaFrameSource source)
    {
        // null subtype = the source's native format.
        var subtypes = new string?[]
        {
            MediaEncodingSubtypes.Bgra8,
            MediaEncodingSubtypes.Nv12,
            null,
        };

        foreach (var subtype in subtypes)
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

    // Lower rank = preferred. RGB needs no chroma reconstruction; 4:2:2 (YUY2/UYVY)
    // keeps full vertical chroma; 4:2:0 (NV12/YV12) is most prone to the green/magenta
    // luma-chroma misalignment, so it's last.
    private static int SubtypeRank(string subtype) => subtype.ToUpperInvariant() switch
    {
        "RGB32" or "ARGB32" or "BGRA8" => 0,
        "RGB24" => 1,
        "YUY2" or "UYVY" => 2,
        _ => 3,
    };

    private static MediaFrameFormat? PickFormat(MediaFrameSource source)
    {
        const long maxArea = 1920L * 1080; // don't pick a laggy 4K mode

        // MJPG/MJPEG are allowed here because the reader decodes them to BGRA8 for us;
        // they're often the only modes that expose the camera's full field of view
        // (lower uncompressed modes are frequently a cropped sensor readout).
        static bool Acceptable(MediaFrameFormat f)
        {
            if (!string.Equals(f.MajorType, "Video", StringComparison.OrdinalIgnoreCase)) return false;
            var s = f.Subtype.ToUpperInvariant();
            return UncompressedSubtypes.Contains(s) || s is "MJPG" or "MJPEG";
        }

        static long Area(MediaFrameFormat f) => (long)f.VideoFormat.Width * f.VideoFormat.Height;
        static double Fps(MediaFrameFormat f) =>
            f.FrameRate.Denominator == 0 ? 0d : f.FrameRate.Numerator / (double)f.FrameRate.Denominator;

        var candidates = source.SupportedFormats.Where(Acceptable).ToList();

        // Largest frame up to 1080p (full field of view) at a usable frame rate;
        // tie-break toward higher color fidelity, then higher fps.
        return candidates
            .Where(f => Area(f) <= maxArea && Fps(f) >= 15)
            .OrderByDescending(Area)
            .ThenBy(f => SubtypeRank(f.Subtype))
            .ThenByDescending(Fps)
            .FirstOrDefault()
            ?? candidates.OrderByDescending(Area).FirstOrDefault();
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

            var op = dispatcher.BeginInvoke(() =>
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
            // If the dispatcher shuts down, the queued op may be aborted without its
            // finally running — release the gate so the pipeline can't wedge.
            op.Aborted += (_, _) => Interlocked.Exchange(ref _updatePending, 0);
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

        if (reader != null)
            reader.FrameArrived -= OnFrameArrived;

        // Tear down off the UI thread so a slow/hung StopAsync never freezes the UI
        // (Dispose is called from close, Refresh, and camera-switch). Sequenced so the
        // reader is stopped before it's disposed. If the process is exiting, the OS
        // releases the camera handle regardless.
        Task.Run(async () =>
        {
            try { if (reader != null) await reader.StopAsync(); } catch { }
            try { reader?.Dispose(); } catch { }
            try { capture?.Dispose(); } catch { }
        });

        _scratch = null;
        _bitmap = null;
        _dispatcher = null;
        FrameReady = null;
        CaptureFailed = null;
    }
}
