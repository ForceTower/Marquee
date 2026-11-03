using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;

namespace Marquee.Playback.Previews;

/// <summary>
/// Headless check of seek-bar previews on a real file: renders pictures spread over the file through
/// the same reader and decoder the player uses, into one contact sheet, with a report of how long
/// each took to read and decode and how its colour was converted.
/// </summary>
internal static class ThumbnailProbe
{
    public static async Task<string> RunAsync(string location, string outputFolder, int count = 12, int width = 432)
    {
        Directory.CreateDirectory(outputFolder);
        var report = new StringBuilder();
        var open = Stopwatch.StartNew();
        using var thumbnailer = FrameThumbnailer.Open(location);
        report.AppendLine($"{Path.GetFileName(location.Split('?')[0])}");
        report.AppendLine($"{thumbnailer.Description}; opened in {open.ElapsedMilliseconds} ms");

        TimeSpan duration = thumbnailer.Duration;
        var frames = new List<PreviewFrame>();
        for (int i = 0; i < count; i++)
        {
            var target = TimeSpan.FromTicks(duration.Ticks * (2 * i + 1) / (2 * count));
            var total = Stopwatch.StartNew();
            var frame = thumbnailer.Render(target, width);
            if (frame is null)
            {
                report.AppendLine($"{target:hh\\:mm\\:ss}: nothing decoded");
                continue;
            }

            frames.Add(frame);
            report.AppendLine($"{target:hh\\:mm\\:ss} → keyframe {frame.Time:hh\\:mm\\:ss\\.fff}: {frame.Width}×{frame.Height} {frame.Kind}, " +
                              $"read {frame.ReadTime.TotalMilliseconds:0} ms, decode+convert {frame.DecodeTime.TotalMilliseconds:0} ms, total {total.ElapsedMilliseconds} ms");
        }

        if (frames.Count > 0)
        {
            string sheet = Path.Combine(outputFolder, "previews.png");
            await SaveSheetAsync(frames, sheet);
            report.AppendLine($"Sheet: {sheet}");
        }

        File.WriteAllText(Path.Combine(outputFolder, "report.txt"), report.ToString());
        return report.ToString();
    }

    private static async Task SaveSheetAsync(List<PreviewFrame> frames, string path)
    {
        const int Columns = 3, Gap = 8;
        int tileW = frames.Max(f => f.Width), tileH = frames.Max(f => f.Height);
        int rows = (frames.Count + Columns - 1) / Columns;
        int width = Columns * tileW + (Columns + 1) * Gap, height = rows * tileH + (rows + 1) * Gap;
        var canvas = new byte[width * height * 4];
        for (int i = 3; i < canvas.Length; i += 4)
        {
            canvas[i - 3] = canvas[i - 2] = canvas[i - 1] = 40;
            canvas[i] = 255;
        }

        for (int n = 0; n < frames.Count; n++)
        {
            var f = frames[n];
            int left = Gap + n % Columns * (tileW + Gap), top = Gap + n / Columns * (tileH + Gap);
            for (int y = 0; y < f.Height; y++)
            {
                Buffer.BlockCopy(f.Pixels, y * f.Width * 4, canvas, ((top + y) * width + left) * 4, f.Width * 4);
            }
        }

        await using var file = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, canvas);
        await encoder.FlushAsync();
    }
}
