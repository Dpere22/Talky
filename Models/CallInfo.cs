namespace Talky.Models;

public enum CallPhase
{
    /// <summary>We invited the peer and are waiting for an answer.</summary>
    OutgoingRinging,

    /// <summary>The peer invited us and we have not answered yet.</summary>
    IncomingRinging,

    Active,
}

/// <summary>Immutable snapshot of the current call, published by <c>ICallService</c>.</summary>
public sealed record CallInfo(
    Guid CallId,
    Guid FriendId,
    bool IsOutgoing,
    CallPhase Phase,
    DateTimeOffset? ConnectedAt,
    bool RemoteMuted,
    bool RemoteDeafened,
    bool RemoteSharing,
    bool LocalSharing,
    string? MediaWarning);
