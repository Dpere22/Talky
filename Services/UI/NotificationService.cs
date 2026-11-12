using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Talky.Models;
using Talky.Services.Storage;

namespace Talky.Services.UI;

public enum NotificationAction
{
    OpenChat,
    AcceptCall,
    DeclineCall,
}

public sealed record NotificationActivation(NotificationAction Action, Guid FriendId);

public interface INotificationService
{
    /// <summary>The user clicked a toast or one of its buttons. Background thread.</summary>
    event EventHandler<NotificationActivation>? Activated;

    void Initialize();

    void Shutdown();

    /// <summary>Parses the arguments of a toast that launched the app.</summary>
    NotificationActivation? ParseActivation(IDictionary<string, string> arguments);

    void ShowMessage(Friend friend, string text);

    void ShowIncomingCall(Friend friend);

    void ClearIncomingCall();
}

/// <summary>Windows App SDK app notifications (toasts) plus taskbar flashing, only when Talky is in the background.</summary>
public sealed class NotificationService(ISettingsService settings, IWindowService window, IUiDispatcher dispatcher) : INotificationService
{
    private const string CallTag = "incoming-call";
    private const string CallGroup = "calls";

    private bool _registered;

    public event EventHandler<NotificationActivation>? Activated;

    public void Initialize()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return;
            }

            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // E.g. running unpackaged without registration: toasts are a nice-to-have.
            Debug.WriteLine($"[Notify] Toasts unavailable: {ex.Message}");
        }
    }

    public void Shutdown()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception)
        {
        }
    }

    public NotificationActivation? ParseActivation(IDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("friendId", out var id) || !Guid.TryParse(id, out var friendId))
        {
            return null;
        }

        var action = arguments.TryGetValue("action", out var a) ? a : "openChat";
        return action switch
        {
            "accept" => new NotificationActivation(NotificationAction.AcceptCall, friendId),
            "decline" => new NotificationActivation(NotificationAction.DeclineCall, friendId),
            _ => new NotificationActivation(NotificationAction.OpenChat, friendId),
        };
    }

    public void ShowMessage(Friend friend, string text)
    {
        dispatcher.Post(() =>
        {
            if (window.IsActive)
            {
                return;
            }

            window.FlashTaskbar();
            if (!_registered || !settings.Current.ShowNotifications)
            {
                return;
            }

            var preview = text.Length > 200 ? text[..200] + "…" : text;
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "openChat")
                .AddArgument("friendId", friend.Id.ToString())
                .AddText(friend.Nickname)
                .AddText(preview)
                .BuildNotification();
            TryShow(notification);
        });
    }

    public void ShowIncomingCall(Friend friend)
    {
        dispatcher.Post(() =>
        {
            if (window.IsActive)
            {
                return;
            }

            window.FlashTaskbar();
            if (!_registered || !settings.Current.ShowNotifications)
            {
                return;
            }

            var notification = new AppNotificationBuilder()
                .SetScenario(AppNotificationScenario.IncomingCall)
                .AddArgument("action", "openChat")
                .AddArgument("friendId", friend.Id.ToString())
                .AddText($"{friend.Nickname} is calling")
                .AddText("Talky voice call")
                .AddButton(new AppNotificationButton("Accept")
                    .AddArgument("action", "accept")
                    .AddArgument("friendId", friend.Id.ToString())
                    .SetButtonStyle(AppNotificationButtonStyle.Success))
                .AddButton(new AppNotificationButton("Decline")
                    .AddArgument("action", "decline")
                    .AddArgument("friendId", friend.Id.ToString())
                    .SetButtonStyle(AppNotificationButtonStyle.Critical))
                .MuteAudio() // our own ringtone is already playing
                .BuildNotification();
            notification.Tag = CallTag;
            notification.Group = CallGroup;
            TryShow(notification);
        });
    }

    public void ClearIncomingCall()
    {
        if (!_registered)
        {
            return;
        }

        _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(CallTag, CallGroup).AsTask().ContinueWith(
            t => Debug.WriteLine($"[Notify] Clear failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static void TryShow(AppNotification notification)
    {
        try
        {
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notify] Show failed: {ex.Message}");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (ParseActivation(args.Arguments) is { } activation)
        {
            Activated?.Invoke(this, activation);
        }
    }
}
