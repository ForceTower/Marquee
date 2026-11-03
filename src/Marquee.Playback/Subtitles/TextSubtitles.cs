using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Marquee.Playback.Subtitles;

/// <summary>Colours for plain-text subtitles, as 0xRRGGBB.</summary>
internal readonly record struct TextStyle(uint Colour, uint OutlineColour)
{
    public static TextStyle Default { get; } = new(0xFFFFFF, 0x000000);

    /// <summary>From "#RRGGBB" strings (settings); anything unreadable keeps the default.</summary>
    public static TextStyle Parse(string? colour, string? outline) =>
        new(Rgb(colour) ?? Default.Colour, Rgb(outline) ?? Default.OutlineColour);

    private static uint? Rgb(string? hex) =>
        hex?.TrimStart('#') is { Length: 6 } digits && uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb) ? rgb : null;
}

/// <summary>
/// Plain-text subtitles (SubRip, WebVTT) as ASS, the way mpv shows them: converted to ASS events
/// with a default style and rendered by libass, so every text format looks and wraps the same.
/// </summary>
internal static partial class TextSubtitles
{
    /// <summary>The style's bottom margin in <see cref="PlayResY"/> units.</summary>
    public const int MarginV = 64;

    public const int PlayResY = 1080;

    /// <summary>The script header converted text events play under. PlayRes is 1080p; libass scales it.</summary>
    public static string Header(TextStyle style) => $"""
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 1920
        PlayResY: {PlayResY}
        ScaledBorderAndShadow: yes
        WrapStyle: 0
        YCbCr Matrix: None

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Segoe UI,58,{AssColour(style.Colour)},&H000000FF,{AssColour(style.OutlineColour)},&H99000000,-1,0,0,0,100,100,0,0,1,3,1.5,2,140,140,{MarginV},1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text

        """;

    /// <summary>0xRRGGBB as ASS's opaque &amp;H00BBGGRR.</summary>
    private static string AssColour(uint rgb) => $"&H00{rgb & 0xFF:X2}{(rgb >> 8) & 0xFF:X2}{(rgb >> 16) & 0xFF:X2}";

    private static readonly Dictionary<string, string> NamedColours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "FFFFFF", ["black"] = "000000", ["red"] = "FF0000", ["green"] = "008000", ["lime"] = "00FF00",
        ["blue"] = "0000FF", ["yellow"] = "FFFF00", ["cyan"] = "00FFFF", ["aqua"] = "00FFFF", ["magenta"] = "FF00FF",
        ["fuchsia"] = "FF00FF", ["gray"] = "808080", ["grey"] = "808080", ["silver"] = "C0C0C0", ["orange"] = "FFA500",
        ["purple"] = "800080", ["pink"] = "FFC0CB", ["brown"] = "A52A2A", ["navy"] = "000080", ["teal"] = "008080",
        ["maroon"] = "800000", ["olive"] = "808000", ["gold"] = "FFD700",
    };

    /// <summary>A Matroska-style ASS event line (what <c>ass_process_chunk</c> takes) for converted text.</summary>
    public static byte[] Chunk(long readOrder, string assText) =>
        Encoding.UTF8.GetBytes($"{readOrder},0,Default,,0,0,0,,{assText}");

    /// <summary>
    /// Converts SubRip's HTML-like markup (also good enough for WebVTT's) to ASS text: i/b/u/s and
    /// font colour/face/size become override tags, ASS override blocks like <c>{\an8}</c> pass
    /// through, other tags are dropped, and line breaks become <c>\N</c>.
    /// </summary>
    public static string ToAss(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
        var sb = new StringBuilder(text.Length + 16);
        var fonts = new Stack<List<string>>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\n':
                    sb.Append("\\N");
                    break;
                case '{' when i + 1 < text.Length && text[i + 1] == '\\' && text.IndexOf('}', i) is int close and > 0:
                    sb.Append(text, i, close - i + 1);
                    i = close;
                    break;
                case '{':
                    sb.Append("\\{");
                    break;
                case '}':
                    sb.Append("\\}");
                    break;
                case '<' when i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '/') && text.IndexOf('>', i) is int end and > 0:
                    AppendTag(sb, text[(i + 1)..end], fonts);
                    i = end;
                    break;
                case '&' when text.IndexOf(';', i) is int semi and > 0 && semi - i <= 8 && Entity(text[(i + 1)..semi]) is { } decoded:
                    sb.Append(decoded);
                    i = semi;
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendTag(StringBuilder sb, string tag, Stack<List<string>> fonts)
    {
        tag = tag.Trim();
        bool closing = tag.StartsWith('/');
        string body = closing ? tag[1..].Trim() : tag;
        string name = body.Split([' ', '\t', '.'], 2)[0].ToLowerInvariant();
        switch (name)
        {
            case "i" or "b" or "u" or "s":
                sb.Append($"{{\\{name}{(closing ? 0 : 1)}}}");
                break;
            case "font" when closing:
                if (fonts.Count > 0)
                {
                    sb.Append('{').Append(string.Concat(fonts.Pop().Select(reset => $"\\{reset}"))).Append('}');
                }

                break;
            case "font":
                var resets = new List<string>();
                var tags = new StringBuilder();
                foreach (Match attribute in FontAttribute().Matches(body))
                {
                    string value = attribute.Groups["value"].Value;
                    switch (attribute.Groups["name"].Value.ToLowerInvariant())
                    {
                        case "color" when Colour(value) is { } bgr:
                            tags.Append($"\\c&H{bgr}&");
                            resets.Add("c");
                            break;
                        case "face" when value.Length > 0:
                            tags.Append($"\\fn{value}");
                            resets.Add("fn");
                            break;
                    }
                }

                fonts.Push(resets);
                if (tags.Length > 0)
                {
                    sb.Append('{').Append(tags).Append('}');
                }

                break;
        }
    }

    /// <summary>An HTML colour ("#RRGGBB", "#RGB", "RRGGBB" or a name) as ASS's BBGGRR.</summary>
    private static string? Colour(string value)
    {
        string hex = value.Trim().TrimStart('#');
        if (NamedColours.TryGetValue(hex, out string? named))
        {
            hex = named;
        }

        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        }

        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return (hex[4..6] + hex[2..4] + hex[0..2]).ToUpperInvariant();
    }

    private static string? Entity(string name) => name switch
    {
        "amp" => "&",
        "lt" => "<",
        "gt" => ">",
        "quot" => "\"",
        "apos" => "'",
        "nbsp" => "\\h",
        _ when name.StartsWith("#x", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) => char.ConvertFromUtf32(hex),
        _ when name.StartsWith('#') && int.TryParse(name[1..], CultureInfo.InvariantCulture, out int dec) => char.ConvertFromUtf32(dec),
        _ => null,
    };

    /// <summary>
    /// Decodes a subtitle file's bytes: a BOM wins, then strict UTF-8, else Windows-1252, which is
    /// what Portuguese and other Western SRT files that aren't UTF-8 almost always are.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>Parses a SubRip or WebVTT file into packets of raw cue text, like a Matroska track's.</summary>
    public static List<SubtitlePacket> ParseCues(string text)
    {
        var packets = new List<SubtitlePacket>();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match timing = CueTiming().Match(lines[i]);
            if (!timing.Success)
            {
                continue;
            }

            long start = Time(timing.Groups["start"].Value);
            long end = Time(timing.Groups["end"].Value);
            var cue = new StringBuilder();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                if (cue.Length > 0)
                {
                    cue.Append('\n');
                }

                cue.Append(lines[i]);
            }

            if (end > start && cue.Length > 0)
            {
                packets.Add(new SubtitlePacket(start, end - start, Encoding.UTF8.GetBytes(cue.ToString())));
            }
        }

        packets.Sort((a, b) => a.TimeTicks.CompareTo(b.TimeTicks));
        return packets;
    }

    /// <summary>"01:02:03,456", "01:02:03.456" or WebVTT's "02:03.456", in ticks.</summary>
    private static long Time(string value)
    {
        string[] parts = value.Replace(',', '.').Split(':');
        double seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        double minutes = double.Parse(parts[^2], CultureInfo.InvariantCulture);
        double hours = parts.Length > 2 ? double.Parse(parts[^3], CultureInfo.InvariantCulture) : 0;
        return (long)Math.Round(((hours * 60 + minutes) * 60 + seconds) * TimeSpan.TicksPerSecond);
    }

    [GeneratedRegex(@"^\s*(?<start>(\d+:)?\d{1,2}:\d{1,2}[,.]\d{1,3})\s*-->\s*(?<end>(\d+:)?\d{1,2}:\d{1,2}[,.]\d{1,3})")]
    private static partial Regex CueTiming();

    [GeneratedRegex("""(?<name>\w+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s>]+))""")]
    private static partial Regex FontAttribute();
}
