using Talky.Models;

namespace Talky.Services.Storage;

public enum FriendChangeKind
{
    Added,
    Updated,
    Removed,
}

public sealed class FriendChangedEventArgs(Friend friend, FriendChangeKind kind) : EventArgs
{
    public Friend Friend { get; } = friend;

    public FriendChangeKind Kind { get; } = kind;
}

public interface IFriendService
{
    /// <summary>Snapshot of all friends.</summary>
    IReadOnlyList<Friend> GetAll();

    Friend? Get(Guid id);

    /// <summary>Raised on the thread that made the change.</summary>
    event EventHandler<FriendChangedEventArgs>? Changed;

    void Load();

    Friend Add(string nickname, string host, int port, bool isPending = false);

    /// <summary>Applies <paramref name="mutate"/> to the friend and persists. Returns false if it no longer exists.</summary>
    bool Update(Guid id, Action<Friend> mutate);

    void Remove(Guid id);
}

public sealed class FriendService(IAppPaths paths) : IFriendService
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(paths.DataDirectory, "friends.json");
    private List<Friend> _friends = [];

    public event EventHandler<FriendChangedEventArgs>? Changed;

    public void Load()
    {
        lock (_gate)
        {
            _friends = JsonFile.Read(_path, StorageJsonContext.Default.ListFriend) ?? [];
        }
    }

    public IReadOnlyList<Friend> GetAll()
    {
        lock (_gate)
        {
            return _friends.ToArray();
        }
    }

    public Friend? Get(Guid id)
    {
        lock (_gate)
        {
            return _friends.Find(f => f.Id == id);
        }
    }

    public Friend Add(string nickname, string host, int port, bool isPending = false)
    {
        var friend = new Friend
        {
            Nickname = nickname.Trim(),
            Host = host.Trim(),
            Port = port,
            IsPending = isPending,
        };

        lock (_gate)
        {
            _friends.Add(friend);
            Save();
        }

        Changed?.Invoke(this, new FriendChangedEventArgs(friend, FriendChangeKind.Added));
        return friend;
    }

    public bool Update(Guid id, Action<Friend> mutate)
    {
        Friend? friend;
        lock (_gate)
        {
            friend = _friends.Find(f => f.Id == id);
            if (friend is null)
            {
                return false;
            }

            mutate(friend);
            Save();
        }

        Changed?.Invoke(this, new FriendChangedEventArgs(friend, FriendChangeKind.Updated));
        return true;
    }

    public void Remove(Guid id)
    {
        Friend? friend;
        lock (_gate)
        {
            friend = _friends.Find(f => f.Id == id);
            if (friend is null)
            {
                return;
            }

            _friends.Remove(friend);
            Save();
        }

        Changed?.Invoke(this, new FriendChangedEventArgs(friend, FriendChangeKind.Removed));
    }

    private void Save()
    {
        try
        {
            JsonFile.Write(_path, _friends, StorageJsonContext.Default.ListFriend);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Friends] Save failed: {ex.Message}");
        }
    }
}
