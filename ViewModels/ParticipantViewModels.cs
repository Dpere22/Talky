using CommunityToolkit.Mvvm.ComponentModel;
using Talky.Models;

namespace Talky.ViewModels;

/// <summary>Anyone who can author a message: the local user or a friend.</summary>
public partial class ParticipantViewModel : ObservableObject
{
    public ParticipantViewModel()
    {
        DisplayName = string.Empty;
    }

    [ObservableProperty]
    public partial string DisplayName { get; set; }
}

public sealed partial class LocalUserViewModel : ParticipantViewModel
{
    public LocalUserViewModel()
    {
        AddressText = string.Empty;
    }

    /// <summary>"192.168.1.5:47820" — what friends need to add you.</summary>
    [ObservableProperty]
    public partial string AddressText { get; set; }
}

public sealed partial class FriendViewModel : ParticipantViewModel
{
    public FriendViewModel(Friend friend)
    {
        Id = friend.Id;
        Address = string.Empty;
        Apply(friend);
    }

    public Guid Id { get; }

    [ObservableProperty]
    public partial string Address { get; set; }

    /// <summary>What the peer calls itself (may differ from the nickname you gave it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteNameHint))]
    public partial string? RemoteDisplayName { get; set; }

    [ObservableProperty]
    public partial bool IsPending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Subtitle))]
    public partial bool IsOnline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadBadgeValue))]
    public partial int UnreadCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string? LastMessagePreview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Subtitle))]
    public partial bool IsTyping { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Subtitle))]
    public partial bool IsInCall { get; set; }

    public DateTimeOffset LastActivity { get; set; }

    public DateTimeOffset TypingUntil { get; set; }

    public bool HasUnread => UnreadCount > 0;

    public int UnreadBadgeValue => Math.Min(UnreadCount, 99);

    public string StatusText => IsInCall ? "In a call" : IsTyping ? "Typing…" : IsOnline ? "Online" : "Offline";

    public string Subtitle => IsInCall || IsTyping ? StatusText : LastMessagePreview ?? StatusText;

    public string? RemoteNameHint =>
        string.IsNullOrWhiteSpace(RemoteDisplayName) || string.Equals(RemoteDisplayName, DisplayName, StringComparison.CurrentCultureIgnoreCase)
            ? null
            : $"Calls themselves “{RemoteDisplayName}”";

    /// <summary>Used by list automation peers (Narrator reads list items through ToString).</summary>
    public override string ToString() => HasUnread ? $"{DisplayName}, {StatusText}, {UnreadCount} unread" : $"{DisplayName}, {StatusText}";

    public void Apply(Friend friend)
    {
        DisplayName = friend.Nickname;
        Address = friend.Address;
        RemoteDisplayName = friend.RemoteDisplayName;
        IsPending = friend.IsPending;
        OnPropertyChanged(nameof(RemoteNameHint));
    }
}
