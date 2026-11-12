using System.Diagnostics;
using Talky.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Talky.Services.ScreenShare;

/// <summary>
/// Captures a window or monitor picked by the user via Windows.Graphics.Capture and hands out the
/// latest frame as tightly packed BGRA (optionally downscaled). Intermediate frames are dropped,
/// so a slow consumer never builds up latency.
/// </summary>
internal sealed class ScreenCaptureSession : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _frameAvailable = new(0, 1);
    private Direct3D11CaptureFrame? _latest;
    private SizeInt32 _poolSize;
    private byte[] _source = [];
    private byte[] _scaled = [];
    private bool _disposed;

    private ScreenCaptureSession(GraphicsCaptureItem item, IDirect3DDevice device)
    {
        _item = item;
        _device = device;
        _poolSize = item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);

        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
        {
            _session.IsCursorCaptureEnabled = true;
        }

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
        lock (_gate)
        {
            frame = _latest;
            _latest = null;
        }

        if (frame is null)
        {
            return null;
        }

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
            }

            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
            int width = Math.Min(contentSize.Width, bitmap.PixelWidth);
            int height = Math.Min(contentSize.Height, bitmap.PixelHeight);
            CopyPixels(bitmap, width, height);

            var (targetWidth, targetHeight) = FitWithin(width, height, maxWidth, maxHeight);
            if (targetWidth == width && targetHeight == height)
            {
                return new CapturedFrame(_source, width, height);
            }

            if (_scaled.Length != targetWidth * targetHeight * 4)
            {
                _scaled = new byte[targetWidth * targetHeight * 4];
            }

            ImageScaler.Downscale(_source, width, height, _scaled, targetWidth, targetHeight);
            return new CapturedFrame(_scaled, targetWidth, targetHeight);
        }
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

        lock (_gate)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }

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

internal readonly record struct CapturedFrame(byte[] Pixels, int Width, int Height);

internal static class ImageScaler
{
    /// <summary>Area-averaging (box) downscale of BGRA pixels; keeps text legible at lower resolutions.</summary>
    public static void Downscale(byte[] source, int sourceWidth, int sourceHeight, byte[] destination, int width, int height)
    {
        double scaleX = (double)sourceWidth / width;
        double scaleY = (double)sourceHeight / height;
        int sourceStride = sourceWidth * 4;

        Parallel.For(0, height, y =>
        {
            int y0 = (int)(y * scaleY);
            int y1 = Math.Max(y0 + 1, Math.Min(sourceHeight, (int)((y + 1) * scaleY)));
            for (int x = 0; x < width; x++)
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
        });
    }
}
