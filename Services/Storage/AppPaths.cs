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
            // Unpackaged: %LOCALAPPDATA%\Talky
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talky");
        }

        DataDirectory = ProfileName is null ? root : Path.Combine(root, "profiles", ProfileName);
        MessagesDirectory = Path.Combine(DataDirectory, "messages");
        Directory.CreateDirectory(MessagesDirectory);
    }

    public string DataDirectory { get; }

    public string MessagesDirectory { get; }

    public string? ProfileName { get; }

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
