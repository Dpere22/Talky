using System.Buffers.Binary;

namespace Talky.Protocol;

/// <summary>
/// UDP voice datagram: <c>'T' | 0x01 | callToken:u32 | seq:u32 | flags:u8 | opus...</c>.
/// </summary>
public static class VoicePacket
{
    public const int HeaderSize = 11;
    public const int MaxPacketSize = 1500;

    private const byte Magic = (byte)'T';
    private const byte KindVoice = 1;

    [Flags]
    public enum Flags : byte
    {
        None = 0,
        Speaking = 1,
    }

    public static uint TokenFromCallId(Guid callId)
    {
        Span<byte> bytes = stackalloc byte[16];
        callId.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public static void WriteHeader(Span<byte> destination, uint token, uint sequence, Flags flags)
    {
        destination[0] = Magic;
        destination[1] = KindVoice;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[2..], token);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[6..], sequence);
        destination[10] = (byte)flags;
    }

    public static bool TryRead(ReadOnlySpan<byte> datagram, out uint token, out uint sequence, out Flags flags, out ReadOnlySpan<byte> opus)
    {
        token = sequence = 0;
        flags = Flags.None;
        opus = default;
        if (datagram.Length <= HeaderSize || datagram[0] != Magic || datagram[1] != KindVoice)
        {
            return false;
        }

        token = BinaryPrimitives.ReadUInt32LittleEndian(datagram[2..]);
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(datagram[6..]);
        flags = (Flags)datagram[10];
        opus = datagram[HeaderSize..];
        return true;
    }
}
