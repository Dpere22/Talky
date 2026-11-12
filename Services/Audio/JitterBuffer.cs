namespace Talky.Services.Audio;

/// <summary>
/// Re-orders incoming voice packets and hides network jitter. Starts playing once
/// <see cref="TargetFrames"/> packets are queued, conceals gaps (PLC/FEC), trims excess latency,
/// and re-buffers after the stream goes quiet (e.g. the peer muted).
/// </summary>
public sealed class JitterBuffer
{
    /// <summary>60 ms of buffering is plenty on a LAN.</summary>
    public const int TargetFrames = 3;

    private const int MaxFrames = 50;
    private const int TrimThreshold = TargetFrames + 6;
    private const int MaxConcealedInRow = 5;

    private readonly object _gate = new();
    private readonly SortedList<uint, byte[]> _packets = [];
    private uint _nextSequence;
    private bool _playing;
    private int _concealedInRow;

    public enum Result
    {
        /// <summary>Nothing to play: output silence.</summary>
        Silence,

        /// <summary><c>packet</c> holds the next frame.</summary>
        Packet,

        /// <summary>Frame missing: decode FEC from <c>fecSource</c> if non-null, else conceal.</summary>
        Lost,
    }

    public void Push(uint sequence, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if ((_playing && sequence < _nextSequence) || _packets.ContainsKey(sequence))
            {
                return;
            }

            _packets.Add(sequence, payload.ToArray());
            while (_packets.Count > MaxFrames)
            {
                _packets.RemoveAt(0);
            }
        }
    }

    public Result Next(out byte[]? packet, out byte[]? fecSource)
    {
        packet = null;
        fecSource = null;

        lock (_gate)
        {
            if (!_playing)
            {
                if (_packets.Count < TargetFrames)
                {
                    return Result.Silence;
                }

                _playing = true;
                _nextSequence = _packets.Keys[0];
                _concealedInRow = 0;
            }

            // Too much queued (clock drift / burst): skip ahead to keep latency low.
            if (_packets.Count > TrimThreshold)
            {
                while (_packets.Count > TargetFrames)
                {
                    _packets.RemoveAt(0);
                }

                _nextSequence = _packets.Keys[0];
            }

            while (_packets.Count > 0 && _packets.Keys[0] < _nextSequence)
            {
                _packets.RemoveAt(0);
            }

            if (_packets.Count > 0 && _packets.Keys[0] == _nextSequence)
            {
                packet = _packets.Values[0];
                _packets.RemoveAt(0);
                _nextSequence++;
                _concealedInRow = 0;
                return Result.Packet;
            }

            if (++_concealedInRow > MaxConcealedInRow)
            {
                _playing = false;
                return Result.Silence;
            }

            if (_packets.Count > 0 && _packets.Keys[0] == _nextSequence + 1)
            {
                fecSource = _packets.Values[0];
            }

            _nextSequence++;
            return Result.Lost;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _packets.Clear();
            _playing = false;
            _concealedInRow = 0;
        }
    }
}
