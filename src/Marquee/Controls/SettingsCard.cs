using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Marquee.Controls;

/// <summary>
/// A settings row in the Windows Settings style: icon, header and description on the left, the
/// control on the right. With <see cref="IsNested"/> it becomes a row inside an expander.
/// </summary>
public sealed class SettingsCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty IsNestedProperty =
        DependencyProperty.Register(nameof(IsNested), typeof(bool), typeof(SettingsCard), new PropertyMetadata(false, OnTextChanged));

    public SettingsCard()
    {
        DefaultStyleKey = typeof(SettingsCard);
    }

    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool IsNested
    {
        get => (bool)GetValue(IsNestedProperty);
        set => SetValue(IsNestedProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateParts();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingsCard)d).UpdateParts();

    private void UpdateParts()
    {
        if (GetTemplateChild("IconPart") is FrameworkElement icon)
        {
            icon.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (GetTemplateChild("DescriptionPart") is FrameworkElement description)
        {
            description.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (GetTemplateChild("RootPart") is Grid root)
        {
            root.MinHeight = IsNested ? 52 : 68;
            root.Padding = IsNested ? new Thickness(string.IsNullOrEmpty(Glyph) ? 54 : 16, 6, 16, 6) : new Thickness(16, 12, 16, 12);
        }
    }
}
