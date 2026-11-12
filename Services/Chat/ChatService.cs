using Talky.Models;
using Talky.Protocol;
using Talky.Services.Networking;
using Talky.Services.Storage;

namespace Talky.Services.Chat;

public interface IChatService
{
    /// <summary>A message was added (sent, received or a local call event). Any thread.</summary>
    event EventHandler<ChatMessage>? MessageAdded;

    /// <summary>A message's delivery status changed. Any thread.</summary>
    event EventHandler<ChatMessage>? MessageUpdated;

    /// <summary>The friend is typing. Any thread.</summary>
    event EventHandler<Guid>? TypingReceived;

    void Start();

    ChatMessage Send(Guid friendId, string text);

    void SendTyping(Guid friendId);

    void AddCallEvent(Guid friendId, string text);
}

/// <summary>
/// Store-and-forward 1:1 chat. Outgoing messages stay <see cref="MessageStatus.Pending"/> until the
/// peer acknowledges them and are re-sent whenever the peer comes back online. Ids dedupe retries.
/// </summary>
public sealed class ChatService : IChatService, IPacketHandler
{
    public const int MaxMessageLength = 4000;

    private readonly IPeerNetwork _network;
    private readonly IMessageStore _store;
    private readonly IFriendService _friends;

    public ChatService(IPeerNetwork network, IMessageStore store, IFriendService friends)
    {
        _network = network;
        _store = store;
        _friends = friends;
        network.RegisterHandler(this);
    }

    public event EventHandler<ChatMessage>? MessageAdded;

    public event EventHandler<ChatMessage>? MessageUpdated;

    public event EventHandler<Guid>? TypingReceived;

    public void Start()
    {
        _network.PeerStatusChanged += OnPeerStatusChanged;
        _friends.Changed += (_, e) =>
        {
            if (e.Kind == FriendChangeKind.Removed)
            {
                _store.DeleteConversation(e.Friend.Id);
            }
        };
    }

    public ChatMessage Send(Guid friendId, string text)
    {
        if (text.Length > MaxMessageLength)
        {
            text = text[..MaxMessageLength];
        }

        var message = new ChatMessage
        {
            FriendId = friendId,
            IsOutgoing = true,
            Text = text,
            Timestamp = DateTimeOffset.Now,
            Status = MessageStatus.Pending,
        };

        _store.Add(message);
        MessageAdded?.Invoke(this, message);

        if (!Transmit(message))
        {
            // Offline: it stays pending and goes out on reconnect. Try reaching them now.
            _network.ConnectNow(friendId);
        }

        return message;
    }

    public void SendTyping(Guid friendId) => _network.Send(friendId, PacketType.Typing);

    public void AddCallEvent(Guid friendId, string text)
    {
        var message = new ChatMessage
        {
            FriendId = friendId,
            Kind = MessageKind.CallEvent,
            Text = text,
            Timestamp = DateTimeOffset.Now,
            Status = MessageStatus.Delivered,
        };

        _store.Add(message);
        MessageAdded?.Invoke(this, message);
    }

    public bool CanHandle(PacketType type) => type.IsChat();

    public ValueTask HandlePacketAsync(Guid friendId, PacketType type, ReadOnlyMemory<byte> payload)
    {
        switch (type)
        {
            case PacketType.Chat:
                var chat = Payload.Deserialize<ChatPayload>(payload.Span);
                if (chat is null || string.IsNullOrWhiteSpace(chat.Text))
                {
                    break;
                }

                // Always ack, even duplicates (our previous ack may have been lost).
                _network.Send(friendId, PacketType.ChatAck, new ChatAckPayload(chat.Id));

                var message = new ChatMessage
                {
                    Id = chat.Id,
                    FriendId = friendId,
                    IsOutgoing = false,
                    Text = chat.Text.Length > MaxMessageLength ? chat.Text[..MaxMessageLength] : chat.Text,
                    // Use our clock: the peer's clock may be off and would mis-order the conversation.
                    Timestamp = DateTimeOffset.Now,
                    Status = MessageStatus.Delivered,
                };

                if (_store.Add(message))
                {
                    MessageAdded?.Invoke(this, message);
                }

                break;

            case PacketType.ChatAck:
                var ack = Payload.Deserialize<ChatAckPayload>(payload.Span);
                if (ack is not null && _store.SetStatus(friendId, ack.Id, MessageStatus.Delivered))
                {
                    var updated = _store.GetMessages(friendId).FirstOrDefault(m => m.Id == ack.Id);
                    if (updated is not null)
                    {
                        MessageUpdated?.Invoke(this, updated);
                    }
                }

                break;

            case PacketType.Typing:
                TypingReceived?.Invoke(this, friendId);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private bool Transmit(ChatMessage message) =>
        _network.Send(message.FriendId, PacketType.Chat, new ChatPayload(message.Id, message.Text, message.Timestamp));

    private void OnPeerStatusChanged(object? sender, PeerStatusChangedEventArgs e)
    {
        if (!e.IsOnline)
        {
            return;
        }

        foreach (var pending in _store.GetPendingOutgoing(e.FriendId))
        {
            Transmit(pending);
        }
    }
}
