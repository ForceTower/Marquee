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

public sealed partial class MovieDetailPage : Page
{
    private MediaItem? _item;
    private ItemDetails? _details;
    private TrackPreferences _prefs = new(null, null, null);
    private bool _filling;

    public MovieDetailPage()
    {
        InitializeComponent();
        PosterFrame.Shadow = new ThemeShadow();
        SizeChanged += (_, e) => Layout(e.NewSize.Width);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        string id = (string)e.Parameter;
        App.Window.LibraryChanged += OnLibraryChanged;
        Show(App.Services.Db.GetItem(id));
        if (_item is { } item)
        {
            _details = await DetailsLoader.LoadAsync(item);
            Show(App.Services.Db.GetItem(id));
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Window.LibraryChanged -= OnLibraryChanged;

    private void OnLibraryChanged()
    {
        if (_item is { } item)
        {
            Show(App.Services.Db.GetItem(item.Id));
        }
    }

    private void Show(MediaItem? item)
    {
        if (item is null)
        {
            return;
        }

        bool first = _item?.Id != item.Id;
        _item = item;
        _details ??= DetailsLoader.Cached(item);
        _prefs = TrackPreferences.Load(item.Id);

        if (first)
        {
            PosterFrame.Background = Theme.TintBrush(item.Tint, item.Title);
            Img.SetSource(BackdropImage, item.Backdrop);
            Img.SetSource(PosterImage, item.Poster);
        }

        Img.SetSource(LogoImage, item.Logo);
        LogoImage.Visibility = item.Logo is null ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = item.Title;
        TitleText.Visibility = item.Logo is null ? Visibility.Visible : Visibility.Collapsed;
        PosterTitle.Text = item.Title;
        TaglineText.Text = item.Tagline ?? "";
        TaglineText.Visibility = item.Tagline is null ? Visibility.Collapsed : Visibility.Visible;

        DetailParts.FillMeta(MetaRow, [item.Year?.ToString(), Format.Duration(item.DurationMs), item.ContentRating, item.Genres], item.Rating);
        DetailParts.FillBadges(BadgeRow, Format.QualityBadges(item, _details));

        PlayText.Text = item.InProgress ? $"Resume · {Format.Clock(TimeSpan.FromMilliseconds(item.PositionMs))}" : "Play";
        RestartButton.Visibility = item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        ProgressRow.Visibility = item.InProgress ? Visibility.Visible : Visibility.Collapsed;
        WatchProgress.Value = item.Progress;
        ProgressText.Text = $"{item.Progress:P0}";
        string watchedLabel = item.IsWatched ? "Mark as unwatched" : "Mark as watched";
        ToolTipService.SetToolTip(WatchedButton, watchedLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WatchedButton, watchedLabel);
        WatchedIcon.Foreground = item.IsWatched ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["TextPrimaryBrush"];

        SummaryText.Text = item.Summary ?? "No overview available.";
        CastSection.Visibility = _details?.Cast.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CastRepeater.ItemsSource = _details is null ? null : DetailParts.CastItems(_details.Cast);
        CastCount.Text = _details is null ? "" : Format.Plural(_details.Cast.Count, "person").Replace("persons", "people");

        DetailParts.FillVersions(VersionList, item, _details, _prefs, ChooseVersion);
        VersionsSection.Visibility = item.Versions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var library = App.Services.Db.GetLibraries().FirstOrDefault(l => l.Id == item.LibraryId);
        DetailParts.FillDetails(DetailsGrid,
        [
            ("Director", _details is null ? null : string.Join(", ", _details.Directors.Take(3))),
            ("Writers", _details is null ? null : string.Join(", ", _details.Writers.Take(3))),
            ("Studio", item.Studio),
            ("Released", Format.Date(item.Released)),
            ("Genres", item.Genres),
            ("Country", _details is null ? null : string.Join(", ", _details.Countries.Take(3))),
            ("Added", Format.Ago(item.AddedAt)),
            ("Source", library?.IsPlex == true ? $"Plex · {library.Title}" : library?.Path),
        ]);

        MatchMenuItem.Visibility = item.IsPlex ? Visibility.Collapsed : Visibility.Visible;
        OpenFolderItem.Visibility = item.IsPlex ? Visibility.Collapsed : Visibility.Visible;

        _filling = true;
        UserRatingControl.PlaceholderValue = item.Rating is { } audience ? audience / 2 : -1;
        UserRatingControl.Value = item.UserRating is { } mine ? mine / 2 : -1;
        var version = _prefs.PickVersion(item);
        var streams = _details?.Versions.FirstOrDefault(v => v.Location == version?.Location)?.Streams ?? [];
        DetailParts.FillTrackBoxes(AudioBox, SubtitleBox, streams, _prefs);
        _filling = false;
    }

    private void Layout(double width)
    {
        bool narrow = width < 1100;
        AsideColumn.Width = narrow ? new GridLength(0) : new GridLength(380);
        Grid.SetColumn(Aside, narrow ? 0 : 1);
        Grid.SetRow(Aside, narrow ? 1 : 0);
        Columns.ColumnSpacing = narrow ? 0 : 40;
        PosterFrame.Visibility = width < 720 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChooseVersion(MediaVersion version)
    {
        if (_item is null)
        {
            return;
        }

        _prefs = _prefs with { Version = version.Location, Audio = null, Subtitle = null };
        _prefs.Save(_item.Id);
        Show(_item);
    }

    private void TrackBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _item is null)
        {
            return;
        }

        _prefs = _prefs with
        {
            Audio = (AudioBox.SelectedItem as ComboBoxItem)?.Tag as int?,
            Subtitle = (SubtitleBox.SelectedItem as ComboBoxItem)?.Tag as int?,
        };
        _prefs.Save(_item.Id);
    }

    private async void UserRating_ValueChanged(RatingControl sender, object args)
    {
        if (!_filling && _item is { } item)
        {
            await WatchState.RateAsync(item, sender.Value < 0 ? null : sender.Value * 2);
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Play(resume: true);

    private void Restart_Click(object sender, RoutedEventArgs e) => Play(resume: false);

    private void PlayMpv_Click(object sender, RoutedEventArgs e) => Play(true, EngineMode.Mpv);

    private void PlayMf_Click(object sender, RoutedEventArgs e) => Play(true, EngineMode.MediaFoundation);

    private void Play(bool resume, EngineMode? engine = null)
    {
        if (_item is { } item)
        {
            PlayerLauncher.Play(item, resume, engine);
        }
    }

    private async void Watched_Click(object sender, RoutedEventArgs e)
    {
        if (_item is { } item)
        {
            await WatchState.SetAsync(item, !item.IsWatched);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_item is { } item)
        {
            _details = await DetailsLoader.LoadAsync(item, force: true);
            Show(App.Services.Db.GetItem(item.Id));
        }
    }

    private async void EditMatch_Click(object sender, RoutedEventArgs e)
    {
        if (_item is { } item)
        {
            await MatchDialog.ShowAsync(item, XamlRoot);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_item?.Versions.FirstOrDefault()?.File is { } file && File.Exists(file))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
        }
    }
}
