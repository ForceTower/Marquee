namespace Marquee.Playback.Matroska;

/// <summary>
/// Writes a time range of one track's raw packets to a file, e.g. a TrueHD clip for tools that
/// read elementary streams. Matroska stores TrueHD as raw access units, so concatenating the
/// block payloads gives a valid .thd stream.
/// </summary>
internal static class TrackExtractor
{
    public static string Run(string path, int trackNumber, TimeSpan start, TimeSpan length, string output)
    {
        using var demuxer = MatroskaDemuxer.Open(path);
        var track = demuxer.Tracks.FirstOrDefault(t => t.Number == trackNumber)
                    ?? throw new ArgumentException($"No track #{trackNumber}");
        int seekTrack = demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.Number ?? trackNumber;

        demuxer.Expose(trackNumber, active: true);
        TimeSpan actual = demuxer.Seek(start, seekTrack);
        long end = (start + length).Ticks;

        using var file = File.Create(output);
        int packets = 0;
        long firstTicks = -1;
        long lastTicks = 0;
        while (demuxer.Next(trackNumber) is { } packet && packet.TimeTicks < end)
        {
            if (packet.TimeTicks < start.Ticks)
            {
                continue;
            }

            file.Write(packet.Data);
            firstTicks = firstTicks < 0 ? packet.TimeTicks : firstTicks;
            lastTicks = packet.TimeTicks;
            packets++;
        }

        return $"track #{trackNumber} {track.CodecId} '{track.Name}': {packets} packets, " +
               $"{TimeSpan.FromTicks(firstTicks):hh\\:mm\\:ss\\.fff}–{TimeSpan.FromTicks(lastTicks):hh\\:mm\\:ss\\.fff} " +
               $"(seek landed at {actual:hh\\:mm\\:ss\\.fff}), {file.Length / 1048576.0:0.0} MB → {output}";
    }
}
