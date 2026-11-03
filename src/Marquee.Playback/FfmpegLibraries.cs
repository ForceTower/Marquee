using FFmpeg.AutoGen;

namespace Marquee.Playback;

/// <summary>
/// The LGPL FFmpeg build bundled in the output's ffmpeg folder: libavcodec for audio and bitmap
/// subtitle decoding, libavformat for MPEG-TS demuxing and MP4 subtitle reading. It's separate from
/// the FFmpeg linked into libmpv.
/// </summary>
internal static class FfmpegLibraries
{
    private static readonly object LoadLock = new();
    private static bool? s_loaded;
    private static string? s_error;
    private static bool? s_demuxing;

    /// <summary>Points FFmpeg.AutoGen at the bundled libraries; false if libavcodec is missing or unloadable.</summary>
    public static bool TryLoad(out string? error)
    {
        lock (LoadLock)
        {
            if (s_loaded is null)
            {
                try
                {
                    ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
                    _ = ffmpeg.avcodec_version();
                    s_loaded = true;
                }
                catch (Exception ex)
                {
                    s_loaded = false;
                    s_error = ex.Message;
                }
            }

            error = s_error;
            return s_loaded.Value;
        }
    }

    /// <summary>Whether libavformat is there too (older checkouts fetched only the decoding libraries).</summary>
    public static bool CanDemux
    {
        get
        {
            if (!TryLoad(out _))
            {
                return false;
            }

            lock (LoadLock)
            {
                if (s_demuxing is null)
                {
                    try
                    {
                        _ = ffmpeg.avformat_version();
                        s_demuxing = true;
                    }
                    catch (Exception)
                    {
                        s_demuxing = false;
                    }
                }

                return s_demuxing.Value;
            }
        }
    }
}
