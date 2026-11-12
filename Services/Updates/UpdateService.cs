using System.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace Talky.Services.Updates;

public enum UpdateStatus
{
    /// <summary>Not a Velopack install (F5, MSIX, unpacked dev build): updates are someone else's job.</summary>
    Unsupported,
    Idle,
    Checking,
    Downloading,
    UpToDate,
    /// <summary>Downloaded; applied on restart (or automatically the next time Talky starts).</summary>
    ReadyToRestart,
    Failed,
}

public interface IUpdateService
{
    UpdateStatus Status { get; }

    /// <summary>Version of the downloaded update, when <see cref="Status"/> is <see cref="UpdateStatus.ReadyToRestart"/>.</summary>
    string? PendingVersion { get; }

    /// <summary>0–100 while downloading.</summary>
    int Progress { get; }

    DateTimeOffset? LastChecked { get; }

    /// <summary>Status, progress or version changed. Raised on a background thread.</summary>
    event EventHandler? Changed;

    /// <summary>Starts the periodic background check (shortly after startup, then every few hours).</summary>
    void Start();

    /// <summary>Checks GitHub Releases and downloads the update if there is one.</summary>
    Task CheckNowAsync();

    /// <summary>
    /// Hands the downloaded update to Velopack's updater, which waits for Talky to exit, applies it and starts
    /// the new version. The caller then closes the app normally.
    /// </summary>
    bool PrepareRestart();
}

/// <summary>Velopack updates from the project's GitHub Releases (public repo, no token).</summary>
public sealed class UpdateService : IUpdateService, IDisposable
{
    private const string RepoUrl = "https://github.com/Dpere22/Talky";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager? _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private VelopackAsset? _pending;
    private int _lastReportedProgress = -5;

    public UpdateService()
    {
        try
        {
            // The channel (win-x64 / win-arm64) comes from the installed package, so each PC gets its own architecture.
            // TALKY_UPDATE_SOURCE (a local folder of vpk output) is for testing an update before releasing it.
            var manager = Environment.GetEnvironmentVariable("TALKY_UPDATE_SOURCE") is { Length: > 0 } testSource
                ? new UpdateManager(testSource)
                : new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            if (manager.IsInstalled)
            {
                _manager = manager;
                _pending = manager.UpdatePendingRestart;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Unavailable: {ex.Message}");
        }

        Status = _manager is null ? UpdateStatus.Unsupported
            : _pending is not null ? UpdateStatus.ReadyToRestart
            : UpdateStatus.Idle;
        PendingVersion = _pending?.Version.ToString();
    }

    public event EventHandler? Changed;

    public UpdateStatus Status { get; private set; }

    public string? PendingVersion { get; private set; }

    public int Progress { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    public void Start()
    {
        if (_manager is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FirstCheckDelay, _stop.Token);
                using var timer = new PeriodicTimer(CheckInterval);
                do
                {
                    await CheckNowAsync();
                }
                while (await timer.WaitForNextTickAsync(_stop.Token));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async Task CheckNowAsync()
    {
        if (_manager is null || Status == UpdateStatus.ReadyToRestart || !await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            SetStatus(UpdateStatus.Checking);
            var update = await _manager.CheckForUpdatesAsync();
            LastChecked = DateTimeOffset.Now;
            if (update is null)
            {
                SetStatus(UpdateStatus.UpToDate);
                return;
            }

            Progress = 0;
            _lastReportedProgress = -5;
            SetStatus(UpdateStatus.Downloading);
            await _manager.DownloadUpdatesAsync(update, OnProgress, _stop.Token);

            _pending = update.TargetFullRelease;
            PendingVersion = _pending.Version.ToString();
            SetStatus(UpdateStatus.ReadyToRestart);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Offline or GitHub unreachable is normal for a LAN app: just try again later.
            Debug.WriteLine($"[Update] Check failed: {ex.Message}");
            LastChecked = DateTimeOffset.Now;
            SetStatus(UpdateStatus.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool PrepareRestart()
    {
        if (_manager is null || _pending is null)
        {
            return false;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: true);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Could not start the updater: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private void OnProgress(int percent)
    {
        Progress = percent;

        // Velopack reports very often; the UI only needs whole steps.
        if (percent / 5 != _lastReportedProgress / 5)
        {
            _lastReportedProgress = percent;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetStatus(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
