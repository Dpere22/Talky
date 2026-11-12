using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Talky.ViewModels;
using Talky.Views.Controls;

namespace Talky.Services.UI;

public interface IDialogService
{
    /// <summary>Shows the add/edit friend dialog. Returns true if the user confirmed.</summary>
    Task<bool> ShowFriendEditorAsync(FriendEditorViewModel viewModel);

    Task<bool> ConfirmAsync(string title, string message, string primaryText, bool destructive = false);

    Task ShowMessageAsync(string title, string message);
}

/// <summary>ContentDialog host. Only one dialog can be open at a time in WinUI, so calls are serialized.</summary>
public sealed class DialogService(IWindowService window) : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> ShowFriendEditorAsync(FriendEditorViewModel viewModel)
    {
        var dialog = new FriendEditorDialog(viewModel);
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            // Destructive actions don't get Enter-to-confirm.
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };

        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog);
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            if (window.XamlRoot is not { } root)
            {
                return ContentDialogResult.None;
            }

            dialog.XamlRoot = root;
            dialog.Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
            if (root.Content is FrameworkElement element)
            {
                dialog.RequestedTheme = element.ActualTheme;
            }

            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
