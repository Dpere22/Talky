using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Talky.Services.UI;
using Talky.ViewModels;

namespace Talky.Views;

/// <summary>Sidebar (friends, call banner, user panel) + content frame + call overlays.</summary>
public sealed partial class ShellPage : Page
{
    public ShellPage()
    {
        ViewModel = App.GetService<ShellViewModel>();
        InitializeComponent();

        App.GetService<INavigationService>().Initialize(ContentFrame, new Dictionary<NavigationTarget, Type>
        {
            [NavigationTarget.Welcome] = typeof(WelcomePage),
            [NavigationTarget.Chat] = typeof(ChatPage),
            [NavigationTarget.Settings] = typeof(SettingsPage),
        });

        ViewModel.Initialize();
    }

    public ShellViewModel ViewModel { get; }

    // Context-menu items live in a DataTemplate, so they resolve their friend from the inherited DataContext.
    private static FriendViewModel? FriendOf(object sender) => (sender as FrameworkElement)?.DataContext as FriendViewModel;

    private void OnCallFriendClick(object sender, RoutedEventArgs e) => ViewModel.CallFriendCommand.Execute(FriendOf(sender));

    private void OnEditFriendClick(object sender, RoutedEventArgs e) => ViewModel.EditFriendCommand.Execute(FriendOf(sender));

    private void OnRemoveFriendClick(object sender, RoutedEventArgs e) => ViewModel.RemoveFriendCommand.Execute(FriendOf(sender));

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.Call.IsTheaterMode)
        {
            ViewModel.Call.ExitTheaterCommand.Execute(null);
            args.Handled = true;
        }
    }
}
