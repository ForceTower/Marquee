using Marquee.Controls;
using Marquee.Data;
using Marquee.Imaging;
using Marquee.Library;
using Marquee.Player;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Marquee.Pages;

public sealed partial class ShowDetailPage : Page
{
    public sealed record Args(string ShowId, int? Season);

    private MediaItem? _show;
    private List<MediaItem> _seasons = [];
    private MediaItem? _nextUp;
    private bool _filling;

    public ShowDetailPage()
    {
        InitializeComponent();
        PosterFrame.Shadow = new ThemeShadow();
        SizeChanged += (_, e) => PosterFrame.Visibility = e.NewSize.Width < 720 ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = e.Parameter as Args ?? new Args((string)e.Parameter, null);
        App.Window.LibraryChanged += OnLibraryChanged;
        Show(App.Services.Db.GetItem(args.ShowId), args.Season);
        if (_show is { } show)
        {
            await DetailsLoader.LoadAsync(show);
            Show(App.Services.Db.GetItem(show.Id), SelectedSeason?.SeasonNumber);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Window.LibraryChanged -= OnLibraryChanged;

    private MediaItem? SelectedSeason => (SeasonBox.SelectedItem as ComboBoxItem)?.Tag as MediaItem;

    private void OnLibraryChanged()
    {
        if (_show is { } show)
        {
            Show(App.Services.Db.GetItem(show.Id), SelectedSeason?.SeasonNumber);
        }
    }

    private void Show(MediaItem? show, int? season)
    {
        if (show is null)
        {
            return;
        }

        bool first = _show?.Id != show.Id;
        _show = show;
        var db = App.Services.Db;
        var episodes = db.GetEpisodes(show.Id);
        _nextUp = episodes.Where(e => e.InProgress).MaxBy(e => e.LastViewedAt ?? 0)
                  ?? episodes.SkipWhile(e => !e.IsWatched && episodes.Any(x => x.IsWatched)).FirstOrDefault(e => !e.IsWatched && (e.SeasonNumber ?? 0) > 0)
                  ?? episodes.FirstOrDefault(e => !e.IsWatched && (e.SeasonNumber ?? 0) > 0)
                  ?? episodes.FirstOrDefault();

        if (first)
        {
            PosterFrame.Background = Theme.TintBrush(show.Tint, show.Title);
            Img.SetSource(BackdropImage, show.Backdrop);
            Img.SetSource(PosterImage, show.Poster);
        }

        Img.SetSource(LogoImage, show.Logo);
        LogoImage.Visibility = show.Logo is null ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = show.Title;
        TitleText.Visibility = show.Logo is null ? Visibility.Visible : Visibility.Collapsed;
        PosterTitle.Text = show.Title;
        SummaryText.Text = show.Summary ?? "";

        _seasons = db.GetChildren(show.Id).Where(s => s.Kind == ItemKind.Season)
            .OrderBy(s => (s.SeasonNumber ?? 0) == 0 ? int.MaxValue : s.SeasonNumber).ToList();
        string years = show.Year is { } y ? $"{y}–" : "";
        DetailParts.FillMeta(MetaRow,
        [
            years,
            Format.Plural(_seasons.Count(s => s.SeasonNumber > 0), "season") + " · " + Format.Plural(show.LeafCount ?? episodes.Count, "episode"),
            show.ContentRating,
            show.Genres,
        ], show.Rating);

        PlayText.Text = _nextUp is { } next
            ? $"{(next.InProgress ? "Resume" : show.ViewedLeafCount > 0 ? "Play" : "Start")} {Format.EpisodeCode(next)}"
            : "Play";
        PlayButton.IsEnabled = _nextUp is not null;
        string watchedLabel = show.IsWatched ? "Mark show as unwatched" : "Mark show as watched";
        ToolTipService.SetToolTip(WatchedButton, watchedLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WatchedButton, watchedLabel);
        WatchedIcon.Foreground = (Brush)Application.Current.Resources[show.IsWatched ? "AccentBrush" : "TextPrimaryBrush"];
        MatchMenuItem.Visibility = show.IsPlex ? Visibility.Collapsed : Visibility.Visible;

        _filling = true;
        UserRatingControl.PlaceholderValue = show.Rating is { } audience ? audience / 2 : -1;
        UserRatingControl.Value = show.UserRating is { } mine ? mine / 2 : -1;
        SeasonBox.Items.Clear();
        foreach (var s in _seasons)
        {
            SeasonBox.Items.Add(new ComboBoxItem { Content = s.Title, Tag = s });
        }

        int? wanted = season ?? _nextUp?.SeasonNumber;
        SeasonBox.SelectedItem = SeasonBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((MediaItem)i.Tag).SeasonNumber == wanted)
                                 ?? SeasonBox.Items.FirstOrDefault();
        _filling = false;
        ShowSeason();
    }

    private void ShowSeason()
    {
        if (SelectedSeason is not { } season)
        {
            EpisodeList.ItemsSource = null;
            return;
        }

        var episodes = App.Services.Db.GetChildren(season.Id).Where(e => e.Kind == ItemKind.Episode).ToList();
        if ((SortBox.SelectedItem as ComboBoxItem)?.Tag as string == "desc")
        {
            episodes.Reverse();
        }

        int watched = episodes.Count(e => e.IsWatched);
        SeasonProgressText.Text = episodes.Count == 0 ? "" : watched == episodes.Count
            ? "All watched"
            : $"{watched} of {episodes.Count} watched";
        SeasonWatchedItem.Text = watched == episodes.Count && episodes.Count > 0 ? $"Mark {season.Title} as unwatched" : $"Mark {season.Title} as watched";
        EpisodeList.ItemsSource = episodes.Select(e => (UIElement)new EpisodeRow { Item = e, IsNextUp = e.Id == _nextUp?.Id }).ToList();
    }

    private void SeasonBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling)
        {
            ShowSeason();
        }
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && IsLoaded)
        {
            ShowSeason();
        }
    }

    private async void UserRating_ValueChanged(RatingControl sender, object args)
    {
        if (!_filling && _show is { } show)
        {
            await WatchState.RateAsync(show, sender.Value < 0 ? null : sender.Value * 2);
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_nextUp is { } episode)
        {
            PlayerLauncher.Play(episode, resume: true);
        }
    }

    private async void Watched_Click(object sender, RoutedEventArgs e)
    {
        if (_show is { } show)
        {
            await WatchState.SetAsync(show, !show.IsWatched);
        }
    }

    private async void SeasonWatched_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSeason is { } season)
        {
            bool allWatched = App.Services.Db.GetChildren(season.Id).All(x => x.IsWatched);
            await WatchState.SetAsync(season, !allWatched);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_show is { } show)
        {
            await DetailsLoader.LoadAsync(show, force: true);
            await App.Services.Sync.SyncAllAsync(refreshServers: false);
        }
    }

    private async void EditMatch_Click(object sender, RoutedEventArgs e)
    {
        if (_show is { } show)
        {
            await MatchDialog.ShowAsync(show, XamlRoot);
        }
    }
}
