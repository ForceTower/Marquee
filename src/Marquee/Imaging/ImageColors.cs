using System.Collections.Concurrent;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace Marquee.Imaging;

internal static class ImageColors
{
    private static readonly ConcurrentDictionary<string, Color?> Cache = new();

    /// <summary>The image's average color: a Fant-filtered decode down to a single pixel.</summary>
    public static async Task<Color?> AverageAsync(string reference, int width, int height)
    {
        if (Cache.TryGetValue(reference, out var cached))
        {
            return cached;
        }

        Color? color = null;
        try
        {
            if (await App.Services.Images.GetAsync(reference, width, height) is { } path)
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using IRandomAccessStream stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform { ScaledWidth = 1, ScaledHeight = 1, InterpolationMode = BitmapInterpolationMode.Fant };
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                byte[] p = pixels.DetachPixelData();
                color = Color.FromArgb(255, p[2], p[1], p[0]);
            }
        }
        catch (Exception)
        {
        }

        Cache[reference] = color;
        return color;
    }
}
