using System.Globalization;
using Marquee.Data;
using Marquee.Plex;

namespace Marquee.Shell;

internal static class Format
{
    public static string Duration(long? ms)
    {
        if (ms is not > 0)
        {
            return "";
        }

        var t = TimeSpan.FromMilliseconds(ms.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, t.Minutes)} min";
    }

    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public static string TimeLeft(MediaItem item)
    {
        if (item.DurationMs is not > 0)
        {
            return "";
        }

        long left = item.DurationMs.Value - item.PositionMs;
        var t = TimeSpan.FromMilliseconds(left);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m left" : $"{Math.Max(1, (int)t.TotalMinutes)} min left";
    }

    public static string EpisodeCode(MediaItem item) =>
        item.SeasonNumber is { } s && item.Number is { } e ? $"S{s} · E{e}" : "";

    public static string PosterSubtitle(MediaItem item) => item.Kind switch
    {
        ItemKind.Show => Join(item.Year?.ToString(), item.ChildCount is { } seasons ? Plural(seasons, "season") : null),
        ItemKind.Episode => Join(EpisodeCode(item), item.Title),
        _ => Join(item.Year?.ToString(), Duration(item.DurationMs)),
    };

    /// <summary>The badge a poster shows: Dolby Vision or HDR when known, otherwise the resolution.</summary>
    public static string PosterBadge(MediaItem item)
    {
        if (item.Kind is ItemKind.Show or ItemKind.Season)
        {
            return "";
        }

        string resolution = MediaLabels.Resolution(item.Resolution);
        return item.VideoRange switch
        {
            "Dolby Vision" => resolution == "4K" ? "4K DV" : "DV",
            "HDR10" or "HLG" => resolution == "4K" ? "4K HDR" : "HDR",
            _ => resolution,
        };
    }

    /// <summary>"4K", "Dolby Vision", "Atmos" — the quality badges on heroes and detail pages.</summary>
    public static List<string> QualityBadges(MediaItem item, ItemDetails? details = null)
    {
        var badges = new List<string>();
        string resolution = MediaLabels.Resolution(item.Resolution, item.Versions.FirstOrDefault()?.Height);
        if (resolution.Length > 0)
        {
            badges.Add(resolution);
        }

        var streams = details?.Versions.FirstOrDefault()?.Streams;
        string? range = streams is not null ? MediaLabels.VideoRange(streams) : item.VideoRange;
        if (range is not null and not "SDR")
        {
            badges.Add(range);
        }

        if (streams is not null)
        {
            var video = streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
            if (video?.DoviProfile is not null && video.ColorTransfer == "smpte2084")
            {
                badges.Add("HDR10");
            }

            var audio = streams.Where(s => s.Kind == StreamKind.Audio).ToList();
            if (audio.Any(a => a.IsAtmos))
            {
                badges.Add("Dolby Atmos");
            }
            else if (audio.Any(a => a.IsDtsX))
            {
                badges.Add("DTS:X");
            }

            if (audio.Select(a => a.Channels ?? 0).DefaultIfEmpty().Max() is var channels and > 2)
            {
                badges.Add(MediaLabels.Channels(channels).Trim());
            }

            if (video?.Codec is { } codec)
            {
                badges.Add(MediaLabels.Codec(codec));
            }
        }
        else if (item.Audio is { } audioBadge)
        {
            badges.Add(audioBadge == "Atmos" ? "Atmos" : audioBadge);
        }

        return badges.Distinct().ToList();
    }

    public static string Size(long? bytes) => bytes switch
    {
        null => "",
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        _ => $"{bytes / (double)(1L << 20):0} MB",
    };

    public static string Ago(long? unixSeconds)
    {
        if (unixSeconds is not { } s)
        {
            return "never";
        }

        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(s);
        return span.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)span.TotalMinutes} min ago",
            < 60 * 24 => Plural((int)span.TotalHours, "hour") + " ago",
            < 60 * 24 * 30 => Plural((int)span.TotalDays, "day") + " ago",
            _ => DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        };
    }

    public static string Date(string? isoDate) =>
        DateTime.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)
            : "";

    public static string ShortDate(string? isoDate) =>
        DateTime.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
            : "";

    public static string Plural(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    public static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => (words[0][..1] + words[^1][..1]).ToUpperInvariant(),
        };
    }
}
