using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Talky.Helpers;
using Windows.UI;

namespace Talky.Views.Controls;

/// <summary>
/// Circular avatar with initials on a colour derived from the name, plus optional presence dot,
/// speaking ring and status badge (e.g. muted).
/// </summary>
public sealed partial class AvatarView : UserControl
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0x00, 0x63, 0xB1), // blue
        Color.FromArgb(255, 0x88, 0x17, 0x98), // purple
        Color.FromArgb(255, 0xC2, 0x39, 0xB3), // orchid
        Color.FromArgb(255, 0x03, 0x83, 0x87), // teal
        Color.FromArgb(255, 0xCA, 0x50, 0x10), // orange
        Color.FromArgb(255, 0x49, 0x82, 0x05), // green
        Color.FromArgb(255, 0x8E, 0x56, 0x2E), // brown
        Color.FromArgb(255, 0x00, 0x7A, 0x6C), // sea
        Color.FromArgb(255, 0xC5, 0x0F, 0x1F), // red
        Color.FromArgb(255, 0x5C, 0x2E, 0x91), // grape
    ];

    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(AvatarView), new PropertyMetadata(null, (d, _) => ((AvatarView)d).Update()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AvatarView), new PropertyMetadata(32d, (d, _) => ((AvatarView)d).Update()));

    public static readonly DependencyProperty ShowPresenceProperty = DependencyProperty.Register(
        nameof(ShowPresence), typeof(bool), typeof(AvatarView), new PropertyMetadata(false, (d, _) => ((AvatarView)d).Update()));

    public static readonly DependencyProperty IsOnlineProperty = DependencyProperty.Register(
        nameof(IsOnline), typeof(bool), typeof(AvatarView), new PropertyMetadata(false, (d, _) => ((AvatarView)d).Update()));

    public static readonly DependencyProperty IsSpeakingProperty = DependencyProperty.Register(
        nameof(IsSpeaking), typeof(bool), typeof(AvatarView), new PropertyMetadata(false, (d, _) => ((AvatarView)d).Update()));

    public static readonly DependencyProperty BadgeGlyphProperty = DependencyProperty.Register(
        nameof(BadgeGlyph), typeof(string), typeof(AvatarView), new PropertyMetadata(null, (d, _) => ((AvatarView)d).Update()));

    public AvatarView()
    {
        InitializeComponent();
        Update();
    }

    public string? DisplayName
    {
        get => (string?)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool ShowPresence
    {
        get => (bool)GetValue(ShowPresenceProperty);
        set => SetValue(ShowPresenceProperty, value);
    }

    public bool IsOnline
    {
        get => (bool)GetValue(IsOnlineProperty);
        set => SetValue(IsOnlineProperty, value);
    }

    public bool IsSpeaking
    {
        get => (bool)GetValue(IsSpeakingProperty);
        set => SetValue(IsSpeakingProperty, value);
    }

    /// <summary>Segoe Fluent Icons glyph shown in a red badge (null hides it).</summary>
    public string? BadgeGlyph
    {
        get => (string?)GetValue(BadgeGlyphProperty);
        set => SetValue(BadgeGlyphProperty, value);
    }

    private void Update()
    {
        double size = Size;
        Root.Width = Root.Height = size;
        Circle.Fill = new SolidColorBrush(ColorFor(DisplayName));
        InitialsText.Text = Format.Initials(DisplayName);
        InitialsText.FontSize = Math.Max(10, size * 0.4);
        SpeakingRing.Visibility = IsSpeaking ? Visibility.Visible : Visibility.Collapsed;

        PresenceDot.Visibility = ShowPresence ? Visibility.Visible : Visibility.Collapsed;
        PresenceDot.Width = PresenceDot.Height = Math.Max(10, size * 0.34);
        PresenceDot.Margin = new Thickness(0, 0, -2, -2);
        PresenceDot.Fill = IsOnline
            ? (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
            : new SolidColorBrush(Colors.Gray);

        BadgeBorder.Visibility = string.IsNullOrEmpty(BadgeGlyph) ? Visibility.Collapsed : Visibility.Visible;
        BadgeIcon.Glyph = BadgeGlyph ?? string.Empty;

        AutomationName();
    }

    private void AutomationName()
    {
        var name = DisplayName ?? string.Empty;
        if (ShowPresence)
        {
            name += IsOnline ? ", online" : ", offline";
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, name);
    }

    private static Color ColorFor(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Palette[0];
        }

        // Stable across runs (string.GetHashCode is randomized per process).
        uint hash = 2166136261;
        foreach (var c in name.ToUpperInvariant())
        {
            hash = (hash ^ c) * 16777619;
        }

        return Palette[hash % (uint)Palette.Length];
    }
}
