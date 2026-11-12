using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Talky.Services.UI;
using Talky.Services.Updates;

namespace Talky.ViewModels;

/// <summary>App updates: the "update ready" banner in the shell and the Updates card in Settings. Singleton.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly IWindowService _window;

    public UpdatesViewModel(IUpdateService updates, IWindowService window, IUiDispatcher dispatcher, CallViewModel call)
    {
        _updates = updates;
        _window = window;
        Call = call;
        Refresh();
        updates.Changed += (_, _) => dispatcher.Post(Refresh);
    }

    public CallViewModel Call { get; }

    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>False for dev / MSIX builds, which aren't updated through GitHub Releases.</summary>
    public bool IsSupported => _updates.Status != UpdateStatus.Unsupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool IsUpdateReady { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    [ObservableProperty]
    public partial string ReadyMessage { get; private set; }

    public bool CanCheck => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => _updates.CheckNowAsync();

    [RelayCommand]
    private void RestartToUpdate()
    {
        // The updater waits for Talky to exit; closing the window hangs up and saves everything as usual.
        if (_updates.PrepareRestart())
        {
            _window.Close();
        }
        else
        {
            StatusText = "Couldn't start the updater. The update will be installed the next time Talky starts.";
        }
    }

    private void Refresh()
    {
        var status = _updates.Status;
        IsBusy = status is UpdateStatus.Checking or UpdateStatus.Downloading;
        IsUpdateReady = status == UpdateStatus.ReadyToRestart;
        ReadyMessage = $"Talky {_updates.PendingVersion} is ready. Restart to finish updating.";
        StatusText = status switch
        {
            UpdateStatus.Unsupported => "Updates are installed by the Talky installer. This copy wasn't installed with it.",
            UpdateStatus.Checking => "Checking for updates…",
            UpdateStatus.Downloading => $"Downloading update… {_updates.Progress}%",
            UpdateStatus.UpToDate => $"You're up to date. Last checked {_updates.LastChecked:t}.",
            UpdateStatus.ReadyToRestart => $"Talky {_updates.PendingVersion} is ready. It installs when you restart Talky.",
            UpdateStatus.Failed => "Couldn't reach GitHub to check for updates. Talky will try again later.",
            _ => "Talky checks for updates automatically.",
        };
    }
}
