using System.Buffers.Binary;
using System.Text;

namespace Marquee.Playback.Demuxing;

/// <summary>
/// 3GPP timed text (MP4's tx3g, FFmpeg's mov_text) as the SubRip-style text Matroska stores for
/// S_TEXT/UTF8, which is how mkvmerge converts it: the sample's text, with its bold, italic and
/// underline style runs as tags. A sample without text (the gap between two lines) gives "".
/// </summary>
internal sealed class MovText
{
    private const int Bold = 1;
    private const int Italic = 2;
    private const int Underline = 4;
    private static readonly (int Flag, char Tag)[] Tags = [(Bold, 'b'), (Italic, 'i'), (Underline, 'u')];

    private readonly int _defaultFace;
    private readonly string _alignment = "";

    /// <param name="sampleEntry">
    /// The track's tx3g sample entry after its header (FFmpeg's extradata): display flags,
    /// justification, background, default text box and default style.
    /// </param>
    public MovText(byte[]? sampleEntry)
    {
        if (sampleEntry is { Length: >= 30 })
        {
            // Justification is 0 for left/top, 1 for centre and -1 for right/bottom: ASS's numpad
            // alignment, where bottom centre (2) is the default and needs no tag.
            int column = (sbyte)sampleEntry[4] switch { 0 => 1, -1 => 3, _ => 2 };
            int row = (sbyte)sampleEntry[5] switch { 0 => 6, 1 => 3, _ => 0 };
            _alignment = row + column == 2 ? "" : $"{{\\an{row + column}}}";
            _defaultFace = sampleEntry[24];
        }
    }

    public string ToText(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 2)
        {
            return "";
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(sample);
        if (length == 0 || 2 + length > sample.Length)
        {
            return "";
        }

        var bytes = sample.Slice(2, length);
        string text = bytes is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(bytes[2..]) : Encoding.UTF8.GetString(bytes);
        var runs = StyleRuns(sample[(2 + length)..]);

        var output = new StringBuilder(text.Length + 16).Append(_alignment);

        int index = 0;
        int current = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int face = FaceAt(runs, index++);
            Switch(output, current, face);
            current = face;
            switch (rune.Value)
            {
                case '<':
                    output.Append("&lt;");
                    break;
                case '&':
                    output.Append("&amp;");
                    break;
                default:
                    output.Append(rune.ToString());
                    break;
            }
        }

        Switch(output, current, 0);
        return output.ToString();
    }

    /// <summary>Style runs from the sample's 'styl' box: start and end in characters, and the face flags.</summary>
    private static List<(int Start, int End, int Face)> StyleRuns(ReadOnlySpan<byte> boxes)
    {
        var runs = new List<(int, int, int)>();
        while (boxes.Length >= 8)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(boxes);
            if (size < 8 || size > boxes.Length)
            {
                break;
            }

            if (boxes[4..8].SequenceEqual("styl"u8) && size >= 10)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(boxes[8..]);
                for (int i = 0, at = 10; i < count && at + 12 <= size; i++, at += 12)
                {
                    runs.Add((BinaryPrimitives.ReadUInt16BigEndian(boxes[at..]), BinaryPrimitives.ReadUInt16BigEndian(boxes[(at + 2)..]), boxes[at + 6]));
                }
            }

            boxes = boxes[size..];
        }

        return runs;
    }

    private int FaceAt(List<(int Start, int End, int Face)> runs, int index)
    {
        foreach (var (start, end, face) in runs)
        {
            if (index >= start && index < end)
            {
                return face;
            }
        }

        return _defaultFace;
    }

    private static void Switch(StringBuilder output, int from, int to)
    {
        foreach (var (flag, tag) in Tags)
        {
            if ((from & flag) != 0 && (to & flag) == 0)
            {
                output.Append("</").Append(tag).Append('>');
            }
            else if ((from & flag) == 0 && (to & flag) != 0)
            {
                output.Append('<').Append(tag).Append('>');
            }
        }
    }
}
