using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Talky.Helpers;
using Talky.Models;
using Talky.Services.Audio;
using Talky.Services.Calls;
using Talky.Services.Chat;
using Talky.Services.Networking;
using Talky.Services.Storage;
using Talky.Services.UI;
using Windows.ApplicationModel.DataTransfer;

namespace Talky.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private static readonly TimeSpan TypingDisplayTime = TimeSpan.FromSeconds(5);

    private readonly IFriendService _friends;
    private readonly IChatService _chat;
    private readonly IMessageStore _store;
    private readonly IPeerNetwork _network;
    private readonly ICallService _call;
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly IWindowService _window;
    private readonly ISoundEffects _sounds;
    private readonly Dictionary<Guid, ConversationViewModel> _conversations = [];
    private readonly DispatcherQueueTimer _typingTimer;

    public ShellViewModel(
        IFriendService friends,
        IChatService chat,
        IMessageStore store,
        IPeerNetwork network,
        ICallService call,
        ISettingsService settings,
        INavigationService navigation,
        IDialogService dialogs,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        IWindowService window,
        ISoundEffects sounds,
        CallViewModel callViewModel,
        UpdatesViewModel updatesViewModel)
    {
        _friends = friends;
        _chat = chat;
        _store = store;
        _network = network;
        _call = call;
        _settings = settings;
        _navigation = navigation;
        _dialogs = dialogs;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _window = window;
        _sounds = sounds;
        Call = callViewModel;
        Updates = updatesViewModel;

        _typingTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _typingTimer.Interval = TimeSpan.FromSeconds(1);
        _typingTimer.Tick += (_, _) => ExpireTyping();
    }

    public ObservableCollection<FriendViewModel> Friends { get; } = [];

    public LocalUserViewModel Me { get; } = new();

    public CallViewModel Call { get; }

    public UpdatesViewModel Updates { get; }

    [ObservableProperty]
    public partial FriendViewModel? SelectedFriend { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNetworkError))]
    public partial string? NetworkError { get; private set; }

    public bool HasNetworkError => !string.IsNullOrEmpty(NetworkError);

    public bool HasFriends => Friends.Count > 0;

    /// <summary>Friend the current call is with (for the incoming-call card / call banner).</summary>
    public FriendViewModel? CallFriend => Call.FriendId is { } id ? Find(id) : null;

    public void Initialize()
    {
        Me.DisplayName = _settings.Current.DisplayName;
        UpdateMyAddress();

        // Subscribe before taking the snapshot below. Handlers marshal to this (UI) thread, so they run
        // after Initialize and see the populated list; duplicates are ignored in the handlers.
        _friends.Changed += (_, e) => _dispatcher.Post(() => OnFriendChanged(e));
        _network.PeerStatusChanged += (_, e) => _dispatcher.Post(() => OnPeerStatusChanged(e));
        _chat.MessageAdded += (_, m) => _dispatcher.Post(() => OnMessageAdded(m));
        _chat.TypingReceived += (_, id) => _dispatcher.Post(() => OnTyping(id));
        _settings.Changed += (_, _) => _dispatcher.Post(() =>
        {
            Me.DisplayName = _settings.Current.DisplayName;
            UpdateMyAddress();
        });
        _notifications.Activated += (_, a) => _dispatcher.Post(() => _ = HandleNotificationAsync(a));
        _window.ActivationChanged += (_, _) =>
        {
            if (_window.IsActive && SelectedFriend is { } selected)
            {
                selected.UnreadCount = 0;
            }
        };

        Call.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CallViewModel.FriendId) or nameof(CallViewModel.IsInCall))
            {
                foreach (var friend in Friends)
                {
                    friend.IsInCall = Call.IsInCall && Call.FriendId == friend.Id;
                }

                OnPropertyChanged(nameof(CallFriend));
            }
        };

        var items = _friends.GetAll().Select(CreateFriendViewModel).OrderByDescending(f => f.LastActivity);
        foreach (var item in items)
        {
            Friends.Add(item);
        }

        Friends.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(HasFriends));
        NetworkError = _network.ListenError;

        _navigation.Navigate(NavigationTarget.Welcome);
    }

    public ConversationViewModel GetConversation(FriendViewModel friend)
    {
        if (!_conversations.TryGetValue(friend.Id, out var conversation))
        {
            conversation = new ConversationViewModel(friend, this, _chat, _store, _friends, _dispatcher);
            _conversations[friend.Id] = conversation;
        }

        return conversation;
    }

    partial void OnSelectedFriendChanged(FriendViewModel? value)
    {
        if (value is not null)
        {
            IsSettingsOpen = false;
            value.UnreadCount = 0;
            _navigation.Navigate(NavigationTarget.Chat, GetConversation(value));
        }
        else if (!IsSettingsOpen)
        {
            _navigation.Navigate(NavigationTarget.Welcome);
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
        SelectedFriend = null;
        _navigation.Navigate(NavigationTarget.Settings);
    }

    [RelayCommand]
    private void ShowCallFriend()
    {
        if (CallFriend is { } friend)
        {
            SelectedFriend = friend;
        }
    }

    [RelayCommand]
    private async Task AddFriendAsync()
    {
        var editor = new FriendEditorViewModel(
            "Add a friend",
            "Add friend",
            nickname: null,
            address: null,
            NetworkInfo.GetLocalIPv4Addresses().Select(a => FormatOwnAddress(a)).ToArray());

        if (!await _dialogs.ShowFriendEditorAsync(editor) || !editor.TryGetEndpoint(out var host, out var port))
        {
            return;
        }

        var friend = _friends.Add(editor.Nickname.Trim(), host, port);
        // OnFriendChanged inserts the view model synchronously when we're on the UI thread.
        if (Find(friend.Id) is { } added)
        {
            SelectedFriend = added;
        }
    }

    [RelayCommand]
    private void CopyMyAddress()
    {
        var package = new DataPackage();
        package.SetText(Me.AddressText);
        Clipboard.SetContent(package);
    }

    [RelayCommand]
    public async Task CallFriendAsync(FriendViewModel? friend)
    {
        if (friend is null)
        {
            return;
        }

        SelectedFriend = friend;
        var error = await _call.StartCallAsync(friend.Id);
        if (error is not null)
        {
            await _dialogs.ShowMessageAsync("Can't start the call", error);
        }
    }

    [RelayCommand]
    public async Task EditFriendAsync(FriendViewModel? friend)
    {
        var model = friend is null ? null : _friends.Get(friend.Id);
        if (model is null)
        {
            return;
        }

        var editor = new FriendEditorViewModel(
            $"Edit {model.Nickname}",
            "Save",
            model.Nickname,
            model.Address,
            NetworkInfo.GetLocalIPv4Addresses().Select(a => FormatOwnAddress(a)).ToArray());

        if (await _dialogs.ShowFriendEditorAsync(editor) && editor.TryGetEndpoint(out var host, out var port))
        {
            _friends.Update(model.Id, f =>
            {
                f.Nickname = editor.Nickname.Trim();
                f.Host = host;
                f.Port = port;
                f.IsPending = false;
            });
        }
    }

    [RelayCommand]
    public async Task RemoveFriendAsync(FriendViewModel? friend)
    {
        if (friend is null)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            $"Remove {friend.DisplayName}?",
            $"{friend.DisplayName} ({friend.Address}) and your chat history with them will be deleted from this PC.",
            "Remove",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        if (Call.FriendId == friend.Id)
        {
            _call.HangUp();
        }

        _friends.Remove(friend.Id);
    }

    // ------------------------------------------------------------------ event handlers (UI thread)

    private FriendViewModel CreateFriendViewModel(Friend friend)
    {
        var viewModel = new FriendViewModel(friend) { IsOnline = _network.IsOnline(friend.Id) };
        if (_store.GetLast(friend.Id) is { } last)
        {
            viewModel.LastMessagePreview = PreviewOf(last);
            viewModel.LastActivity = last.Timestamp;
        }
        else
        {
            viewModel.LastActivity = friend.AddedAt;
        }

        return viewModel;
    }

    private void OnFriendChanged(FriendChangedEventArgs e)
    {
        var existing = Find(e.Friend.Id);
        switch (e.Kind)
        {
            case FriendChangeKind.Added when existing is null:
                Friends.Insert(0, CreateFriendViewModel(e.Friend));
                break;

            case FriendChangeKind.Updated when existing is not null:
                existing.Apply(e.Friend);
                break;

            case FriendChangeKind.Removed when existing is not null:
                if (SelectedFriend == existing)
                {
                    SelectedFriend = null;
                }

                Friends.Remove(existing);
                if (_conversations.Remove(existing.Id, out var conversation))
                {
                    conversation.Dispose();
                }

                break;
        }
    }

    private void OnPeerStatusChanged(PeerStatusChangedEventArgs e)
    {
        if (Find(e.FriendId) is { } friend)
        {
            friend.IsOnline = e.IsOnline;
            if (!e.IsOnline)
            {
                friend.IsTyping = false;
            }
        }
    }

    private void OnMessageAdded(ChatMessage message)
    {
        if (Find(message.FriendId) is not { } friend)
        {
            return;
        }

        friend.LastMessagePreview = PreviewOf(message);
        friend.LastActivity = message.Timestamp;

        if (message.IsOutgoing || message.Kind != MessageKind.Text)
        {
            return;
        }

        friend.IsTyping = false;
        bool viewing = SelectedFriend == friend && _window.IsActive;
        if (!viewing)
        {
            if (SelectedFriend != friend)
            {
                friend.UnreadCount++;
            }

            _sounds.PlayMessage();
            if (_friends.Get(friend.Id) is { } model)
            {
                _notifications.ShowMessage(model, message.Text);
            }
        }
    }

    private void OnTyping(Guid friendId)
    {
        if (Find(friendId) is { } friend)
        {
            friend.IsTyping = true;
            friend.TypingUntil = DateTimeOffset.Now + TypingDisplayTime;
            _typingTimer.Start();
        }
    }

    private void ExpireTyping()
    {
        var now = DateTimeOffset.Now;
        bool anyTyping = false;
        foreach (var friend in Friends)
        {
            if (friend.IsTyping && friend.TypingUntil < now)
            {
                friend.IsTyping = false;
            }

            anyTyping |= friend.IsTyping;
        }

        if (!anyTyping)
        {
            _typingTimer.Stop();
        }
    }

    private async Task HandleNotificationAsync(NotificationActivation activation)
    {
        _window.BringToFront();
        if (Find(activation.FriendId) is { } friend)
        {
            SelectedFriend = friend;
        }

        if (Call.FriendId != activation.FriendId)
        {
            return;
        }

        switch (activation.Action)
        {
            case NotificationAction.AcceptCall:
                await _call.AcceptAsync();
                break;
            case NotificationAction.DeclineCall:
                _call.Decline();
                break;
        }
    }

    /// <summary>Handles a toast that launched the app (it was not running).</summary>
    public void HandleLaunchActivation(NotificationActivation activation)
    {
        if (Find(activation.FriendId) is { } friend)
        {
            SelectedFriend = friend;
        }
    }

    private FriendViewModel? Find(Guid id) => Friends.FirstOrDefault(f => f.Id == id);

    private void UpdateMyAddress()
    {
        var address = NetworkInfo.GetLocalIPv4Addresses().FirstOrDefault() ?? "127.0.0.1";
        Me.AddressText = FormatOwnAddress(address);
    }

    private string FormatOwnAddress(string address) =>
        _network.ListenPort == NetworkDefaults.Port || _network.ListenPort == 0 ? address : $"{address}:{_network.ListenPort}";

    private static string PreviewOf(ChatMessage message) => message.Kind switch
    {
        MessageKind.CallEvent => message.Text,
        _ when message.IsOutgoing => "You: " + Format.Preview(message.Text),
        _ => Format.Preview(message.Text),
    };
}
