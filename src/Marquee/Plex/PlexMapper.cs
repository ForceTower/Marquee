using System.Text.Json;
using Marquee.Data;

namespace Marquee.Plex;

/// <summary>Plex JSON → library rows. Image references are stored as "plex:{server}:{path}".</summary>
internal static class PlexMapper
{
    public static string ItemId(string serverId, string ratingKey) => $"p:{serverId}:{ratingKey}";

    public static string? RatingKey(string itemId) =>
        itemId.StartsWith("p:", StringComparison.Ordinal) ? itemId[(itemId.LastIndexOf(':') + 1)..] : null;

    public static string? ServerId(string itemId)
    {
        if (!itemId.StartsWith("p:", StringComparison.Ordinal))
        {
            return null;
        }

        int last = itemId.LastIndexOf(':');
        return itemId[2..last];
    }

    public static MediaItem? ToItem(JsonElement m, string serverId, long libraryId)
    {
        string? key = m.Str("ratingKey");
        ItemKind? kind = m.Str("type") switch
        {
            "movie" => ItemKind.Movie,
            "show" => ItemKind.Show,
            "season" => ItemKind.Season,
            "episode" => ItemKind.Episode,
            _ => null,
        };
        if (key is null || kind is not { } k)
        {
            return null;
        }

        string? Ref(string? path) => path is null ? null : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"plex:{serverId}:{path}";
        string? Id(string? ratingKey) => ratingKey is null ? null : ItemId(serverId, ratingKey);

        var media = m.Arr("Media").ToList();
        var first = media.FirstOrDefault();
        string? logo = m.Arr("Image").FirstOrDefault(i => i.Str("type") == "clearLogo").Str("url");

        return new MediaItem
        {
            Id = ItemId(serverId, key),
            LibraryId = libraryId,
            Kind = k,
            ParentId = k switch
            {
                ItemKind.Season or ItemKind.Episode => Id(m.Str("parentRatingKey")),
                _ => null,
            },
            ShowId = k switch
            {
                ItemKind.Season => Id(m.Str("parentRatingKey")),
                ItemKind.Episode => Id(m.Str("grandparentRatingKey")),
                _ => null,
            },
            Title = m.Str("title") ?? "Untitled",
            SortTitle = m.Str("titleSort"),
            OriginalTitle = m.Str("originalTitle"),
            ShowTitle = k switch
            {
                ItemKind.Episode => m.Str("grandparentTitle"),
                ItemKind.Season => m.Str("parentTitle"),
                _ => null,
            },
            SeasonNumber = k switch
            {
                ItemKind.Episode => m.Int("parentIndex"),
                ItemKind.Season => m.Int("index"),
                _ => null,
            },
            Number = m.Int("index"),
            Year = m.Int("year") ?? m.Int("parentYear"),
            Summary = NullIfEmpty(m.Str("summary")),
            Tagline = NullIfEmpty(m.Str("tagline")),
            Rating = m.Double("audienceRating") ?? m.Double("rating"),
            UserRating = m.Double("userRating"),
            ContentRating = m.Str("contentRating"),
            Studio = m.Str("studio"),
            Genres = m.Tags("Genre", 3),
            DurationMs = m.Long("duration") ?? first.Long("duration"),
            Released = m.Str("originallyAvailableAt"),
            AddedAt = m.Long("addedAt"),
            UpdatedAt = m.Long("updatedAt"),
            Poster = Ref(k switch
            {
                ItemKind.Episode => m.Str("grandparentThumb") ?? m.Str("parentThumb"),
                _ => m.Str("thumb"),
            }),
            Backdrop = Ref(m.Str("art") ?? m.Str("grandparentArt")),
            Logo = Ref(logo),
            Still = k == ItemKind.Episode ? Ref(m.Str("thumb")) : null,
            Tint = Tint(m),
            ChildCount = m.Int("childCount"),
            LeafCount = m.Int("leafCount"),
            ViewedLeafCount = m.Int("viewedLeafCount"),
            PositionMs = m.Long("viewOffset") ?? 0,
            ViewCount = m.Int("viewCount") ?? 0,
            LastViewedAt = m.Long("lastViewedAt"),
            Resolution = first.Str("videoResolution"),
            Versions = media.Select(v => ToVersion(v, serverId)).OfType<MediaVersion>().ToList(),
        };
    }

    private static MediaVersion? ToVersion(JsonElement media, string serverId)
    {
        var part = media.Arr("Part").FirstOrDefault();
        if (part.Str("key") is not { } partKey)
        {
            return null;
        }

        return new MediaVersion(
            $"plex:{serverId}:{partKey}",
            part.Str("file"),
            part.Long("size"),
            media.Str("container") ?? part.Str("container"),
            media.Int("width"),
            media.Int("height"),
            media.Str("videoCodec"),
            media.Str("videoResolution"),
            media.Str("audioCodec"),
            media.Int("audioChannels"),
            media.Long("bitrate"),
            media.Long("duration"));
    }

    private static string? Tint(JsonElement m)
    {
        var colors = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("UltraBlurColors", out var c) ? c : default;
        if (colors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string?[] corners = [colors.Str("topLeft"), colors.Str("topRight"), colors.Str("bottomRight"), colors.Str("bottomLeft")];
        return corners.All(x => x is not null) ? string.Join(",", corners) : null;
    }

    public static (ItemDetails Details, string? VideoRange, string? Audio) ToDetails(JsonElement m, string serverId)
    {
        string? Ref(string? path) => path is null ? null : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"plex:{serverId}:{path}";

        var versions = new List<VersionDetails>();
        foreach (var media in m.Arr("Media"))
        {
            var part = media.Arr("Part").FirstOrDefault();
            if (part.Str("key") is not { } partKey)
            {
                continue;
            }

            var ordinals = new Dictionary<StreamKind, int>();
            var streams = new List<StreamInfo>();
            foreach (var s in part.Arr("Stream").OrderBy(s => s.Int("index") ?? int.MaxValue))
            {
                var kind = (StreamKind)(s.Int("streamType") ?? 0);
                if (kind is not (StreamKind.Video or StreamKind.Audio or StreamKind.Subtitle))
                {
                    continue;
                }

                bool external = s.Str("key") is not null && s.Int("index") is null;
                int ordinal = external ? -1 : ordinals.GetValueOrDefault(kind);
                if (!external)
                {
                    ordinals[kind] = ordinal + 1;
                }

                streams.Add(new StreamInfo(
                    kind,
                    ordinal,
                    s.Str("language"),
                    s.Str("languageTag") ?? s.Str("languageCode"),
                    s.Str("title"),
                    s.Str("displayTitle"),
                    s.Str("codec"),
                    s.Str("profile"),
                    s.Int("channels"),
                    s.Bool("forced"),
                    s.Bool("default"),
                    s.Bool("DOVIPresent") ? s.Int("DOVIProfile") ?? 0 : null,
                    s.Str("colorTrc"),
                    external ? s.Str("key") : null));
            }

            versions.Add(new VersionDetails($"plex:{serverId}:{partKey}", streams));
        }

        var details = new ItemDetails
        {
            Cast = m.Arr("Role").Select(r => new Person(r.Str("tag") ?? "", r.Str("role"), Ref(r.Str("thumb")))).Where(p => p.Name.Length > 0).ToList(),
            Directors = m.Arr("Director").Select(d => d.Str("tag")).OfType<string>().ToList(),
            Writers = m.Arr("Writer").Select(d => d.Str("tag")).OfType<string>().ToList(),
            Countries = m.Arr("Country").Select(d => d.Str("tag")).OfType<string>().ToList(),
            Chapters = m.Arr("Chapter")
                .Select(c => new Chapter(c.Long("startTimeOffset") ?? 0, c.Long("endTimeOffset") ?? 0, c.Str("tag"), Ref(c.Str("thumb"))))
                .ToList(),
            Markers = m.Arr("Marker")
                .Select(x => new Marker(x.Str("type") ?? "", x.Long("startTimeOffset") ?? 0, x.Long("endTimeOffset") ?? 0))
                .ToList(),
            Versions = versions,
        };

        var mainStreams = versions.FirstOrDefault()?.Streams ?? [];
        return (details, MediaLabels.VideoRange(mainStreams), MediaLabels.AudioBadge(mainStreams));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Short labels for badges, from a file's streams.</summary>
internal static class MediaLabels
{
    public static string? VideoRange(IReadOnlyList<StreamInfo> streams)
    {
        var video = streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        if (video is null)
        {
            return null;
        }

        if (video.DoviProfile is not null)
        {
            return "Dolby Vision";
        }

        return video.ColorTransfer switch
        {
            "smpte2084" => "HDR10",
            "arib-std-b67" => "HLG",
            _ => "SDR",
        };
    }

    public static string? AudioBadge(IReadOnlyList<StreamInfo> streams)
    {
        var audio = streams.Where(s => s.Kind == StreamKind.Audio).ToList();
        if (audio.Any(a => a.IsAtmos))
        {
            return "Atmos";
        }

        if (audio.Any(a => a.IsDtsX))
        {
            return "DTS:X";
        }

        return audio.Count == 0 ? null : Codec(audio[0].Codec, audio[0].Profile) + Channels(audio[0].Channels);
    }

    public static string Resolution(string? plexResolution, int? height = null) =>
        plexResolution?.ToLowerInvariant() switch
        {
            "4k" => "4K",
            "1080" => "1080p",
            "720" => "720p",
            "480" or "576" or "sd" => "SD",
            { } other => other.ToUpperInvariant(),
            null => height switch
            {
                >= 1600 => "4K",
                >= 1000 => "1080p",
                >= 700 => "720p",
                > 0 => "SD",
                _ => "",
            },
        };

    public static string Codec(string? codec, string? profile = null) => codec?.ToLowerInvariant() switch
    {
        "truehd" => "TrueHD",
        "eac3" => "DD+",
        "ac3" => "Dolby Digital",
        "dca" or "dts" => profile?.ToLowerInvariant() switch
        {
            { } p when p.Contains("dts:x") => "DTS:X",
            { } p when p.Contains("ma") => "DTS-HD MA",
            _ => "DTS",
        },
        "aac" => "AAC",
        "flac" => "FLAC",
        "opus" => "Opus",
        "mp3" => "MP3",
        "pcm" => "PCM",
        "hevc" => "HEVC",
        "h264" => "H.264",
        "av1" => "AV1",
        "vp9" => "VP9",
        "mpeg2video" => "MPEG-2",
        "vc1" => "VC-1",
        null => "",
        { } other => other.ToUpperInvariant(),
    };

    public static string Channels(int? channels) => channels switch
    {
        null or 0 => "",
        1 => " Mono",
        2 => " Stereo",
        6 => " 5.1",
        8 => " 7.1",
        { } n => $" {n}ch",
    };
}
