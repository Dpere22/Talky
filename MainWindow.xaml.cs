using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Talky.Services.Storage;
using Talky.Views;

namespace Talky;

/// <summary>Hosts the custom title bar and the shell page.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // A profile name (used to run two instances on one PC) is shown in the title bar.
        if (App.GetService<IAppPaths>().ProfileName is { } profile)
        {
            AppTitleBar.Subtitle = profile;
            Title = $"Talky ({profile})";
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }

        // AppWindow sizes are in physical pixels.
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(780 * scale)));

        // Theater mode switches to the full-screen presenter: hide the custom title bar with it.
        AppWindow.Changed += (sender, args) =>
        {
            if (args.DidPresenterChange)
            {
                AppTitleBar.Visibility = sender.Presenter.Kind == AppWindowPresenterKind.FullScreen
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        };

        RootFrame.Navigate(typeof(ShellPage));
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
}
