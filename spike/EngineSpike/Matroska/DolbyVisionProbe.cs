namespace EngineSpike.Matroska;

/// <summary>Reads a file's first Dolby Vision RPU to tell, e.g., profile 7 FEL from MEL.</summary>
internal static class DolbyVisionProbe
{
    public static string Describe(string path)
    {
        using var demuxer = MatroskaDemuxer.Open(path, 64 * 1024);
        var video = demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)
                    ?? throw new InvalidDataException("No video track");
        string config = video.DolbyVision?.ToString() ?? "no Dolby Vision configuration";
        if (video.CodecId != "V_MPEGH/ISO/HEVC" || video.CodecPrivate is not { Length: > 22 } hvcc)
        {
            return config;
        }

        int lengthSize = (hvcc[21] & 3) + 1;
        demuxer.Expose(video.Number, active: true);
        for (int packets = 0; packets < 200 && demuxer.Next(video.Number) is { } packet; packets++)
        {
            byte[] data = packet.Data;
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

                if (((data[pos] >> 1) & 0x3F) == 62)
                {
                    byte[]? converted = DoviConverter.ConvertTo81(data.AsSpan(pos, length), out string source);
                    return $"{config} · first RPU: profile {source}{(converted is null ? " (conversion failed)" : ", converts to 8.1")}";
                }

                pos += length;
            }
        }

        return $"{config} · no RPU in the first 200 packets";
    }
}
