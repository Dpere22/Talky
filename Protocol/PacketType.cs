namespace Talky.Protocol;

/// <summary>
/// TCP packet types. Wire format of every packet: <c>[uint32 LE length][byte type][payload]</c>
/// where <c>length</c> counts the type byte plus the payload.
/// </summary>
public enum PacketType : byte
{
    Hello = 1,
    Ping = 2,
    Pong = 3,

    Chat = 10,
    ChatAck = 11,
    Typing = 12,

    CallInvite = 20,
    CallAccept = 21,
    CallDecline = 22,
    CallEnd = 23,
    CallState = 24,

    ScreenStart = 30,

    /// <summary>Binary tile frame, see <c>TileFrameEncoder</c>.</summary>
    ScreenFrame = 31,

    ScreenStop = 32,
}

public static class PacketTypeExtensions
{
    public static bool IsChat(this PacketType type) => type is PacketType.Chat or PacketType.ChatAck or PacketType.Typing;

    public static bool IsCall(this PacketType type) => (byte)type is >= 20 and < 40;
}
