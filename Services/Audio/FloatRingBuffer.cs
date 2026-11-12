namespace Talky.Services.Audio;

/// <summary>Bounded single-producer/single-consumer sample FIFO. Overflow drops the oldest samples.</summary>
public sealed class FloatRingBuffer(int capacity)
{
    private readonly object _gate = new();
    private readonly float[] _buffer = new float[capacity];
    private int _read;
    private int _count;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Write(ReadOnlySpan<float> data)
    {
        lock (_gate)
        {
            if (data.Length >= _buffer.Length)
            {
                data[^_buffer.Length..].CopyTo(_buffer);
                _read = 0;
                _count = _buffer.Length;
                return;
            }

            int overflow = _count + data.Length - _buffer.Length;
            if (overflow > 0)
            {
                _read = (_read + overflow) % _buffer.Length;
                _count -= overflow;
            }

            int write = (_read + _count) % _buffer.Length;
            int first = Math.Min(data.Length, _buffer.Length - write);
            data[..first].CopyTo(_buffer.AsSpan(write));
            data[first..].CopyTo(_buffer);
            _count += data.Length;
        }
    }

    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            int n = Math.Min(destination.Length, _count);
            int first = Math.Min(n, _buffer.Length - _read);
            _buffer.AsSpan(_read, first).CopyTo(destination);
            _buffer.AsSpan(0, n - first).CopyTo(destination[first..]);
            _read = (_read + n) % _buffer.Length;
            _count -= n;
            return n;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = 0;
            _count = 0;
        }
    }
}
