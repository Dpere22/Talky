using System.Text.Json;
using System.Text.Json.Serialization;

namespace Talky.Protocol;

public sealed record HelloPayload(Guid PeerId, string DisplayName, int ListenPort, int ProtocolVersion, string AppVersion);

public sealed record ChatPayload(Guid Id, string Text, DateTimeOffset SentAt);

public sealed record ChatAckPayload(Guid Id);

public sealed record CallInvitePayload(Guid CallId, int UdpPort);

public sealed record CallAcceptPayload(Guid CallId, int UdpPort);

public sealed record CallDeclinePayload(Guid CallId, string Reason);

public sealed record CallEndPayload(Guid CallId);

public sealed record CallStatePayload(Guid CallId, bool Muted, bool Deafened);

public sealed record ScreenStartPayload(Guid CallId, int Width, int Height, string SourceName);

public sealed record ScreenStopPayload(Guid CallId);

public static class DeclineReasons
{
    public const string Declined = "declined";
    public const string Busy = "busy";
}

/// <summary>Source-generated serializer context: reflection-free, so it survives trimming/AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HelloPayload))]
[JsonSerializable(typeof(ChatPayload))]
[JsonSerializable(typeof(ChatAckPayload))]
[JsonSerializable(typeof(CallInvitePayload))]
[JsonSerializable(typeof(CallAcceptPayload))]
[JsonSerializable(typeof(CallDeclinePayload))]
[JsonSerializable(typeof(CallEndPayload))]
[JsonSerializable(typeof(CallStatePayload))]
[JsonSerializable(typeof(ScreenStartPayload))]
[JsonSerializable(typeof(ScreenStopPayload))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;

public static class Payload
{
    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ProtocolJsonContext.Default.GetTypeInfo(typeof(T))!);

    public static T? Deserialize<T>(ReadOnlySpan<byte> data) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(data, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ProtocolJsonContext.Default.GetTypeInfo(typeof(T))!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
