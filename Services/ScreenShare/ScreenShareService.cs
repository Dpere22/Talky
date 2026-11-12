using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Talky.Models;
using Talky.Protocol;
using Talky.Services.Networking;
using Talky.Services.Storage;
using Talky.Services.UI;

namespace Talky.Services.ScreenShare;

/// <summary>The peer's shared screen, as seen by the view layer.</summary>
public interface IRemoteScreen
{
    bool IsActive { get; }

    string? SourceName { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>Remote share started/stopped. Background thread.</summary>
    event EventHandler? StateChanged;

    /// <summary>A new frame was decoded. Background thread.</summary>
    event EventHandler? FrameReady;

    bool CopyFrame(Span<byte> destination, out int width, out int height);
}

public interface IScreenShareService
{
    bool IsSharing { get; }

    string? SharingSourceName { get; }

    /// <summary>Local sharing started/stopped. May be raised from a background thread.</summary>
    event EventHandler? LocalSharingChanged;

    IRemoteScreen RemoteScreen { get; }

    /// <summary>Called by the call layer when a call becomes active / ends.</summary>
    void BindCall(Guid callId, Guid friendId);

    void UnbindCall();

    /// <summary>Shows the Windows capture picker and starts streaming. Call from the UI thread.</summary>
    Task<bool> StartSharingAsync();

    void StopSharing();
}

public sealed class ScreenShareService : IScreenShareService, IRemoteScreen, IPacketHandler
{
    private readonly IPeerNetwork _network;
    private readonly ISettingsService _settings;
    private readonly IWindowService _window;
    private readonly TileFrameDecoder _decoder = new();
    private readonly object _gate = new();
    private CallBinding? _call;
    private ScreenCaptureSession? _capture;
    private CancellationTokenSource? _shareCts;
    private volatile bool _remoteActive;

    public ScreenShareService(IPeerNetwork network, ISettingsService settings, IWindowService window)
    {
        _network = network;
        _settings = settings;
        _window = window;
        network.RegisterHandler(this);
    }

    public event EventHandler? LocalSharingChanged;

    public event EventHandler? StateChanged;

    public event EventHandler? FrameReady;

    public bool IsSharing { get; private set; }

    public string? SharingSourceName { get; private set; }

    public IRemoteScreen RemoteScreen => this;

    bool IRemoteScreen.IsActive => _remoteActive;

    public string? SourceName { get; private set; }

    public int Width => _decoder.Width;

    public int Height => _decoder.Height;

    public void BindCall(Guid callId, Guid friendId)
    {
        lock (_gate)
        {
            _call = new CallBinding(callId, friendId);
        }
    }

    public void UnbindCall()
    {
        lock (_gate)
        {
            _call = null;
        }

        StopSharing();
        if (_remoteActive)
        {
            _remoteActive = false;
            _decoder.Reset();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<bool> StartSharingAsync()
    {
        CallBinding? call;
        lock (_gate)
        {
            call = _call;
            if (call is null)
            {
                return false;
            }

            if (IsSharing)
            {
                return true;
            }
        }

        ScreenCaptureSession? capture;
        try
        {
            capture = await ScreenCaptureSession.PickAsync(_window.Handle);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Screen] Capture failed to start: {ex}");
            return false;
        }

        if (capture is null)
        {
            return false;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_call != call || IsSharing)
            {
                capture.Dispose();
                return IsSharing;
            }

            cts = new CancellationTokenSource();
            _capture = capture;
            _shareCts = cts;
            IsSharing = true;
            SharingSourceName = capture.DisplayName;
        }

        capture.Closed += (_, _) => StopSharing();
        LocalSharingChanged?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() => ShareLoopAsync(capture, call, cts.Token));
        return true;
    }

    public void StopSharing()
    {
        CallBinding? call;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!IsSharing)
            {
                return;
            }

            IsSharing = false;
            SharingSourceName = null;
            cts = _shareCts;
            _shareCts = null;
            _capture = null;
            call = _call;
        }

        cts?.Cancel();
        if (call is not null)
        {
            _network.Send(call.FriendId, PacketType.ScreenStop, new ScreenStopPayload(call.CallId));
        }

        LocalSharingChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CopyFrame(Span<byte> destination, out int width, out int height) =>
        _decoder.CopyTo(destination, out width, out height);

    // ------------------------------------------------------------------ sending

    private async Task ShareLoopAsync(ScreenCaptureSession capture, CallBinding call, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var (maxWidth, maxHeight) = settings.ShareResolution switch
        {
            ScreenShareResolution.P1440 => (2560, 1440),
            ScreenShareResolution.P1080 => (1920, 1080),
            ScreenShareResolution.P720 => (1280, 720),
            _ => (0, 0),
        };

        int frameRate = Math.Clamp(settings.ShareFrameRate, 5, 240);
        // Capture is already paced by the display; only throttle explicitly for low frame-rate caps
        // (Task.Delay's ~15 ms granularity would otherwise eat into 60 fps).
        TimeSpan? minInterval = frameRate < 60 ? TimeSpan.FromSeconds(1.0 / frameRate) : null;

        var encoder = new TileFrameEncoder { Quality = settings.ShareQuality };
        var output = new ArrayBufferWriter<byte>(1 << 20);
        (int Width, int Height) announced = default;
        CapturedFrame? last = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                var frame = await capture.NextFrameAsync(maxWidth, maxHeight, TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    // Nothing new on screen: give motion-quality tiles a chance to refine.
                    if (last is null || !encoder.HasPendingRefinement)
                    {
                        continue;
                    }

                    frame = last;
                }

                last = frame;
                var current = frame.Value;
                if ((current.Width, current.Height) != announced)
                {
                    announced = (current.Width, current.Height);
                    _network.Send(call.FriendId, PacketType.ScreenStart, new ScreenStartPayload(call.CallId, current.Width, current.Height, capture.DisplayName));
                }

                if (await encoder.EncodeAsync(current.Pixels, current.Width, current.Height, output).ConfigureAwait(false)
                    && MemoryMarshal.TryGetArray(output.WrittenMemory, out var segment))
                {
                    // Awaiting the write gives natural back-pressure: we never queue more than one frame.
                    if (!await _network.SendBulkAsync(call.FriendId, PacketType.ScreenFrame, segment.Array!, segment.Count).ConfigureAwait(false))
                    {
                        break;
                    }
                }

                if (minInterval is { } interval)
                {
                    var remaining = interval - Stopwatch.GetElapsedTime(started);
                    if (remaining > TimeSpan.FromMilliseconds(2))
                    {
                        await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Screen] Share loop failed: {ex}");
        }
        finally
        {
            capture.Dispose();
            bool stillCurrent;
            lock (_gate)
            {
                stillCurrent = IsSharing && _call == call && _shareCts?.Token == cancellationToken;
            }

            if (stillCurrent)
            {
                StopSharing();
            }
        }
    }

    // ------------------------------------------------------------------ receiving

    public bool CanHandle(PacketType type) => type is PacketType.ScreenStart or PacketType.ScreenFrame or PacketType.ScreenStop;

    public async ValueTask HandlePacketAsync(Guid friendId, PacketType type, ReadOnlyMemory<byte> payload)
    {
        CallBinding? call;
        lock (_gate)
        {
            call = _call;
        }

        if (call is null || call.FriendId != friendId)
        {
            return;
        }

        switch (type)
        {
            case PacketType.ScreenStart:
                var start = Payload.Deserialize<ScreenStartPayload>(payload.Span);
                if (start is null || start.CallId != call.CallId)
                {
                    return;
                }

                bool wasActive = _remoteActive;
                SourceName = start.SourceName;
                _remoteActive = true;
                if (!wasActive)
                {
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }

                break;

            case PacketType.ScreenFrame:
                if (_remoteActive && await _decoder.ApplyAsync(payload).ConfigureAwait(false))
                {
                    FrameReady?.Invoke(this, EventArgs.Empty);
                }

                break;

            case PacketType.ScreenStop:
                if (_remoteActive)
                {
                    _remoteActive = false;
                    _decoder.Reset();
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }

                break;
        }
    }

    private sealed record CallBinding(Guid CallId, Guid FriendId);
}
