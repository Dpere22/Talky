using Talky.Models;

namespace Talky.Services.Storage;

public interface IMessageStore
{
    IReadOnlyList<ChatMessage> GetMessages(Guid friendId);

    ChatMessage? GetLast(Guid friendId);

    bool Contains(Guid friendId, Guid messageId);

    /// <summary>Adds a message. Returns false if a message with the same id already exists (dedupe).</summary>
    bool Add(ChatMessage message);

    bool SetStatus(Guid friendId, Guid messageId, MessageStatus status);

    IReadOnlyList<ChatMessage> GetPendingOutgoing(Guid friendId);

    void DeleteConversation(Guid friendId);

    /// <summary>Writes all unsaved conversations to disk immediately.</summary>
    void Flush();
}

/// <summary>
/// One JSON file per conversation, kept in memory once loaded and written back with a short debounce.
/// LAN chat histories are small, so a full rewrite per change is fine.
/// </summary>
public sealed class MessageStore : IMessageStore, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(750);

    private readonly object _gate = new();
    private readonly IAppPaths _paths;
    private readonly Dictionary<Guid, Conversation> _conversations = [];
    private readonly HashSet<Guid> _dirty = [];
    private readonly Timer _saveTimer;

    public MessageStore(IAppPaths paths)
    {
        _paths = paths;
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<ChatMessage> GetMessages(Guid friendId)
    {
        lock (_gate)
        {
            return GetConversation(friendId).Messages.ToArray();
        }
    }

    public ChatMessage? GetLast(Guid friendId)
    {
        lock (_gate)
        {
            var messages = GetConversation(friendId).Messages;
            return messages.Count == 0 ? null : messages[^1];
        }
    }

    public bool Contains(Guid friendId, Guid messageId)
    {
        lock (_gate)
        {
            return GetConversation(friendId).Ids.Contains(messageId);
        }
    }

    public bool Add(ChatMessage message)
    {
        lock (_gate)
        {
            var conversation = GetConversation(message.FriendId);
            if (!conversation.Ids.Add(message.Id))
            {
                return false;
            }

            conversation.Messages.Add(message);
            MarkDirty(message.FriendId);
            return true;
        }
    }

    public bool SetStatus(Guid friendId, Guid messageId, MessageStatus status)
    {
        lock (_gate)
        {
            var message = GetConversation(friendId).Messages.Find(m => m.Id == messageId);
            if (message is null || message.Status == status)
            {
                return false;
            }

            message.Status = status;
            MarkDirty(friendId);
            return true;
        }
    }

    public IReadOnlyList<ChatMessage> GetPendingOutgoing(Guid friendId)
    {
        lock (_gate)
        {
            return GetConversation(friendId).Messages
                .Where(m => m.IsOutgoing && m.Kind == MessageKind.Text && m.Status == MessageStatus.Pending)
                .ToArray();
        }
    }

    public void DeleteConversation(Guid friendId)
    {
        lock (_gate)
        {
            _conversations.Remove(friendId);
            _dirty.Remove(friendId);
            try
            {
                File.Delete(PathFor(friendId));
            }
            catch (IOException)
            {
            }
        }
    }

    public void Flush()
    {
        List<(Guid Id, List<ChatMessage> Messages)> work;
        lock (_gate)
        {
            work = _dirty
                .Where(_conversations.ContainsKey)
                .Select(id => (id, _conversations[id].Messages.ToList()))
                .ToList();
            _dirty.Clear();
        }

        foreach (var (id, messages) in work)
        {
            try
            {
                JsonFile.Write(PathFor(id), messages, StorageJsonContext.Default.ListChatMessage);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Messages] Save failed for {id}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    private Conversation GetConversation(Guid friendId)
    {
        if (!_conversations.TryGetValue(friendId, out var conversation))
        {
            var messages = JsonFile.Read(PathFor(friendId), StorageJsonContext.Default.ListChatMessage) ?? [];
            messages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            conversation = new Conversation(messages, messages.Select(m => m.Id).ToHashSet());
            _conversations[friendId] = conversation;
        }

        return conversation;
    }

    private void MarkDirty(Guid friendId)
    {
        _dirty.Add(friendId);
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private string PathFor(Guid friendId) => Path.Combine(_paths.MessagesDirectory, $"{friendId:N}.json");

    private sealed record Conversation(List<ChatMessage> Messages, HashSet<Guid> Ids);
}
