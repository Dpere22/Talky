using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Talky.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace Talky.Views;

public sealed partial class ChatPage : Page
{
    public ChatPage()
    {
        InitializeComponent();
    }

    public ConversationViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (ConversationViewModel)e.Parameter;
        base.OnNavigatedTo(e);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (ViewModel.Messages.Count > 0)
        {
            MessageList.ScrollIntoView(ViewModel.Messages[^1], ScrollIntoViewAlignment.Leading);
        }

        Composer.Focus(FocusState.Programmatic);
    }

    /// <summary>Enter sends, Shift+Enter inserts a new line.</summary>
    private void OnComposerPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shift.HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        e.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
        }
    }
}
