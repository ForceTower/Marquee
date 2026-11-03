using System.Diagnostics;
using FFmpeg.AutoGen;
using Marquee.Playback.Demuxing;
using Marquee.Playback.Matroska;

namespace Marquee.Playback.Previews;

/// <summary>A preview picture: opaque BGRA pixels, and the time of the keyframe it shows.</summary>
internal sealed record PreviewFrame(TimeSpan Time, int Width, int Height, byte[] Pixels, string Kind, TimeSpan ReadTime, TimeSpan DecodeTime);

/// <summary>
/// Pictures of a file for the seek bar, decoded from its keyframes with libavcodec through a reader
/// of its own, so playback isn't disturbed. In Matroska the cues locate each keyframe's block
/// (mkvmerge writes its position in the cluster), so a picture costs one block read; MP4 and
/// MPEG-TS go through libavformat's timestamp search. Decoding is software, slice-threaded and
/// without the loop filters, which a picture this small can't show. Not thread-safe.
/// </summary>
internal sealed unsafe class FrameThumbnailer : IDisposable
{
    private const int MaxPacketsToKeyframe = 600;

    private readonly IDemuxer _demuxer;
    private readonly MediaTrack _video;
    private readonly List<CuePoint> _cues = [];
    private readonly bool _reshape;
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;

    private FrameThumbnailer(string location)
    {
        if (!FfmpegLibraries.TryLoad(out string? error))
        {
            throw new NotSupportedException($"FFmpeg libraries unavailable: {error}");
        }

        _demuxer = Demuxer.Open(location, 256 * 1024);
        try
        {
            _video = _demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)
                     ?? throw new InvalidDataException("No video track");
            if (_demuxer is MatroskaDemuxer matroska)
            {
                // Without video cues, other tracks' still lead to clusters, which start at or near a
                // keyframe. Without any, every picture would be the first one.
                var cues = matroska.Cues.Where(c => c.Track == _video.Number).ToList();
                _cues.AddRange((cues.Count > 0 ? cues : matroska.Cues).OrderBy(c => c.TimeTicks));
                if (_cues.Count == 0)
                {
                    throw new NotSupportedException("The file has no index (Matroska cues) to find keyframes by");
                }

                _demuxer.Expose(_video.Number, active: true);
            }

            // Profile 5's base layer is IPTPQc2; the others' (8.1, 8.4, 7) are HDR10 or HLG as they stand.
            _reshape = _video.DolbyVision is { Profile: 5 };
            OpenDecoder();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static FrameThumbnailer Open(string location) => new(location);

    public TimeSpan Duration => _demuxer.Duration;

    /// <summary>For logs: "HEVC 3840×2160, Dolby Vision profile 5…, 1520 keyframe cues".</summary>
    public string Description =>
        $"{ffmpeg.avcodec_get_name(_context->codec_id)} {_video.Width}×{_video.Height}" +
        (_video.DolbyVision is { } dv ? $", {dv}" : "") +
        (_demuxer is MatroskaDemuxer ? $", {_cues.Count} keyframe cues" : $", {_demuxer.Container} timestamp search");

    /// <summary>
    /// The keyframe a picture for <paramref name="target"/> would show, when the index says without
    /// reading (Matroska's cues), so a cached picture can be found first.
    /// </summary>
    public TimeSpan? KeyframeAt(TimeSpan target) => Cue(target) is { } cue ? TimeSpan.FromTicks(cue.TimeTicks) : null;

    private CuePoint? Cue(TimeSpan target)
    {
        if (_cues.Count == 0)
        {
            return null;
        }

        int lo = 0, hi = _cues.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_cues[mid].TimeTicks <= target.Ticks)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return _cues[found];
    }

    /// <summary>A picture <paramref name="width"/> pixels wide near <paramref name="target"/>, or null if none could be decoded there.</summary>
    public PreviewFrame? Render(TimeSpan target, int width)
    {
        var clock = Stopwatch.StartNew();
        if (ReadKeyframe(target) is not { } packet)
        {
            return null;
        }

        var read = clock.Elapsed;
        if (!Decode(packet))
        {
            return null;
        }

        try
        {
            if (PreviewPicture.ToBgra(_frame, width, _video.Colour, _reshape) is not { } picture)
            {
                return null;
            }

            return new PreviewFrame(TimeSpan.FromTicks(packet.TimeTicks), picture.Width, picture.Height, picture.Pixels, picture.Kind,
                read, clock.Elapsed - read);
        }
        finally
        {
            ffmpeg.av_frame_unref(_frame);
        }
    }

    private MediaPacket? ReadKeyframe(TimeSpan target)
    {
        switch (_demuxer)
        {
            case FfmpegDemuxer libav:
                return libav.ReadKeyframe(_video.Number, target);
            case MatroskaDemuxer matroska when Cue(target) is { } cue:
                if (cue.Track == _video.Number && cue.RelativePosition > 0 && matroska.ReadPacketAt(cue) is { KeyFrame: true } block)
                {
                    return block;
                }

                matroska.Seek(TimeSpan.FromTicks(cue.TimeTicks), _video.Number);
                break;
            default:
                _demuxer.Seek(target, _video.Number);
                break;
        }

        for (int i = 0; i < MaxPacketsToKeyframe && _demuxer.Next(_video.Number) is { } packet; i++)
        {
            if (packet.KeyFrame)
            {
                return packet;
            }
        }

        return null;
    }

    /// <summary>Decodes one keyframe on its own: send it, drain, take the picture, and start afresh for the next.</summary>
    private bool Decode(MediaPacket keyframe)
    {
        ffmpeg.avcodec_flush_buffers(_context);
        if (ffmpeg.av_new_packet(_packet, keyframe.Data.Length) < 0)
        {
            return false;
        }

        keyframe.Data.CopyTo(new Span<byte>(_packet->data, keyframe.Data.Length));
        _packet->flags = ffmpeg.AV_PKT_FLAG_KEY;
        int sent = ffmpeg.avcodec_send_packet(_context, _packet);
        ffmpeg.av_packet_unref(_packet);
        if (sent < 0)
        {
            return false;
        }

        ffmpeg.avcodec_send_packet(_context, null);
        return ffmpeg.avcodec_receive_frame(_context, _frame) >= 0;
    }

    private void OpenDecoder()
    {
        var (codecId, extradata) = Codec(_video);
        AVCodec* codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec == null)
        {
            throw new NotSupportedException($"No decoder for {_video.CodecId}");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        // Half the cores at most: playback (TrueHD decoding, Atmos rendering) keeps running beside it.
        _context->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        _context->thread_type = ffmpeg.FF_THREAD_SLICE;
        _context->skip_loop_filter = AVDiscard.AVDISCARD_ALL;
        _context->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST | ffmpeg.AV_CODEC_FLAG2_SHOW_ALL;
        if (extradata is { Length: > 0 })
        {
            _context->extradata = (byte*)ffmpeg.av_mallocz((ulong)(extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            _context->extradata_size = extradata.Length;
            extradata.CopyTo(new Span<byte>(_context->extradata, extradata.Length));
        }

        if (_video.DolbyVision is { } dv)
        {
            // AVDOVIDecoderConfigurationRecord: version major, minor, profile, level, RPU, EL and BL
            // present flags, BL signal compatibility id, metadata compression; a byte each.
            AVPacketSideData* side = ffmpeg.av_packet_side_data_new(&_context->coded_side_data, &_context->nb_coded_side_data,
                AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF, 9, 0);
            if (side != null)
            {
                byte* r = side->data;
                r[0] = 1;
                r[1] = 0;
                r[2] = (byte)dv.Profile;
                r[3] = (byte)dv.Level;
                r[4] = (byte)(dv.RpuPresent ? 1 : 0);
                r[5] = (byte)(dv.ElPresent ? 1 : 0);
                r[6] = (byte)(dv.BlPresent ? 1 : 0);
                r[7] = (byte)dv.Compatibility;
                r[8] = 0;
            }
        }

        int opened = ffmpeg.avcodec_open2(_context, codec, null);
        if (opened < 0)
        {
            throw new InvalidDataException($"Couldn't open the {_video.CodecId} decoder ({opened})");
        }

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>The decoder for a Matroska codec ID and its extradata (CodecPrivate, or what follows a VfW BITMAPINFOHEADER).</summary>
    private static (AVCodecID Id, byte[]? Extradata) Codec(MediaTrack track) => track.CodecId switch
    {
        "V_MPEGH/ISO/HEVC" => (AVCodecID.AV_CODEC_ID_HEVC, track.CodecPrivate),
        "V_MPEG4/ISO/AVC" => (AVCodecID.AV_CODEC_ID_H264, track.CodecPrivate),
        "V_AV1" => (AVCodecID.AV_CODEC_ID_AV1, track.CodecPrivate),
        "V_VP9" => (AVCodecID.AV_CODEC_ID_VP9, null),
        "V_VP8" => (AVCodecID.AV_CODEC_ID_VP8, null),
        "V_MPEG2" or "V_MPEG1" => (AVCodecID.AV_CODEC_ID_MPEG2VIDEO, track.CodecPrivate),
        "V_MS/VFW/FOURCC" when track.CodecPrivate is { Length: >= 40 } bih && Fourcc(bih) is "WVC1" => (AVCodecID.AV_CODEC_ID_VC1, bih[40..]),
        _ => throw new NotSupportedException($"No preview decoder for {track.CodecId}"),
    };

    private static string Fourcc(byte[] bitmapInfoHeader) => System.Text.Encoding.ASCII.GetString(bitmapInfoHeader, 16, 4);

    public void Dispose()
    {
        if (_frame != null)
        {
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet != null)
        {
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_context != null)
        {
            AVCodecContext* context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }

        _demuxer.Dispose();
    }
}
