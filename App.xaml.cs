using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Talky.Services.Audio;
using Talky.Services.Calls;
using Talky.Services.Chat;
using Talky.Services.Networking;
using Talky.Services.ScreenShare;
using Talky.Services.Storage;
using Talky.Services.UI;
using Talky.Services.Updates;
using Talky.ViewModels;

namespace Talky;

/// <summary>
/// Composition root: builds the service container, loads local data, starts networking and shows the window.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Debug.WriteLine($"[App] Unhandled: {e.Exception}");
    }

    /// <summary>The main application window.</summary>
    public static Window Window { get; private set; } = null!;

    public static IServiceProvider Services { get; private set; } = null!;

    public static T GetService<T>() where T : class => Services.GetRequiredService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild))
        {
            // The MSIX manifest blocks older Windows at install time; the portable build has to check itself.
            _ = MessageBox(0, "Talky needs Windows 11, version 24H2 or later.", "Talky", MB_ICONERROR);
            Exit();
            return;
        }

        Services =ConfigureServices(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        GetService<ISettingsService>().Load();
        GetService<IFriendService>().Load();

        // Networking first, so the shell sees the real listen port / errors when it initializes.
        GetService<IPeerNetwork>().Start();
        GetService<IChatService>().Start();
        GetService<ICallService>().Start();

        var windowService = (WindowService)GetService<IWindowService>();
        Window = new MainWindow();
        windowService.Attach(Window);
        windowService.ApplyTheme(GetService<ISettingsService>().Current.Theme);
        Window.Closed += OnWindowClosed;

        var notifications = GetService<INotificationService>();
        notifications.Initialize();
        HandleNotificationLaunch(notifications);

        Window.Activate();

        GetService<IUpdateService>().Start();
    }

    private static IServiceProvider ConfigureServices(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        var services = new ServiceCollection();

        // Infrastructure
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(dispatcherQueue));
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INotificationService, NotificationService>();

        // Storage
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IFriendService, FriendService>();
        services.AddSingleton<IMessageStore, MessageStore>();

        // Networking, chat, calls, media
        services.AddSingleton<IPeerNetwork, PeerNetworkService>();
        services.AddSingleton<IVoiceTransport, VoiceTransport>();
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IAudioDeviceService, AudioDeviceService>();
        services.AddSingleton<IVoiceEngine, VoiceEngine>();
        services.AddSingleton<ISoundEffects, SoundEffects>();
        services.AddSingleton<IScreenShareService, ScreenShareService>();
        services.AddSingleton<ICallService, CallService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        // View models
        services.AddSingleton<CallViewModel>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    private static void HandleNotificationLaunch(INotificationService notifications)
    {
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.AppNotification
                && activation.Data is AppNotificationActivatedEventArgs toast
                && notifications.ParseActivation(toast.Arguments) is { } parsed)
            {
                GetService<ShellViewModel>().HandleLaunchActivation(parsed);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Activation args unavailable: {ex.Message}");
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Tell the peer we're leaving instead of letting the call time out.
        GetService<ICallService>().HangUp();
        GetService<INotificationService>().Shutdown();
        GetService<IVoiceEngine>().Stop();
        GetService<IVoiceTransport>().Stop();
        GetService<IPeerNetwork>().Stop();
        GetService<IMessageStore>().Flush();
        (GetService<IUpdateService>() as IDisposable)?.Dispose();
        (GetService<ISoundEffects>() as IDisposable)?.Dispose();
    }

    /// <summary>Windows 11 24H2. Keep in sync with TargetPlatformMinVersion and the manifest MinVersion.</summary>
    private const int MinimumWindowsBuild = 26100;

    private const uint MB_ICONERROR = 0x10;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}
