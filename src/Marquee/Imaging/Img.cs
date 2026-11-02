using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Marquee.Imaging;

/// <summary>
/// <c>img:Img.Source="{x:Bind Item.Poster}"</c> on an <see cref="Image"/>: loads an image reference
/// through the cache at <c>Img.Width</c> × <c>Img.Height</c> pixels and fades it in.
/// </summary>
public static class Img
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached("Source", typeof(string), typeof(Img), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty WidthProperty =
        DependencyProperty.RegisterAttached("Width", typeof(int), typeof(Img), new PropertyMetadata(300));

    public static readonly DependencyProperty HeightProperty =
        DependencyProperty.RegisterAttached("Height", typeof(int), typeof(Img), new PropertyMetadata(450));

    public static string? GetSource(Image image) => (string?)image.GetValue(SourceProperty);

    public static void SetSource(Image image, string? value) => image.SetValue(SourceProperty, value);

    public static int GetWidth(Image image) => (int)image.GetValue(WidthProperty);

    public static void SetWidth(Image image, int value) => image.SetValue(WidthProperty, value);

    public static int GetHeight(Image image) => (int)image.GetValue(HeightProperty);

    public static void SetHeight(Image image, int value) => image.SetValue(HeightProperty, value);

    private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var image = (Image)d;
        image.Source = null;
        image.Opacity = 0;
        if (e.NewValue is not string reference || reference.Length == 0)
        {
            return;
        }

        int width = GetWidth(image);
        int height = GetHeight(image);
        string? path = await App.Services.Images.GetAsync(reference, width, height);
        if (path is null || GetSource(image) != reference)
        {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.ImageOpened += (_, _) => FadeIn(image);
        bitmap.UriSource = new Uri(path);
        image.Source = bitmap;
    }

    private static void FadeIn(Image image)
    {
        var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(180) };
        Storyboard.SetTarget(fade, image);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
