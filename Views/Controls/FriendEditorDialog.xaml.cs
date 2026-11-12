using Microsoft.UI.Xaml.Controls;
using Talky.ViewModels;

namespace Talky.Views.Controls;

public sealed partial class FriendEditorDialog : ContentDialog
{
    public FriendEditorDialog(FriendEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) => NicknameBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    public FriendEditorViewModel ViewModel { get; }
}
