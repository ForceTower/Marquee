using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;
using Marquee.Playback.IO;

namespace Marquee.Playback.Demuxing;

/// <summary>
/// libavformat for what Marquee doesn't read itself: MPEG-TS (Blu-ray M2TS too) for playback, and
/// MP4 for its subtitle tracks while Windows' own MP4 source plays the rest. Input goes through
/// <see cref="MediaFile.OpenRead"/>, so Plex URLs use <see cref="HttpRangeStream"/>.
///
/// Tracks are numbered from 1 in FFmpeg's stream order, which is Plex's order too (Plex's scanner
/// is FFmpeg). They and their packets are described as Matroska would store them (see
/// <see cref="MediaTrack"/>): MP4 timed text becomes S_TEXT/UTF8, a PGS display set becomes one
/// packet, and AAC from MPEG-TS stays ADTS, with no CodecPrivate. Times count from the file's
/// start time, as FFmpeg-based players and Plex count them.
///
/// Blu-ray's dual-layer Dolby Vision carries the enhancement layer, and with it the RPU, on a
/// stream of its own. Each base-layer access unit is held until the enhancement layer's for the
/// same time has been read, and goes out with its RPU appended, as Matroska stores profile 7
/// (without the enhancement layer, which Marquee drops anyway). The enhancement stream isn't
/// listed as a track.
/// </summary>
internal sealed unsafe class FfmpegDemuxer : IDemuxer
{
    private const int MaxQueuedInactivePackets = 400;
    private const int MaxHeldBaseUnits = 16;
    private const int MaxEarlyRpus = 64;
    private const int IoBufferSize = 64 * 1024;
    private const int EIO = 5;
    private static readonly AVRational TickBase = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };

    /// <summary>How far before a seek target to look for a keyframe, widening until one turns up.</summary>
    private static readonly int[] SeekMarginsSeconds = [1, 4, 15, 60];

    private readonly Stream _stream;
    private readonly avio_alloc_context_read_packet _read;
    private readonly avio_alloc_context_seek _seek;
    private readonly object _gate = new();
    private readonly List<MediaTrack> _tracks = [];
    private readonly Dictionary<int, MediaTrack> _tracksByNumber = new();
    private readonly Dictionary<int, MovText> _movText = new();
    private readonly Dictionary<int, Queue<MediaPacket>> _queues = new();
    private readonly HashSet<int> _active = new();
    private readonly Dictionary<int, Action<MediaPacket>> _taps = new();
    private readonly Dictionary<int, long> _nextTicks = new();
    private readonly Dictionary<int, (long Ticks, MemoryStream Data)> _displaySets = new();
    private readonly List<HeldBaseUnit> _held = [];
    private readonly SortedDictionary<long, byte[]> _earlyRpus = new();
    private readonly Queue<ReadPacket> _merged = new();
    private int _baseLayer;
    private int _enhancementLayer;
    private long _enhancementDecodeTicks = long.MinValue;
    private AVFormatContext* _format;
    private AVIOContext* _io;
    private AVPacket* _packet;
    private long _originTicks;
    private bool _endOfFile;
    private Exception? _ioError;
    private bool _disposed;

    private readonly record struct ReadPacket(MediaPacket Packet, long DecodeTicks);

    /// <summary>A base-layer access unit waiting for its RPU from the enhancement layer.</summary>
    private sealed class HeldBaseUnit(ReadPacket read)
    {
        public ReadPacket Read { get; } = read;
        public byte[]? Rpu { get; set; }
    }

    public string Container { get; private set; } = "";
    public IReadOnlyList<MediaTrack> Tracks => _tracks;
    public TimeSpan Duration { get; private set; }
    public bool CanSeek => true;
    public string SeekDescription => "seeks through libavformat";

    private FfmpegDemuxer(string location, int bufferSize, bool probeStreams)
    {
        if (!FfmpegLibraries.CanDemux)
        {
            throw new NotSupportedException("FFmpeg's libavformat isn't available (run tools/fetch-ffmpeg.ps1)");
        }

        _stream = MediaFile.OpenRead(location, bufferSize);
        _read = ReadStream;
        _seek = SeekStream;
        try
        {
            _io = ffmpeg.avio_alloc_context((byte*)ffmpeg.av_malloc(IoBufferSize), IoBufferSize, 0, null, _read, default, _seek);
            _format = ffmpeg.avformat_alloc_context();
            _format->pb = _io;
            AVFormatContext* format = _format;
            int opened = ffmpeg.avformat_open_input(&format, "input" + MediaFile.Extension(location), null, null);
            _format = format;
            Check(opened, "opening the file");
            if (probeStreams)
            {
                Check(ffmpeg.avformat_find_stream_info(_format, null), "reading stream info");
            }

            _packet = ffmpeg.av_packet_alloc();
            string name = Marshal.PtrToStringAnsi((IntPtr)_format->iformat->name) ?? "";
            Container = name switch
            {
                "mpegts" => "MPEG-TS",
                _ when name.StartsWith("mov,", StringComparison.Ordinal) => "MP4",
                _ => name,
            };
            // An MPEG-TS clock starts anywhere (often minutes in); MP4's media time starts at zero,
            // as Windows' own source counts it.
            _originTicks = probeStreams && _format->start_time != ffmpeg.AV_NOPTS_VALUE ? _format->start_time * 10 : 0;
            Duration = _format->duration > 0 ? TimeSpan.FromTicks(_format->duration * 10) : TimeSpan.Zero;
            BuildTracks(readHdrMetadata: probeStreams);
            PairDolbyVisionLayers();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <param name="probeStreams">
    /// Read packets to fill in what the header leaves out, and HDR10's static metadata. MPEG-TS
    /// needs it (it has no header beyond its program tables: codec parameters and the start time
    /// come from the packets). MP4's header describes everything a subtitle reader needs, and
    /// probing it would read into the interleaved media.
    /// </param>
    public static FfmpegDemuxer Open(string location, int bufferSize = 1 << 20, bool probeStreams = true) =>
        new(location, bufferSize, probeStreams);

    private void BuildTracks(bool readHdrMetadata)
    {
        for (int i = 0; i < _format->nb_streams; i++)
        {
            AVStream* stream = _format->streams[i];
            stream->discard = AVDiscard.AVDISCARD_ALL;
            AVCodecParameters* p = stream->codecpar;
            TrackKind? kind = p->codec_type switch
            {
                AVMediaType.AVMEDIA_TYPE_VIDEO when (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0 => TrackKind.Video,
                AVMediaType.AVMEDIA_TYPE_AUDIO => TrackKind.Audio,
                AVMediaType.AVMEDIA_TYPE_SUBTITLE => TrackKind.Subtitle,
                _ => null,
            };
            if (kind is not { } k)
            {
                continue;
            }

            byte[]? extradata = p->extradata_size > 0 ? new ReadOnlySpan<byte>(p->extradata, p->extradata_size).ToArray() : null;
            var track = new MediaTrack
            {
                Number = i + 1,
                Kind = k,
                CodecId = CodecId(p->codec_id, k),
                CodecPrivate = p->codec_id is AVCodecID.AV_CODEC_ID_MOV_TEXT or AVCodecID.AV_CODEC_ID_AAC ? null : extradata,
                Name = Tag(stream->metadata, "title"),
                Language = Tag(stream->metadata, "language") ?? "und",
                IsDefault = (stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0,
                IsForced = (stream->disposition & ffmpeg.AV_DISPOSITION_FORCED) != 0,
                Width = p->width,
                Height = p->height,
                SampleRate = p->sample_rate,
                Channels = p->ch_layout.nb_channels,
                DefaultDurationNs = k == TrackKind.Video ? FrameDurationNs(stream) : 0,
                DolbyVision = DolbyVision(p),
            };
            if (p->codec_id == AVCodecID.AV_CODEC_ID_MOV_TEXT)
            {
                _movText[track.Number] = new MovText(extradata);
            }

            _tracks.Add(track);
            _tracksByNumber[track.Number] = track;
            if (k == TrackKind.Video)
            {
                track.Colour = Colour(track, p, readHdrMetadata);
            }
        }
    }

    /// <summary>
    /// libavformat groups a dual-layer Dolby Vision pair (the enhancement layer's DOVI descriptor
    /// names the base layer's PID). The base layer takes over the profile 7 configuration, which
    /// is what makes the media source convert it to 8.1.
    /// </summary>
    private void PairDolbyVisionLayers()
    {
        for (int g = 0; g < _format->nb_stream_groups; g++)
        {
            AVStreamGroup* group = _format->stream_groups[g];
            if (group->type != AVStreamGroupParamsType.AV_STREAM_GROUP_PARAMS_DOLBY_VISION || group->nb_streams != 2)
            {
                continue;
            }

            var layers = new[] { group->streams[0]->index + 1, group->streams[1]->index + 1 }
                .Select(number => _tracksByNumber.GetValueOrDefault(number))
                .ToList();
            var enhancement = layers.FirstOrDefault(t => t?.DolbyVision is { BlPresent: false });
            var baseLayer = layers.FirstOrDefault(t => t is not null && t != enhancement && t.CodecId == "V_MPEGH/ISO/HEVC");
            if (enhancement is null || baseLayer is null)
            {
                continue;
            }

            baseLayer.DolbyVision = enhancement.DolbyVision! with { BlPresent = true };
            _tracks.Remove(enhancement);
            _baseLayer = baseLayer.Number;
            _enhancementLayer = enhancement.Number;
            return;
        }
    }

    private static string CodecId(AVCodecID id, TrackKind kind) => id switch
    {
        AVCodecID.AV_CODEC_ID_HEVC => "V_MPEGH/ISO/HEVC",
        AVCodecID.AV_CODEC_ID_H264 => "V_MPEG4/ISO/AVC",
        AVCodecID.AV_CODEC_ID_AV1 => "V_AV1",
        AVCodecID.AV_CODEC_ID_VP9 => "V_VP9",
        AVCodecID.AV_CODEC_ID_MPEG2VIDEO => "V_MPEG2",
        AVCodecID.AV_CODEC_ID_TRUEHD => "A_TRUEHD",
        AVCodecID.AV_CODEC_ID_MLP => "A_MLP",
        AVCodecID.AV_CODEC_ID_AC3 => "A_AC3",
        AVCodecID.AV_CODEC_ID_EAC3 => "A_EAC3",
        AVCodecID.AV_CODEC_ID_DTS => "A_DTS",
        AVCodecID.AV_CODEC_ID_AAC => "A_AAC",
        AVCodecID.AV_CODEC_ID_FLAC => "A_FLAC",
        AVCodecID.AV_CODEC_ID_OPUS => "A_OPUS",
        AVCodecID.AV_CODEC_ID_MP2 => "A_MPEG/L2",
        AVCodecID.AV_CODEC_ID_MP3 => "A_MPEG/L3",
        AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE => "S_HDMV/PGS",
        AVCodecID.AV_CODEC_ID_DVB_SUBTITLE => "S_DVBSUB",
        AVCodecID.AV_CODEC_ID_MOV_TEXT => "S_TEXT/UTF8",

        // Anything else keeps FFmpeg's name, which nothing downstream takes: WebVTT and VobSub in
        // MP4, for instance, are stored differently from their Matroska forms.
        _ => $"{(kind switch { TrackKind.Video => 'V', TrackKind.Audio => 'A', _ => 'S' })}_FFMPEG/{ffmpeg.avcodec_get_name(id)}",
    };

    private static string? Tag(AVDictionary* metadata, string key)
    {
        AVDictionaryEntry* entry = ffmpeg.av_dict_get(metadata, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((IntPtr)entry->value);
    }

    private static long FrameDurationNs(AVStream* stream)
    {
        AVRational rate = stream->avg_frame_rate is { num: > 0, den: > 0 } average ? average : stream->r_frame_rate;
        return rate is { num: > 0, den: > 0 } ? 1_000_000_000L * rate.den / rate.num : 0;
    }

    /// <summary>The stream's DOVI configuration (from an MPEG-TS DOVI descriptor or an MP4 dvcC/dvvC box).</summary>
    private static DolbyVisionConfig? DolbyVision(AVCodecParameters* p)
    {
        AVPacketSideData* data = ffmpeg.av_packet_side_data_get(p->coded_side_data, p->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF);
        if (data == null || data->size < 8)
        {
            return null;
        }

        // AVDOVIDecoderConfigurationRecord: version major, minor, profile, level, RPU, EL and BL
        // present flags, BL signal compatibility id, all one byte each.
        byte* r = data->data;
        return new DolbyVisionConfig(r[2], r[3], r[4] != 0, r[5] != 0, r[6] != 0, r[7]);
    }

    /// <summary>
    /// The colour description FFmpeg read from the bitstream, plus HDR10's static metadata, which
    /// only a decoded frame carries (the stream's SEI): one keyframe is decoded for PQ and HLG video.
    /// </summary>
    private ColourInfo Colour(MediaTrack track, AVCodecParameters* p, bool readHdrMetadata)
    {
        static int? Specified(int code, int unspecified) => code == unspecified || code <= 0 && unspecified != 0 ? null : code;

        int? transfer = Specified((int)p->color_trc, (int)AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED);
        int? maxCll = null, maxFall = null;
        double? masteringMax = null, masteringMin = null;
        if (readHdrMetadata && transfer is 16 or 18)
        {
            (maxCll, maxFall, masteringMax, masteringMin) = HdrMetadata(track, p);
        }

        return new ColourInfo(
            Matrix: Specified((int)p->color_space, (int)AVColorSpace.AVCOL_SPC_UNSPECIFIED),
            Range: Specified((int)p->color_range, (int)AVColorRange.AVCOL_RANGE_UNSPECIFIED),
            Transfer: transfer,
            Primaries: Specified((int)p->color_primaries, (int)AVColorPrimaries.AVCOL_PRI_UNSPECIFIED),
            MaxCll: maxCll,
            MaxFall: maxFall,
            MasteringMax: masteringMax,
            MasteringMin: masteringMin);
    }

    private (int? MaxCll, int? MaxFall, double? MasteringMax, double? MasteringMin) HdrMetadata(MediaTrack track, AVCodecParameters* p)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(p->codec_id);
        if (codec == null)
        {
            return default;
        }

        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            context->thread_count = 1;
            if (ffmpeg.avcodec_parameters_to_context(context, p) < 0 || ffmpeg.avcodec_open2(context, codec, null) < 0)
            {
                return default;
            }

            foreach (var first in FirstPackets(track.Number, 16))
            {
                ffmpeg.av_new_packet(packet, first.Data.Length);
                first.Data.CopyTo(new Span<byte>(packet->data, first.Data.Length));
                packet->flags = first.KeyFrame ? ffmpeg.AV_PKT_FLAG_KEY : 0;
                ffmpeg.avcodec_send_packet(context, packet);
                ffmpeg.av_packet_unref(packet);
                if (ffmpeg.avcodec_receive_frame(context, frame) >= 0)
                {
                    return HdrMetadata(frame);
                }
            }

            ffmpeg.avcodec_send_packet(context, null);
            return ffmpeg.avcodec_receive_frame(context, frame) >= 0 ? HdrMetadata(frame) : default;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&context);
        }
    }

    private static (int? MaxCll, int? MaxFall, double? MasteringMax, double? MasteringMin) HdrMetadata(AVFrame* frame)
    {
        int? maxCll = null, maxFall = null;
        double? max = null, min = null;
        AVFrameSideData* light = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (light != null)
        {
            var cll = (AVContentLightMetadata*)light->data;
            maxCll = (int)cll->MaxCLL;
            maxFall = (int)cll->MaxFALL;
        }

        AVFrameSideData* display = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        if (display != null && ((AVMasteringDisplayMetadata*)display->data)->has_luminance != 0)
        {
            var mastering = (AVMasteringDisplayMetadata*)display->data;
            max = ffmpeg.av_q2d(mastering->max_luminance);
            min = ffmpeg.av_q2d(mastering->min_luminance);
        }

        return (maxCll, maxFall, max, min);
    }

    public void Expose(int track, bool active)
    {
        lock (_gate)
        {
            _queues.TryAdd(track, new Queue<MediaPacket>());
            Stream(track)->discard = AVDiscard.AVDISCARD_DEFAULT;
            if (track == _baseLayer)
            {
                Stream(_enhancementLayer)->discard = AVDiscard.AVDISCARD_DEFAULT;
            }

            SetActive(track, active);
        }
    }

    public void SetActive(int track, bool active)
    {
        lock (_gate)
        {
            if (active)
            {
                _active.Add(track);
            }
            else
            {
                _active.Remove(track);
            }
        }
    }

    public void DropBefore(int track, long timeTicks)
    {
        lock (_gate)
        {
            if (_queues.TryGetValue(track, out var queue))
            {
                while (queue.Count > 0 && queue.Peek().TimeTicks < timeTicks)
                {
                    queue.Dequeue();
                }
            }
        }
    }

    public void Tap(int track, Action<MediaPacket>? sink)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (sink is null)
            {
                _taps.Remove(track);
                if (!_queues.ContainsKey(track))
                {
                    Stream(track)->discard = AVDiscard.AVDISCARD_ALL;
                }
            }
            else
            {
                _taps[track] = sink;
                Stream(track)->discard = AVDiscard.AVDISCARD_DEFAULT;
            }
        }
    }

    public MediaPacket? Next(int track)
    {
        lock (_gate)
        {
            var queue = _queues[track];
            while (queue.Count == 0)
            {
                if (_endOfFile || Read(t => _queues.ContainsKey(t) || _taps.ContainsKey(t)) is not { } read)
                {
                    _endOfFile = true;
                    return null;
                }

                Route(read.Packet);
            }

            return queue.Dequeue();
        }
    }

    public List<MediaPacket> FirstPackets(int track, int count)
    {
        lock (_gate)
        {
            AVStream* stream = Stream(track);
            AVDiscard discard = stream->discard;
            stream->discard = AVDiscard.AVDISCARD_DEFAULT;
            var packets = new List<MediaPacket>();
            try
            {
                SeekTo(track, 0);
                while (packets.Count < count && Read(t => t == track) is { } read)
                {
                    packets.Add(read.Packet);
                }
            }
            finally
            {
                stream->discard = discard;
                Reset();
                SeekTo(track, 0);
            }

            return packets;
        }
    }

    /// <summary>
    /// MPEG-TS has no index: libavformat finds a position by searching timestamps, from which
    /// reading goes forward to the last keyframe at or before the target. What's read on the way
    /// from that keyframe is kept for delivery, the other tracks' packets from its time on.
    /// </summary>
    public TimeSpan Seek(TimeSpan target, int track)
    {
        lock (_gate)
        {
            Reset();
            bool video = _tracksByNumber.GetValueOrDefault(track)?.Kind == TrackKind.Video;
            long targetTicks = Math.Max(target.Ticks, 0);
            foreach (int margin in SeekMarginsSeconds)
            {
                long from = targetTicks - margin * TimeSpan.TicksPerSecond;
                SeekTo(track, Math.Max(from, 0));
                if (Scan(track, video, targetTicks, fromStart: from <= 0) is { } found)
                {
                    foreach (var packet in found.Packets)
                    {
                        Route(packet);
                    }

                    return TimeSpan.FromTicks(Math.Max(found.Time, 0));
                }

                if (from <= 0)
                {
                    break;
                }
            }

            Reset();
            SeekTo(track, 0);
            return TimeSpan.Zero;
        }
    }

    private (long Time, List<MediaPacket> Packets)? Scan(int track, bool video, long target, bool fromStart)
    {
        long? start = null;
        var kept = new List<MediaPacket>();
        while (Read(t => t == track || _queues.ContainsKey(t) || _taps.ContainsKey(t)) is { } read)
        {
            var (packet, decodeTicks) = read;
            if (packet.Track != track)
            {
                kept.Add(packet);
                continue;
            }

            // From the start of the file, or for a track that can start anywhere (audio, subtitles),
            // the first start point will do even if it's past the target.
            if (packet.KeyFrame && (packet.TimeTicks <= target || start is null && (fromStart || !video)))
            {
                long time = packet.TimeTicks;
                start = time;
                kept.RemoveAll(k => k.Track == track || k.TimeTicks < time);
                kept.Add(packet);
                if (time > target)
                {
                    break;
                }

                continue;
            }

            if (start is null)
            {
                if (decodeTicks > target)
                {
                    return null;
                }

                continue;
            }

            kept.Add(packet);
            if (decodeTicks > target)
            {
                break;
            }
        }

        if (start is not long startTime)
        {
            return null;
        }

        kept.RemoveAll(k => k.Track != track && k.TimeTicks < startTime);
        return (startTime, kept);
    }

    /// <summary>
    /// Positions libavformat at or before <paramref name="ticks"/> by the track's timestamps. In
    /// MPEG-TS that's a search by decode time reading packets of the track, so it mustn't be
    /// discarded meanwhile (the search would read to the end of the file looking for one), and the
    /// start is the start of the file: searching for time 0 lands after the first keyframe, which
    /// is decoded before it's presented.
    /// </summary>
    private void SeekTo(int track, long ticks)
    {
        // MP4 can't seek by bytes; its sample tables find the first sample by time.
        if (ticks <= 0 && ffmpeg.av_seek_frame(_format, -1, 0, ffmpeg.AVSEEK_FLAG_BYTE) >= 0)
        {
            ThrowIfIoFailed();
            return;
        }

        int index = track - 1;
        AVStream* stream = _format->streams[index];
        AVDiscard discard = stream->discard;
        stream->discard = AVDiscard.AVDISCARD_DEFAULT;
        try
        {
            long timestamp = ticks <= 0 ? long.MinValue : ffmpeg.av_rescale_q(ticks + _originTicks, TickBase, stream->time_base);
            if (ffmpeg.avformat_seek_file(_format, index, long.MinValue, timestamp, timestamp, 0) < 0)
            {
                ffmpeg.av_seek_frame(_format, -1, 0, ffmpeg.AVSEEK_FLAG_BYTE);
            }
        }
        finally
        {
            stream->discard = discard;
        }

        ThrowIfIoFailed();
    }

    private void Reset()
    {
        foreach (var queue in _queues.Values)
        {
            queue.Clear();
        }

        _nextTicks.Clear();
        _displaySets.Clear();
        _held.Clear();
        _earlyRpus.Clear();
        _merged.Clear();
        _enhancementDecodeTicks = long.MinValue;
        _endOfFile = false;
    }

    private void Route(MediaPacket packet)
    {
        if (_taps.TryGetValue(packet.Track, out var sink))
        {
            sink(packet);
        }
        else if (_queues.TryGetValue(packet.Track, out var queue))
        {
            queue.Enqueue(packet);
            if (!_active.Contains(packet.Track) && queue.Count > MaxQueuedInactivePackets)
            {
                queue.Dequeue();
            }
        }
    }

    private ReadPacket? Read(Func<int, bool> wanted)
    {
        while (true)
        {
            if (_merged.TryDequeue(out var merged))
            {
                if (wanted(merged.Packet.Track))
                {
                    return merged;
                }

                continue;
            }

            int result = ffmpeg.av_read_frame(_format, _packet);
            if (result < 0)
            {
                ThrowIfIoFailed();
                if (_held.Count > 0)
                {
                    ReleaseHeld(all: true);
                    continue;
                }

                return null;
            }

            ReadPacket? read = null;
            int number = _packet->stream_index + 1;
            try
            {
                bool layer = number == _enhancementLayer;
                if ((layer || wanted(number)) && _tracksByNumber.TryGetValue(number, out var track))
                {
                    read = Convert(track);
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }

            if (read is not { } packet)
            {
                continue;
            }

            if (number == _enhancementLayer)
            {
                TakeRpu(packet);
            }
            else if (number == _baseLayer && Stream(_enhancementLayer)->discard != AVDiscard.AVDISCARD_ALL)
            {
                Hold(packet);
            }
            else
            {
                return packet;
            }
        }
    }

    private void Hold(ReadPacket baseUnit)
    {
        var held = new HeldBaseUnit(baseUnit);
        if (_earlyRpus.TryGetValue(baseUnit.Packet.TimeTicks, out byte[]? rpu))
        {
            _earlyRpus.Remove(baseUnit.Packet.TimeTicks);
            held.Rpu = rpu;
        }

        _held.Add(held);
        ReleaseHeld(all: false);
    }

    /// <summary>Takes the RPU (NAL unit type 62) out of an enhancement-layer access unit; the rest of the layer is dropped.</summary>
    private void TakeRpu(ReadPacket enhancementUnit)
    {
        _enhancementDecodeTicks = Math.Max(_enhancementDecodeTicks, enhancementUnit.DecodeTicks);
        var rpu = AnnexB.Units(enhancementUnit.Packet.Data).FirstOrDefault(unit => AnnexB.HevcType(unit.Span) == 62);
        if (!rpu.IsEmpty)
        {
            long time = enhancementUnit.Packet.TimeTicks;
            if (_held.Find(waiting => waiting.Rpu is null && waiting.Read.Packet.TimeTicks == time) is { } held)
            {
                held.Rpu = rpu.ToArray();
            }
            else
            {
                _earlyRpus[time] = rpu.ToArray();
                if (_earlyRpus.Count > MaxEarlyRpus)
                {
                    _earlyRpus.Remove(_earlyRpus.Keys.First());
                }
            }
        }

        ReleaseHeld(all: false);
    }

    /// <summary>
    /// Lets base-layer units go in decode order, each once it has its RPU, or once the enhancement
    /// layer has gone past it in decode order without one (or too many wait: the layer stopped).
    /// </summary>
    private void ReleaseHeld(bool all)
    {
        while (_held.Count > 0)
        {
            var head = _held[0];
            if (!all && head.Rpu is null && _enhancementDecodeTicks < head.Read.DecodeTicks && _held.Count <= MaxHeldBaseUnits)
            {
                return;
            }

            _held.RemoveAt(0);
            _merged.Enqueue(head.Rpu is { } rpu ? WithRpu(head.Read, rpu) : head.Read);
        }
    }

    private static ReadPacket WithRpu(ReadPacket baseUnit, byte[] rpu)
    {
        byte[] data = [.. baseUnit.Packet.Data, 0, 0, 0, 1, .. rpu];
        return baseUnit with { Packet = baseUnit.Packet with { Data = data, FrameSizes = [data.Length] } };
    }

    private ReadPacket? Convert(MediaTrack track)
    {
        AVStream* stream = _format->streams[_packet->stream_index];
        long pts = _packet->pts != ffmpeg.AV_NOPTS_VALUE ? _packet->pts : _packet->dts;
        long dts = _packet->dts != ffmpeg.AV_NOPTS_VALUE ? _packet->dts : _packet->pts;
        long ticks = pts != ffmpeg.AV_NOPTS_VALUE
            ? ffmpeg.av_rescale_q(pts, stream->time_base, TickBase) - _originTicks
            : _nextTicks.GetValueOrDefault(track.Number);
        long decodeTicks = dts != ffmpeg.AV_NOPTS_VALUE ? ffmpeg.av_rescale_q(dts, stream->time_base, TickBase) - _originTicks : ticks;
        long duration = _packet->duration > 0 ? ffmpeg.av_rescale_q(_packet->duration, stream->time_base, TickBase) : 0;
        _nextTicks[track.Number] = ticks + duration;

        var payload = new ReadOnlySpan<byte>(_packet->data, _packet->size);
        if (track.CodecId == "S_HDMV/PGS")
        {
            return CollectDisplaySet(track.Number, payload, ticks, decodeTicks);
        }

        byte[] data;
        if (_movText.TryGetValue(track.Number, out var movText))
        {
            string text = movText.ToText(payload);
            if (text.Length == 0)
            {
                return null;
            }

            data = Encoding.UTF8.GetBytes(text);
        }
        else
        {
            data = payload.ToArray();
        }

        bool keyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
        return new ReadPacket(new MediaPacket(track.Number, ticks, duration, keyFrame, data, [data.Length]), decodeTicks);
    }

    /// <summary>
    /// MPEG-TS carries a PGS display set as a PES packet per segment (presentation composition,
    /// windows, palettes, objects, end). Matroska stores the whole set as one block at the
    /// composition's time, which is what the renderer decodes, so the segments are collected
    /// from a composition to its end segment.
    /// </summary>
    private ReadPacket? CollectDisplaySet(int track, ReadOnlySpan<byte> payload, long ticks, long decodeTicks)
    {
        const byte PresentationComposition = 0x16;
        const byte End = 0x80;
        for (int pos = 0; pos + 3 <= payload.Length;)
        {
            byte type = payload[pos];
            int end = Math.Min(payload.Length, pos + 3 + ((payload[pos + 1] << 8) | payload[pos + 2]));
            if (type == PresentationComposition)
            {
                _displaySets[track] = (ticks, new MemoryStream());
            }

            if (_displaySets.TryGetValue(track, out var set))
            {
                set.Data.Write(payload[pos..end]);
            }

            pos = end;
            if (type == End && _displaySets.Remove(track, out var done))
            {
                byte[] data = done.Data.ToArray();
                return new ReadPacket(new MediaPacket(track, done.Ticks, 0, true, data, [data.Length]), decodeTicks);
            }
        }

        return null;
    }

    private AVStream* Stream(int track) => _format->streams[track - 1];

    private int ReadStream(void* opaque, byte* buffer, int size)
    {
        try
        {
            int read = _stream.Read(new Span<byte>(buffer, size));
            return read > 0 ? read : ffmpeg.AVERROR_EOF;
        }
        catch (Exception ex)
        {
            _ioError = ex;
            return ffmpeg.AVERROR(EIO);
        }
    }

    private long SeekStream(void* opaque, long offset, int whence)
    {
        try
        {
            if ((whence & ffmpeg.AVSEEK_SIZE) != 0)
            {
                return _stream.Length;
            }

            long position = (whence & ~ffmpeg.AVSEEK_FORCE) switch
            {
                0 => offset,
                1 => _stream.Position + offset,
                2 => _stream.Length + offset,
                _ => -1,
            };
            if (position < 0)
            {
                return -1;
            }

            _stream.Position = position;
            return position;
        }
        catch (Exception ex)
        {
            _ioError = ex;
            return -1;
        }
    }

    /// <summary>A failed read reaches libavformat as an I/O error; surface the real exception, as the Matroska reader would.</summary>
    private void ThrowIfIoFailed()
    {
        if (_ioError is { } error)
        {
            _ioError = null;
            throw new IOException($"Reading the file failed: {error.Message}", error);
        }
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            byte* buffer = stackalloc byte[256];
            ffmpeg.av_strerror(result, buffer, 256);
            throw new InvalidDataException($"libavformat failed {what}: {Marshal.PtrToStringAnsi((IntPtr)buffer)}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_packet != null)
            {
                AVPacket* packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }

            if (_format != null)
            {
                AVFormatContext* format = _format;
                ffmpeg.avformat_close_input(&format);
                _format = null;
            }

            if (_io != null)
            {
                ffmpeg.av_freep(&_io->buffer);
                AVIOContext* io = _io;
                ffmpeg.avio_context_free(&io);
                _io = null;
            }

            _stream.Dispose();
        }
    }
}
