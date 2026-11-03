using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Marquee.Playback.Subtitles;

// libass 0.17.5 (tools/build-libass.ps1). Struct layouts and enum values follow its ass.h and
// ass_types.h.
internal static unsafe class LibAss
{
    private const string Dll = "libass.dll";

    public struct Library { }

    public struct Renderer { }

    public const int MessageFatal = 0;
    public const int MessageError = 1;
    public const int MessageWarning = 2;
    public const int MessageInfo = 4;
    public const int MessageVerbose = 6;
    public const int MessageDebug = 7;

    public enum Hinting
    {
        None = 0,
        Light = 1,
        Normal = 2,
        Native = 3,
    }

    public enum ShapingLevel
    {
        Simple = 0,
        Complex = 1,
    }

    [Flags]
    public enum OverrideBits
    {
        Default = 0,
        Style = 1 << 0,
        SelectiveFontScale = 1 << 1,
        FontSizeFields = 1 << 2,
        FontName = 1 << 3,
        Colors = 1 << 4,
        Attributes = 1 << 5,
        Border = 1 << 6,
        Alignment = 1 << 7,
        Margins = 1 << 8,
        FullStyle = 1 << 9,
        Justify = 1 << 10,
        Blur = 1 << 11,
    }

    public enum DefaultFontProvider
    {
        None = 0,
        Autodetect = 1,
        CoreText = 2,
        Fontconfig = 3,
        DirectWrite = 4,
    }

    public enum Feature
    {
        IncompatibleExtensions = 0,
        BidiBrackets = 1,
        WholeTextLayout = 2,
        WrapUnicode = 3,
    }

    public enum ImageType
    {
        Character = 0,
        Outline = 1,
        Shadow = 2,
    }

    public enum TrackType
    {
        Unknown = 0,
        Ass = 1,
        Ssa = 2,
    }

    public enum YCbCrMatrix
    {
        Default = 0,
        Unknown = 1,
        None = 2,
        Bt601Tv = 3,
        Bt601Pc = 4,
        Bt709Tv = 5,
        Bt709Pc = 6,
        Smpte240MTv = 7,
        Smpte240MPc = 8,
        FccTv = 9,
        FccPc = 10,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Image
    {
        public int W;
        public int H;
        public int Stride;
        // One byte of coverage per pixel. Only stride * (h - 1) + w bytes are guaranteed to exist.
        public byte* Bitmap;
        // 0xRRGGBBAA, where AA is transparency: 0 is opaque, 255 invisible.
        public uint Color;
        public int DstX;
        public int DstY;
        public Image* Next;
        public ImageType Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Style
    {
        public byte* Name;
        public byte* FontName;
        public double FontSize;
        public uint PrimaryColour;
        public uint SecondaryColour;
        public uint OutlineColour;
        public uint BackColour;
        public int Bold;
        public int Italic;
        public int Underline;
        public int StrikeOut;
        public double ScaleX;
        public double ScaleY;
        public double Spacing;
        public double Angle;
        public int BorderStyle;
        public double Outline;
        public double Shadow;
        public int Alignment;
        public int MarginL;
        public int MarginR;
        public int MarginV;
        public int Encoding;
        public int TreatFontNameAsPattern;
        public double Blur;
        public int Justify;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public long Start;
        public long Duration;
        public int ReadOrder;
        public int Layer;
        public int Style;
        public byte* Name;
        public int MarginL;
        public int MarginR;
        public int MarginV;
        public byte* Effect;
        public byte* Text;
        public void* RenderPriv;
    }

    // Allocated by libass; read it, don't write it.
    [StructLayout(LayoutKind.Sequential)]
    public struct Track
    {
        public int StyleCount;
        public int MaxStyles;
        public int EventCount;
        public int MaxEvents;
        public Style* Styles;
        public Event* Events;
        public byte* StyleFormat;
        public byte* EventFormat;
        public TrackType TrackType;
        public int PlayResX;
        public int PlayResY;
        public double Timer;
        public int WrapStyle;
        public int ScaledBorderAndShadow;
        public int Kerning;
        public byte* Language;
        public YCbCrMatrix YCbCrMatrix;
        public int DefaultStyle;
        public byte* Name;
        public Library* Library;
        public void* ParserPriv;
        public int LayoutResX;
        public int LayoutResY;
    }

    [DllImport(Dll)]
    public static extern int ass_library_version();

    [DllImport(Dll)]
    public static extern Library* ass_library_init();

    [DllImport(Dll, EntryPoint = "ass_library_done")]
    private static extern void LibraryDone(Library* library);

    [DllImport(Dll)]
    private static extern void ass_set_message_cb(Library* library, delegate* unmanaged[Cdecl]<int, byte*, byte*, void*, void> callback, void* data);

    [DllImport(Dll)]
    public static extern void ass_set_extract_fonts(Library* library, int extract);

    [DllImport(Dll)]
    public static extern void ass_add_font(Library* library, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte* data, int dataSize);

    [DllImport(Dll)]
    public static extern void ass_clear_fonts(Library* library);

    [DllImport(Dll)]
    public static extern void ass_get_available_font_providers(Library* library, DefaultFontProvider** providers, nuint* count);

    [DllImport(Dll)]
    public static extern Renderer* ass_renderer_init(Library* library);

    [DllImport(Dll)]
    public static extern void ass_renderer_done(Renderer* renderer);

    [DllImport(Dll)]
    public static extern void ass_set_frame_size(Renderer* renderer, int width, int height);

    [DllImport(Dll)]
    public static extern void ass_set_storage_size(Renderer* renderer, int width, int height);

    [DllImport(Dll)]
    public static extern void ass_set_shaper(Renderer* renderer, ShapingLevel level);

    [DllImport(Dll)]
    public static extern void ass_set_margins(Renderer* renderer, int top, int bottom, int left, int right);

    [DllImport(Dll)]
    public static extern void ass_set_use_margins(Renderer* renderer, int use);

    [DllImport(Dll)]
    public static extern void ass_set_pixel_aspect(Renderer* renderer, double pixelAspect);

    [DllImport(Dll)]
    public static extern void ass_set_font_scale(Renderer* renderer, double fontScale);

    [DllImport(Dll)]
    public static extern void ass_set_hinting(Renderer* renderer, Hinting hinting);

    [DllImport(Dll)]
    public static extern void ass_set_line_spacing(Renderer* renderer, double lineSpacing);

    [DllImport(Dll)]
    public static extern void ass_set_line_position(Renderer* renderer, double linePosition);

    [DllImport(Dll)]
    public static extern void ass_set_fonts(
        Renderer* renderer,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? defaultFont,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? defaultFamily,
        DefaultFontProvider provider,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? fontconfigConfig,
        int update);

    [DllImport(Dll)]
    public static extern void ass_set_selective_style_override_enabled(Renderer* renderer, OverrideBits bits);

    [DllImport(Dll)]
    public static extern void ass_set_selective_style_override(Renderer* renderer, Style* style);

    [DllImport(Dll)]
    public static extern void ass_set_cache_limits(Renderer* renderer, int glyphMax, int bitmapMaxMegabytes);

    [DllImport(Dll)]
    public static extern Image* ass_render_frame(Renderer* renderer, Track* track, long nowMs, int* detectChange);

    [DllImport(Dll)]
    public static extern Track* ass_new_track(Library* library);

    [DllImport(Dll)]
    public static extern void ass_free_track(Track* track);

    [DllImport(Dll)]
    public static extern int ass_track_set_feature(Track* track, Feature feature, int enable);

    [DllImport(Dll)]
    public static extern void ass_process_data(Track* track, byte* data, int size);

    [DllImport(Dll)]
    public static extern void ass_process_codec_private(Track* track, byte* data, int size);

    [DllImport(Dll)]
    public static extern void ass_process_chunk(Track* track, byte* data, int size, long timecodeMs, long durationMs);

    [DllImport(Dll)]
    public static extern void ass_set_check_readorder(Track* track, int checkReadOrder);

    [DllImport(Dll)]
    public static extern void ass_flush_events(Track* track);

    [DllImport(Dll)]
    public static extern void ass_prune_events(Track* track, long deadlineMs);

    [DllImport(Dll)]
    public static extern void ass_configure_prune(Track* track, long delayMs);

    [DllImport(Dll)]
    public static extern Track* ass_read_memory(Library* library, byte* buffer, nuint size, byte* codepage);

    [DllImport(Dll)]
    public static extern void ass_free(void* pointer);

    // libass formats its messages printf-style. It is built against the UCRT, whose vsnprintf is an
    // inline wrapper over this export.
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int __stdio_common_vsprintf(ulong options, byte* buffer, nuint count, byte* format, void* locale, byte* args);

    private const ulong StandardSnprintfBehavior = 1 << 1;

    private sealed record MessageSink(Action<int, string> Callback, int MaxLevel);

    private static readonly ConcurrentDictionary<nint, MessageSink> MessageSinks = new();

    // ass_set_selective_style_override copies the style but keeps the Name pointer, so it must
    // never be freed.
    private static readonly byte* OverrideStyleName = (byte*)Marshal.StringToCoTaskMemUTF8("MarqueeOverride");

    public static string VersionString(int version) =>
        $"{(version >> 28) & 0xF:x}.{(version >> 20) & 0xFF:x}.{(version >> 12) & 0xFF:x}";

    public static void ass_library_done(Library* library)
    {
        MessageSinks.TryRemove((nint)library, out _);
        LibraryDone(library);
    }

    // Levels run from MessageFatal (0) to MessageDebug (7). The callback runs synchronously on
    // whichever thread is calling into libass.
    public static void SetMessageCallback(Library* library, Action<int, string> callback, int maxLevel = MessageInfo)
    {
        MessageSinks[(nint)library] = new MessageSink(callback, maxLevel);
        ass_set_message_cb(library, &OnMessage, library);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMessage(int level, byte* format, byte* args, void* data)
    {
        try
        {
            if (MessageSinks.TryGetValue((nint)data, out var sink) && level <= sink.MaxLevel)
            {
                sink.Callback(level, FormatMessage(format, args));
            }
        }
        catch
        {
            // An exception must not unwind into libass.
        }
    }

    // On x64 Windows a va_list is a plain pointer into the caller's argument area, so formatting
    // twice from the same va_list is fine.
    private static string FormatMessage(byte* format, byte* args)
    {
        const int StackLength = 512;
        byte* stack = stackalloc byte[StackLength];
        int length = __stdio_common_vsprintf(StandardSnprintfBehavior, stack, StackLength, format, null, args);
        if (length < 0)
        {
            return Marshal.PtrToStringUTF8((nint)format) ?? "";
        }

        if (length < StackLength)
        {
            return Encoding.UTF8.GetString(stack, length);
        }

        var heap = new byte[length + 1];
        fixed (byte* buffer = heap)
        {
            __stdio_common_vsprintf(StandardSnprintfBehavior, buffer, (nuint)heap.Length, format, null, args);
            return Encoding.UTF8.GetString(buffer, length);
        }
    }

    public static DefaultFontProvider[] AvailableFontProviders(Library* library)
    {
        DefaultFontProvider* providers = null;
        nuint count = 0;
        ass_get_available_font_providers(library, &providers, &count);
        try
        {
            return count == nuint.MaxValue || providers == null
                ? []
                : new ReadOnlySpan<DefaultFontProvider>(providers, (int)count).ToArray();
        }
        finally
        {
            ass_free(providers);
        }
    }

    public static void AddFont(Library* library, string name, ReadOnlySpan<byte> data)
    {
        fixed (byte* bytes = data)
        {
            ass_add_font(library, name, bytes, data.Length);
        }
    }

    // libass copies the font name; strings in the passed style are ignored.
    public static void SetSelectiveStyleOverride(Renderer* renderer, Style style, string fontName)
    {
        fixed (byte* font = NullTerminated(fontName))
        {
            style.Name = OverrideStyleName;
            style.FontName = font;
            ass_set_selective_style_override(renderer, &style);
        }
    }

    // Without iconv in this build the input must be UTF-8 (a BOM is fine). libass copies the buffer.
    public static Track* ReadMemory(Library* library, ReadOnlySpan<byte> utf8)
    {
        fixed (byte* bytes = utf8)
        {
            return ass_read_memory(library, bytes, (nuint)utf8.Length, null);
        }
    }

    public static Track* ReadMemory(Library* library, string script) =>
        ReadMemory(library, Encoding.UTF8.GetBytes(script));

    public static void ProcessData(Track* track, ReadOnlySpan<byte> utf8)
    {
        fixed (byte* bytes = utf8)
        {
            ass_process_data(track, bytes, utf8.Length);
        }
    }

    public static void ProcessData(Track* track, string text) =>
        ProcessData(track, Encoding.UTF8.GetBytes(text));

    public static void ProcessCodecPrivate(Track* track, ReadOnlySpan<byte> codecPrivate)
    {
        fixed (byte* bytes = codecPrivate)
        {
            ass_process_codec_private(track, bytes, codecPrivate.Length);
        }
    }

    public static void ProcessCodecPrivate(Track* track, string header) =>
        ProcessCodecPrivate(track, Encoding.UTF8.GetBytes(header));

    // A Matroska block: "ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text".
    public static void ProcessChunk(Track* track, ReadOnlySpan<byte> block, long timecodeMs, long durationMs)
    {
        fixed (byte* bytes = block)
        {
            ass_process_chunk(track, bytes, block.Length, timecodeMs, durationMs);
        }
    }

    public static void ProcessChunk(Track* track, string block, long timecodeMs, long durationMs) =>
        ProcessChunk(track, Encoding.UTF8.GetBytes(block), timecodeMs, durationMs);

    private static byte[] NullTerminated(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }
}
