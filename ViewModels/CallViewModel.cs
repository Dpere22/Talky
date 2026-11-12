using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Talky.Helpers;
using Talky.Models;
using Talky.Services.Calls;
using Talky.Services.ScreenShare;
using Talky.Services.Storage;
using Talky.Services.UI;

namespace Talky.ViewModels;

/// <summary>
/// The single active call (Talky is 1:1). Mute / deafen / noise suppression are global like in
/// Discord, so this view model is also bound by the sidebar's user panel when no call is active.
/// </summary>
public sealed partial class CallViewModel : ObservableObject
{
    private readonly ICallService _call;
    private readonly IFriendService _friends;
    private readonly IUiDispatcher _dispatcher;
    private readonly IWindowService _window;
    private readonly IDialogService _dialogs;
    private readonly DispatcherQueueTimer _timer;
    private bool _refreshing;

    public CallViewModel(ICallService call, IFriendService friends, IUiDispatcher dispatcher, IWindowService window, IDialogService dialogs)
    {
        _call = call;
        _friends = friends;
        _dispatcher = dispatcher;
        _window = window;
        _dialogs = dialogs;
        FriendName = string.Empty;
        StatusText = string.Empty;
        BannerTitle = string.Empty;
        BannerSubtitle = string.Empty;

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Tick();

        call.StateChanged += (_, _) => _dispatcher.Post(Refresh);
        Refresh();
    }

    public IRemoteScreen RemoteScreen => _call.RemoteScreen;

    public bool NoiseSuppressionAvailable => _call.NoiseSuppressionAvailable;

    [ObservableProperty]
    public partial Guid? FriendId { get; private set; }

    [ObservableProperty]
    public partial string FriendName { get; private set; }

    /// <summary>Any call phase (ringing either way, or connected).</summary>
    [ObservableProperty]
    public partial bool IsInCall { get; private set; }

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial bool IsOutgoingRinging { get; private set; }

    [ObservableProperty]
    public partial bool IsIncomingRinging { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>Sidebar banner: "Voice connected" / "Calling…".</summary>
    [ObservableProperty]
    public partial string BannerTitle { get; private set; }

    /// <summary>Sidebar banner: "Bob · 4:12".</summary>
    [ObservableProperty]
    public partial string BannerSubtitle { get; private set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsDeafened { get; set; }

    [ObservableProperty]
    public partial bool NoiseSuppressionEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsLocalSpeaking { get; private set; }

    [ObservableProperty]
    public partial bool IsRemoteSpeaking { get; private set; }

    [ObservableProperty]
    public partial bool RemoteMuted { get; private set; }

    [ObservableProperty]
    public partial bool RemoteDeafened { get; private set; }

    [ObservableProperty]
    public partial bool IsSharing { get; private set; }

    [ObservableProperty]
    public partial string? SharingSourceName { get; private set; }

    [ObservableProperty]
    public partial bool IsRemoteSharing { get; private set; }

    [ObservableProperty]
    public partial string? MediaWarning { get; private set; }

    /// <summary>Playback volume 0..200 %.</summary>
    [ObservableProperty]
    public partial double VolumePercent { get; set; }

    /// <summary>The remote screen fills the whole window.</summary>
    [ObservableProperty]
    public partial bool IsTheaterMode { get; private set; }

    public bool HasMediaWarning => !string.IsNullOrEmpty(MediaWarning);

    partial void OnIsMutedChanged(bool value)
    {
        if (!_refreshing)
        {
            _call.IsMuted = value;
        }
    }

    partial void OnIsDeafenedChanged(bool value)
    {
        if (!_refreshing)
        {
            _call.IsDeafened = value;
        }
    }

    partial void OnNoiseSuppressionEnabledChanged(bool value)
    {
        if (!_refreshing)
        {
            _call.NoiseSuppressionEnabled = value;
        }
    }

    partial void OnVolumePercentChanged(double value)
    {
        if (!_refreshing)
        {
            _call.OutputVolume = value / 100.0;
        }
    }

    partial void OnMediaWarningChanged(string? value) => OnPropertyChanged(nameof(HasMediaWarning));

    partial void OnIsRemoteSharingChanged(bool value)
    {
        if (!value && IsTheaterMode)
        {
            ExitTheater();
        }
    }

    [RelayCommand]
    private async Task AcceptAsync() => await _call.AcceptAsync();

    [RelayCommand]
    private void Decline() => _call.Decline();

    [RelayCommand]
    private void HangUp() => _call.HangUp();

    [RelayCommand]
    private async Task ToggleScreenShareAsync()
    {
        if (IsSharing)
        {
            _call.StopScreenShare();
            return;
        }

        if (!IsActive)
        {
            return;
        }

        if (!await _call.StartScreenShareAsync() && IsActive && !IsSharing)
        {
            // Cancelled picker returns false too; only complain when capture itself is unavailable.
            if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
            {
                await _dialogs.ShowMessageAsync("Screen sharing unavailable", "This PC doesn't support Windows screen capture.");
            }
        }
    }

    [RelayCommand]
    private void EnterTheater()
    {
        if (!IsRemoteSharing)
        {
            return;
        }

        IsTheaterMode = true;
        _window.SetFullScreen(true);
    }

    [RelayCommand]
    private void ExitTheater()
    {
        IsTheaterMode = false;
        _window.SetFullScreen(false);
    }

    private void Refresh()
    {
        var call = _call.Current;
        _refreshing = true;
        try
        {
            IsMuted = _call.IsMuted;
            IsDeafened = _call.IsDeafened;
            NoiseSuppressionEnabled = _call.NoiseSuppressionEnabled;
            VolumePercent = Math.Round(_call.OutputVolume * 100);

            FriendId = call?.FriendId;
            FriendName = call is null ? string.Empty : _friends.Get(call.FriendId)?.Nickname ?? "Unknown";
            IsInCall = call is not null;
            IsActive = call?.Phase == CallPhase.Active;
            IsOutgoingRinging = call?.Phase == CallPhase.OutgoingRinging;
            IsIncomingRinging = call?.Phase == CallPhase.IncomingRinging;
            RemoteMuted = call?.RemoteMuted ?? false;
            RemoteDeafened = call?.RemoteDeafened ?? false;
            IsSharing = call?.LocalSharing ?? false;
            SharingSourceName = IsSharing ? _call.SharingSourceName : null;
            IsRemoteSharing = call?.RemoteSharing ?? false;
            MediaWarning = call?.MediaWarning;
        }
        finally
        {
            _refreshing = false;
        }

        if (IsInCall)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            IsLocalSpeaking = IsRemoteSpeaking = false;
            if (IsTheaterMode)
            {
                ExitTheater();
            }
        }

        Tick();
    }

    private void Tick()
    {
        var call = _call.Current;
        IsLocalSpeaking = call?.Phase == CallPhase.Active && _call.IsLocalSpeaking;
        IsRemoteSpeaking = call?.Phase == CallPhase.Active && _call.IsRemoteSpeaking;
        StatusText = call switch
        {
            null => string.Empty,
            { Phase: CallPhase.OutgoingRinging } => "Calling…",
            { Phase: CallPhase.IncomingRinging } => "Incoming call…",
            { ConnectedAt: { } start } => Format.Duration(DateTimeOffset.Now - start),
            _ => "Connecting…",
        };
        BannerTitle = IsActive ? "Voice connected" : StatusText;
        BannerSubtitle = IsActive ? $"{FriendName} · {StatusText}" : FriendName;
    }
}
