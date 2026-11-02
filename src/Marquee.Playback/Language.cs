using System.Globalization;

namespace Marquee.Playback;

/// <summary>
/// Reduces language tags from either engine or Plex ("pt-BR", "por", "en", "eng") to a comparable
/// ISO 639-1 key, so track choices survive an engine swap.
/// </summary>
internal static class Language
{
    private static readonly Dictionary<string, string> ThreeToTwo = CultureInfo
        .GetCultures(CultureTypes.NeutralCultures)
        .Where(c => c.ThreeLetterISOLanguageName.Length == 3 && c.TwoLetterISOLanguageName.Length == 2)
        .GroupBy(c => c.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().TwoLetterISOLanguageName, StringComparer.OrdinalIgnoreCase);

    public static string? Key(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string primary = tag.Split('-', '_')[0].ToLowerInvariant();
        return primary.Length == 3 && ThreeToTwo.TryGetValue(primary, out string? two) ? two : primary;
    }
}
