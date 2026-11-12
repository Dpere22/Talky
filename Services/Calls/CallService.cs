using System.Diagnostics;
using System.Net;
using Talky.Models;
using Talky.Protocol;
using Talky.Services.Audio;
using Talky.Services.Chat;
using Talky.Services.Networking;
using Talky.Services.ScreenShare;
using Talky.Services.Storage;
using Talky.Services.UI;

namespace Talky.Services.Calls;

public interface ICallService
{
    /// <summary>The current call, or null. Immutable snapshot.</summary>
    CallInfo? Current { get; }

    /// <summary>Anything about the call or the local audio state changed. Any thread.</summary>
    event EventHandler? StateChanged;

    bool IsMuted { get; set; }

    bool IsDeafened { get; set; }

    bool NoiseSuppressionEnabled { get; set; }

    bool NoiseSuppressionAvailable { get; }

    /// <summary>Playback volume, 0..2.</summary>
    double OutputVolume { get; set; }

    bool IsLocalSpeaking { get; }

    bool IsRemoteSpeaking { get; }

    IRemoteScreen RemoteScreen { get; }

    string? SharingSourceName { get; }

    void Start();

    /// <summary>Rings the friend. Returns an error message, or null on success.</summary>
    Task<string?> StartCallAsync(Guid friendId);

    Task AcceptAsync();

    void Decline();

    void HangUp();

    Task<bool> StartScreenShareAsync();

    void StopScreenShare();
}

/// <summary>
/// 1:1 call state machine. Signalling rides the friend's TCP link; voice goes over UDP.
/// </summary>
public sealed class CallService : ICallService, IPacketHandler
{
    private static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(35);

    private readonly IPeerNetwork _network;
    private readonly IVoiceTransport _transport;
    private readonly IVoiceEngine _engine;
    private readonly IScreenShareService _screen;
    private readonly IChatService _chat;
    private readonly IFriendService _friends;
    private readonly ISettingsService _settings;
    private readonly ISoundEffects _sounds;
    private readonly INotificationService _notifications;
    private readonly object _gate = new();

    private CallInfo? _current;
    private int _pendingRemoteUdpPort;
    private CancellationTokenSource? _ringTimeout;
    private IPEndPoint? _remoteVoiceEndpoint;
    private uint _token;
    private uint _sequence;
    private bool _isMuted;
    private bool _isDeafened;
    private int _voiceSession;

    public CallService(
        IPeerNetwork network,
        IVoiceTransport transport,
        IVoiceEngine engine,
        IScreenShareService screen,
        IChatService chat,
        IFriendService friends,
        ISettingsService settings,
        ISoundEffects sounds,
        INotificationService notifications)
    {
        _network = network;
        _transport = transport;
        _engine = engine;
        _screen = screen;
        _chat = chat;
        _friends = friends;
        _settings = settings;
        _sounds = sounds;
        _notifications = notifications;
        network.RegisterHandler(this);
    }

    public event EventHandler? StateChanged;

    public CallInfo? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value)
            {
                return;
            }

            _isMuted = value;
            // Un-muting while deafened also un-deafens (same as Discord).
            if (!value && _isDeafened)
            {
                _isDeafened = false;
            }

            ApplyAudioState();
        }
    }

    public bool IsDeafened
    {
        get => _isDeafened;
        set
        {
            if (_isDeafened == value)
            {
                return;
            }

            _isDeafened = value;
            ApplyAudioState();
        }
    }

    public bool NoiseSuppressionEnabled
    {
        get => _engine.NoiseSuppressionEnabled;
        set
        {
            if (_engine.NoiseSuppressionEnabled == value)
            {
                return;
            }

            _engine.NoiseSuppressionEnabled = value;
            _settings.Update(s => s.NoiseSuppression = value);
            Raise();
        }
    }

    public bool NoiseSuppressionAvailable => _engine.NoiseSuppressionAvailable;

    public double OutputVolume
    {
        get => _engine.OutputVolume;
        set
        {
            _engine.OutputVolume = (float)Math.Clamp(value, 0, 2);
            _settings.Update(s => s.OutputVolume = _engine.OutputVolume);
        }
    }

    public bool IsLocalSpeaking => _engine.Mode == VoiceMode.Call && _engine.IsLocalSpeaking;

    public bool IsRemoteSpeaking => _engine.Mode == VoiceMode.Call && _engine.IsRemoteSpeaking;

    public IRemoteScreen RemoteScreen => _screen.RemoteScreen;

    public string? SharingSourceName => _screen.SharingSourceName;

    public void Start()
    {
        _transport.Start(_settings.Current.Port);
        _network.PeerStatusChanged += OnPeerStatusChanged;
        _screen.LocalSharingChanged += (_, _) => Update(c => c with { LocalSharing = _screen.IsSharing });
        _screen.RemoteScreen.StateChanged += (_, _) => Update(c => c with { RemoteSharing = _screen.RemoteScreen.IsActive });
        _engine.DeviceFailed += (_, message) => Update(c => c with { MediaWarning = message });
    }

    public Task<string?> StartCallAsync(Guid friendId) => Task.FromResult(StartCall(friendId));

    private string? StartCall(Guid friendId)
    {
        var friend = _friends.Get(friendId);
        if (friend is null)
        {
            return "That friend no longer exists.";
        }

        if (Current is not null)
        {
            return "You're already in a call.";
        }

        if (!_network.IsOnline(friendId))
        {
            _network.ConnectNow(friendId);
            return $"{friend.Nickname} is offline right now.";
        }

        var callId = Guid.NewGuid();
        lock (_gate)
        {
            if (_current is not null)
            {
                return "You're already in a call.";
            }

            _current = NewCall(callId, friendId, isOutgoing: true, CallPhase.OutgoingRinging);
        }

        if (!_network.Send(friendId, PacketType.CallInvite, new CallInvitePayload(callId, _transport.Port)))
        {
            EndCall(callId, notifyPeer: false, EndReason.Failed);
            return $"Couldn't reach {friend.Nickname}.";
        }

        _sounds.StartRingback();
        ArmRingTimeout(callId);
        Raise();
        return null;
    }

    public async Task AcceptAsync()
    {
        CallInfo call;
        int remotePort;
        lock (_gate)
        {
            if (_current is not { Phase: CallPhase.IncomingRinging } ringing)
            {
                return;
            }

            call = ringing with { Phase = CallPhase.Active, ConnectedAt = DateTimeOffset.Now };
            _current = call;
            remotePort = _pendingRemoteUdpPort;
        }

        CancelRingTimeout();
        _sounds.StopRinging();
        _notifications.ClearIncomingCall();
        _network.Send(call.FriendId, PacketType.CallAccept, new CallAcceptPayload(call.CallId, _transport.Port));
        Raise();
        await StartMediaAsync(call.CallId, call.FriendId, remotePort);
    }

    public void Decline()
    {
        CallInfo call;
        lock (_gate)
        {
            if (_current is not { Phase: CallPhase.IncomingRinging } ringing)
            {
                return;
            }

            call = ringing;
        }

        _network.Send(call.FriendId, PacketType.CallDecline, new CallDeclinePayload(call.CallId, DeclineReasons.Declined));
        EndCall(call.CallId, notifyPeer: false, EndReason.DeclinedByMe);
    }

    public void HangUp()
    {
        var call = Current;
        if (call is null)
        {
            return;
        }

        if (call.Phase == CallPhase.IncomingRinging)
        {
            Decline();
            return;
        }

        EndCall(call.CallId, notifyPeer: true, call.Phase == CallPhase.Active ? EndReason.Ended : EndReason.Cancelled);
    }

    public Task<bool> StartScreenShareAsync() =>
        Current is { Phase: CallPhase.Active } ? _screen.StartSharingAsync() : Task.FromResult(false);

    public void StopScreenShare() => _screen.StopSharing();

    // ------------------------------------------------------------------ signalling

    public bool CanHandle(PacketType type) => type is PacketType.CallInvite or PacketType.CallAccept
        or PacketType.CallDecline or PacketType.CallEnd or PacketType.CallState;

    public async ValueTask HandlePacketAsync(Guid friendId, PacketType type, ReadOnlyMemory<byte> payload)
    {
        switch (type)
        {
            case PacketType.CallInvite when Payload.Deserialize<CallInvitePayload>(payload.Span) is { } invite:
                await OnInviteAsync(friendId, invite);
                break;

            case PacketType.CallAccept when Payload.Deserialize<CallAcceptPayload>(payload.Span) is { } accept:
                await OnAcceptedAsync(friendId, accept);
                break;

            case PacketType.CallDecline when Payload.Deserialize<CallDeclinePayload>(payload.Span) is { } decline:
                if (Current is { Phase: CallPhase.OutgoingRinging } outgoing && outgoing.CallId == decline.CallId && outgoing.FriendId == friendId)
                {
                    EndCall(decline.CallId, notifyPeer: false, decline.Reason == DeclineReasons.Busy ? EndReason.Busy : EndReason.DeclinedByThem);
                }

                break;

            case PacketType.CallEnd when Payload.Deserialize<CallEndPayload>(payload.Span) is { } end:
                if (Current is { } current && current.CallId == end.CallId && current.FriendId == friendId)
                {
                    EndCall(end.CallId, notifyPeer: false, current.Phase switch
                    {
                        CallPhase.IncomingRinging => EndReason.Missed,
                        CallPhase.OutgoingRinging => EndReason.DeclinedByThem,
                        _ => EndReason.Ended,
                    });
                }

                break;

            case PacketType.CallState when Payload.Deserialize<CallStatePayload>(payload.Span) is { } state:
                Update(c => c.CallId == state.CallId ? c with { RemoteMuted = state.Muted, RemoteDeafened = state.Deafened } : c);
                break;
        }
    }

    private async Task OnInviteAsync(Guid friendId, CallInvitePayload invite)
    {
        Guid? cancelOwnCall = null;
        bool busy = false;
        lock (_gate)
        {
            if (_current is null)
            {
                _current = NewCall(invite.CallId, friendId, isOutgoing: false, CallPhase.IncomingRinging);
                _pendingRemoteUdpPort = invite.UdpPort;
            }
            else if (_current.CallId == invite.CallId)
            {
                return; // duplicate
            }
            else if (_current.FriendId == friendId && _current.Phase == CallPhase.OutgoingRinging)
            {
                // We called each other at the same moment. The side with the larger PeerId yields
                // and answers the other's call; the other side ignores this invite and waits.
                var theirs = _friends.Get(friendId)?.PeerId ?? Guid.Empty;
                if (_settings.Current.PeerId.CompareTo(theirs) <= 0)
                {
                    return;
                }

                cancelOwnCall = _current.CallId;
                _current = NewCall(invite.CallId, friendId, isOutgoing: false, CallPhase.IncomingRinging);
                _pendingRemoteUdpPort = invite.UdpPort;
            }
            else
            {
                busy = true;
            }
        }

        if (busy)
        {
            _network.Send(friendId, PacketType.CallDecline, new CallDeclinePayload(invite.CallId, DeclineReasons.Busy));
            return;
        }

        if (cancelOwnCall is { } own)
        {
            CancelRingTimeout();
            _sounds.StopRinging();
            _network.Send(friendId, PacketType.CallEnd, new CallEndPayload(own));
            await AcceptAsync();
            return;
        }

        _sounds.StartRingtone();
        ArmRingTimeout(invite.CallId);
        if (_friends.Get(friendId) is { } friend)
        {
            _notifications.ShowIncomingCall(friend);
        }

        Raise();
    }

    private async Task OnAcceptedAsync(Guid friendId, CallAcceptPayload accept)
    {
        lock (_gate)
        {
            if (_current is not { Phase: CallPhase.OutgoingRinging } outgoing || outgoing.CallId != accept.CallId || outgoing.FriendId != friendId)
            {
                return;
            }

            _current = outgoing with { Phase = CallPhase.Active, ConnectedAt = DateTimeOffset.Now };
        }

        CancelRingTimeout();
        _sounds.StopRinging();
        Raise();
        await StartMediaAsync(accept.CallId, friendId, accept.UdpPort);
    }

    private void OnPeerStatusChanged(object? sender, PeerStatusChangedEventArgs e)
    {
        if (!e.IsOnline && Current is { } call && call.FriendId == e.FriendId)
        {
            EndCall(call.CallId, notifyPeer: false, EndReason.ConnectionLost);
        }
    }

    // ------------------------------------------------------------------ media

    private async Task StartMediaAsync(Guid callId, Guid friendId, int remoteUdpPort)
    {
        var address = _network.GetRemoteAddress(friendId);
        if (address is null)
        {
            EndCall(callId, notifyPeer: true, EndReason.ConnectionLost);
            return;
        }

        lock (_gate)
        {
            _remoteVoiceEndpoint = new IPEndPoint(address, remoteUdpPort);
            _token = VoicePacket.TokenFromCallId(callId);
            _sequence = 0;
        }

        _transport.Receiver = OnDatagram;
        ApplyAudioState(sendState: false);
        _engine.NoiseSuppressionEnabled = _settings.Current.NoiseSuppression;
        _engine.OutputVolume = (float)_settings.Current.OutputVolume;
        _engine.InputGain = (float)_settings.Current.InputGain;

        var result = await _engine.StartAsync(VoiceMode.Call, OnEncodedVoice);
        Volatile.Write(ref _voiceSession, result.Session);

        if (Current?.CallId != callId)
        {
            // Hung up while audio was starting.
            Interlocked.CompareExchange(ref _voiceSession, 0, result.Session);
            _ = Task.Run(() => _engine.Stop(result.Session));
            return;
        }

        string? warning = result.Message;
        if (!_transport.IsBound)
        {
            warning ??= "The voice port could not be opened, so audio won't flow.";
        }

        _screen.BindCall(callId, friendId);
        Update(c => c with { MediaWarning = warning });
        SendState();
        _sounds.PlayJoin();
    }

    private void OnEncodedVoice(ReadOnlySpan<byte> opus, bool speaking)
    {
        var endpoint = _remoteVoiceEndpoint;
        if (endpoint is null)
        {
            return;
        }

        Span<byte> packet = stackalloc byte[VoicePacket.HeaderSize + opus.Length];
        VoicePacket.WriteHeader(packet, _token, _sequence++, speaking ? VoicePacket.Flags.Speaking : VoicePacket.Flags.None);
        opus.CopyTo(packet[VoicePacket.HeaderSize..]);
        _transport.Send(endpoint, packet);
    }

    private void OnDatagram(IPAddress from, ReadOnlySpan<byte> datagram)
    {
        if (VoicePacket.TryRead(datagram, out var token, out var sequence, out var flags, out var opus) && token == _token && _remoteVoiceEndpoint is not null)
        {
            _engine.PushRemotePacket(sequence, opus, flags.HasFlag(VoicePacket.Flags.Speaking));
        }
    }

    private void ApplyAudioState(bool sendState = true)
    {
        _engine.IsDeafened = _isDeafened;
        _engine.IsMuted = _isMuted || _isDeafened;
        if (sendState)
        {
            SendState();
            Raise();
        }
    }

    private void SendState()
    {
        if (Current is { Phase: CallPhase.Active } call)
        {
            _network.Send(call.FriendId, PacketType.CallState, new CallStatePayload(call.CallId, _isMuted || _isDeafened, _isDeafened));
        }
    }

    // ------------------------------------------------------------------ teardown

    private enum EndReason
    {
        Ended,
        Cancelled,
        NoAnswer,
        DeclinedByThem,
        DeclinedByMe,
        Busy,
        Missed,
        ConnectionLost,
        Failed,
    }

    private void EndCall(Guid callId, bool notifyPeer, EndReason reason)
    {
        CallInfo call;
        lock (_gate)
        {
            if (_current is null || _current.CallId != callId)
            {
                return;
            }

            call = _current;
            _current = null;
            _remoteVoiceEndpoint = null;
        }

        CancelRingTimeout();
        _sounds.StopRinging();
        _notifications.ClearIncomingCall();
        _transport.Receiver = null;

        if (notifyPeer)
        {
            _network.Send(call.FriendId, PacketType.CallEnd, new CallEndPayload(call.CallId));
        }

        _screen.UnbindCall();
        int session = Interlocked.Exchange(ref _voiceSession, 0);
        if (session != 0)
        {
            // Never block the caller (often the UI thread) on audio teardown.
            _ = Task.Run(() => _engine.Stop(session));
        }

        var friend = _friends.Get(call.FriendId);
        var name = friend?.Nickname ?? "They";
        var text = (reason, call.Phase) switch
        {
            (EndReason.Ended, _) => $"Call ended · {FormatDuration(call)}",
            (EndReason.ConnectionLost, CallPhase.Active) => $"Call ended (connection lost) · {FormatDuration(call)}",
            (EndReason.ConnectionLost, CallPhase.IncomingRinging) or (EndReason.Missed, _) => "Missed call",
            (EndReason.ConnectionLost, _) or (EndReason.Failed, _) => "Call failed",
            (EndReason.Cancelled, _) => "Outgoing call · cancelled",
            (EndReason.NoAnswer, _) => "Outgoing call · no answer",
            (EndReason.DeclinedByThem, _) => "Outgoing call · declined",
            (EndReason.Busy, _) => $"Outgoing call · {name} was in another call",
            (EndReason.DeclinedByMe, _) => "Declined call",
            _ => "Call ended",
        };

        _chat.AddCallEvent(call.FriendId, text);
        if (reason == EndReason.Missed && friend is not null)
        {
            _notifications.ShowMessage(friend, "Missed call");
        }

        if (call.Phase == CallPhase.Active)
        {
            _sounds.PlayLeave();
        }

        Raise();
    }

    private void ArmRingTimeout(Guid callId)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _ringTimeout, cts)?.Cancel();
        _ = Task.Delay(RingTimeout, cts.Token).ContinueWith(
            _ =>
            {
                if (Current is { } call && call.CallId == callId && call.Phase != CallPhase.Active)
                {
                    EndCall(callId, notifyPeer: call.IsOutgoing, call.IsOutgoing ? EndReason.NoAnswer : EndReason.Missed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void CancelRingTimeout() => Interlocked.Exchange(ref _ringTimeout, null)?.Cancel();

    private void Update(Func<CallInfo, CallInfo> mutate)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            _current = mutate(_current);
        }

        Raise();
    }

    private void Raise()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Call] StateChanged handler failed: {ex}");
        }
    }

    private static CallInfo NewCall(Guid callId, Guid friendId, bool isOutgoing, CallPhase phase) =>
        new(callId, friendId, isOutgoing, phase, null, false, false, false, false, null);

    private static string FormatDuration(CallInfo call)
    {
        var duration = call.ConnectedAt is { } start ? DateTimeOffset.Now - start : TimeSpan.Zero;
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }
}
