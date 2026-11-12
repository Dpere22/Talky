using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Talky.Services.Networking;

public delegate void DatagramHandler(IPAddress from, ReadOnlySpan<byte> datagram);

public interface IVoiceTransport
{
    /// <summary>Local UDP port (same number as the TCP port when available).</summary>
    int Port { get; }

    bool IsBound { get; }

    /// <summary>Single consumer (the active call). Invoked on the receive thread.</summary>
    DatagramHandler? Receiver { get; set; }

    void Start(int preferredPort);

    void Stop();

    void Send(IPEndPoint destination, ReadOnlySpan<byte> datagram);
}

/// <summary>Dual-stack UDP socket used for the latency-sensitive Opus voice stream.</summary>
public sealed class VoiceTransport : IVoiceTransport, IDisposable
{
    // Windows reports ICMP "port unreachable" as WSAECONNRESET on the next receive; turn that off.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private Socket? _socket;
    private Thread? _thread;

    public int Port { get; private set; }

    public bool IsBound => _socket is not null;

    public DatagramHandler? Receiver { get; set; }

    public void Start(int preferredPort)
    {
        if (_socket is not null)
        {
            return;
        }

        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            socket.ReceiveBufferSize = 1 << 20;
            socket.SendBufferSize = 1 << 20;
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, preferredPort));
            }
            catch (SocketException)
            {
                // Port taken: fall back to an ephemeral port (it is announced in the call handshake).
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            }
        }
        catch (SocketException ex)
        {
            Debug.WriteLine($"[Voice] UDP bind failed: {ex.Message}");
            socket.Dispose();
            return;
        }

        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _socket = socket;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Talky UDP receive", Priority = ThreadPriority.AboveNormal };
        _thread.Start(socket);
    }

    public void Stop()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        socket?.Dispose();
    }

    public void Dispose() => Stop();

    public void Send(IPEndPoint destination, ReadOnlySpan<byte> datagram)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        try
        {
            var target = destination.AddressFamily == AddressFamily.InterNetwork
                ? new IPEndPoint(destination.Address.MapToIPv6(), destination.Port)
                : destination;
            socket.SendTo(datagram, SocketFlags.None, target);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private void ReceiveLoop(object? state)
    {
        var socket = (Socket)state!;
        var buffer = new byte[2048];
        EndPoint remote = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (true)
        {
            int received;
            try
            {
                received = socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                Receiver?.Invoke(NetworkInfo.Normalize(((IPEndPoint)remote).Address), buffer.AsSpan(0, received));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Voice] Receiver failed: {ex.Message}");
            }
        }
    }
}
