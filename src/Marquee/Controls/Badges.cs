using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Marquee.Controls;

internal static class Badges
{
    /// <summary>An outlined quality badge ("4K", "Dolby Vision").</summary>
    public static Border Create(string text) => new()
    {
        Style = (Style)Application.Current.Resources["BadgeBorderStyle"],
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["BadgeTextStyle"] },
    };

    /// <summary>A filled spec chip ("3840×2160", "TrueHD 7.1").</summary>
    public static Border Chip(string text) => new()
    {
        Style = (Style)Application.Current.Resources["ChipBorderStyle"],
        Child = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["ChipTextStyle"] },
    };
}
