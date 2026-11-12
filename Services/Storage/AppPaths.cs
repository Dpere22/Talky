using System.Diagnostics;

namespace Talky.Services.Storage;

public interface IAppPaths
{
    /// <summary>Root folder for settings, friends and message history.</summary>
    string DataDirectory { get; }

    string MessagesDirectory { get; }

    /// <summary>Optional profile name (from <c>--profile name</c> or <c>TALKY_PROFILE</c>), used to run several instances on one PC.</summary>
    string? ProfileName { get; }
}

public sealed class AppPaths : IAppPaths
{
    /// <summary>Everything Talky stores, relative to the data root (used when migrating the old location).</summary>
    private static readonly string[] DataEntries = ["settings.json", "friends.json", "messages", "profiles"];

    public AppPaths()
    {
        ProfileName = ReadProfileName();

        string root;
        try
        {
            // Packaged: per-package local folder.
            root = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (InvalidOperationException)
        {
            // Unpackaged: %APPDATA%\Talky. Not %LOCALAPPDATA%\Talky: that is the Velopack install folder,
            // which the installer and uninstaller replace wholesale.
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Talky");
            MigrateFromLocalAppData(root);
        }

        DataDirectory = ProfileName is null ? root : Path.Combine(root, "profiles", ProfileName);
        MessagesDirectory = Path.Combine(DataDirectory, "messages");
        Directory.CreateDirectory(MessagesDirectory);
    }

    public string DataDirectory { get; }

    public string MessagesDirectory { get; }

    public string? ProfileName { get; }

    /// <summary>
    /// Talky 1.0 (portable zip) kept its data in %LOCALAPPDATA%\Talky. Move it once, entry by entry, so that
    /// a Velopack install living in the same folder (Update.exe, current\, packages\) is never touched.
    /// </summary>
    private static void MigrateFromLocalAppData(string newRoot)
    {
        var oldRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talky");
        if (Directory.Exists(newRoot) || !Directory.Exists(oldRoot))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(newRoot);
            foreach (var entry in DataEntries)
            {
                var from = Path.Combine(oldRoot, entry);
                var to = Path.Combine(newRoot, entry);
                if (File.Exists(from))
                {
                    File.Move(from, to);
                }
                else if (Directory.Exists(from))
                {
                    MoveDirectory(from, to);
                }
            }
        }
        catch (Exception ex)
        {
            // Worst case the user starts fresh; the old files are still where they were.
            Debug.WriteLine($"[Paths] Migrating {oldRoot} failed: {ex.Message}");
        }
    }

    private static void MoveDirectory(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException)
        {
            // Different volumes (redirected AppData): copy, then remove the original.
            CopyDirectory(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    private static string? ReadProfileName()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--profile", StringComparison.OrdinalIgnoreCase))
            {
                return Sanitize(args[i + 1]);
            }
        }

        return Sanitize(Environment.GetEnvironmentVariable("TALKY_PROFILE"));
    }

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length == 0 ? null : clean;
    }
}
