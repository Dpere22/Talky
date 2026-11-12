using System.Diagnostics;
using Talky.Interop;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Talky.Services.ScreenShare;

/// <summary>
/// Captures a window or monitor picked by the user via Windows.Graphics.Capture and hands out the
/// latest frame as tightly packed BGRA (optionally downscaled). Intermediate frames are dropped,
/// so a slow consumer never builds up latency. Windows reports the areas each frame redrew; they are
/// accumulated across dropped frames and handed out with the frame, so only those areas get re-examined.
///
/// The reports can lag by a frame: a GDI window that draws between composition and capture shows the change
/// in frame N but reports it with frame N+1 (measured: 20 of 240 frames; the next report always covered it).
/// Consumers therefore keep their own copy of what they last saw instead of trusting unreported areas forever,
/// and every <see cref="FullPassInterval"/> (plus once when the screen goes quiet, see
/// <see cref="RefreshLastFrame"/>) a frame is handed out as fully changed.
/// </summary>
internal sealed class ScreenCaptureSession : IDisposable
{
    /// <summary>Past this many pending dirty rects (a stalled consumer) the whole frame counts as changed.</summary>
    private const int MaxDirtyRects = 512;

    private static readonly TimeSpan FullPassInterval = TimeSpan.FromSeconds(2);

    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _frameAvailable = new(0, 1);
    private readonly List<RectInt32> _dirty = [];
    private bool _dirtyUnknown = true;
    private Direct3D11CaptureFrame? _latest;
    private SizeInt32 _poolSize;
    private byte[] _source = [];
    private byte[] _scaled = [];
    private (int SourceWidth, int SourceHeight, int Width, int Height) _scaledShape;
    private (int SourceWidth, int SourceHeight, int Width, int Height) _lastDelivered;
    private long _lastFullPass;
    private bool _disposed;

    private ScreenCaptureSession(GraphicsCaptureItem item, IDirect3DDevice device)
    {
        _item = item;
        _device = device;
        _poolSize = item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = true;
        // Frames still contain everything; Windows additionally lists the areas it redrew.
        _session.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportOnly;

        item.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        _session.StartCapture();
    }

    /// <summary>The captured window was closed.</summary>
    public event EventHandler? Closed;

    public string DisplayName => _item.DisplayName;

    /// <summary>Shows the system picker. Returns null if the user cancelled or capture is unsupported.</summary>
    public static async Task<ScreenCaptureSession?> PickAsync(nint ownerWindow)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            return null;
        }

        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        var item = await picker.PickSingleItemAsync();
        if (item is null)
        {
            return null;
        }

        return new ScreenCaptureSession(item, Direct3D11Interop.CreateDevice());
    }

    /// <summary>Caps the capture rate at the source, so Windows doesn't compose frames that would only be dropped.</summary>
    public void SetMaxFrameRate(int framesPerSecond)
    {
        // Frames land on display refreshes, so exactly 1/fps can miss one and undershoot
        // (1/30 s gave 28 fps on a 60 Hz display); a little slack hits the target.
        var interval = TimeSpan.FromSeconds(1.0 / framesPerSecond) - TimeSpan.FromMilliseconds(1.5);
        _session.MinUpdateInterval = interval > TimeSpan.Zero ? interval : TimeSpan.Zero;
    }

    /// <summary>
    /// Waits for the next frame. Returns null on timeout. The returned buffer is reused by the next call.
    /// </summary>
    public async Task<CapturedFrame?> NextFrameAsync(int maxWidth, int maxHeight, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await _frameAvailable.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Direct3D11CaptureFrame? frame;
        RectInt32[]? dirty = null;
        lock (_gate)
        {
            frame = _latest;
            _latest = null;
            if (frame is not null)
            {
                dirty = _dirtyUnknown ? null : [.. _dirty];
                _dirty.Clear();
                _dirtyUnknown = false;
            }
        }

        if (frame is null)
        {
            return null;
        }

        bool delivered = false;
        try
        {
            using (frame)
            {
                var contentSize = frame.ContentSize;
                if (contentSize.Width <= 0 || contentSize.Height <= 0)
                {
                    return null;
                }

                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    // Window was resized: grow/shrink the pool; the next frames will have the new size.
                    _poolSize = contentSize;
                    _framePool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
                    dirty = null;
                    MarkAllDirty();
                }

                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
                int width = Math.Min(contentSize.Width, bitmap.PixelWidth);
                int height = Math.Min(contentSize.Height, bitmap.PixelHeight);
                CopyPixels(bitmap, width, height);
                if (dirty is not null && Stopwatch.GetElapsedTime(_lastFullPass) >= FullPassInterval)
                {
                    dirty = null; // periodic safety net, see the class remarks
                }

                if (dirty is null)
                {
                    _lastFullPass = Stopwatch.GetTimestamp();
                }
                else
                {
                    dirty = Clip(dirty, width, height);
                }

                CapturedFrame result;
                var (targetWidth, targetHeight) = FitWithin(width, height, maxWidth, maxHeight);
                if (targetWidth == width && targetHeight == height)
                {
                    _scaledShape = default;
                    result = new CapturedFrame(_source, width, height, dirty);
                }
                else
                {
                    // Partial rescaling needs _scaled to hold the previous frame at exactly this geometry.
                    var shape = (width, height, targetWidth, targetHeight);
                    if (shape != _scaledShape)
                    {
                        dirty = null;
                    }

                    if (_scaled.Length != targetWidth * targetHeight * 4)
                    {
                        _scaled = new byte[targetWidth * targetHeight * 4];
                    }

                    var scaledDirty = dirty is null ? null : ImageScaler.MapToTarget(dirty, width, height, targetWidth, targetHeight);
                    _scaledShape = default; // until the buffer is consistent again
                    ImageScaler.Downscale(_source, width, height, _scaled, targetWidth, targetHeight, scaledDirty);
                    _scaledShape = shape;
                    result = new CapturedFrame(_scaled, targetWidth, targetHeight, scaledDirty);
                }

                _lastDelivered = (width, height, targetWidth, targetHeight);
                delivered = true;
                return result;
            }
        }
        finally
        {
            if (!delivered)
            {
                // This frame's changes were consumed but never handed out: the next frame must cover everything.
                MarkAllDirty();
            }
        }
    }

    /// <summary>
    /// Hands out the last frame again, fully re-processed and marked as entirely changed, so anything a lagging
    /// dirty report left behind is picked up. Call when the screen has gone quiet (no new frame). Null before
    /// the first frame. Same buffer rules as <see cref="NextFrameAsync"/>.
    /// </summary>
    public CapturedFrame? RefreshLastFrame()
    {
        var (sourceWidth, sourceHeight, width, height) = _lastDelivered;
        if (width == 0 || _source.Length != sourceWidth * sourceHeight * 4)
        {
            return null;
        }

        _lastFullPass = Stopwatch.GetTimestamp();
        if (width == sourceWidth && height == sourceHeight)
        {
            return new CapturedFrame(_source, width, height, null);
        }

        if (_scaled.Length != width * height * 4)
        {
            return null;
        }

        _scaledShape = default;
        ImageScaler.Downscale(_source, sourceWidth, sourceHeight, _scaled, width, height);
        _scaledShape = _lastDelivered;
        return new CapturedFrame(_scaled, width, height, null);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }

        _framePool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _framePool.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        // Copied outside the lock: every access is a WinRT call.
        RectInt32[]? regions = frame.DirtyRegionMode == GraphicsCaptureDirtyRegionMode.ReportOnly ? [.. frame.DirtyRegions] : null;

        lock (_gate)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }

            AddDirty(regions);
            _latest?.Dispose();
            _latest = frame;
        }

        try
        {
            if (_frameAvailable.CurrentCount == 0)
            {
                _frameAvailable.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>Adds a frame's regions to the pending set. Call under <see cref="_gate"/>.</summary>
    private void AddDirty(RectInt32[]? regions)
    {
        if (_dirtyUnknown)
        {
            return;
        }

        // A frame without an explicit list could have changed anywhere: compare everything rather than miss it.
        if (regions is null || regions.Length == 0 || _dirty.Count + regions.Length > MaxDirtyRects)
        {
            _dirtyUnknown = true;
            _dirty.Clear();
            return;
        }

        _dirty.AddRange(regions);
    }

    private void MarkAllDirty()
    {
        lock (_gate)
        {
            _dirtyUnknown = true;
            _dirty.Clear();
        }
    }

    private static RectInt32[] Clip(RectInt32[] regions, int width, int height)
    {
        var result = new List<RectInt32>(regions.Length);
        foreach (var r in regions)
        {
            int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
            int x1 = Math.Min(width, r.X + r.Width), y1 = Math.Min(height, r.Y + r.Height);
            if (x1 > x0 && y1 > y0)
            {
                result.Add(new RectInt32 { X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 });
            }
        }

        return [.. result];
    }

    private unsafe void CopyPixels(SoftwareBitmap bitmap, int width, int height)
    {
        int rowBytes = width * 4;
        if (_source.Length != rowBytes * height)
        {
            _source = new byte[rowBytes * height];
        }

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        byte* data = BufferInterop.GetPointer(reference, out _);
        fixed (byte* destination = _source)
        {
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(data + plane.StartIndex + (long)y * plane.Stride, destination + (long)y * rowBytes, rowBytes, rowBytes);
            }
        }
    }

    private static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0 || (width <= maxWidth && height <= maxHeight))
        {
            return (width, height);
        }

        double scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        return (Math.Max(2, (int)Math.Round(width * scale)), Math.Max(2, (int)Math.Round(height * scale)));
    }
}

/// <param name="Changed">
/// Areas that may differ from the previous frame handed out (frame coordinates); null when unknown,
/// meaning anything may have changed.
/// </param>
internal readonly record struct CapturedFrame(byte[] Pixels, int Width, int Height, IReadOnlyList<RectInt32>? Changed);

internal static class ImageScaler
{
    /// <summary>
    /// Area-averaging (box) downscale of BGRA pixels; keeps text legible at lower resolutions. With
    /// <paramref name="regions"/> (destination coordinates) only those areas are recomputed; the rest of
    /// <paramref name="destination"/> is left untouched.
    /// </summary>
    public static void Downscale(byte[] source, int sourceWidth, int sourceHeight, byte[] destination, int width, int height, IReadOnlyList<RectInt32>? regions = null)
    {
        double scaleX = (double)sourceWidth / width;
        double scaleY = (double)sourceHeight / height;

        if (regions is null)
        {
            Parallel.For(0, height, y => DownscaleRow(source, sourceWidth, sourceHeight, destination, width, scaleX, scaleY, y, 0, width));
            return;
        }

        // One span per destination row covering all regions on it, so no two workers write the same pixel.
        var start = new int[height];
        var end = new int[height];
        Array.Fill(start, int.MaxValue);
        foreach (var r in regions)
        {
            int x0 = Math.Max(0, r.X), x1 = Math.Min(width, r.X + r.Width);
            for (int y = Math.Max(0, r.Y); y < Math.Min(height, r.Y + r.Height); y++)
            {
                start[y] = Math.Min(start[y], x0);
                end[y] = Math.Max(end[y], x1);
            }
        }

        Parallel.For(0, height, y =>
        {
            if (end[y] > start[y])
            {
                DownscaleRow(source, sourceWidth, sourceHeight, destination, width, scaleX, scaleY, y, start[y], end[y]);
            }
        });
    }

    /// <summary>
    /// Maps source rects to the destination pixels whose box filter reads them, padded by a pixel against rounding.
    /// </summary>
    public static RectInt32[] MapToTarget(IReadOnlyList<RectInt32> regions, int sourceWidth, int sourceHeight, int width, int height)
    {
        double scaleX = (double)sourceWidth / width;
        double scaleY = (double)sourceHeight / height;
        var result = new List<RectInt32>(regions.Count);
        foreach (var r in regions)
        {
            int x0 = Math.Max(0, (int)Math.Floor(r.X / scaleX) - 1);
            int y0 = Math.Max(0, (int)Math.Floor(r.Y / scaleY) - 1);
            int x1 = Math.Min(width, (int)Math.Ceiling((r.X + r.Width) / scaleX) + 1);
            int y1 = Math.Min(height, (int)Math.Ceiling((r.Y + r.Height) / scaleY) + 1);
            if (x1 > x0 && y1 > y0)
            {
                result.Add(new RectInt32 { X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 });
            }
        }

        return [.. result];
    }

    private static void DownscaleRow(byte[] source, int sourceWidth, int sourceHeight, byte[] destination, int width, double scaleX, double scaleY, int y, int xStart, int xEnd)
    {
        int sourceStride = sourceWidth * 4;
        int y0 = (int)(y * scaleY);
        int y1 = Math.Max(y0 + 1, Math.Min(sourceHeight, (int)((y + 1) * scaleY)));
        for (int x = xStart; x < xEnd; x++)
        {
            int x0 = (int)(x * scaleX);
            int x1 = Math.Max(x0 + 1, Math.Min(sourceWidth, (int)((x + 1) * scaleX)));
            int b = 0, g = 0, r = 0, count = 0;
            for (int sy = y0; sy < y1; sy++)
            {
                int row = sy * sourceStride;
                for (int sx = x0; sx < x1; sx++)
                {
                    int i = row + sx * 4;
                    b += source[i];
                    g += source[i + 1];
                    r += source[i + 2];
                    count++;
                }
            }

            int o = (y * width + x) * 4;
            destination[o] = (byte)(b / count);
            destination[o + 1] = (byte)(g / count);
            destination[o + 2] = (byte)(r / count);
            destination[o + 3] = 255;
        }
    }
}
