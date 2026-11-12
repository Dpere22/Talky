using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Talky.Models;

namespace Talky.Services.UI;

public interface IWindowService
{
    nint Handle { get; }

    bool IsActive { get; }

    XamlRoot? XamlRoot { get; }

    bool IsFullScreen { get; }

    event EventHandler? ActivationChanged;

    void BringToFront();

    /// <summary>Flashes the taskbar button until the window is focused (no-op if already focused).</summary>
    void FlashTaskbar();

    void SetFullScreen(bool fullScreen);

    void ApplyTheme(AppTheme theme);

    /// <summary>Closes the main window, which runs the normal shutdown and exits the app.</summary>
    void Close();
}

public sealed partial class WindowService : IWindowService
{
    private Window? _window;

    public event EventHandler? ActivationChanged;

    public nint Handle => _window is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public bool IsActive { get; private set; } = true;

    public XamlRoot? XamlRoot => _window?.Content?.XamlRoot;

    public bool IsFullScreen => _window?.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public void Attach(Window window)
    {
        _window = window;
        window.Activated += (_, e) =>
        {
            IsActive = e.WindowActivationState != WindowActivationState.Deactivated;
            ActivationChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Close() => _window?.Close();

    public void BringToFront()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _window.AppWindow.Show();
        _window.Activate();
        SetForegroundWindow(Handle);
    }

    public void FlashTaskbar()
    {
        if (_window is null || IsActive)
        {
            return;
        }

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = Handle,
            Flags = FlashAll | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    public void SetFullScreen(bool fullScreen)
    {
        _window?.AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    public void ApplyTheme(AppTheme theme)
    {
        if (_window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        if (_window is not null)
        {
            _window.AppWindow.TitleBar.PreferredTheme = theme switch
            {
                AppTheme.Light => TitleBarTheme.Light,
                AppTheme.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode,
            };
        }
    }

    private const uint FlashAll = 0x3;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWindowInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}
