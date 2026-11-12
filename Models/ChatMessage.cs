namespace Talky.Models;

public enum MessageKind
{
    Text,

    /// <summary>A local system entry such as "Missed call" or "Call ended · 4:12".</summary>
    CallEvent,
}

public enum MessageStatus
{
    /// <summary>Outgoing message not yet acknowledged by the peer (will be retried on reconnect).</summary>
    Pending,

    Delivered,
}

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FriendId { get; set; }

    public bool IsOutgoing { get; set; }

    public MessageKind Kind { get; set; } = MessageKind.Text;

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public MessageStatus Status { get; set; } = MessageStatus.Delivered;
}
