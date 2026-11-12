using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Talky.Models;
using Talky.Services.Chat;
using Talky.Services.Storage;
using Talky.Services.UI;
using Windows.ApplicationModel.DataTransfer;

namespace Talky.ViewModels;

/// <summary>One 1:1 conversation. Created lazily per friend by <see cref="ShellViewModel"/> and cached.</summary>
public sealed partial class ConversationViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(3);

    private readonly ShellViewModel _shell;
    private readonly IChatService _chat;
    private readonly IMessageStore _store;
    private readonly IFriendService _friends;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<Guid, MessageViewModel> _byId = [];
    private DateTimeOffset _lastTypingSent;

    public ConversationViewModel(
        FriendViewModel friend,
        ShellViewModel shell,
        IChatService chat,
        IMessageStore store,
        IFriendService friends,
        IUiDispatcher dispatcher)
    {
        Friend = friend;
        _shell = shell;
        _chat = chat;
        _store = store;
        _friends = friends;
        _dispatcher = dispatcher;
        DraftText = string.Empty;

        foreach (var message in store.GetMessages(friend.Id))
        {
            Append(message);
        }

        _chat.MessageAdded += OnMessageAdded;
        _chat.MessageUpdated += OnMessageUpdated;
        Call.PropertyChanged += OnCallPropertyChanged;
        Friend.PropertyChanged += OnFriendPropertyChanged;
        UpdateIsCallHere();
    }

    public FriendViewModel Friend { get; }

    public CallViewModel Call => _shell.Call;

    public LocalUserViewModel Me => _shell.Me;

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public int MaxMessageLength => ChatService.MaxMessageLength;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string DraftText { get; set; }

    /// <summary>A call (ringing or connected) with this friend is in progress.</summary>
    [ObservableProperty]
    public partial bool IsCallHere { get; private set; }

    public string ComposerPlaceholder => $"Message @{Friend.DisplayName}";

    public bool IsEmpty => Messages.Count == 0;

    private bool CanSend() => !string.IsNullOrWhiteSpace(DraftText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = DraftText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        DraftText = string.Empty;
        _chat.Send(Friend.Id, text);
    }

    [RelayCommand]
    private Task StartCallAsync() => _shell.CallFriendAsync(Friend);

    [RelayCommand]
    private Task EditAsync() => _shell.EditFriendAsync(Friend);

    [RelayCommand]
    private Task RemoveAsync() => _shell.RemoveFriendAsync(Friend);

    [RelayCommand]
    private void AcceptFriend() => _friends.Update(Friend.Id, f => f.IsPending = false);

    [RelayCommand]
    private void CopyAddress()
    {
        var package = new DataPackage();
        package.SetText(Friend.Address);
        Clipboard.SetContent(package);
    }

    partial void OnDraftTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Friend.IsOnline)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (now - _lastTypingSent > TypingInterval)
        {
            _lastTypingSent = now;
            _chat.SendTyping(Friend.Id);
        }
    }

    public void Dispose()
    {
        _chat.MessageAdded -= OnMessageAdded;
        _chat.MessageUpdated -= OnMessageUpdated;
        Call.PropertyChanged -= OnCallPropertyChanged;
        Friend.PropertyChanged -= OnFriendPropertyChanged;
    }

    private void Append(ChatMessage message)
    {
        if (_byId.ContainsKey(message.Id))
        {
            return;
        }

        var author = message.IsOutgoing ? (ParticipantViewModel)Me : Friend;
        var viewModel = new MessageViewModel(message, author, Messages.Count > 0 ? Messages[^1] : null);
        _byId[message.Id] = viewModel;
        Messages.Add(viewModel);
        if (Messages.Count == 1)
        {
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void OnMessageAdded(object? sender, ChatMessage message)
    {
        if (message.FriendId == Friend.Id)
        {
            _dispatcher.Post(() => Append(message));
        }
    }

    private void OnMessageUpdated(object? sender, ChatMessage message)
    {
        if (message.FriendId != Friend.Id)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (_byId.TryGetValue(message.Id, out var viewModel))
            {
                viewModel.IsPending = message.Status == MessageStatus.Pending;
            }
        });
    }

    private void OnCallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CallViewModel.FriendId) or nameof(CallViewModel.IsInCall))
        {
            UpdateIsCallHere();
        }
    }

    private void OnFriendPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FriendViewModel.DisplayName))
        {
            OnPropertyChanged(nameof(ComposerPlaceholder));
        }
    }

    private void UpdateIsCallHere() => IsCallHere = Call.IsInCall && Call.FriendId == Friend.Id;
}
