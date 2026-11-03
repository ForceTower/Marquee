using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Marquee.Playback.Matroska;

/// <summary>
/// Decodes audio codecs Windows has no decoder for (TrueHD, DTS) with FFmpeg's libavcodec, producing
/// interleaved 32-bit float PCM for Media Foundation. Same decoders mpv uses: TrueHD gives its
/// lossless 7.1 bed and DTS-HD MA its lossless core + XLL; Atmos/DTS:X objects are not decoded.
/// </summary>
internal sealed unsafe class FfmpegAudioDecoder : IDisposable
{
    private static readonly Dictionary<string, AVCodecID> CodecsByMatroskaId = new()
    {
        ["A_TRUEHD"] = AVCodecID.AV_CODEC_ID_TRUEHD,
        ["A_MLP"] = AVCodecID.AV_CODEC_ID_MLP,
        ["A_DTS"] = AVCodecID.AV_CODEC_ID_DTS,
        ["A_DTS/EXPRESS"] = AVCodecID.AV_CODEC_ID_DTS,
        ["A_DTS/LOSSLESS"] = AVCodecID.AV_CODEC_ID_DTS,
    };

    private readonly AVCodecContext* _context;
    private readonly AVPacket* _packet;
    private readonly AVFrame* _frame;
    private readonly MemoryStream _output = new();

    public string CodecName { get; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public ulong ChannelMask { get; private set; }
    public int Errors { get; private set; }

    public static bool CanDecode(string matroskaCodecId) => CodecsByMatroskaId.ContainsKey(matroskaCodecId);

    public FfmpegAudioDecoder(string matroskaCodecId, byte[]? codecPrivate, int sampleRate, int channels)
    {
        AVCodecID id = CodecsByMatroskaId[matroskaCodecId];
        AVCodec* codec = ffmpeg.avcodec_find_decoder(id);
        if (codec == null)
        {
            throw new NotSupportedException($"FFmpeg has no decoder for {id}");
        }

        CodecName = ffmpeg.avcodec_get_name(id);
        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->sample_rate = sampleRate;
        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, Math.Max(channels, 1));
        _context->ch_layout = layout;
        _context->request_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;

        if (codecPrivate is { Length: > 0 })
        {
            _context->extradata = (byte*)ffmpeg.av_mallocz((ulong)(codecPrivate.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            Marshal.Copy(codecPrivate, 0, (IntPtr)_context->extradata, codecPrivate.Length);
            _context->extradata_size = codecPrivate.Length;
        }

        Check(ffmpeg.avcodec_open2(_context, codec, null), "avcodec_open2");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>
    /// Decodes one compressed frame (one lace of a Matroska block) and returns the PCM produced, as
    /// interleaved float32. Returns an empty array while the decoder is still waiting for a sync
    /// point, e.g. right after a seek into TrueHD.
    /// </summary>
    public byte[] Decode(ReadOnlySpan<byte> frame)
    {
        _output.SetLength(0);
        Check(ffmpeg.av_new_packet(_packet, frame.Length), "av_new_packet");
        frame.CopyTo(new Span<byte>(_packet->data, frame.Length));
        int sent = ffmpeg.avcodec_send_packet(_context, _packet);
        ffmpeg.av_packet_unref(_packet);
        if (sent < 0)
        {
            Errors++;
            return [];
        }

        while (true)
        {
            int received = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (received < 0)
            {
                Errors++;
                break;
            }

            AppendInterleavedFloat(_frame);
            ffmpeg.av_frame_unref(_frame);
        }

        return _output.ToArray();
    }

    public void Flush() => ffmpeg.avcodec_flush_buffers(_context);

    private void AppendInterleavedFloat(AVFrame* frame)
    {
        int channels = frame->ch_layout.nb_channels;
        int samples = frame->nb_samples;
        SampleRate = frame->sample_rate;
        Channels = channels;
        ChannelMask = frame->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_NATIVE ? frame->ch_layout.u.mask : 0;

        var format = (AVSampleFormat)frame->format;
        bool planar = ffmpeg.av_sample_fmt_is_planar(format) != 0;
        var interleaved = new float[samples * channels];
        for (int c = 0; c < channels; c++)
        {
            byte* plane = planar ? frame->extended_data[c] : frame->extended_data[0];
            for (int s = 0; s < samples; s++)
            {
                int index = planar ? s : s * channels + c;
                interleaved[s * channels + c] = format switch
                {
                    AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP => ((float*)plane)[index],
                    AVSampleFormat.AV_SAMPLE_FMT_S32 or AVSampleFormat.AV_SAMPLE_FMT_S32P => ((int*)plane)[index] / 2147483648f,
                    AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P => ((short*)plane)[index] / 32768f,
                    AVSampleFormat.AV_SAMPLE_FMT_DBL or AVSampleFormat.AV_SAMPLE_FMT_DBLP => (float)((double*)plane)[index],
                    _ => throw new NotSupportedException($"Sample format {format}"),
                };
            }
        }

        _output.Write(MemoryMarshal.AsBytes(interleaved.AsSpan()));
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            byte* buffer = stackalloc byte[256];
            ffmpeg.av_strerror(result, buffer, 256);
            throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi((IntPtr)buffer)}");
        }
    }

    public void Dispose()
    {
        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        AVCodecContext* context = _context;
        ffmpeg.avcodec_free_context(&context);
    }
}
