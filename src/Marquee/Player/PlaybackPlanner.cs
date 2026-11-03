using Marquee.Data;
using Marquee.Library;
using Marquee.Playback;
using Marquee.Playback.Interop;
using Marquee.Playback.IO;
using Marquee.Plex;

namespace Marquee.Player;

internal sealed record PlaybackPlan(PlaybackRequest Request, EngineMode Engine, string? Note);

/// <summary>
/// Decides what to play and how: which file, which audio and subtitle tracks, where to start,
/// and which engine. Media Foundation is chosen only when it adds something mpv can't (real Dolby
/// Vision, Atmos objects, DTS:X) and can show the chosen subtitles.
/// </summary>
internal static class PlaybackPlanner
{
    /// <summary>The <c>--subtitle N</c> test flag: the subtitle to start the next plan with, whatever the preferences say.</summary>
    public static int? StartSubtitle { get; set; }

    /// <summary>The <c>--start SECONDS</c> test flag: where the next plan starts, whatever the resume point.</summary>
    public static TimeSpan? StartAt { get; set; }

    public static async Task<PlaybackPlan> PlanAsync(MediaItem item, bool resume, EngineMode? engine, TimeSpan? start = null)
    {
        var settings = App.Services.Settings.Current;
        var prefs = TrackPreferences.Load(item.Id);
        var version = prefs.PickVersion(item) ?? throw new InvalidOperationException("This item has no playable file.");
        var details = await DetailsLoader.LoadAsync(item);
        var streams = details?.Versions.FirstOrDefault(v => v.Location == version.Location)?.Streams ?? [];

        string location = version.Location.StartsWith("plex:", StringComparison.Ordinal)
            ? App.Services.Plex.ResolveMedia(version.Location) ?? throw new InvalidOperationException("The Plex server for this item is offline.")
            : version.Location;
        if (!MediaFile.IsUrl(location) && !File.Exists(location))
        {
            throw new FileNotFoundException($"{Path.GetFileName(location)} isn't reachable. Is the drive or share connected?");
        }

        int? audio = prefs.Audio ?? PickAudio(streams, settings);
        var audioStream = streams.FirstOrDefault(s => s.Kind == StreamKind.Audio && s.Ordinal == audio);
        int? subtitle = StartSubtitle ?? PickSubtitle(streams, settings, prefs, audioStream);
        TimeSpan? testStart = StartAt;
        StartSubtitle = null;
        StartAt = null;

        var externals = new List<(string, string?, string)>();
        int embeddedSubtitles = streams.Count(s => s.Kind == StreamKind.Subtitle && s.Ordinal >= 0);
        if (PlexService.ParseRef(version.Location) is var (serverId, _) && App.Services.Plex.Server(serverId) is { } server)
        {
            foreach (var external in streams.Where(s => s.Kind == StreamKind.Subtitle && s.ExternalKey is not null))
            {
                externals.Add((Pages.DetailParts.TrackLabel(external), external.LanguageTag, server.MediaUrl(external.ExternalKey!)));
            }
        }

        bool objects = settings.TrueHdAtmosObjects && audioStream is { Codec: "truehd", IsAtmos: true };
        var startAt = start ?? testStart ?? (resume && settings.Resume == ResumeMode.Always && item.InProgress && item.Progress < 0.95
            ? TimeSpan.FromMilliseconds(Math.Max(0, item.PositionMs - 3000))
            : TimeSpan.Zero);

        var request = new PlaybackRequest
        {
            Item = item,
            Version = version,
            Location = location,
            Start = startAt,
            Streams = streams,
            Chapters = details?.Chapters ?? [],
            Markers = details?.Markers ?? [],
            Audio = audio,
            Subtitle = subtitle,
            ExternalSubtitles = externals,
            PlayTrueHdAsObjects = objects,
        };

        var (chosen, note) = engine is { } forced and not EngineMode.Automatic
            ? (forced, null)
            : Route(request, settings, audioStream);
        return new PlaybackPlan(request, chosen, note);
    }

    private static (EngineMode, string?) Route(PlaybackRequest r, AppSettings settings, StreamInfo? audio)
    {
        if (settings.Engine != EngineMode.Automatic)
        {
            return (settings.Engine, null);
        }

        string container = (r.Version.Container ?? MediaFile.Extension(r.Location).TrimStart('.')).ToLowerInvariant();
        var video = r.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        string? codec = video?.Codec ?? r.Version.VideoCodec;
        bool mfCanPlay = MediaFoundationEngine.PlaysContainer(container) && codec is "hevc" or "h264";
        if (!mfCanPlay)
        {
            return (EngineMode.Mpv, null);
        }

        // On a dual-layer Blu-ray the Dolby Vision configuration is on the enhancement layer's stream.
        bool dolbyVision = settings.DolbyVision && r.Streams.Any(s => s.Kind == StreamKind.Video && s.DoviProfile is 5 or 7 or 8);
        bool objects = r.PlayTrueHdAsObjects || audio is { Codec: "eac3", IsAtmos: true };
        bool dtsX = audio?.IsDtsX == true && StoreCodecRuntime.HasDtsDecoder;
        bool mfAdds = dolbyVision || objects || dtsX;
        if (!mfAdds)
        {
            return (EngineMode.Mpv, null);
        }

        if (r.Subtitle is int subtitle && !MediaFoundationEngine.CanShow(r, subtitle))
        {
            return (EngineMode.Mpv, "These subtitles play through mpv, so Dolby Vision is shown as HDR10.");
        }

        return (EngineMode.MediaFoundation, null);
    }

    /// <summary>
    /// The preferred language if set, else the track the file marks as default. Within that
    /// language: Atmos first, then lossless, then the most channels.
    /// </summary>
    public static int? PickAudio(IReadOnlyList<StreamInfo> streams, AppSettings settings)
    {
        var audio = streams.Where(s => s.Kind == StreamKind.Audio).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        string? language = settings.PreferredAudioLanguage is { Length: > 0 } preferred
            ? preferred
            : Language.Key((audio.FirstOrDefault(a => a.Default) ?? audio[0]).LanguageTag);
        var candidates = audio.Where(a => Language.Key(a.LanguageTag) == language).ToList();
        if (candidates.Count == 0)
        {
            candidates = audio;
        }

        return candidates
            .OrderByDescending(a => a.IsAtmos)
            .ThenByDescending(a => a.Codec is "truehd" || a.Profile?.Contains("ma") == true || a.Codec is "flac" or "pcm")
            .ThenByDescending(a => a.Channels ?? 0)
            .ThenByDescending(a => a.Default)
            .First().Ordinal;
    }

    private static int? PickSubtitle(IReadOnlyList<StreamInfo> streams, AppSettings settings, TrackPreferences prefs, StreamInfo? audio)
    {
        if (prefs.Subtitle is int chosen)
        {
            return chosen == TrackPreferences.SubtitlesOff ? null : chosen;
        }

        var subtitles = streams.Where(s => s.Kind == StreamKind.Subtitle && s.Ordinal >= 0).ToList();
        string? audioLanguage = Language.Key(audio?.LanguageTag);
        switch (settings.Subtitles)
        {
            case SubtitleMode.ForcedOnly:
                var forced = subtitles.FirstOrDefault(s => s.Forced && (audioLanguage is null || Language.Key(s.LanguageTag) == audioLanguage));
                return forced?.Ordinal;
            case SubtitleMode.Always:
                string? wanted = settings.PreferredSubtitleLanguage is { Length: > 0 } l ? l : audioLanguage;
                var full = subtitles.FirstOrDefault(s => !s.Forced && Language.Key(s.LanguageTag) == wanted)
                           ?? subtitles.FirstOrDefault(s => !s.Forced);
                return full?.Ordinal;
            default:
                return null;
        }
    }
}
