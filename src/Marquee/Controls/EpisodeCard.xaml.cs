using System.Numerics;
using Marquee.Data;
using Marquee.Imaging;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Marquee.Controls;

/// <summary>A 16:9 card for an episode (or a movie) to continue: still, progress, and what's left.</summary>
public sealed partial class EpisodeCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(MediaItem), typeof(EpisodeCard), new PropertyMetadata(null, (d, _) => ((EpisodeCard)d).Update()));

    private readonly CardHover _hover;

    public EpisodeCard()
    {
        InitializeComponent();
        _hover = new CardHover(this, Lift, GlowHost);
    }

    public MediaItem? Item
    {
        get => (MediaItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void Update()
    {
        if (Item is not { } item)
        {
            return;
        }

        Art.Background = Theme.TintBrush(item.Tint, item.ShowTitle ?? item.Title);
        Img.SetSource(Still, item.Still ?? item.Backdrop);
        bool episode = item.Kind == ItemKind.Episode;
        CodeText.Text = episode ? Format.EpisodeCode(item) : item.Year?.ToString() ?? "";
        ShowText.Text = episode ? item.ShowTitle ?? item.Title : item.Title;
        DetailText.Text = episode
            ? Format.Join(item.Title, item.InProgress ? Format.TimeLeft(item) : Format.Duration(item.DurationMs))
            : item.InProgress ? Format.TimeLeft(item) : Format.Duration(item.DurationMs);
        NewBadge.Visibility = episode && item.ViewCount == 0 && !item.InProgress && item.Number == 1 ? Visibility.Visible : Visibility.Collapsed;
        ProgressTrack.Visibility = item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, $"{ShowText.Text}, {CodeText.Text} {item.Title}");
        UpdateProgressWidth();
        _ = UpdateGlowAsync(item);
    }

    private async Task UpdateGlowAsync(MediaItem item)
    {
        if (await CardHover.ColorForAsync(item.Tint, item.Still ?? item.Backdrop, 480, 270) is { } color && Item == item)
        {
            _hover.SetColor(color);
        }
    }

    private void UpdateProgressWidth() => ProgressFill.Width = Art.ActualWidth * (Item?.Progress ?? 0);

    private void Art_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgressWidth();

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e) => _hover.Enter();

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e) => _hover.Exit();

    private void Root_Click(object sender, RoutedEventArgs e)
    {
        if (Item is { } item)
        {
            App.Window.OpenItem(item);
        }
    }
}
