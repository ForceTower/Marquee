using System.Numerics;
using Marquee.Data;
using Marquee.Imaging;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Marquee.Controls;

public sealed partial class PosterCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(MediaItem), typeof(PosterCard), new PropertyMetadata(null, (d, _) => ((PosterCard)d).Update()));

    private readonly CardHover _hover;

    public PosterCard()
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

        Art.Background = Theme.TintBrush(item.Tint, item.Title);
        PlaceholderTitle.Text = item.Title;
        Img.SetSource(Poster, item.Poster);
        TitleText.Text = item.Title;
        SubtitleText.Text = Format.PosterSubtitle(item);
        ToolTipService.SetToolTip(Root, item.Year is { } y ? $"{item.Title} ({y})" : item.Title);
        AutomationName(item);

        string badge = Format.PosterBadge(item);
        BadgeText.Text = badge;
        Badge.Visibility = badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        int unwatched = item.Kind == ItemKind.Show ? (item.LeafCount ?? 0) - (item.ViewedLeafCount ?? 0) : 0;
        CountBadge.Visibility = unwatched > 0 && item.ViewedLeafCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = unwatched.ToString();
        UnwatchedDot.Visibility = !item.IsWatched && !item.InProgress && CountBadge.Visibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;

        ProgressTrack.Visibility = item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressWidth();
        _ = UpdateGlowAsync(item);
    }

    private async Task UpdateGlowAsync(MediaItem item)
    {
        if (await CardHover.ColorForAsync(item.Tint, item.Poster, 300, 450) is { } color && Item == item)
        {
            _hover.SetColor(color);
        }
    }

    private void AutomationName(MediaItem item) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, item.Year is { } y ? $"{item.Title}, {y}" : item.Title);

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
