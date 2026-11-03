using System.Runtime.InteropServices.WindowsRuntime;
using Marquee.Playback.Subtitles;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Marquee.Player;

/// <summary>
/// Draws subtitle frames over the Media Foundation video: one Image per bitmap on a Canvas covering
/// the stage. Frames arrive on the subtitle thread; only the newest is drawn, on the UI thread.
/// </summary>
internal sealed class SubtitleOverlay
{
    private readonly Canvas _canvas;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<(Image Image, WriteableBitmap? Bitmap)> _images = [];
    private SubtitleFrame? _pending;
    private int _queued;

    public SubtitleOverlay(Canvas canvas)
    {
        _canvas = canvas;
        _dispatcher = canvas.DispatcherQueue;
    }

    /// <summary>Takes ownership of <paramref name="frame"/>. Callable from any thread.</summary>
    public void Show(SubtitleFrame frame)
    {
        Interlocked.Exchange(ref _pending, frame)?.Release();
        if (Interlocked.Exchange(ref _queued, 1) == 0 && !_dispatcher.TryEnqueue(Draw))
        {
            _queued = 0;
        }
    }

    public void Clear() => Show(SubtitleFrame.Empty);

    private void Draw()
    {
        _queued = 0;
        if (Interlocked.Exchange(ref _pending, null) is not { } frame)
        {
            return;
        }

        try
        {
            double scale = _canvas.XamlRoot?.RasterizationScale ?? 1;
            for (int i = 0; i < frame.Images.Count; i++)
            {
                var source = frame.Images[i];
                if (i == _images.Count)
                {
                    var created = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                    _canvas.Children.Add(created);
                    _images.Add((created, null));
                }

                var (image, bitmap) = _images[i];
                if (bitmap is null || bitmap.PixelWidth != source.Width || bitmap.PixelHeight != source.Height)
                {
                    bitmap = new WriteableBitmap(source.Width, source.Height);
                    _images[i] = (image, bitmap);
                }

                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Write(source.Pixels, 0, source.Width * source.Height * 4);
                }

                bitmap.Invalidate();
                image.Source = bitmap;
                image.Width = source.DisplayWidth / scale;
                image.Height = source.DisplayHeight / scale;
                Canvas.SetLeft(image, source.X / scale);
                Canvas.SetTop(image, source.Y / scale);
                image.Visibility = Visibility.Visible;
            }

            for (int i = frame.Images.Count; i < _images.Count; i++)
            {
                _images[i].Image.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            frame.Release();
        }
    }
}
