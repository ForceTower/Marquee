using Marquee.Playback.Demuxing;
using Marquee.Playback.Matroska;
using Marquee.Playback.MediaFoundation;

namespace Marquee.Playback.Subtitles;

/// <summary>Builds a <see cref="SubtitlePlayback"/> for an embedded track or a subtitle file.</summary>
internal static class SubtitleOpener
{
    /// <summary>Whether this build can show subtitles of <paramref name="codec"/> (its native libraries are present).</summary>
    public static bool CanShow(SubtitleCodec? codec) => codec switch
    {
        null => false,
        { } c when c.IsBitmap() => FfmpegLibraries.TryLoad(out _),
        _ => AssRenderer.Available,
    };

    /// <summary>Text formats that can be loaded from a file.</summary>
    public static bool CanShowFile(SubtitleCodec? codec) => codec is SubtitleCodec.Ass or SubtitleCodec.SubRip or SubtitleCodec.WebVtt && CanShow(codec);

    /// <summary>
    /// Whether embedded subtitles of this file can be read: from the demuxer that feeds Media
    /// Foundation, or for MP4 (played by Windows' own source) through libavformat.
    /// </summary>
    public static bool CanReadEmbedded(string location) =>
        DemuxerMediaSource.Handles(location) || Demuxer.IsMp4(location) && FfmpegLibraries.CanDemux;

    /// <summary>Whether an embedded track of this codec in this file can be shown. From MP4 that's only timed text (tx3g).</summary>
    public static bool CanShowEmbedded(string location, SubtitleCodec? codec) =>
        CanReadEmbedded(location) && (!Demuxer.IsMp4(location) || codec == SubtitleCodec.SubRip) && CanShow(codec);

    /// <param name="playbackDemuxer">
    /// The demuxer feeding the video, or null for a file Windows' own source plays (MP4), whose
    /// track is then read through a demuxer of the subtitles' own.
    /// </param>
    /// <param name="video">The video track, for its size and frame rate.</param>
    /// <param name="style">Colours for plain-text subtitles.</param>
    public static SubtitlePlayback OpenEmbedded(string location, MediaTrack track, IDemuxer? playbackDemuxer, MediaTrack? video,
        Func<VideoClock> clock, Action<string>? log = null, TextStyle? style = null)
    {
        var codec = SubtitleCodecs.FromMatroska(track.CodecId) ?? throw new NotSupportedException($"Subtitle codec {track.CodecId} isn't supported");
        int width = video?.Width ?? 0, height = video?.Height ?? 0;
        double frameRate = video is { DefaultDurationNs: > 0 } ? 1e9 / video.DefaultDurationNs : 24;
        List<MatroskaAttachment> fonts = codec == SubtitleCodec.Ass && Demuxer.IsMatroska(location) ? Fonts(location) : [];
        ISubtitleRenderer renderer = codec.IsBitmap()
            ? new BitmapSubtitleRenderer(codec, track.CodecPrivate, width, height)
            : new AssRenderer(codec, track.CodecPrivate, fonts, width, height, log, style);

        ISubtitleSource source;
        string reading;
        try
        {
            (source, reading) = OpenSource(location, track.Number, playbackDemuxer, log);
        }
        catch
        {
            renderer.Dispose();
            throw;
        }

        string description = $"subtitle #{track.Number} [{track.DisplayLanguage}] {track.CodecId}" +
                             $"{(track.Name is { Length: > 0 } name ? $" '{name}'" : "")}, {reading}" +
                             $"{(fonts.Count > 0 ? $", {fonts.Count} attached fonts" : "")}";
        var playback = new SubtitlePlayback(source, renderer, clock, frameRate, description);
        if (log is not null)
        {
            playback.Failed += log;
        }

        return playback;
    }

    /// <summary>Where an embedded track's packets come from, and how that reads, for the log.</summary>
    internal static (ISubtitleSource Source, string Reading) OpenSource(string location, int track, IDemuxer? playbackDemuxer, Action<string>? log)
    {
        switch (playbackDemuxer)
        {
            case MatroskaDemuxer mkv when MatroskaSubtitleSource.CanRead(mkv, track):
                var indexed = new MatroskaSubtitleSource(location, track, mkv);
                if (log is not null)
                {
                    indexed.Failed += log;
                }

                return (indexed, "read through the index");
            case null:
                var seeking = new SeekingSubtitleSource(Demuxer.Open(location, 16 * 1024), track);
                if (log is not null)
                {
                    seeking.Failed += log;
                }

                return (seeking, "read through its own demuxer");
            default:
                return (new TappedSubtitleSource(playbackDemuxer, track), "following playback (no per-block index)");
        }
    }

    /// <summary>A whole subtitle file: ASS/SSA, SubRip or WebVTT, in any common encoding.</summary>
    public static SubtitlePlayback OpenFile(byte[] content, SubtitleCodec codec, string label, int videoWidth, int videoHeight, double frameRate,
        Func<VideoClock> clock, Action<string>? log = null, TextStyle? style = null)
    {
        string text = TextSubtitles.Decode(content);
        ISubtitleRenderer renderer;
        List<SubtitlePacket> packets = [];
        switch (codec)
        {
            case SubtitleCodec.Ass:
                renderer = new AssRenderer(codec, System.Text.Encoding.UTF8.GetBytes(text), [], videoWidth, videoHeight, log);
                break;
            case SubtitleCodec.SubRip or SubtitleCodec.WebVtt:
                packets = TextSubtitles.ParseCues(text);
                renderer = new AssRenderer(codec, null, [], videoWidth, videoHeight, log, style);
                break;
            default:
                throw new NotSupportedException($"{codec} subtitle files aren't supported");
        }

        var playback = new SubtitlePlayback(new FileSubtitleSource(packets), renderer, clock, frameRate,
            $"subtitle file '{label}' ({codec}{(packets.Count > 0 ? $", {packets.Count} cues" : "")})");
        if (log is not null)
        {
            playback.Failed += log;
        }

        return playback;
    }

    private static List<MatroskaAttachment> Fonts(string location)
    {
        using var demuxer = MatroskaDemuxer.Open(location, 64 * 1024);
        return demuxer.ReadAttachments().Where(IsFont).ToList();
    }

    private static bool IsFont(MatroskaAttachment file) =>
        file.MimeType is { } mime && (mime.Contains("font", StringComparison.OrdinalIgnoreCase) ||
                                      mime.Contains("truetype", StringComparison.OrdinalIgnoreCase) ||
                                      mime.Contains("opentype", StringComparison.OrdinalIgnoreCase)) ||
        Path.GetExtension(file.Name).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc";
}
