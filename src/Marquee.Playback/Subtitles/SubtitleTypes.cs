using System.Buffers;

namespace Marquee.Playback.Subtitles;

internal enum SubtitleCodec
{
    Ass,
    SubRip,
    WebVtt,
    Pgs,
    VobSub,
    DvbSub,
}

internal static class SubtitleCodecs
{
    public static SubtitleCodec? FromMatroska(string codecId) => codecId switch
    {
        "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" => SubtitleCodec.Ass,
        "S_TEXT/UTF8" or "S_TEXT/ASCII" => SubtitleCodec.SubRip,
        "S_TEXT/WEBVTT" => SubtitleCodec.WebVtt,
        "S_HDMV/PGS" => SubtitleCodec.Pgs,
        "S_VOBSUB" => SubtitleCodec.VobSub,
        "S_DVBSUB" => SubtitleCodec.DvbSub,
        _ => null,
    };

    /// <summary>From a stream's codec name (Plex's or the library's) or a file extension without the dot.</summary>
    public static SubtitleCodec? FromName(string? name) => name?.ToLowerInvariant() switch
    {
        "ass" or "ssa" => SubtitleCodec.Ass,
        "srt" or "subrip" => SubtitleCodec.SubRip,
        "vtt" or "webvtt" => SubtitleCodec.WebVtt,
        "pgs" or "hdmv_pgs_subtitle" => SubtitleCodec.Pgs,
        "vobsub" or "dvd_subtitle" => SubtitleCodec.VobSub,
        "dvb_subtitle" or "dvbsub" => SubtitleCodec.DvbSub,
        _ => null,
    };

    public static bool IsBitmap(this SubtitleCodec codec) => codec is SubtitleCodec.Pgs or SubtitleCodec.VobSub or SubtitleCodec.DvbSub;
}

/// <summary>One subtitle event or display set, as stored in the container.</summary>
internal sealed record SubtitlePacket(long TimeTicks, long DurationTicks, byte[] Data);

/// <summary>The subtitle overlay in physical pixels: its size and where the video sits in it.</summary>
internal readonly record struct SubtitleLayout(int Width, int Height, int VideoX, int VideoY, int VideoWidth, int VideoHeight)
{
    public bool IsEmpty => Width <= 0 || Height <= 0 || VideoWidth <= 0 || VideoHeight <= 0;

    /// <summary>The video fitted (letterboxed or pillarboxed) into an overlay of the given size.</summary>
    public static SubtitleLayout Fit(int width, int height, int videoWidth, int videoHeight)
    {
        if (width <= 0 || height <= 0 || videoWidth <= 0 || videoHeight <= 0)
        {
            return new SubtitleLayout(width, height, 0, 0, width, height);
        }

        double scale = Math.Min(width / (double)videoWidth, height / (double)videoHeight);
        int w = (int)Math.Round(videoWidth * scale);
        int h = (int)Math.Round(videoHeight * scale);
        return new SubtitleLayout(width, height, (width - w) / 2, (height - h) / 2, w, h);
    }
}

/// <summary>
/// A premultiplied BGRA bitmap (stride = <see cref="Width"/> × 4) and the overlay rectangle it
/// covers, in overlay pixels. Bitmap subtitles are drawn at their own resolution and scaled.
/// </summary>
internal sealed class SubtitleImage
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double DisplayWidth { get; init; }
    public required double DisplayHeight { get; init; }

    /// <summary>Rented from <see cref="ArrayPool{T}.Shared"/>; <see cref="SubtitleFrame.Release"/> returns it.</summary>
    public bool Pooled { get; init; }
}

/// <summary>What the overlay shows now. No images clears it.</summary>
internal sealed class SubtitleFrame(IReadOnlyList<SubtitleImage> images)
{
    public static SubtitleFrame Empty { get; } = new([]);

    public IReadOnlyList<SubtitleImage> Images { get; } = images;

    /// <summary>Returns pooled pixel buffers. Call once the pixels have been copied out.</summary>
    public void Release()
    {
        foreach (var image in Images)
        {
            if (image.Pooled)
            {
                ArrayPool<byte>.Shared.Return(image.Pixels);
            }
        }
    }
}

/// <summary>Turns a subtitle track's packets into frames. Only the subtitle thread calls it.</summary>
internal interface ISubtitleRenderer : IDisposable
{
    void Add(SubtitlePacket packet);

    /// <summary>
    /// The frame at <paramref name="ticks"/> (media time), or null if it's the same as the last one
    /// returned and <paramref name="force"/> is false.
    /// </summary>
    SubtitleFrame? Render(long ticks, SubtitleLayout layout, bool force);
}

/// <summary>Where a track's packets come from: a container read alongside playback, or a whole file.</summary>
internal interface ISubtitleSource : IDisposable
{
    bool TryTake(out SubtitlePacket packet);

    /// <summary>Tells the source what time is playing, so it can fetch what's needed around it.</summary>
    void Focus(long ticks);
}
