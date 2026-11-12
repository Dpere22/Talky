using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Talky.Models;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Talky.Services.ScreenShare;

/// <summary>
/// LAN-optimised screen codec. Frames are split into a 128 px grid; only tiles that changed since the
/// previous frame are sent, merged into horizontal strips and JPEG-encoded in parallel with WIC.
/// When the caller knows which areas were redrawn (capture dirty regions), only tiles touching them are compared.
/// When most of the screen is moving (video, games) strips use a lighter "motion" quality, and once
/// those tiles settle they are re-sent at full quality, so static text ends up sharp.
///
/// Payload: <c>u8 version | u16 width | u16 height | u16 rectCount | { u16 x, y, w, h | i32 len | jpeg }*</c>
/// </summary>
public sealed class TileFrameEncoder
{
    public const int TileSize = 128;
    private const byte Version = 1;
    private const int RefineAfterStableFrames = 3;
    private const double MotionThreshold = 0.25;

    private readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _stripPool = new();
    private byte[]? _previous;
    private TileState[] _tiles = [];
    private int _width;
    private int _height;
    private int _tilesX;
    private int _tilesY;

    public ScreenShareQuality Quality { get; set; } = ScreenShareQuality.Maximum;

    /// <summary>
    /// True while some tiles were last sent at motion quality. Capture only produces frames when
    /// something changes, so the caller should re-submit the last frame to let them refine.
    /// </summary>
    public bool HasPendingRefinement { get; private set; }

    /// <summary>Forces the next frame to be sent in full (e.g. after the viewer reset).</summary>
    public void Invalidate() => _previous = null;

    /// <summary>
    /// Encodes the changes in <paramref name="frame"/> (tightly packed BGRA, stride = width * 4).
    /// <paramref name="changedRegions"/> are the areas that may differ from the previous frame passed to this
    /// method (null = unknown); tiles outside them are assumed unchanged and not compared.
    /// Returns false when nothing needs sending.
    /// </summary>
    public async Task<bool> EncodeAsync(byte[] frame, int width, int height, IReadOnlyList<RectInt32>? changedRegions, ArrayBufferWriter<byte> output)
    {
        output.ResetWrittenCount();
        bool full = _previous is null || width != _width || height != _height;
        if (full)
        {
            Reset(width, height);
        }

        var previous = _previous!;
        int total = _tilesX * _tilesY;
        var changed = new bool[total];

        if (full)
        {
            Array.Fill(changed, true);
        }
        else if (changedRegions is null)
        {
            Parallel.For(0, total, i => changed[i] = !TileEquals(frame, previous, i));
        }
        else
        {
            // Still compared exactly: a redrawn area often ends up with the same pixels.
            var candidates = TilesTouching(changedRegions);
            Parallel.For(0, candidates.Count, k => changed[candidates[k]] = !TileEquals(frame, previous, candidates[k]));
        }

        int changedCount = changed.Count(c => c);
        bool motion = !full && changedCount > total * MotionThreshold;

        // Decide what to send and at which quality.
        var send = new sbyte[total]; // 0 = skip, 1 = motion quality, 2 = full quality
        bool pendingRefinement = false;
        for (int i = 0; i < total; i++)
        {
            ref var state = ref _tiles[i];
            if (changed[i])
            {
                state.Stable = 0;
                state.SentHigh = !motion;
                send[i] = (sbyte)(motion ? 1 : 2);
            }
            else
            {
                state.Stable++;
                if (!state.SentHigh && state.Stable >= RefineAfterStableFrames)
                {
                    state.SentHigh = true;
                    send[i] = 2;
                }
            }

            pendingRefinement |= !state.SentHigh;
        }

        HasPendingRefinement = pendingRefinement;

        // Merge horizontal runs with equal quality into strips.
        var rects = new List<(int X, int Y, int W, int H, bool High)>();
        for (int ty = 0; ty < _tilesY; ty++)
        {
            int tx = 0;
            while (tx < _tilesX)
            {
                var level = send[ty * _tilesX + tx];
                if (level == 0)
                {
                    tx++;
                    continue;
                }

                int start = tx;
                while (tx < _tilesX && send[ty * _tilesX + tx] == level)
                {
                    tx++;
                }

                int x = start * TileSize, y = ty * TileSize;
                rects.Add((x, y, Math.Min(tx * TileSize, width) - x, Math.Min(TileSize, height - y), level == 2));
            }
        }

        if (full)
        {
            System.Buffer.BlockCopy(frame, 0, previous, 0, width * height * 4);
        }
        else
        {
            // Unchanged tiles are already identical.
            for (int i = 0; i < total; i++)
            {
                if (changed[i])
                {
                    CopyTile(frame, previous, i);
                }
            }
        }

        if (rects.Count == 0)
        {
            return false;
        }

        var encoded = await Task.WhenAll(rects.Select(r => Task.Run(() => EncodeRectAsync(frame, width, r.X, r.Y, r.W, r.H, r.High)))).ConfigureAwait(false);

        WriteHeader(output, width, height, rects.Count);
        for (int i = 0; i < rects.Count; i++)
        {
            var (x, y, w, h, _) = rects[i];
            var span = output.GetSpan(12);
            BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)x);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)y);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)h);
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], encoded[i].Length);
            output.Advance(12);
            output.Write(encoded[i]);
        }

        return true;
    }

    private void Reset(int width, int height)
    {
        _width = width;
        _height = height;
        _tilesX = (width + TileSize - 1) / TileSize;
        _tilesY = (height + TileSize - 1) / TileSize;
        _tiles = new TileState[_tilesX * _tilesY];
        _previous = new byte[width * height * 4];
    }

    private (int X, int Y, int W, int H) TileBounds(int index)
    {
        int x = index % _tilesX * TileSize, y = index / _tilesX * TileSize;
        return (x, y, Math.Min(TileSize, _width - x), Math.Min(TileSize, _height - y));
    }

    private bool TileEquals(byte[] current, byte[] previous, int index)
    {
        var (x, y, w, h) = TileBounds(index);
        int stride = _width * 4;
        for (int row = 0; row < h; row++)
        {
            int offset = (y + row) * stride + x * 4;
            if (!current.AsSpan(offset, w * 4).SequenceEqual(previous.AsSpan(offset, w * 4)))
            {
                return false;
            }
        }

        return true;
    }

    private void CopyTile(byte[] source, byte[] destination, int index)
    {
        var (x, y, w, h) = TileBounds(index);
        int stride = _width * 4;
        for (int row = 0; row < h; row++)
        {
            int offset = (y + row) * stride + x * 4;
            System.Buffer.BlockCopy(source, offset, destination, offset, w * 4);
        }
    }

    /// <summary>Indices of the tiles intersecting <paramref name="regions"/>, each once.</summary>
    private List<int> TilesTouching(IReadOnlyList<RectInt32> regions)
    {
        var marked = new bool[_tilesX * _tilesY];
        var result = new List<int>();
        foreach (var r in regions)
        {
            int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
            int x1 = Math.Min(_width, r.X + r.Width), y1 = Math.Min(_height, r.Y + r.Height);
            if (x1 <= x0 || y1 <= y0)
            {
                continue;
            }

            for (int ty = y0 / TileSize; ty <= (y1 - 1) / TileSize; ty++)
            {
                for (int tx = x0 / TileSize; tx <= (x1 - 1) / TileSize; tx++)
                {
                    int i = ty * _tilesX + tx;
                    if (!marked[i])
                    {
                        marked[i] = true;
                        result.Add(i);
                    }
                }
            }
        }

        return result;
    }

    private async Task<byte[]> EncodeRectAsync(byte[] frame, int frameWidth, int x, int y, int w, int h, bool high)
    {
        int length = w * h * 4;
        var bag = _stripPool.GetOrAdd(length, _ => []);
        if (!bag.TryTake(out var pixels))
        {
            pixels = new byte[length];
        }

        try
        {
            int stride = frameWidth * 4;
            for (int row = 0; row < h; row++)
            {
                System.Buffer.BlockCopy(frame, (y + row) * stride + x * 4, pixels, row * w * 4, w * 4);
            }

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, CreateOptions(high));
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, pixels);
            await encoder.FlushAsync();

            var result = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(result.AsBuffer(), (uint)result.Length, InputStreamOptions.None);
            return result;
        }
        finally
        {
            bag.Add(pixels);
        }
    }

    private BitmapPropertySet CreateOptions(bool high)
    {
        var (quality, subsampling) = (Quality, high) switch
        {
            (ScreenShareQuality.Maximum, true) => (0.93f, Subsampling444),
            (ScreenShareQuality.Maximum, false) => (0.80f, Subsampling420),
            (ScreenShareQuality.Balanced, true) => (0.85f, Subsampling420),
            _ => (0.65f, Subsampling420),
        };

        return new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(quality, PropertyType.Single) },
            { "JpegYCrCbSubsampling", new BitmapTypedValue(subsampling, PropertyType.UInt8) },
        };
    }

    // WICJpegYCrCbSubsamplingOption values.
    private const byte Subsampling420 = 1;
    private const byte Subsampling444 = 3;

    private static void WriteHeader(ArrayBufferWriter<byte> output, int width, int height, int count)
    {
        var span = output.GetSpan(7);
        span[0] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(span[1..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[3..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[5..], (ushort)count);
        output.Advance(7);
    }

    private struct TileState
    {
        public int Stable;
        public bool SentHigh;
    }
}

/// <summary>Reassembles tile frames into a BGRA framebuffer for display.</summary>
public sealed class TileFrameDecoder
{
    private readonly object _gate = new();
    private byte[] _frame = [];

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Increments every time a frame was applied.</summary>
    public long FrameNumber { get; private set; }

    public void Reset()
    {
        lock (_gate)
        {
            _frame = [];
            Width = Height = 0;
            FrameNumber = 0;
        }
    }

    public async Task<bool> ApplyAsync(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 7 || span[0] != 1)
        {
            return false;
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(span[1..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(span[3..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[5..]);
        if (width == 0 || height == 0)
        {
            return false;
        }

        var rects = new List<(int X, int Y, int W, int H, ReadOnlyMemory<byte> Data)>(count);
        int offset = 7;
        for (int i = 0; i < count; i++)
        {
            if (offset + 12 > span.Length)
            {
                return false;
            }

            int x = BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]);
            int y = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 2)..]);
            int w = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 4)..]);
            int h = BinaryPrimitives.ReadUInt16LittleEndian(span[(offset + 6)..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 8)..]);
            offset += 12;
            if (length < 0 || offset + length > span.Length || x + w > width || y + h > height)
            {
                return false;
            }

            rects.Add((x, y, w, h, payload.Slice(offset, length)));
            offset += length;
        }

        lock (_gate)
        {
            if (width != Width || height != Height)
            {
                Width = width;
                Height = height;
                _frame = new byte[width * height * 4];
            }
        }

        var decoded = await Task.WhenAll(rects.Select(r => Task.Run(() => DecodeAsync(r.Data)))).ConfigureAwait(false);

        lock (_gate)
        {
            if (width != Width || height != Height)
            {
                return false;
            }

            int stride = width * 4;
            for (int i = 0; i < rects.Count; i++)
            {
                var (x, y, w, h, _) = rects[i];
                var pixels = decoded[i];
                if (pixels is null || pixels.Length < w * h * 4)
                {
                    continue;
                }

                for (int row = 0; row < h; row++)
                {
                    System.Buffer.BlockCopy(pixels, row * w * 4, _frame, (y + row) * stride + x * 4, w * 4);
                }
            }

            FrameNumber++;
        }

        return true;
    }

    /// <summary>Copies the current framebuffer into <paramref name="destination"/> if it is large enough.</summary>
    public bool CopyTo(Span<byte> destination, out int width, out int height)
    {
        lock (_gate)
        {
            width = Width;
            height = Height;
            if (_frame.Length == 0 || destination.Length < _frame.Length)
            {
                return false;
            }

            _frame.AsSpan().CopyTo(destination);
            return true;
        }
    }

    private static async Task<byte[]?> DecodeAsync(ReadOnlyMemory<byte> jpeg)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            if (MemoryMarshal.TryGetArray(jpeg, out var segment) && segment.Array is not null)
            {
                await stream.WriteAsync(segment.Array.AsBuffer(segment.Offset, segment.Count));
            }
            else
            {
                await stream.WriteAsync(jpeg.ToArray().AsBuffer());
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, stream);
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            return data.DetachPixelData();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Screen] Tile decode failed: {ex.Message}");
            return null;
        }
    }
}
