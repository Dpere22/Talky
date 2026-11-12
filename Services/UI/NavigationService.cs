using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Talky.Services.UI;

public enum NavigationTarget
{
    Welcome,
    Chat,
    Settings,
}

/// <summary>Drives the shell's content frame without view models knowing about page types.</summary>
public interface INavigationService
{
    NavigationTarget? Current { get; }

    object? CurrentParameter { get; }

    void Initialize(Frame frame, IReadOnlyDictionary<NavigationTarget, Type> pages);

    void Navigate(NavigationTarget target, object? parameter = null);
}

public sealed class NavigationService : INavigationService
{
    private Frame? _frame;
    private IReadOnlyDictionary<NavigationTarget, Type> _pages = new Dictionary<NavigationTarget, Type>();

    public NavigationTarget? Current { get; private set; }

    public object? CurrentParameter { get; private set; }

    public void Initialize(Frame frame, IReadOnlyDictionary<NavigationTarget, Type> pages)
    {
        _frame = frame;
        _pages = pages;
    }

    public void Navigate(NavigationTarget target, object? parameter = null)
    {
        if (_frame is null || (Current == target && ReferenceEquals(CurrentParameter, parameter)))
        {
            return;
        }

        Current = target;
        CurrentParameter = parameter;
        _frame.Navigate(_pages[target], parameter, new EntranceNavigationTransitionInfo());
        _frame.BackStack.Clear();
    }
}
