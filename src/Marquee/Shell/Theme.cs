using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Marquee.Shell;

internal static class Theme
{
    public static readonly string[] Accents = ["#F5B544", "#E8653A", "#4CC2FF", "#9A8CFF"];

    private static readonly string[] PosterFallbacks =
        ["#2E3A4A", "#4A2E33", "#2F4237", "#3F3350", "#4A3B2A", "#2A3F45", "#3A3A3E", "#4A2F2F"];

    public static Color Parse(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 255;
        if (hex.Length == 8)
        {
            a = Convert.ToByte(hex[..2], 16);
            hex = hex[2..];
        }

        return Color.FromArgb(a, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    /// <summary>
    /// Sets the system-accent resources WinUI's controls read. Call before the window loads;
    /// later changes take effect for newly created brushes (restyling a live tree needs a theme flip).
    /// </summary>
    public static void ApplyAccent(string hex)
    {
        var accent = Parse(hex);
        var resources = Application.Current.Resources;
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.15);
        resources["SystemAccentColorLight2"] = accent;
        resources["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.4);
        resources["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.12);
        resources["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.27);
        resources["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.43);
        resources["AccentColor"] = accent;
        if (resources["AccentBrush"] is SolidColorBrush brush)
        {
            brush.Color = accent;
        }
    }

    public static Color Accent => Application.Current.Resources["AccentColor"] is Color c ? c : Parse(Accents[0]);

    /// <summary>A poster/backdrop placeholder: Plex's UltraBlur corner colors, darkened, or a stable color from the title.</summary>
    public static Brush TintBrush(string? tint, string seed, double darken = 0.45)
    {
        var corners = tint?.Split(',');
        if (corners is { Length: 4 })
        {
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Mix(Parse(corners[0]), Colors.Black, darken), Offset = 0 },
                    new GradientStop { Color = Mix(Parse(corners[2]), Colors.Black, darken + 0.1), Offset = 1 },
                },
            };
        }

        return new SolidColorBrush(Parse(PosterFallbacks[(uint)StableHash(seed) % PosterFallbacks.Length]));
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (char c in s)
            {
                h = h * 31 + c;
            }

            return h;
        }
    }
}
