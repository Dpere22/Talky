using CommunityToolkit.Mvvm.ComponentModel;
using Talky.Helpers;
using Talky.Models;

namespace Talky.ViewModels;

public sealed partial class MessageViewModel : ObservableObject
{
    /// <summary>Messages from the same author within this window collapse into one group (Discord-style).</summary>
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(7);

    public MessageViewModel(ChatMessage message, ParticipantViewModel author, MessageViewModel? previous)
    {
        Id = message.Id;
        Author = author;
        Text = message.Text;
        Timestamp = message.Timestamp;
        IsOutgoing = message.IsOutgoing;
        Kind = message.Kind;
        IsPending = message.IsOutgoing && message.Status == MessageStatus.Pending;

        ShowDateDivider = previous is null || previous.Timestamp.ToLocalTime().Date != Timestamp.ToLocalTime().Date;
        IsGroupStart = ShowDateDivider
            || previous!.Kind != MessageKind.Text
            || Kind != MessageKind.Text
            || previous.IsOutgoing != IsOutgoing
            || Timestamp - previous.Timestamp > GroupWindow;
    }

    public Guid Id { get; }

    public ParticipantViewModel Author { get; }

    public string Text { get; }

    public DateTimeOffset Timestamp { get; }

    public bool IsOutgoing { get; }

    public MessageKind Kind { get; }

    public bool IsGroupStart { get; }

    public bool IsContinuation => !IsGroupStart;

    public bool ShowDateDivider { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentOpacity))]
    public partial bool IsPending { get; set; }

    public double ContentOpacity => IsPending ? 0.55 : 1.0;

    public string TimeText => Format.MessageTime(Timestamp);

    public string ShortTimeText => Format.ShortTime(Timestamp);

    public string DateDividerText => Format.DateDivider(Timestamp);

    public bool IsMissedCall => Kind == MessageKind.CallEvent && Text.StartsWith("Missed", StringComparison.Ordinal);

    /// <summary>Used by list automation peers (Narrator reads list items through ToString).</summary>
    public override string ToString() => Kind == MessageKind.CallEvent
        ? $"{Text}, {TimeText}"
        : $"{Author.DisplayName}, {TimeText}: {Text}{(IsPending ? " (not delivered yet)" : string.Empty)}";
}
