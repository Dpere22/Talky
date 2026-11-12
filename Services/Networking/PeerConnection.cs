using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Talky.Protocol;

namespace Talky.Services.Networking;

/// <summary>
/// A single framed TCP connection to a peer. Frames: <c>[uint32 LE length][byte type][payload]</c>.
/// Writes go through one writer loop with two queues: control packets always go before bulk
/// (screen frames), so chat and signalling stay responsive while a share saturates the link.
/// </summary>
internal sealed class PeerConnection
{
    public const int MaxFrameSize = 64 * 1024 * 1024;

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly ConcurrentQueue<Outgoing> _control = new();
    private readonly ConcurrentQueue<Outgoing> _bulk = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _readHeader = new byte[5];
    private readonly byte[] _writeHeader = new byte[5];
    private long _lastReceivedTimestamp = Stopwatch.GetTimestamp();
    private int _closed;
    private Action<PeerConnection>? _onClosed;

    public PeerConnection(Socket socket, bool isOutbound)
    {
        _socket = socket;
        _socket.NoDelay = true;
        _stream = new NetworkStream(socket, ownsSocket: true);
        IsOutbound = isOutbound;
        RemoteAddress = NetworkInfo.Normalize(((IPEndPoint)socket.RemoteEndPoint!).Address);
    }

    public bool IsOutbound { get; }

    public IPAddress RemoteAddress { get; }

    public HelloPayload? RemoteHello { get; private set; }

    /// <summary>PeerId of whoever opened this connection; used to deterministically resolve duplicate connections.</summary>
    public Guid InitiatorPeerId { get; set; }

    public Guid FriendId { get; set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Sends our Hello and waits for the peer's. Must be called before <see cref="Start"/>.</summary>
    public async Task<HelloPayload?> HandshakeAsync(HelloPayload local, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        await WriteFrameAsync(PacketType.Hello, Payload.Serialize(local), timeout.Token).ConfigureAwait(false);
        var (type, buffer, length) = await ReadFrameAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (type != PacketType.Hello)
            {
                return null;
            }

            RemoteHello = Payload.Deserialize<HelloPayload>(buffer.AsSpan(0, length));
            return RemoteHello;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Start(Func<PeerConnection, PacketType, ReadOnlyMemory<byte>, ValueTask> onPacket, Action<PeerConnection> onClosed)
    {
        _onClosed = onClosed;
        _ = Task.Run(() => ReadLoopAsync(onPacket));
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>Queues a control packet. Returns false if the connection is closed.</summary>
    public bool Enqueue(PacketType type, byte[]? payload)
    {
        if (IsClosed)
        {
            return false;
        }

        _control.Enqueue(new Outgoing(type, payload ?? [], payload?.Length ?? 0, null));
        _signal.Release();
        return true;
    }

    /// <summary>Queues a bulk packet; the task completes once it has been written to the socket.</summary>
    public Task<bool> EnqueueBulk(PacketType type, byte[] payload, int length)
    {
        if (IsClosed)
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _bulk.Enqueue(new Outgoing(type, payload, length, completion));
        _signal.Release();
        if (IsClosed)
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        _stream.Dispose();
        _signal.Release();

        while (_bulk.TryDequeue(out var pending))
        {
            pending.Completion?.TrySetResult(false);
        }

        _onClosed?.Invoke(this);
    }

    private async Task ReadLoopAsync(Func<PeerConnection, PacketType, ReadOnlyMemory<byte>, ValueTask> onPacket)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (type, buffer, length) = await ReadFrameAsync(_cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _lastReceivedTimestamp, Stopwatch.GetTimestamp());
                try
                {
                    switch (type)
                    {
                        case PacketType.Ping:
                            Enqueue(PacketType.Pong, null);
                            break;
                        case PacketType.Pong:
                            break;
                        default:
                            if (type == PacketType.Hello)
                            {
                                RemoteHello = Payload.Deserialize<HelloPayload>(buffer.AsSpan(0, length)) ?? RemoteHello;
                            }

                            // Handlers run inline: a slow consumer (e.g. screen decode) applies TCP back-pressure to the sender.
                            await onPacket(this, type, buffer.AsMemory(0, length)).ConfigureAwait(false);
                            break;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Peer] Read loop crashed: {ex}");
        }
        finally
        {
            Close();
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                bool signaled = await _signal.WaitAsync(PingInterval, _cts.Token).ConfigureAwait(false);

                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastReceivedTimestamp)) > ReceiveTimeout)
                {
                    Debug.WriteLine($"[Peer] {RemoteAddress} timed out");
                    break;
                }

                if (!signaled)
                {
                    await WriteFrameAsync(PacketType.Ping, ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                // Drain everything; control packets always win over bulk.
                while (!_cts.IsCancellationRequested)
                {
                    if (_control.TryDequeue(out var packet) || _bulk.TryDequeue(out packet))
                    {
                        try
                        {
                            await WriteFrameAsync(packet.Type, packet.Payload.AsMemory(0, packet.Length), _cts.Token).ConfigureAwait(false);
                            packet.Completion?.TrySetResult(true);
                        }
                        catch
                        {
                            packet.Completion?.TrySetResult(false);
                            throw;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Peer] Write loop crashed: {ex}");
        }
        finally
        {
            Close();
        }
    }

    private async ValueTask WriteFrameAsync(PacketType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_writeHeader, (uint)(payload.Length + 1));
        _writeHeader[4] = (byte)type;

        if (payload.Length <= 8192)
        {
            // Coalesce header + small payload into one segment.
            var combined = ArrayPool<byte>.Shared.Rent(payload.Length + 5);
            try
            {
                _writeHeader.CopyTo(combined, 0);
                payload.Span.CopyTo(combined.AsSpan(5));
                await _stream.WriteAsync(combined.AsMemory(0, payload.Length + 5), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(combined);
            }
        }
        else
        {
            await _stream.WriteAsync(_writeHeader, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one frame into a pooled buffer which the caller must return.</summary>
    private async ValueTask<(PacketType Type, byte[] Buffer, int Length)> ReadFrameAsync(CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_readHeader, cancellationToken).ConfigureAwait(false);
        uint frameLength = BinaryPrimitives.ReadUInt32LittleEndian(_readHeader);
        if (frameLength is < 1 or > MaxFrameSize)
        {
            throw new InvalidDataException($"Invalid frame length {frameLength}");
        }

        var type = (PacketType)_readHeader[4];
        int length = (int)frameLength - 1;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            await _stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return (type, buffer, length);
    }

    private readonly record struct Outgoing(PacketType Type, byte[] Payload, int Length, TaskCompletionSource<bool>? Completion);
}
