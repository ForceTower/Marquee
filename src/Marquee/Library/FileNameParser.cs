using System.Globalization;
using System.Text.RegularExpressions;

namespace Marquee.Library;

internal sealed record ParsedName(string Title, int? Year, int? Season, int? Episode, string? EpisodeTitle, string? Resolution);

/// <summary>Pulls title, year, season/episode and resolution out of scene-style and Plex-style names.</summary>
internal static partial class FileNameParser
{
    [GeneratedRegex(@"[Ss](\d{1,2})[ ._-]?[Ee](\d{1,4})|(\d{1,2})x(\d{2,3})")]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"(?<![\d])(19\d{2}|20\d{2})(?![\d])")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\b(2160p|4k|uhd|1080p|1080i|720p|576p|480p)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"\b(bluray|blu-ray|bdremux|remux|web-?dl|webrip|hdtv|dvdrip|bdrip|brrip|x264|x265|h\.?264|h\.?265|hevc|avc|hdr10?|dv|dovi|atmos|truehd|dts|ddp?5\.1|aac|proper|repack|extended|unrated|imax)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JunkPattern();

    [GeneratedRegex(@"^\s*(.+?)\s*\((\d{4})\)")]
    private static partial Regex PlexFolderPattern();

    [GeneratedRegex(@"^(?:season|temporada|staffel|saison)\s*(\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderPattern();

    public static ParsedName Parse(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        string? resolution = ResolutionPattern().Match(name) is { Success: true } r ? NormalizeResolution(r.Value) : null;

        if (EpisodePattern().Match(name) is { Success: true } ep)
        {
            int season = int.Parse(ep.Groups[1].Success ? ep.Groups[1].Value : ep.Groups[3].Value, CultureInfo.InvariantCulture);
            int episode = int.Parse(ep.Groups[2].Success ? ep.Groups[2].Value : ep.Groups[4].Value, CultureInfo.InvariantCulture);
            string show = Clean(name[..ep.Index]);
            string rest = Clean(Cut(name[(ep.Index + ep.Length)..]));
            return new ParsedName(show, null, season, episode, rest.Length > 0 ? rest : null, resolution);
        }

        if (PlexFolderPattern().Match(name) is { Success: true } plex)
        {
            return new ParsedName(Clean(plex.Groups[1].Value), int.Parse(plex.Groups[2].Value, CultureInfo.InvariantCulture), null, null, null, resolution);
        }

        var years = YearPattern().Matches(name);
        var year = years.Count > 0 ? years[^1] : null;
        if (year is not null && year.Index > 0)
        {
            return new ParsedName(Clean(name[..year.Index]), int.Parse(year.Value, CultureInfo.InvariantCulture), null, null, null, resolution);
        }

        return new ParsedName(Clean(Cut(name)), null, null, null, null, resolution);
    }

    /// <summary>"Dune Part Two (2024)" → ("Dune Part Two", 2024), for movie folders.</summary>
    public static (string Title, int? Year)? ParseFolder(string folderName) =>
        PlexFolderPattern().Match(folderName) is { Success: true } m
            ? (Clean(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;

    public static int? SeasonFromFolder(string folderName) =>
        SeasonFolderPattern().Match(folderName.Trim()) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    private static string Cut(string name)
    {
        var junk = JunkPattern().Match(name);
        var res = ResolutionPattern().Match(name);
        int cut = new[] { junk.Success ? junk.Index : int.MaxValue, res.Success ? res.Index : int.MaxValue }.Min();
        return cut == int.MaxValue ? name : name[..cut];
    }

    private static string Clean(string s)
    {
        s = s.Replace('.', ' ').Replace('_', ' ');
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '(', '[');
        return s;
    }

    private static string NormalizeResolution(string r) => r.ToLowerInvariant() switch
    {
        "2160p" or "4k" or "uhd" => "4k",
        "1080p" or "1080i" => "1080",
        "720p" => "720",
        _ => "sd",
    };
}
