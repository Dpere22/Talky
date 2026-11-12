using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Talky.Views.Controls;

public enum ButtonTint
{
    Danger,
    Success,
}

/// <summary>
/// An accent button re-coloured red (hang up / decline) or green (accept) through WinUI
/// lightweight styling: the accent brushes are overridden in the button's own resources, so
/// hover/pressed states keep working. High-contrast themes keep the system colours.
/// </summary>
public sealed partial class TintedButton : Button
{
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(ButtonTint), typeof(TintedButton), new PropertyMetadata(ButtonTint.Danger, (d, _) => ((TintedButton)d).ApplyTint()));

    public TintedButton()
    {
        Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        ApplyTint();
    }

    public ButtonTint Tint
    {
        get => (ButtonTint)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    private void ApplyTint()
    {
        if (new AccessibilitySettings().HighContrast)
        {
            return;
        }

        var (rest, hover, pressed) = Tint == ButtonTint.Danger
            ? (Color.FromArgb(255, 0xC4, 0x2B, 0x1C), Color.FromArgb(255, 0xB0, 0x27, 0x19), Color.FromArgb(255, 0x9C, 0x22, 0x16))
            : (Color.FromArgb(255, 0x0F, 0x7B, 0x0F), Color.FromArgb(255, 0x0D, 0x6E, 0x0D), Color.FromArgb(255, 0x0B, 0x60, 0x0B));

        Resources["AccentButtonBackground"] = new SolidColorBrush(rest);
        Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(pressed);
        Resources["AccentButtonBorderBrush"] = new SolidColorBrush(rest);
        Resources["AccentButtonBorderBrushPointerOver"] = new SolidColorBrush(hover);
        Resources["AccentButtonBorderBrushPressed"] = new SolidColorBrush(pressed);
        Resources["AccentButtonForeground"] = new SolidColorBrush(Colors.White);
        Resources["AccentButtonForegroundPointerOver"] = new SolidColorBrush(Colors.White);
        Resources["AccentButtonForegroundPressed"] = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    }
}
