using FFmpeg.AutoGen;
using Marquee.Playback.Matroska;

namespace Marquee.Playback.Subtitles;

/// <summary>
/// Bitmap subtitles (PGS from Blu-ray, VobSub from DVD, DVB) decoded by FFmpeg's decoders, the ones
/// mpv uses. Each packet is a display set that replaces what's on screen until the next one. A PGS
/// set inside an epoch can reuse objects and palettes from earlier sets, so after a jump decoding
/// restarts from the last set that stands alone (an epoch start or acquisition point).
/// </summary>
internal sealed unsafe class BitmapSubtitleRenderer : ISubtitleRenderer
{
    private const int Padding = 64;

    private readonly SubtitleCodec _codec;
    private readonly AVCodecID _codecId;
    private readonly byte[]? _extradata;
    private readonly int _storageWidth;
    private readonly int _storageHeight;
    private readonly List<SubtitlePacket> _packets = [];
    private AVCodecContext* _context;
    private int _decoded = -1;
    private int _shown = -2;
    private DisplaySet? _set;
    private SubtitleLayout _layout;
    private bool _visible;

    private sealed record DisplaySet(long Start, long End, List<Bitmap> Bitmaps, int CanvasWidth, int CanvasHeight);

    private sealed record Bitmap(byte[] Pixels, int X, int Y, int Width, int Height);

    public int Errors { get; private set; }

    /// <param name="extradata">The track's CodecPrivate: a VobSub .idx (palette, size) or DVB page ids.</param>
    /// <param name="storageWidth">The video's coded size; a canvas of that size maps onto the video exactly.</param>
    public BitmapSubtitleRenderer(SubtitleCodec codec, byte[]? extradata, int storageWidth, int storageHeight)
    {
        if (!FfmpegAudioDecoder.TryLoad(out string? error))
        {
            throw new NotSupportedException($"FFmpeg libraries unavailable: {error}");
        }

        _codec = codec;
        _codecId = codec switch
        {
            SubtitleCodec.Pgs => AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE,
            SubtitleCodec.VobSub => AVCodecID.AV_CODEC_ID_DVD_SUBTITLE,
            SubtitleCodec.DvbSub => AVCodecID.AV_CODEC_ID_DVB_SUBTITLE,
            _ => throw new ArgumentException($"{codec} isn't a bitmap subtitle codec", nameof(codec)),
        };
        _extradata = extradata;
        _storageWidth = storageWidth;
        _storageHeight = storageHeight;
        Open();
    }

    public void Add(SubtitlePacket packet)
    {
        int index = _packets.Count;
        while (index > 0 && _packets[index - 1].TimeTicks > packet.TimeTicks)
        {
            index--;
        }

        _packets.Insert(index, packet);
        if (index <= _decoded)
        {
            _decoded = -1;
        }

        if (index <= _shown + 1)
        {
            _shown = -2;
        }
    }

    public SubtitleFrame? Render(long ticks, SubtitleLayout layout, bool force)
    {
        int index = LastAtOrBefore(ticks);
        if (index != _shown)
        {
            _set = index < 0 ? null : Decode(index);
            _shown = index;
            force = true;
        }

        bool visible = _set is { Bitmaps.Count: > 0 } set && ticks >= set.Start && ticks < set.End;
        if (!force && visible == _visible && layout == _layout)
        {
            return null;
        }

        _visible = visible;
        _layout = layout;
        return visible ? Frame(_set!, layout) : SubtitleFrame.Empty;
    }

    private int LastAtOrBefore(long ticks)
    {
        int lo = 0, hi = _packets.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_packets[mid].TimeTicks <= ticks)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    private DisplaySet? Decode(int index)
    {
        int key = index;
        while (_codec == SubtitleCodec.Pgs && key > 0 && !StandsAlone(_packets[key].Data))
        {
            key--;
        }

        int from = _decoded >= key && _decoded < index ? _decoded + 1 : key;
        if (from == key)
        {
            Close();
            Open();
        }

        DisplaySet? result = null;
        for (int i = from; i <= index; i++)
        {
            result = DecodeOne(i, keep: i == index);
        }

        _decoded = index;
        return result;
    }

    /// <summary>Whether a PGS display set's composition is an epoch start or acquisition point.</summary>
    private static bool StandsAlone(byte[] data)
    {
        for (int pos = 0; pos + 3 <= data.Length;)
        {
            int type = data[pos];
            int size = (data[pos + 1] << 8) | data[pos + 2];
            if (type == 0x16 && pos + 3 + 8 <= data.Length)
            {
                return data[pos + 3 + 7] != 0;
            }

            pos += 3 + size;
        }

        return false;
    }

    private DisplaySet? DecodeOne(int index, bool keep)
    {
        var packet = _packets[index];
        byte[] padded = new byte[packet.Data.Length + Padding];
        packet.Data.CopyTo(padded, 0);

        AVSubtitle subtitle;
        int got = 0;
        AVPacket* av = ffmpeg.av_packet_alloc();
        try
        {
            fixed (byte* data = padded)
            {
                av->data = data;
                av->size = packet.Data.Length;
                av->pts = packet.TimeTicks / TimeSpan.TicksPerMillisecond;
                if (ffmpeg.avcodec_decode_subtitle2(_context, &subtitle, &got, av) < 0)
                {
                    Errors++;
                    return null;
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&av);
        }

        if (got == 0)
        {
            return null;
        }

        try
        {
            if (!keep)
            {
                return null;
            }

            long start = packet.TimeTicks + subtitle.start_display_time * TimeSpan.TicksPerMillisecond;
            long end = index + 1 < _packets.Count ? _packets[index + 1].TimeTicks : long.MaxValue;
            if (subtitle.end_display_time > subtitle.start_display_time && subtitle.end_display_time != uint.MaxValue)
            {
                end = Math.Min(end, packet.TimeTicks + subtitle.end_display_time * TimeSpan.TicksPerMillisecond);
            }

            if (packet.DurationTicks > 0)
            {
                end = Math.Min(end, packet.TimeTicks + packet.DurationTicks);
            }

            var bitmaps = new List<Bitmap>();
            for (int r = 0; r < subtitle.num_rects; r++)
            {
                AVSubtitleRect* rect = subtitle.rects[r];
                if (rect->type == AVSubtitleType.SUBTITLE_BITMAP && rect->w > 0 && rect->h > 0 && rect->data[0] != null && rect->data[1] != null)
                {
                    bitmaps.Add(new Bitmap(ToBgra(rect), rect->x, rect->y, rect->w, rect->h));
                }
            }

            int canvasWidth = _context->width > 0 ? _context->width : _storageWidth;
            int canvasHeight = _context->height > 0 ? _context->height : _storageHeight;
            return new DisplaySet(start, end, bitmaps, canvasWidth, canvasHeight);
        }
        finally
        {
            ffmpeg.avsubtitle_free(&subtitle);
        }
    }

    /// <summary>Expands a paletted rect (palette entries are 0xAARRGGBB) to premultiplied BGRA.</summary>
    private static byte[] ToBgra(AVSubtitleRect* rect)
    {
        uint* palette = (uint*)rect->data[1];
        Span<uint> colours = stackalloc uint[256];
        for (int i = 0; i < Math.Min(rect->nb_colors, 256); i++)
        {
            uint c = palette[i];
            uint a = c >> 24;
            uint r = ((c >> 16) & 0xFF) * a / 255;
            uint g = ((c >> 8) & 0xFF) * a / 255;
            uint b = (c & 0xFF) * a / 255;
            colours[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }

        int w = rect->w, h = rect->h, stride = rect->linesize[0];
        byte* indices = rect->data[0];
        var pixels = new byte[w * h * 4];
        fixed (byte* output = pixels)
        {
            uint* o = (uint*)output;
            for (int y = 0; y < h; y++)
            {
                byte* row = indices + y * stride;
                for (int x = 0; x < w; x++)
                {
                    o[y * w + x] = colours[row[x]];
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// Places the set's canvas over the video. A canvas the video's size (or shape) maps onto it
    /// exactly, anamorphic DVDs included. Anything else (a 1080p Blu-ray canvas over a cropped
    /// encode) is scaled to the video's width and centred on it, as if the cropped bars were still
    /// there, so bottom subtitles land below a scope picture rather than squashed onto it.
    /// </summary>
    private SubtitleFrame Frame(DisplaySet set, SubtitleLayout layout)
    {
        double x0 = layout.VideoX, y0 = layout.VideoY, sx, sy;
        bool sameShape = _storageWidth > 0 && _storageHeight > 0 &&
                         Math.Abs(set.CanvasWidth / (double)set.CanvasHeight - _storageWidth / (double)_storageHeight) < 0.01;
        if (sameShape || set.CanvasWidth <= 0 || set.CanvasHeight <= 0)
        {
            sx = layout.VideoWidth / (double)Math.Max(1, set.CanvasWidth);
            sy = layout.VideoHeight / (double)Math.Max(1, set.CanvasHeight);
        }
        else
        {
            double scale = layout.VideoWidth / (double)set.CanvasWidth;
            if (set.CanvasHeight * scale > layout.Height)
            {
                scale = layout.Height / (double)set.CanvasHeight;
            }

            sx = sy = scale;
            x0 = layout.VideoX + (layout.VideoWidth - set.CanvasWidth * scale) / 2;
            y0 = Math.Clamp(layout.VideoY + (layout.VideoHeight - set.CanvasHeight * scale) / 2, 0, Math.Max(0, layout.Height - set.CanvasHeight * scale));
        }

        return new SubtitleFrame(set.Bitmaps.Select(b => new SubtitleImage
        {
            Pixels = b.Pixels,
            Width = b.Width,
            Height = b.Height,
            X = x0 + b.X * sx,
            Y = y0 + b.Y * sy,
            DisplayWidth = b.Width * sx,
            DisplayHeight = b.Height * sy,
        }).ToList());
    }

    private void Open()
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(_codecId);
        if (codec == null)
        {
            throw new NotSupportedException($"FFmpeg has no decoder for {_codecId}");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->pkt_timebase = new AVRational { num = 1, den = 1000 };
        if (_storageWidth > 0 && _codec == SubtitleCodec.VobSub)
        {
            _context->width = _storageWidth;
            _context->height = _storageHeight;
        }

        if (_extradata is { Length: > 0 } extradata)
        {
            _context->extradata = (byte*)ffmpeg.av_mallocz((ulong)(extradata.Length + Padding));
            extradata.CopyTo(new Span<byte>(_context->extradata, extradata.Length));
            _context->extradata_size = extradata.Length;
        }

        int result = ffmpeg.avcodec_open2(_context, codec, null);
        if (result < 0)
        {
            Close();
            throw new InvalidOperationException($"Couldn't open FFmpeg's {_codecId} decoder ({result})");
        }
    }

    private void Close()
    {
        AVCodecContext* context = _context;
        ffmpeg.avcodec_free_context(&context);
        _context = null;
    }

    public void Dispose() => Close();
}
