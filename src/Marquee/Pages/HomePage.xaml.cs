using Marquee.Data;
using Marquee.Controls;
using Marquee.Imaging;
using Marquee.Player;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Marquee.Pages;

public sealed partial class HomePage : Page
{
    private List<MediaItem> _recent = [];
    private MediaItem? _hero;
    private int _recentColumns;

    public HomePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Window.LibraryChanged += Reload;
        Reload();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Window.LibraryChanged -= Reload;

    private void Reload()
    {
        var db = App.Services.Db;
        bool hasSources = db.GetLibraries().Any(l => l.Enabled);
        var continuing = db.GetContinueWatching(24);
        _recent = db.GetRecentlyAdded(40);

        Welcome.Visibility = hasSources || _recent.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        _hero = continuing.FirstOrDefault() ?? _recent.FirstOrDefault(i => i.Kind == ItemKind.Movie);
        ShowHero(_hero, continuing.Count > 0);

        UpNextRepeater.ItemsSource = continuing.Skip(1).Take(8).ToList();
        UpNextSection.Visibility = continuing.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RecentSection.Visibility = _recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _recentColumns = 0;
        UpdateRecent(RecentRepeater.ActualWidth);
    }

    private void ShowHero(MediaItem? item, bool continuing)
    {
        Hero.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null)
        {
            return;
        }

        var show = item.Kind == ItemKind.Episode && item.ShowId is { } showId ? App.Services.Db.GetItem(showId) : null;
        Img.SetSource(HeroBackdrop, item.Backdrop ?? show?.Backdrop);
        HeroSummary.Text = item.Summary ?? show?.Summary ?? "";
        HeroSummary.Visibility = HeroSummary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        string? logo = item.Kind == ItemKind.Episode ? show?.Logo : item.Logo;
        Img.SetSource(HeroLogo, logo);
        HeroLogo.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
        HeroTitle.Text = item.Kind == ItemKind.Episode ? item.ShowTitle ?? item.Title : item.Title;
        HeroTitle.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
        HeroOverline.Text = continuing ? (item.InProgress ? "CONTINUE WATCHING" : "UP NEXT") : "RECENTLY ADDED";

        HeroMeta.Children.Clear();
        string meta = item.Kind == ItemKind.Episode
            ? Format.Join(Format.EpisodeCode(item), item.Title, Format.Duration(item.DurationMs))
            : Format.Join(item.Year?.ToString(), Format.Duration(item.DurationMs), item.Genres?.Split(", ")[0]);
        HeroMeta.Children.Add(new TextBlock { Text = meta, Style = (Style)Application.Current.Resources["MetaTextStyle"], VerticalAlignment = VerticalAlignment.Center });
        foreach (string badge in Format.QualityBadges(item).Take(4))
        {
            HeroMeta.Children.Add(Badges.Create(badge));
        }

        HeroProgressRow.Visibility = item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        HeroProgress.Value = item.Progress;
        HeroTimeLeft.Text = Format.TimeLeft(item);
        HeroPlayText.Text = item.InProgress ? "Resume" : "Play";

        var library = App.Services.Db.GetLibraries().FirstOrDefault(l => l.Id == item.LibraryId);
        var server = library?.IsPlex == true ? App.Services.Plex.Server(library.ServerId) : null;
        SourceText.Text = server is not null ? $"Plex · {server.Server.Name}" : "This PC";
        SourceDot.Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[server is null || server.Server.Online ? "SuccessBrush" : "TextMutedBrush"];
    }

    private void UpdateRecent(double width)
    {
        if (width <= 0)
        {
            return;
        }

        int columns = Math.Max(1, (int)((width + 16) / (148 + 16)));
        if (columns == _recentColumns)
        {
            return;
        }

        _recentColumns = columns;
        RecentRepeater.ItemsSource = _recent.Take(columns * 2).ToList();
    }

    private void RecentRepeater_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRecent(e.NewSize.Width);

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_hero is { } item)
        {
            PlayerLauncher.Play(item, resume: true);
        }
    }

    private void HeroDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_hero is { } item)
        {
            App.Window.OpenItem(item);
        }
    }

    private void SetUp_Click(object sender, RoutedEventArgs e) => App.Window.Navigate("sources");

    private void SeeMovies_Click(object sender, RoutedEventArgs e) => App.Window.Navigate("movies");

    private void SeeShows_Click(object sender, RoutedEventArgs e) => App.Window.Navigate("shows");
}
