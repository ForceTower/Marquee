using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Marquee.Playback.Matroska;
using Windows.Graphics.Imaging;

namespace Marquee.Playback.Subtitles;

/// <summary>
/// Headless check of the subtitle pipeline on a real file: lists the subtitle tracks, their index
/// coverage and attached fonts, then reads each track around a time through the same source and
/// renderer playback uses and saves what would be on screen as a PNG.
/// </summary>
internal static class SubtitleProbe
{
    public static async Task<string> RunAsync(string location, string outputFolder, TimeSpan at, int? onlyTrack, int width = 1920, int height = 1080)
    {
        Directory.CreateDirectory(outputFolder);
        var report = new StringBuilder();
        using var demuxer = MatroskaDemuxer.Open(location, 64 * 1024);
        var video = demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
        var attachments = demuxer.ReadAttachments();
        report.AppendLine($"{location}");
        report.AppendLine($"video {video?.CodecId} {video?.Width}x{video?.Height}, {demuxer.Cues.Count} cues, {attachments.Count} attachments: " +
                          string.Join(", ", attachments.Select(a => $"{a.Name} ({a.MimeType}, {a.Data.Length / 1024} KB)")));
        var layout = SubtitleLayout.Fit(width, height, video?.Width ?? width, video?.Height ?? height);

        foreach (var track in demuxer.Tracks.Where(t => t.Kind == TrackKind.Subtitle && (onlyTrack is null || t.Number == onlyTrack)))
        {
            var cues = demuxer.Cues.Where(c => c.Track == track.Number).ToList();
            report.AppendLine();
            report.AppendLine($"#{track.Number} {track.CodecId} [{track.DisplayLanguage}] '{track.Name}' default={track.IsDefault} forced={track.IsForced} " +
                              $"private={track.CodecPrivate?.Length ?? 0} B, compression={track.CompressionAlgorithm?.ToString() ?? "none"}; " +
                              $"{cues.Count} cue entries, {cues.Count(c => c.RelativePosition > 0)} with a relative position, {cues.Count(c => c.DurationTicks > 0)} with a duration");
            try
            {
                report.AppendLine(await RenderTrackAsync(location, track, video, demuxer, layout, at, outputFolder));
            }
            catch (Exception ex)
            {
                report.AppendLine($"  failed: {ex}");
            }
        }

        return report.ToString();
    }

    private static async Task<string> RenderTrackAsync(string location, MatroskaTrack track, MatroskaTrack? video, MatroskaDemuxer demuxer,
        SubtitleLayout layout, TimeSpan at, string outputFolder)
    {
        var codec = SubtitleCodecs.FromMatroska(track.CodecId);
        if (!SubtitleOpener.CanShow(codec))
        {
            return $"  not supported ({codec?.ToString() ?? "unknown codec"})";
        }

        var log = new StringBuilder();
        using var playbackDemuxer = MatroskaDemuxer.Open(location);
        using var source = new MatroskaSubtitleSource(location, track.Number, playbackDemuxer);
        var stopwatch = Stopwatch.StartNew();
        source.Focus(at.Ticks);
        if (!source.Indexed && video is not null)
        {
            playbackDemuxer.Expose(video.Number, active: true);
            playbackDemuxer.Seek(at - TimeSpan.FromSeconds(30), video.Number);
            while (playbackDemuxer.Next(video.Number) is { } packet && packet.TimeTicks < (at + TimeSpan.FromSeconds(60)).Ticks)
            {
            }
        }

        var packets = new List<SubtitlePacket>();
        long quietSince = stopwatch.ElapsedMilliseconds;
        while (stopwatch.ElapsedMilliseconds - quietSince < 1500 && stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            while (source.TryTake(out var packet))
            {
                packets.Add(packet);
                quietSince = stopwatch.ElapsedMilliseconds;
            }

            await Task.Delay(50);
        }

        log.AppendLine($"  {(source.Indexed ? "indexed" : "following playback")}: {packets.Count} packets around {at:hh\\:mm\\:ss} in {stopwatch.ElapsedMilliseconds - 1500} ms");
        packets.Sort((a, b) => a.TimeTicks.CompareTo(b.TimeTicks));
        foreach (var p in packets.Where(p => p.TimeTicks >= at.Ticks - TimeSpan.TicksPerSecond * 5).Take(6))
        {
            log.AppendLine($"    {TimeSpan.FromTicks(p.TimeTicks):hh\\:mm\\:ss\\.fff} +{p.DurationTicks / TimeSpan.TicksPerMillisecond} ms, {p.Data.Length} B" +
                           (codec!.Value.IsBitmap() ? "" : $": {Encoding.UTF8.GetString(p.Data).ReplaceLineEndings(" | ")}"));
        }

        int width = video?.Width ?? 0, height = video?.Height ?? 0;
        List<MatroskaAttachment> fonts = codec == SubtitleCodec.Ass ? demuxer.ReadAttachments() : [];
        using ISubtitleRenderer renderer = codec!.Value.IsBitmap()
            ? new BitmapSubtitleRenderer(codec.Value, track.CodecPrivate, width, height)
            : new AssRenderer(codec.Value, track.CodecPrivate, fonts, width, height, message => log.AppendLine($"    libass: {message}"));
        foreach (var packet in packets)
        {
            renderer.Add(packet);
        }

        int shot = 0;
        foreach (var packet in packets.Where(p => p.TimeTicks >= at.Ticks).Take(40))
        {
            long time = packet.TimeTicks + Math.Min(packet.DurationTicks > 0 ? packet.DurationTicks / 2 : 0, TimeSpan.TicksPerSecond / 2) + TimeSpan.TicksPerMillisecond * 40;
            var timer = Stopwatch.StartNew();
            var frame = renderer.Render(time, layout, force: true);
            double ms = timer.Elapsed.TotalMilliseconds;
            if (frame is null || frame.Images.Count == 0)
            {
                continue;
            }

            string file = Path.Combine(outputFolder, $"track{track.Number}-{shot}-{TimeSpan.FromTicks(time):hhmmss}.png");
            await SaveAsync(frame, layout, file);
            log.AppendLine($"  {TimeSpan.FromTicks(time):hh\\:mm\\:ss\\.fff}: {frame.Images.Count} images " +
                           string.Join(", ", frame.Images.Select(i => $"{i.Width}x{i.Height} at ({i.X:0},{i.Y:0}) shown {i.DisplayWidth:0}x{i.DisplayHeight:0}")) +
                           $", rendered in {ms:0.0} ms → {file}");
            frame.Release();
            if (++shot == 3)
            {
                break;
            }
        }

        if (shot == 0)
        {
            log.AppendLine("  nothing rendered");
        }

        return log.ToString().TrimEnd();
    }

    /// <summary>The frame over a dark grey canvas, with the video's rectangle a shade lighter.</summary>
    private static async Task SaveAsync(SubtitleFrame frame, SubtitleLayout layout, string path)
    {
        var canvas = new byte[layout.Width * layout.Height * 4];
        for (int y = 0; y < layout.Height; y++)
        {
            for (int x = 0; x < layout.Width; x++)
            {
                bool inVideo = x >= layout.VideoX && x < layout.VideoX + layout.VideoWidth && y >= layout.VideoY && y < layout.VideoY + layout.VideoHeight;
                byte shade = (byte)(inVideo ? 70 : 30);
                int i = (y * layout.Width + x) * 4;
                canvas[i] = canvas[i + 1] = canvas[i + 2] = shade;
                canvas[i + 3] = 255;
            }
        }

        foreach (var image in frame.Images)
        {
            for (int y = 0; y < (int)image.DisplayHeight; y++)
            {
                int ty = (int)image.Y + y;
                if (ty < 0 || ty >= layout.Height)
                {
                    continue;
                }

                int sy = Math.Min(image.Height - 1, (int)(y * image.Height / image.DisplayHeight));
                for (int x = 0; x < (int)image.DisplayWidth; x++)
                {
                    int tx = (int)image.X + x;
                    if (tx < 0 || tx >= layout.Width)
                    {
                        continue;
                    }

                    int sx = Math.Min(image.Width - 1, (int)(x * image.Width / image.DisplayWidth));
                    int s = (sy * image.Width + sx) * 4;
                    int d = (ty * layout.Width + tx) * 4;
                    int inverse = 255 - image.Pixels[s + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        canvas[d + c] = (byte)(image.Pixels[s + c] + canvas[d + c] * inverse / 255);
                    }
                }
            }
        }

        await using var file = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)layout.Width, (uint)layout.Height, 96, 96, canvas);
        await encoder.FlushAsync();
    }
}
