using Marquee.Data;
using Marquee.Imaging;
using Marquee.Library;
using Marquee.Player;
using Marquee.Plex;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Marquee.Controls;

/// <summary>One episode in a season list. Clicking plays it (resuming if started).</summary>
public sealed partial class EpisodeRow : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(MediaItem), typeof(EpisodeRow), new PropertyMetadata(null, (d, _) => ((EpisodeRow)d).Update()));

    public static readonly DependencyProperty IsNextUpProperty =
        DependencyProperty.Register(nameof(IsNextUp), typeof(bool), typeof(EpisodeRow), new PropertyMetadata(false, (d, _) => ((EpisodeRow)d).Update()));

    public EpisodeRow()
    {
        InitializeComponent();
        Art.SizeChanged += (_, _) => ProgressFill.Width = Art.ActualWidth * (Item?.Progress ?? 0);
    }

    public MediaItem? Item
    {
        get => (MediaItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public bool IsNextUp
    {
        get => (bool)GetValue(IsNextUpProperty);
        set => SetValue(IsNextUpProperty, value);
    }

    private void Update()
    {
        if (Item is not { } item)
        {
            return;
        }

        Art.Background = Theme.TintBrush(item.Tint, item.ShowTitle ?? item.Title);
        Img.SetSource(Still, item.Still ?? item.Backdrop);
        NumberText.Text = item.Number is { } n ? $"E{n}" : "";
        TitleText.Text = item.Title;
        SummaryText.Text = item.Summary ?? "";
        SummaryText.Visibility = item.Summary is null ? Visibility.Collapsed : Visibility.Visible;
        MetaText.Text = Format.Join(
            Format.Duration(item.DurationMs),
            Format.ShortDate(item.Released),
            Format.PosterBadge(item) is { Length: > 0 } badge ? badge : null,
            item.InProgress ? Format.TimeLeft(item) : null);

        WatchedMark.Visibility = item.IsWatched ? Visibility.Visible : Visibility.Collapsed;
        ContinueBadge.Visibility = item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        UnwatchedDot.Visibility = !item.IsWatched && !item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        ProgressTrack.Visibility = item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        ProgressFill.Width = Art.ActualWidth * item.Progress;
        PlayFromStartItem.Visibility = item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        WatchedItem.Text = item.IsWatched ? "Mark as unwatched" : "Mark as watched";

        bool highlight = IsNextUp || item.InProgress;
        Root.Background = (Brush)Application.Current.Resources[highlight ? "SurfaceRaisedBrush" : "SurfaceBrush"];
        Root.BorderBrush = (Brush)Application.Current.Resources[highlight ? "AccentBrush" : "SurfaceStrokeBrush"];
        Root.BorderThickness = highlight ? new Thickness(3, 1, 1, 1) : new Thickness(1);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, $"Episode {item.Number}: {item.Title}");
    }

    private void Root_Click(object sender, RoutedEventArgs e)
    {
        if (Item is { } item)
        {
            PlayerLauncher.Play(item, resume: true);
        }
    }

    private void PlayFromStart_Click(object sender, RoutedEventArgs e)
    {
        if (Item is { } item)
        {
            PlayerLauncher.Play(item, resume: false);
        }
    }

    private async void Watched_Click(object sender, RoutedEventArgs e)
    {
        if (Item is { } item)
        {
            await WatchState.SetAsync(item, !item.IsWatched);
        }
    }
}
