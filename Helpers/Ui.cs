using Microsoft.UI.Xaml;

namespace Talky.Helpers;

/// <summary>Pure functions used from x:Bind (replaces value converters).</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAll(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool AndNot(bool a, bool b) => a && !b;

    public static bool IsNotEmpty(string? value) => !string.IsNullOrEmpty(value);

    /// <summary>Badge on a call avatar: muted mic or deafened speaker.</summary>
    public static string? MuteBadge(bool muted, bool deafened) => deafened ? "" : muted ? "" : null;

    /// <summary>The call area grows when a screen is being shown.</summary>
    public static GridLength CallRowHeight(bool callHere, bool remoteSharing) =>
        !callHere ? new GridLength(0) : remoteSharing ? new GridLength(3, GridUnitType.Star) : GridLength.Auto;

    public static GridLength MessagesRowHeight(bool callHere, bool remoteSharing) => new(1, GridUnitType.Star);

    public static string Percent(double value) => $"{value:0}%";

    public static string TypingText(string name) => $"{name} is typing…";

    public static string OfflineHint(string name) => $"{name} is offline — messages will be delivered when they're back.";

    public static string BeginningText(string name) => $"This is the beginning of your conversation with {name}.";

    public static string PendingFriendText(string name, string address) =>
        $"{name} ({address}) reached out to you but isn't in your friends yet.";

    public static string FriendStatusLine(string status, string address) => $"{status} · {address}";

    public static string SharingText(string? source) => string.IsNullOrEmpty(source) ? "You're sharing your screen" : $"You're sharing “{source}”";

    public static string ShareToolTip(bool sharing) => sharing ? "Stop sharing" : "Share your screen";

    public static string MuteToolTip(bool muted) => muted ? "Unmute" : "Mute";

    public static string DeafenToolTip(bool deafened) => deafened ? "Undeafen" : "Deafen";

    public static string NoiseToolTip(bool on, bool available) =>
        !available ? "Noise suppression isn't available on this device" : on ? "Noise suppression: on" : "Noise suppression: off";

    public static double RingingOpacity(bool active) => active ? 1.0 : 0.45;
}
