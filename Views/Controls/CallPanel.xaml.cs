using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Talky.ViewModels;

namespace Talky.Views.Controls;

public sealed partial class CallPanel : UserControl
{
    public static readonly DependencyProperty CallProperty = DependencyProperty.Register(
        nameof(Call), typeof(CallViewModel), typeof(CallPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty FriendProperty = DependencyProperty.Register(
        nameof(Friend), typeof(FriendViewModel), typeof(CallPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty MeProperty = DependencyProperty.Register(
        nameof(Me), typeof(LocalUserViewModel), typeof(CallPanel), new PropertyMetadata(null));

    public CallPanel()
    {
        InitializeComponent();
    }

    public CallViewModel Call
    {
        get => (CallViewModel)GetValue(CallProperty);
        set => SetValue(CallProperty, value);
    }

    public FriendViewModel Friend
    {
        get => (FriendViewModel)GetValue(FriendProperty);
        set => SetValue(FriendProperty, value);
    }

    public LocalUserViewModel Me
    {
        get => (LocalUserViewModel)GetValue(MeProperty);
        set => SetValue(MeProperty, value);
    }
}
