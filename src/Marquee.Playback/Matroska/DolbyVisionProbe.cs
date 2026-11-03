using Marquee.Playback.Demuxing;

namespace Marquee.Playback.Matroska;

/// <summary>
/// Reads a file's Dolby Vision RPUs through the demuxer playback uses: the first one tells, e.g.,
/// profile 7 FEL from MEL, and the count shows whether every frame carries one (for a Blu-ray's
/// dual-layer M2TS, whether the enhancement layer's RPUs reach the base layer).
/// </summary>
internal static class DolbyVisionProbe
{
    private const int Frames = 500;

    public static string Describe(string path)
    {
        using var demuxer = Demuxer.Open(path, 64 * 1024);
        var video = demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)
                    ?? throw new InvalidDataException("No video track");
        string config = video.DolbyVision?.ToString() ?? "no Dolby Vision configuration";
        if (video.CodecId != "V_MPEGH/ISO/HEVC")
        {
            return config;
        }

        // Matroska stores length-prefixed NAL units, with the length's size in the hvcC record;
        // MPEG-TS video stays Annex B.
        int lengthSize = video.CodecPrivate is { Length: > 22 } hvcc && hvcc[0] == 1 ? (hvcc[21] & 3) + 1 : 0;
        demuxer.Expose(video.Number, active: true);
        string? first = null;
        int frames = 0;
        int withRpu = 0;
        for (; frames < Frames && demuxer.Next(video.Number) is { } packet; frames++)
        {
            var rpu = Units(packet.Data, lengthSize).FirstOrDefault(unit => AnnexB.HevcType(unit.Span) == 62);
            if (rpu.IsEmpty)
            {
                continue;
            }

            withRpu++;
            if (first is null)
            {
                byte[]? converted = DoviConverter.ConvertTo81(rpu.Span, out string source);
                first = $"profile {source}{(converted is null ? " (conversion failed)" : ", converts to 8.1")}";
            }
        }

        return first is null
            ? $"{config} · no RPU in the first {frames} frames"
            : $"{config} · first RPU: {first} · RPUs in {withRpu} of the first {frames} frames";
    }

    private static List<ReadOnlyMemory<byte>> Units(byte[] data, int lengthSize)
    {
        if (lengthSize == 0)
        {
            return AnnexB.Units(data);
        }

        var units = new List<ReadOnlyMemory<byte>>();
        for (int pos = 0; pos + lengthSize <= data.Length;)
        {
            int length = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                length = (length << 8) | data[pos + i];
            }

            pos += lengthSize;
            if (length <= 0 || pos + length > data.Length)
            {
                break;
            }

            units.Add(data.AsMemory(pos, length));
            pos += length;
        }

        return units;
    }
}
