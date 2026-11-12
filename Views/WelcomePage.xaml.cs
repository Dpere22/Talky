using Microsoft.UI.Xaml.Controls;
using Talky.ViewModels;

namespace Talky.Views;

public sealed partial class WelcomePage : Page
{
    public WelcomePage()
    {
        ViewModel = App.GetService<ShellViewModel>();
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }
}
