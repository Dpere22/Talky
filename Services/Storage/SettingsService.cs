using Talky.Models;

namespace Talky.Services.Storage;

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Raised (on the caller's thread) after <see cref="Update"/> saved new settings.</summary>
    event EventHandler? Changed;

    void Load();

    /// <summary>Mutates the settings and persists them.</summary>
    void Update(Action<AppSettings> mutate);
}

public sealed class SettingsService(IAppPaths paths) : ISettingsService
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(paths.DataDirectory, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        var loaded = JsonFile.Read(_path, StorageJsonContext.Default.AppSettings);
        if (loaded is null)
        {
            // First run: persist so the generated PeerId stays stable.
            Current = new AppSettings();
            Save();
            return;
        }

        if (string.IsNullOrWhiteSpace(loaded.DisplayName))
        {
            loaded.DisplayName = Environment.UserName;
        }

        if (loaded.Port is < 1024 or > 65535)
        {
            loaded.Port = NetworkDefaults.Port;
        }

        Current = loaded;
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        try
        {
            JsonFile.Write(_path, Current, StorageJsonContext.Default.AppSettings);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Save failed: {ex.Message}");
        }
    }
}
