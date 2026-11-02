using System.Security.Cryptography;
using System.Text;
using Marquee.Data;
using Marquee.Playback;
using Marquee.Playback.Matroska;

namespace Marquee.Library;

/// <summary>
/// Turns a folder of video files into library items. Movies come from "Title (Year)" folders or
/// file names; episodes from SxxEyy names under "Show/Season N/". MKV files are probed with
/// Marquee's own demuxer for resolution, HDR/Dolby Vision and audio tracks.
/// </summary>
internal static class FolderScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mk3d", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".webm", ".wmv", ".mpg", ".mpeg",
    };

    private static readonly string[] SkippedFolders = ["extras", "featurettes", "behind the scenes", "deleted scenes", "trailers", "samples", "sample", "@eaDir", "$RECYCLE.BIN"];

    public sealed record Result(List<MediaItem> Items, Dictionary<string, ItemDetails> Probes);

    /// <param name="alreadyProbed">Items whose files were probed by an earlier scan.</param>
    public static Result Scan(LibraryInfo library, ISet<string> alreadyProbed, Action<int, int> progress, CancellationToken ct)
    {
        string root = library.Path!;
        var files = Enumerate(root).ToList();
        var items = new Dictionary<string, MediaItem>();
        var probes = new Dictionary<string, ItemDetails>();

        for (int n = 0; n < files.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            progress(n, files.Count);
            var file = files[n];
            var parsed = FileNameParser.Parse(file.Name);
            var version = new MediaVersion(file.FullName, file.FullName, file.Length, file.Extension.TrimStart('.').ToLowerInvariant(),
                null, null, null, parsed.Resolution, null, null, null, null);
            long added = new DateTimeOffset(file.CreationTimeUtc).ToUnixTimeSeconds();

            MediaItem item;
            if (library.Type == LibraryType.Shows && parsed.Season is { } season && parsed.Episode is { } episode)
            {
                string showName = ShowFolderName(root, file) ?? parsed.Title;
                var (showTitle, showYear) = FileNameParser.ParseFolder(showName) ?? (showName, null);
                string showId = Id("fs", library.Id + "/" + showTitle.ToLowerInvariant());
                string seasonId = $"{showId}/s{season}";
                string? showFolder = ShowFolderPath(root, file);

                items.TryAdd(showId, new MediaItem
                {
                    Id = showId, LibraryId = library.Id, Kind = ItemKind.Show, Title = showTitle, Year = showYear,
                    Poster = Artwork(showFolder, "poster", "folder", "cover"), Backdrop = Artwork(showFolder, "fanart", "backdrop", "background"),
                    AddedAt = added,
                });
                items.TryAdd(seasonId, new MediaItem
                {
                    Id = seasonId, LibraryId = library.Id, Kind = ItemKind.Season, ParentId = showId, ShowId = showId,
                    Title = season == 0 ? "Specials" : $"Season {season}", ShowTitle = showTitle, SeasonNumber = season, Number = season,
                    Poster = Artwork(file.DirectoryName, $"season{season:00}-poster", "poster", "folder") ?? items[showId].Poster,
                });
                item = new MediaItem
                {
                    Id = Id("f", file.FullName.ToLowerInvariant()), LibraryId = library.Id, Kind = ItemKind.Episode,
                    ParentId = seasonId, ShowId = showId, ShowTitle = showTitle, SeasonNumber = season, Number = episode,
                    Title = parsed.EpisodeTitle ?? $"Episode {episode}", AddedAt = added,
                    Poster = items[showId].Poster, Backdrop = items[showId].Backdrop,
                    Still = Artwork(file.DirectoryName, Path.GetFileNameWithoutExtension(file.Name) + "-thumb", Path.GetFileNameWithoutExtension(file.Name)),
                    Resolution = parsed.Resolution, Versions = [version],
                };
            }
            else
            {
                var folder = file.Directory!;
                var fromFolder = folder.FullName.Length > root.TrimEnd('\\', '/').Length ? FileNameParser.ParseFolder(folder.Name) : null;
                string title = fromFolder?.Title ?? parsed.Title;
                item = new MediaItem
                {
                    Id = Id("f", file.FullName.ToLowerInvariant()), LibraryId = library.Id, Kind = ItemKind.Movie,
                    Title = title.Length > 0 ? title : Path.GetFileNameWithoutExtension(file.Name),
                    Year = fromFolder?.Year ?? parsed.Year, AddedAt = added,
                    Poster = fromFolder is not null ? Artwork(folder.FullName, "poster", "folder", "cover", "movie") : Artwork(folder.FullName, Path.GetFileNameWithoutExtension(file.Name) + "-poster"),
                    Backdrop = fromFolder is not null ? Artwork(folder.FullName, "fanart", "backdrop", "background") : null,
                    Resolution = parsed.Resolution, Versions = [version],
                };
            }

            if (!alreadyProbed.Contains(item.Id) && MatroskaMediaSource.Handles(file.FullName) && Probe(file.FullName) is { } probe)
            {
                probes[item.Id] = probe.Details;
                item = item with
                {
                    Resolution = probe.Resolution ?? item.Resolution,
                    DurationMs = probe.DurationMs,
                    VideoRange = probe.Range,
                    Audio = probe.Audio,
                    Versions = [version with { Width = probe.Width, Height = probe.Height, Resolution = probe.Resolution ?? version.Resolution, DurationMs = probe.DurationMs, VideoCodec = probe.VideoCodec, AudioCodec = probe.AudioCodec, AudioChannels = probe.AudioChannels }],
                };
            }

            items[item.Id] = item;
        }

        foreach (var show in items.Values.Where(i => i.Kind == ItemKind.Show).ToList())
        {
            var episodes = items.Values.Where(i => i.ShowId == show.Id && i.Kind == ItemKind.Episode).ToList();
            items[show.Id] = show with
            {
                LeafCount = episodes.Count,
                ChildCount = items.Values.Count(i => i.ParentId == show.Id),
                AddedAt = episodes.Max(e => e.AddedAt),
            };
        }

        foreach (var season in items.Values.Where(i => i.Kind == ItemKind.Season).ToList())
        {
            items[season.Id] = season with { LeafCount = items.Values.Count(i => i.ParentId == season.Id) };
        }

        return new Result(items.Values.ToList(), probes);
    }

    private sealed record ProbeResult(ItemDetails Details, string? Range, string? Audio, string? Resolution, int? Width, int? Height, long? DurationMs, string? VideoCodec, string? AudioCodec, int? AudioChannels);

    private static ProbeResult? Probe(string path)
    {
        try
        {
            using var demuxer = MatroskaDemuxer.Open(path, 64 * 1024);
            var streams = new List<StreamInfo>();
            var ordinals = new Dictionary<StreamKind, int>();
            foreach (var t in demuxer.Tracks)
            {
                StreamKind? kind = t.Kind switch
                {
                    TrackKind.Video => StreamKind.Video,
                    TrackKind.Audio => StreamKind.Audio,
                    TrackKind.Subtitle => StreamKind.Subtitle,
                    _ => null,
                };
                if (kind is not { } k)
                {
                    continue;
                }

                int ordinal = ordinals.GetValueOrDefault(k);
                ordinals[k] = ordinal + 1;
                string codec = CodecName(t.CodecId);
                string? profile = t.Name?.Contains("Atmos", StringComparison.OrdinalIgnoreCase) == true ? "atmos"
                    : t.Name?.Contains("DTS:X", StringComparison.OrdinalIgnoreCase) == true ? "dts:x" : null;
                string language = Language.Key(t.DisplayLanguage) ?? t.DisplayLanguage;
                string display = k switch
                {
                    StreamKind.Audio => $"{LanguageName(language)} ({Plex.MediaLabels.Codec(codec)}{Plex.MediaLabels.Channels(t.Channels)})",
                    StreamKind.Subtitle => LanguageName(language),
                    _ => $"{t.Width}×{t.Height}",
                };
                streams.Add(new StreamInfo(k, ordinal, LanguageName(language), language, t.Name, display, codec, profile,
                    t.Channels == 0 ? null : t.Channels, false, t.IsDefault, t.DolbyVision?.Profile,
                    t.Colour?.Transfer switch { 16 => "smpte2084", 18 => "arib-std-b67", _ => null }, null));
            }

            var video = demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
            var audio = demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio);
            var location = new VersionDetails(path, streams);
            string? resolution = video is null ? null : video.Height >= 1600 || video.Width >= 3200 ? "4k" : video.Height >= 1000 || video.Width >= 1800 ? "1080" : video.Height >= 700 ? "720" : "sd";
            return new ProbeResult(
                new ItemDetails { Versions = [location] },
                Plex.MediaLabels.VideoRange(streams),
                Plex.MediaLabels.AudioBadge(streams),
                resolution,
                video?.Width,
                video?.Height,
                (long)demuxer.Duration.TotalMilliseconds,
                video is null ? null : CodecName(video.CodecId),
                audio is null ? null : CodecName(audio.CodecId),
                audio?.Channels);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CodecName(string codecId) => codecId switch
    {
        "V_MPEGH/ISO/HEVC" => "hevc",
        "V_MPEG4/ISO/AVC" => "h264",
        "V_AV1" => "av1",
        "V_VP9" => "vp9",
        "V_MPEG2" => "mpeg2video",
        "A_TRUEHD" => "truehd",
        "A_EAC3" => "eac3",
        "A_AC3" => "ac3",
        "A_DTS" => "dca",
        "A_AAC" => "aac",
        "A_FLAC" => "flac",
        "A_OPUS" => "opus",
        "S_TEXT/UTF8" => "srt",
        "S_TEXT/ASS" or "S_TEXT/SSA" => "ass",
        "S_HDMV/PGS" => "pgs",
        "S_VOBSUB" => "vobsub",
        _ => codecId.Split('/')[0].Replace("A_", "").Replace("V_", "").Replace("S_", "").ToLowerInvariant(),
    };

    private static string LanguageName(string tag)
    {
        try
        {
            return tag is "und" or "" ? "Unknown" : System.Globalization.CultureInfo.GetCultureInfo(tag).NativeName is { Length: > 0 } n
                ? char.ToUpper(n[0]) + n[1..]
                : tag;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return tag;
        }
    }

    private static IEnumerable<FileInfo> Enumerate(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, path);
            if (relative.Split(Path.DirectorySeparatorChar).SkipLast(1).Any(d => SkippedFolders.Contains(d, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var file = new FileInfo(path);
            if (Path.GetFileNameWithoutExtension(path).EndsWith("-sample", StringComparison.OrdinalIgnoreCase) || file.Length < 20_000_000)
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>The first folder under the library root: the show's folder in a "Show/Season 1/file" layout.</summary>
    private static string? ShowFolderName(string root, FileInfo file)
    {
        var parts = Path.GetRelativePath(root, file.FullName).Split(Path.DirectorySeparatorChar);
        return parts.Length > 1 ? parts[0] : null;
    }

    private static string? ShowFolderPath(string root, FileInfo file) =>
        ShowFolderName(root, file) is { } name ? Path.Combine(root, name) : null;

    private static string? Artwork(string? folder, params string[] names)
    {
        if (folder is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            foreach (string ext in new[] { ".jpg", ".jpeg", ".png", ".webp" })
            {
                string candidate = Path.Combine(folder, name + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Id(string prefix, string key) =>
        $"{prefix}:{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant()}";
}
