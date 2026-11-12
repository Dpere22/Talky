namespace Talky.Models;

/// <summary>
/// A friend is simply a LAN address (IP or hostname + port) that the user gave a nickname.
/// There is no server: identity is learned from the peer's Hello handshake.
/// </summary>
public sealed class Friend
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Nickname { get; set; } = string.Empty;

    /// <summary>IPv4/IPv6 literal or a LAN hostname.</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = NetworkDefaults.Port;

    /// <summary>The peer's stable id, learned from its Hello. Lets us recognise it even if its IP changes.</summary>
    public Guid? PeerId { get; set; }

    /// <summary>The display name the peer advertises about itself.</summary>
    public string? RemoteDisplayName { get; set; }

    /// <summary>
    /// True when the friend was created automatically because an unknown peer contacted us.
    /// The user can accept (keep) or remove it.
    /// </summary>
    public bool IsPending { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    public string Address => Port == NetworkDefaults.Port ? Host : FormatEndpoint(Host, Port);

    public static string FormatEndpoint(string host, int port) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";
}

public static class NetworkDefaults
{
    /// <summary>Default TCP (control/chat/screen) and UDP (voice) port.</summary>
    public const int Port = 47820;

    public const int ProtocolVersion = 1;
}
