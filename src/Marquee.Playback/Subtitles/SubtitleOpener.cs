using Marquee.Playback.Matroska;
using Marquee.Playback.MediaFoundation;

namespace Marquee.Playback.Subtitles;

/// <summary>Builds a <see cref="SubtitlePlayback"/> for an embedded Matroska track or a subtitle file.</summary>
internal static class SubtitleOpener
{
    /// <summary>Whether this build can show subtitles of <paramref name="codec"/> (its native libraries are present).</summary>
    public static bool CanShow(SubtitleCodec? codec) => codec switch
    {
        null => false,
        { } c when c.IsBitmap() => FfmpegAudioDecoder.TryLoad(out _),
        _ => AssRenderer.Available,
    };

    /// <summary>Text formats that can be loaded from a file.</summary>
    public static bool CanShowFile(SubtitleCodec? codec) => codec is SubtitleCodec.Ass or SubtitleCodec.SubRip or SubtitleCodec.WebVtt && CanShow(codec);

    /// <param name="playbackDemuxer">The demuxer feeding the video, for files whose index doesn't cover the track.</param>
    /// <param name="video">The video track, for its size and frame rate.</param>
    public static SubtitlePlayback OpenEmbedded(string location, MatroskaTrack track, MatroskaDemuxer playbackDemuxer, MatroskaTrack? video,
        Func<VideoClock> clock, Action<string>? log = null)
    {
        var codec = SubtitleCodecs.FromMatroska(track.CodecId) ?? throw new NotSupportedException($"Subtitle codec {track.CodecId} isn't supported");
        int width = video?.Width ?? 0, height = video?.Height ?? 0;
        double frameRate = video is { DefaultDurationNs: > 0 } ? 1e9 / video.DefaultDurationNs : 24;
        List<MatroskaAttachment> fonts = codec == SubtitleCodec.Ass ? Fonts(location) : [];
        ISubtitleRenderer renderer = codec.IsBitmap()
            ? new BitmapSubtitleRenderer(codec, track.CodecPrivate, width, height)
            : new AssRenderer(codec, track.CodecPrivate, fonts, width, height, log);

        MatroskaSubtitleSource source;
        try
        {
            source = new MatroskaSubtitleSource(location, track.Number, playbackDemuxer);
        }
        catch
        {
            renderer.Dispose();
            throw;
        }

        if (log is not null)
        {
            source.Failed += log;
        }

        string description = $"subtitle #{track.Number} [{track.DisplayLanguage}] {track.CodecId}" +
                             $"{(track.Name is { Length: > 0 } name ? $" '{name}'" : "")}" +
                             $"{(source.Indexed ? ", read through the index" : ", following playback (no per-block index)")}" +
                             $"{(fonts.Count > 0 ? $", {fonts.Count} attached fonts" : "")}";
        var playback = new SubtitlePlayback(source, renderer, clock, frameRate, description);
        if (log is not null)
        {
            playback.Failed += log;
        }

        return playback;
    }

    /// <summary>A whole subtitle file: ASS/SSA, SubRip or WebVTT, in any common encoding.</summary>
    public static SubtitlePlayback OpenFile(byte[] content, SubtitleCodec codec, string label, int videoWidth, int videoHeight, double frameRate,
        Func<VideoClock> clock, Action<string>? log = null)
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
                renderer = new AssRenderer(codec, null, [], videoWidth, videoHeight, log);
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
