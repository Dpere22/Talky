using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Talky.Models;
using Talky.Protocol;
using Talky.Services.Storage;

namespace Talky.Services.Networking;

/// <summary>Consumes packets for a subset of <see cref="PacketType"/>s.</summary>
public interface IPacketHandler
{
    bool CanHandle(PacketType type);

    /// <summary>
    /// Called on the connection's read loop. <paramref name="payload"/> is only valid until the returned task
    /// completes; copy it if it must outlive the call.
    /// </summary>
    ValueTask HandlePacketAsync(Guid friendId, PacketType type, ReadOnlyMemory<byte> payload);
}

public sealed class PeerStatusChangedEventArgs(Guid friendId, bool isOnline) : EventArgs
{
    public Guid FriendId { get; } = friendId;

    public bool IsOnline { get; } = isOnline;
}

public interface IPeerNetwork
{
    int ListenPort { get; }

    bool IsListening { get; }

    string? ListenError { get; }

    /// <summary>Raised from a background thread.</summary>
    event EventHandler<PeerStatusChangedEventArgs>? PeerStatusChanged;

    void RegisterHandler(IPacketHandler handler);

    void Start();

    void Stop();

    bool IsOnline(Guid friendId);

    IPAddress? GetRemoteAddress(Guid friendId);

    /// <summary>Queues a control packet. Returns false if the friend is not connected.</summary>
    bool Send(Guid friendId, PacketType type, byte[]? payload = null);

    bool Send<T>(Guid friendId, PacketType type, T payload);

    /// <summary>Queues a low-priority packet and completes once it has been written (for back-pressure).</summary>
    Task<bool> SendBulkAsync(Guid friendId, PacketType type, byte[] payload, int length);

    /// <summary>Skips the reconnect back-off and tries to reach the friend right away.</summary>
    void ConnectNow(Guid friendId);
}

/// <summary>
/// Keeps one TCP connection per online friend. Both sides dial each other, so when two connections
/// race, both peers keep the one opened by the smaller PeerId (deterministic on both ends).
/// </summary>
public sealed class PeerNetworkService : IPeerNetwork, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private readonly ISettingsService _settings;
    private readonly IFriendService _friends;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PeerConnection> _active = [];
    private readonly HashSet<Guid> _connecting = [];
    private readonly Dictionary<Guid, Backoff> _backoff = [];
    private readonly Dictionary<Guid, (string Host, int Port)> _knownEndpoints = [];
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTime Expires)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _matchLock = new(1, 1);
    private readonly SemaphoreSlim _kick = new(0);
    private readonly List<IPacketHandler> _handlers = [];
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private string? _announcedName;

    public PeerNetworkService(ISettingsService settings, IFriendService friends)
    {
        _settings = settings;
        _friends = friends;
    }

    public int ListenPort { get; private set; }

    public bool IsListening { get; private set; }

    public string? ListenError { get; private set; }

    public event EventHandler<PeerStatusChangedEventArgs>? PeerStatusChanged;

    public void RegisterHandler(IPacketHandler handler)
    {
        lock (_handlers)
        {
            _handlers.Add(handler);
        }
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        ListenPort = _settings.Current.Port;
        _announcedName = _settings.Current.DisplayName;

        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, ListenPort);
            _listener.Server.DualMode = true;
            _listener.Start(backlog: 32);
            IsListening = true;
            _ = AcceptLoopAsync(_listener, _cts.Token);
        }
        catch (SocketException ex)
        {
            IsListening = false;
            ListenError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {ListenPort} is already in use. Pick another port in Settings."
                : $"Could not listen on port {ListenPort}: {ex.Message}";
            Debug.WriteLine($"[Net] {ListenError}");
        }

        foreach (var friend in _friends.GetAll())
        {
            _knownEndpoints[friend.Id] = (friend.Host, friend.Port);
        }

        _friends.Changed += OnFriendsChanged;
        _settings.Changed += OnSettingsChanged;
        _ = ConnectLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _friends.Changed -= OnFriendsChanged;
        _settings.Changed -= OnSettingsChanged;
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }

        PeerConnection[] connections;
        lock (_gate)
        {
            connections = _active.Values.ToArray();
            _active.Clear();
        }

        foreach (var connection in connections)
        {
            connection.Close();
        }
    }

    public void Dispose() => Stop();

    public bool IsOnline(Guid friendId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(friendId, out var c) && !c.IsClosed;
        }
    }

    public IPAddress? GetRemoteAddress(Guid friendId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(friendId, out var c) ? c.RemoteAddress : null;
        }
    }

    public bool Send(Guid friendId, PacketType type, byte[]? payload = null) =>
        TryGetConnection(friendId)?.Enqueue(type, payload) ?? false;

    public bool Send<T>(Guid friendId, PacketType type, T payload) =>
        Send(friendId, type, Payload.Serialize(payload));

    public Task<bool> SendBulkAsync(Guid friendId, PacketType type, byte[] payload, int length) =>
        TryGetConnection(friendId)?.EnqueueBulk(type, payload, length) ?? Task.FromResult(false);

    public void ConnectNow(Guid friendId)
    {
        lock (_gate)
        {
            _backoff.Remove(friendId);
        }

        _kick.Release();
    }

    private PeerConnection? TryGetConnection(Guid friendId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(friendId, out var c) && !c.IsClosed ? c : null;
        }
    }

    private HelloPayload LocalHello() =>
        new(_settings.Current.PeerId, _settings.Current.DisplayName, ListenPort, NetworkDefaults.ProtocolVersion, AppVersion);

    // ---------------------------------------------------------------- inbound

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"[Net] Accept failed: {ex.Message}");
                continue;
            }

            _ = HandleInboundAsync(socket, cancellationToken);
        }
    }

    private async Task HandleInboundAsync(Socket socket, CancellationToken cancellationToken)
    {
        PeerConnection? connection = null;
        try
        {
            connection = new PeerConnection(socket, isOutbound: false);
            var hello = await connection.HandshakeAsync(LocalHello(), cancellationToken).ConfigureAwait(false);
            if (hello is null || hello.PeerId == _settings.Current.PeerId)
            {
                connection.Close();
                return;
            }

            var friend = await MatchOrCreateFriendAsync(connection.RemoteAddress, hello).ConfigureAwait(false);
            if (friend is null)
            {
                connection.Close();
                return;
            }

            connection.InitiatorPeerId = hello.PeerId;
            Register(connection, friend, hello);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
            if (connection is not null)
            {
                connection.Close();
            }
            else
            {
                socket.Dispose();
            }
        }
    }

    private async Task<Friend?> MatchOrCreateFriendAsync(IPAddress remote, HelloPayload hello)
    {
        await _matchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var all = _friends.GetAll();

            var byId = all.FirstOrDefault(f => f.PeerId == hello.PeerId);
            if (byId is not null)
            {
                // Same person, new IP (DHCP): follow them if the friend was stored as a literal IP.
                if (IPAddress.TryParse(byId.Host, out var stored) && !NetworkInfo.Normalize(stored).Equals(remote))
                {
                    _friends.Update(byId.Id, f => f.Host = remote.ToString());
                }

                return byId;
            }

            var byAddress = new List<Friend>();
            foreach (var friend in all)
            {
                if (friend.PeerId is not null)
                {
                    // Already bound to a different identity (byId didn't match): never hijack it.
                    continue;
                }

                var addresses = await ResolveAsync(friend.Host).ConfigureAwait(false);
                if (addresses.Contains(remote))
                {
                    byAddress.Add(friend);
                }
            }

            var match = byAddress.FirstOrDefault(f => f.Port == hello.ListenPort)
                ?? (byAddress.Count == 1 ? byAddress[0] : null);
            if (match is not null)
            {
                return match;
            }

            // Unknown peer: add as a pending friend that the user can accept or remove.
            var nickname = string.IsNullOrWhiteSpace(hello.DisplayName) ? remote.ToString() : hello.DisplayName.Trim();
            if (nickname.Length > 64)
            {
                nickname = nickname[..64];
            }

            var created = _friends.Add(nickname, remote.ToString(), hello.ListenPort, isPending: true);
            lock (_gate)
            {
                _knownEndpoints[created.Id] = (created.Host, created.Port);
            }

            return created;
        }
        finally
        {
            _matchLock.Release();
        }
    }

    // ---------------------------------------------------------------- outbound

    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            foreach (var friend in _friends.GetAll())
            {
                lock (_gate)
                {
                    if ((_active.TryGetValue(friend.Id, out var c) && !c.IsClosed) || _connecting.Contains(friend.Id))
                    {
                        continue;
                    }

                    if (_backoff.TryGetValue(friend.Id, out var backoff) && backoff.NextAttempt > now)
                    {
                        continue;
                    }

                    _connecting.Add(friend.Id);
                }

                _ = ConnectAsync(friend, cancellationToken);
            }

            try
            {
                await _kick.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAsync(Friend friend, CancellationToken cancellationToken)
    {
        bool success = false;
        try
        {
            var addresses = await ResolveAsync(friend.Host).ConfigureAwait(false);
            foreach (var address in addresses)
            {
                if (await TryConnectAsync(friend, address, cancellationToken).ConfigureAwait(false))
                {
                    success = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Net] Connect to {friend.Address} failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _connecting.Remove(friend.Id);
                if (success)
                {
                    _backoff.Remove(friend.Id);
                }
                else
                {
                    var failures = _backoff.TryGetValue(friend.Id, out var b) ? b.Failures + 1 : 1;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(1.6, failures)));
                    _backoff[friend.Id] = new Backoff(failures, DateTime.UtcNow + delay);
                }
            }
        }
    }

    private async Task<bool> TryConnectAsync(Friend friend, IPAddress address, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        PeerConnection? connection = null;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(new IPEndPoint(address, friend.Port), timeout.Token).ConfigureAwait(false);
            }

            connection = new PeerConnection(socket, isOutbound: true);
            var hello = await connection.HandshakeAsync(LocalHello(), cancellationToken).ConfigureAwait(false);
            if (hello is null || hello.PeerId == _settings.Current.PeerId)
            {
                connection.Close();
                return false;
            }

            connection.InitiatorPeerId = _settings.Current.PeerId;
            Register(connection, friend, hello);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
            if (connection is not null)
            {
                connection.Close();
            }
            else
            {
                socket.Dispose();
            }

            return false;
        }
    }

    // ---------------------------------------------------------------- registry

    private void Register(PeerConnection connection, Friend friend, HelloPayload hello)
    {
        if (friend.PeerId != hello.PeerId || friend.RemoteDisplayName != hello.DisplayName)
        {
            _friends.Update(friend.Id, f =>
            {
                f.PeerId = hello.PeerId;
                f.RemoteDisplayName = hello.DisplayName;
            });
        }

        connection.FriendId = friend.Id;
        PeerConnection? loser = null;
        bool becameOnline = false;

        lock (_gate)
        {
            if (_active.TryGetValue(friend.Id, out var existing) && !existing.IsClosed)
            {
                if (ShouldKeepExisting(existing, connection))
                {
                    loser = connection;
                }
                else
                {
                    loser = existing;
                    _active[friend.Id] = connection;
                }
            }
            else
            {
                _active[friend.Id] = connection;
                becameOnline = true;
            }
        }

        if (loser == connection)
        {
            connection.Close();
            return;
        }

        connection.Start(OnPacketAsync, OnConnectionClosed);
        loser?.Close();

        Debug.WriteLine($"[Net] Connected to {friend.Nickname} at {connection.RemoteAddress} ({(connection.IsOutbound ? "out" : "in")})");
        if (becameOnline)
        {
            PeerStatusChanged?.Invoke(this, new PeerStatusChangedEventArgs(friend.Id, true));
        }
    }

    /// <summary>Both peers evaluate this identically: keep the connection opened by the smaller PeerId.</summary>
    private static bool ShouldKeepExisting(PeerConnection existing, PeerConnection candidate)
    {
        int comparison = existing.InitiatorPeerId.CompareTo(candidate.InitiatorPeerId);
        return comparison != 0 ? comparison < 0 : false;
    }

    private void OnConnectionClosed(PeerConnection connection)
    {
        bool wentOffline = false;
        lock (_gate)
        {
            if (_active.TryGetValue(connection.FriendId, out var current) && current == connection)
            {
                _active.Remove(connection.FriendId);
                _backoff.Remove(connection.FriendId);
                wentOffline = true;
            }
        }

        if (wentOffline)
        {
            Debug.WriteLine($"[Net] Disconnected from {connection.RemoteAddress}");
            PeerStatusChanged?.Invoke(this, new PeerStatusChangedEventArgs(connection.FriendId, false));
            _kick.Release();
        }
    }

    private async ValueTask OnPacketAsync(PeerConnection connection, PacketType type, ReadOnlyMemory<byte> payload)
    {
        if (type == PacketType.Hello)
        {
            // Mid-session Hello = the peer changed its display name.
            if (connection.RemoteHello is { } hello)
            {
                _friends.Update(connection.FriendId, f => f.RemoteDisplayName = hello.DisplayName);
            }

            return;
        }

        IPacketHandler[] handlers;
        lock (_handlers)
        {
            handlers = _handlers.ToArray();
        }

        foreach (var handler in handlers)
        {
            if (!handler.CanHandle(type))
            {
                continue;
            }

            try
            {
                await handler.HandlePacketAsync(connection.FriendId, type, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Net] Handler {handler.GetType().Name} failed on {type}: {ex}");
            }
        }
    }

    // ---------------------------------------------------------------- change tracking

    private void OnFriendsChanged(object? sender, FriendChangedEventArgs e)
    {
        PeerConnection? toClose = null;
        lock (_gate)
        {
            switch (e.Kind)
            {
                case FriendChangeKind.Added:
                    _knownEndpoints[e.Friend.Id] = (e.Friend.Host, e.Friend.Port);
                    _backoff.Remove(e.Friend.Id);
                    break;

                case FriendChangeKind.Removed:
                    _knownEndpoints.Remove(e.Friend.Id);
                    _backoff.Remove(e.Friend.Id);
                    if (_active.Remove(e.Friend.Id, out var removed))
                    {
                        toClose = removed;
                    }

                    break;

                case FriendChangeKind.Updated:
                    var endpoint = (e.Friend.Host, e.Friend.Port);
                    if (_knownEndpoints.TryGetValue(e.Friend.Id, out var previous) && previous != endpoint)
                    {
                        // Address edited by the user: drop the old link and dial the new address.
                        _backoff.Remove(e.Friend.Id);
                        if (_active.TryGetValue(e.Friend.Id, out var stale) && !IsSameHost(stale.RemoteAddress, e.Friend.Host))
                        {
                            toClose = stale;
                        }
                    }

                    _knownEndpoints[e.Friend.Id] = endpoint;
                    break;
            }
        }

        if (toClose is not null)
        {
            toClose.Close();
            if (e.Kind == FriendChangeKind.Removed)
            {
                PeerStatusChanged?.Invoke(this, new PeerStatusChangedEventArgs(e.Friend.Id, false));
            }
        }

        _kick.Release();
    }

    private static bool IsSameHost(IPAddress connected, string host) =>
        IPAddress.TryParse(host, out var parsed) && NetworkInfo.Normalize(parsed).Equals(connected);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Broadcast our new display name, if it changed.
        var name = _settings.Current.DisplayName;
        if (name == _announcedName)
        {
            return;
        }

        _announcedName = name;
        var hello = Payload.Serialize(LocalHello());
        PeerConnection[] connections;
        lock (_gate)
        {
            connections = _active.Values.ToArray();
        }

        foreach (var connection in connections)
        {
            connection.Enqueue(PacketType.Hello, hello);
        }
    }

    private async Task<IPAddress[]> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [NetworkInfo.Normalize(literal)];
        }

        if (_dnsCache.TryGetValue(host, out var cached) && cached.Expires > DateTime.UtcNow)
        {
            return cached.Addresses;
        }

        try
        {
            var addresses = (await Dns.GetHostAddressesAsync(host).ConfigureAwait(false))
                .Select(NetworkInfo.Normalize)
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ToArray();
            _dnsCache[host] = (addresses, DateTime.UtcNow.AddMinutes(1));
            return addresses;
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private readonly record struct Backoff(int Failures, DateTime NextAttempt);
}
