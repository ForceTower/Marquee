using Marquee.Playback.IO;
using Marquee.Playback.Matroska;

namespace Marquee.Playback.Demuxing;

/// <summary>Picks the reader for a file: Marquee's own for Matroska, libavformat for the rest.</summary>
internal static class Demuxer
{
    public static bool IsMatroska(string location) => MediaFile.Extension(location) is ".mkv" or ".mk3d";

    public static bool IsTransportStream(string location) => MediaFile.Extension(location) is ".ts" or ".m2ts" or ".mts";

    public static bool IsMp4(string location) => MediaFile.Extension(location) is ".mp4" or ".m4v" or ".mov";

    /// <param name="bufferSize">
    /// Read buffer. A Matroska reader that only wants a small track (e.g. audio) skips the other
    /// tracks' blocks and should use a small one, or every skip refills a large buffer it then throws
    /// away. MPEG-TS interleaves every track packet by packet, so it's read through whatever is
    /// wanted from it and always gets at least 1 MB.
    /// </param>
    public static IDemuxer Open(string location, int bufferSize = 1 << 20)
    {
        if (IsMatroska(location))
        {
            return MatroskaDemuxer.Open(location, bufferSize);
        }

        if (IsTransportStream(location))
        {
            return FfmpegDemuxer.Open(location, Math.Max(bufferSize, 1 << 20));
        }

        return FfmpegDemuxer.Open(location, bufferSize, probeStreams: !IsMp4(location));
    }
}
