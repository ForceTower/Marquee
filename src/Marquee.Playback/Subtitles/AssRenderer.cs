using System.Buffers;
using System.Text;
using Marquee.Playback.Matroska;

namespace Marquee.Playback.Subtitles;

/// <summary>
/// ASS/SSA rendered by libass, as mpv does: typesetting, karaoke, transforms and the track's
/// embedded fonts. SubRip and WebVTT are converted to ASS events under <see cref="TextSubtitles.Header"/>
/// and, like mpv's defaults for plain text, may sit in the black bars and scale with the window
/// rather than the picture. Renders at the overlay's own resolution, so text stays sharp.
/// </summary>
internal sealed unsafe class AssRenderer : ISubtitleRenderer
{
    private readonly LibAss.Library* _library;
    private readonly LibAss.Renderer* _renderer;
    private readonly LibAss.Track* _track;
    private readonly SubtitleCodec _codec;
    private readonly int _storageWidth;
    private readonly int _storageHeight;
    private long _readOrder;
    private SubtitleLayout _layout;
    private bool _empty = true;

    /// <param name="header">
    /// For ASS: the track's CodecPrivate (script header), or a whole .ass/.ssa file as UTF-8. Ignored
    /// for converted text formats.
    /// </param>
    /// <param name="fonts">Fonts attached to the file, which ASS styles often name.</param>
    /// <param name="storageWidth">The video's coded size, which libass scales blur and borders by.</param>
    public AssRenderer(SubtitleCodec codec, byte[]? header, IEnumerable<MatroskaAttachment> fonts, int storageWidth, int storageHeight, Action<string>? log = null)
    {
        _codec = codec;
        _storageWidth = storageWidth;
        _storageHeight = storageHeight;
        _library = LibAss.ass_library_init();
        if (_library == null)
        {
            throw new InvalidOperationException("libass couldn't initialise");
        }

        if (log is not null)
        {
            LibAss.SetMessageCallback(_library, (_, message) => log(message), LibAss.MessageWarning);
        }

        LibAss.ass_set_extract_fonts(_library, 1);
        foreach (var font in fonts)
        {
            LibAss.AddFont(_library, font.Name, font.Data);
        }

        _renderer = LibAss.ass_renderer_init(_library);
        LibAss.ass_set_fonts(_renderer, null, "Segoe UI", LibAss.DefaultFontProvider.Autodetect, null, 1);
        LibAss.ass_set_shaper(_renderer, LibAss.ShapingLevel.Complex);
        LibAss.ass_set_hinting(_renderer, LibAss.Hinting.None);

        _track = LibAss.ass_new_track(_library);
        if (codec == SubtitleCodec.Ass && header is { Length: > 0 })
        {
            LibAss.ProcessCodecPrivate(_track, header);
        }
        else
        {
            LibAss.ProcessCodecPrivate(_track, TextSubtitles.Header);
        }
    }

    public static bool Available
    {
        get
        {
            try
            {
                return LibAss.ass_library_version() > 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }
    }

    private bool Converted => _codec != SubtitleCodec.Ass;

    public void Add(SubtitlePacket packet)
    {
        long start = packet.TimeTicks / TimeSpan.TicksPerMillisecond;
        long duration = Math.Max(1, packet.DurationTicks / TimeSpan.TicksPerMillisecond);
        if (Converted)
        {
            string text = TextSubtitles.ToAss(Encoding.UTF8.GetString(packet.Data));
            LibAss.ProcessChunk(_track, TextSubtitles.Chunk(_readOrder++, text), start, duration);
        }
        else
        {
            LibAss.ProcessChunk(_track, packet.Data, start, duration);
        }
    }

    public SubtitleFrame? Render(long ticks, SubtitleLayout layout, bool force)
    {
        if (layout != _layout)
        {
            Configure(layout);
            force = true;
        }

        int change;
        LibAss.Image* images = LibAss.ass_render_frame(_renderer, _track, ticks / TimeSpan.TicksPerMillisecond, &change);
        if (!force && change == 0)
        {
            return null;
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (var image = images; image != null; image = image->Next)
        {
            if (image->W > 0 && image->H > 0)
            {
                left = Math.Min(left, image->DstX);
                top = Math.Min(top, image->DstY);
                right = Math.Max(right, image->DstX + image->W);
                bottom = Math.Max(bottom, image->DstY + image->H);
            }
        }

        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        right = Math.Min(right, layout.Width);
        bottom = Math.Min(bottom, layout.Height);
        if (right <= left || bottom <= top)
        {
            bool wasEmpty = _empty;
            _empty = true;
            return wasEmpty && !force ? null : SubtitleFrame.Empty;
        }

        _empty = false;
        int width = right - left, height = bottom - top;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(width * height * 4);
        Array.Clear(pixels, 0, width * height * 4);
        fixed (byte* canvas = pixels)
        {
            for (var image = images; image != null; image = image->Next)
            {
                Blend(image, canvas, left, top, width, height);
            }
        }

        return new SubtitleFrame([new SubtitleImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            X = left,
            Y = top,
            DisplayWidth = width,
            DisplayHeight = height,
            Pooled = true,
        }]);
    }

    /// <summary>
    /// The whole overlay is libass's frame and the video's rectangle in it its margins. Converted
    /// text may use the margins (the black bars) and scales with the overlay's height, not the
    /// picture's, as mpv does with <c>sub-use-margins</c> and <c>sub-scale-by-window</c>.
    /// </summary>
    private void Configure(SubtitleLayout layout)
    {
        _layout = layout;
        LibAss.ass_set_frame_size(_renderer, layout.Width, layout.Height);
        LibAss.ass_set_storage_size(_renderer, _storageWidth > 0 ? _storageWidth : layout.VideoWidth, _storageHeight > 0 ? _storageHeight : layout.VideoHeight);
        LibAss.ass_set_margins(_renderer, layout.VideoY, layout.Height - layout.VideoY - layout.VideoHeight, layout.VideoX, layout.Width - layout.VideoX - layout.VideoWidth);
        LibAss.ass_set_use_margins(_renderer, Converted ? 1 : 0);
        LibAss.ass_set_font_scale(_renderer, Converted ? layout.Height / (double)Math.Max(1, layout.VideoHeight) : 1);
    }

    /// <summary>Draws one libass coverage mask, in its colour, over the premultiplied BGRA canvas.</summary>
    private static void Blend(LibAss.Image* image, byte* canvas, int left, int top, int width, int height)
    {
        uint colour = image->Color;
        uint opacity = 255 - (colour & 0xFF);
        if (opacity == 0)
        {
            return;
        }

        uint r = colour >> 24, g = (colour >> 16) & 0xFF, b = (colour >> 8) & 0xFF;
        int x0 = Math.Max(image->DstX, left), y0 = Math.Max(image->DstY, top);
        int x1 = Math.Min(image->DstX + image->W, left + width), y1 = Math.Min(image->DstY + image->H, top + height);
        for (int y = y0; y < y1; y++)
        {
            byte* mask = image->Bitmap + (y - image->DstY) * image->Stride - image->DstX;
            byte* row = canvas + ((y - top) * width - left) * 4;
            for (int x = x0; x < x1; x++)
            {
                uint coverage = mask[x];
                if (coverage == 0)
                {
                    continue;
                }

                uint a = Div255(coverage * opacity);
                uint inverse = 255 - a;
                byte* p = row + x * 4;
                p[0] = (byte)(Div255(b * a) + Div255(p[0] * inverse));
                p[1] = (byte)(Div255(g * a) + Div255(p[1] * inverse));
                p[2] = (byte)(Div255(r * a) + Div255(p[2] * inverse));
                p[3] = (byte)(a + Div255(p[3] * inverse));
            }
        }
    }

    private static uint Div255(uint value) => (value + 128 + ((value + 128) >> 8)) >> 8;

    public void Dispose()
    {
        LibAss.ass_free_track(_track);
        LibAss.ass_renderer_done(_renderer);
        LibAss.ass_library_done(_library);
    }
}
