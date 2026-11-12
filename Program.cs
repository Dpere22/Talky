using Microsoft.Windows.AppNotifications;
using Velopack;

namespace Talky;

/// <summary>
/// Entry point (the XAML-generated Main is disabled in Talky.csproj).
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must run first: when Velopack's installer/updater launches Talky with a hook argument, this handles it
        // and exits. On a normal start it applies an already downloaded update (restarting into the new version).
        // In F5 / MSIX / non-Velopack builds it does nothing.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => RemoveNotificationRegistration())
            .Run();

        XamlGeneratedProgram.XamlGeneratedMain();
    }

    /// <summary>Unpackaged toasts register an AUMID + COM activator under HKCU; don't leave them behind.</summary>
    private static void RemoveNotificationRegistration()
    {
        try
        {
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception)
        {
            // Best effort: uninstall must not fail because of this.
        }
    }
}
