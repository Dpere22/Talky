using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Talky.Views.Controls;

/// <summary>A Segoe Fluent icon that can be struck through (mic → muted mic, headphones → deafened).</summary>
public sealed partial class SlashedGlyph : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SlashedGlyph), new PropertyMetadata(string.Empty, (d, e) => ((SlashedGlyph)d)._icon.Glyph = (string)e.NewValue));

    public static readonly DependencyProperty IsSlashedProperty = DependencyProperty.Register(
        nameof(IsSlashed), typeof(bool), typeof(SlashedGlyph), new PropertyMetadata(false, (d, e) =>
            ((SlashedGlyph)d)._slash.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    private readonly FontIcon _icon = new() { FontSize = 16 };
    private readonly Line _slash = new()
    {
        X1 = 2,
        Y1 = 2,
        X2 = 18,
        Y2 = 18,
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Visibility = Visibility.Collapsed,
    };

    public SlashedGlyph()
    {
        IsTabStop = false;
        // The slash follows the (inherited) icon foreground so it matches in hover/pressed/checked states.
        _slash.SetBinding(Shape.StrokeProperty, new Binding { Source = _icon, Path = new PropertyPath(nameof(FontIcon.Foreground)) });
        var root = new Grid { Width = 20, Height = 20 };
        root.Children.Add(_icon);
        root.Children.Add(_slash);
        Content = root;
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool IsSlashed
    {
        get => (bool)GetValue(IsSlashedProperty);
        set => SetValue(IsSlashedProperty, value);
    }
}
