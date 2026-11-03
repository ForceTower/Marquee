using System.ComponentModel;
using Marquee.Data;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Marquee.Player;

/// <summary>
/// The player's overlay controls. One instance sits over Media Foundation's video in the main
/// window; another lives in a transparent window over mpv's (XAML can't draw over mpv's HWND).
/// </summary>
public sealed partial class PlayerControls : UserControl
{
    private readonly IPlayerActions _actions;
    private bool _updating;
    private bool _dragging;
    private bool _filling;
    private int _openFlyouts;

    internal PlayerControls(PlayerViewModel viewModel, IPlayerActions actions)
    {
        ViewModel = viewModel;
        _actions = actions;
        InitializeComponent();
        Seek.ThumbToolTipValueConverter = new ClockConverter();
        Seek.Loaded += (_, _) => HookSeekThumb();
        ViewModel.PropertyChanged += OnViewModelChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
        Sync();
    }

    internal PlayerViewModel ViewModel { get; }

    /// <summary>True while the user is using the controls, so they shouldn't auto-hide.</summary>
    public bool IsBusy => _dragging || _openFlyouts > 0;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Position) or nameof(PlayerViewModel.Duration):
                UpdateSeek();
                break;
            case nameof(PlayerViewModel.Buffered):
                UpdateBuffered();
                break;
            case nameof(PlayerViewModel.Chapters):
                UpdateTicks();
                break;
            case nameof(PlayerViewModel.Volume):
                _updating = true;
                VolumeSlider.Value = ViewModel.Volume;
                _updating = false;
                break;
            case nameof(PlayerViewModel.Badges) or nameof(PlayerViewModel.EngineText):
                UpdateBadges();
                break;
            case nameof(PlayerViewModel.IsCompact):
                UpdateCompact();
                break;
        }
    }

    private void Sync()
    {
        UpdateSeek();
        UpdateBuffered();
        UpdateTicks();
        UpdateBadges();
        UpdateCompact();
        _updating = true;
        VolumeSlider.Value = ViewModel.Volume;
        _updating = false;
    }

    private void UpdateSeek()
    {
        if (_dragging)
        {
            return;
        }

        _updating = true;
        Seek.Maximum = Math.Max(1, ViewModel.Duration);
        Seek.Value = Math.Clamp(ViewModel.Position, 0, Seek.Maximum);
        _updating = false;
        UpdateBuffered();
    }

    private void UpdateBuffered()
    {
        double width = Math.Max(0, SeekArea.ActualWidth - 20);
        double end = Math.Clamp((ViewModel.Position + ViewModel.Buffered) / Math.Max(1, ViewModel.Duration), 0, 1);
        BufferedBar.Width = ViewModel.Buffered > 0 ? width * end : 0;
    }

    private void UpdateTicks()
    {
        ChapterTicks.Children.Clear();
        double width = Math.Max(0, SeekArea.ActualWidth - 20);
        double duration = Math.Max(1, ViewModel.Duration);
        foreach (var chapter in ViewModel.Chapters.Skip(1))
        {
            double x = chapter.StartMs / 1000.0 / duration * width;
            var tick = new Rectangle { Width = 2, Height = 4, Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(150, 0, 0, 0)) };
            Canvas.SetLeft(tick, x - 1);
            Canvas.SetTop(tick, 14);
            ChapterTicks.Children.Add(tick);
        }
    }

    private void UpdateBadges()
    {
        BadgePanel.Children.Clear();
        foreach (string badge in ViewModel.Badges)
        {
            BadgePanel.Children.Add(Pill(badge, null));
        }

        if (ViewModel.EngineText.Length > 0)
        {
            BadgePanel.Children.Add(Pill(ViewModel.EngineText, (Brush)Application.Current.Resources["SuccessBrush"]));
        }
    }

    private static Border Pill(string text, Brush? dot)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (dot is not null)
        {
            content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        }

        content.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        return new Border
        {
            Child = content,
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(115, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(46, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
    }

    private void UpdateCompact()
    {
        bool compact = ViewModel.IsCompact;
        var collapsedInCompact = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumeGroup.Visibility = collapsedInCompact;
        SettingsButton.Visibility = collapsedInCompact;
        FullScreenButton.Visibility = collapsedInCompact;
        ChapterText.Visibility = collapsedInCompact;
        BadgePanel.Visibility = collapsedInCompact;
        TracksButton.Visibility = collapsedInCompact;
        if (compact)
        {
            ChaptersButton.Visibility = Visibility.Collapsed;
        }

        BottomBar.Padding = compact ? new Thickness(12, 24, 12, 8) : new Thickness(24, 64, 24, 20);
        TopBar.Padding = compact ? new Thickness(8, 8, 8, 24) : new Thickness(20, 16, 24, 56);
        PlayButton.Width = PlayButton.Height = compact ? 40 : 52;
        PlayButton.CornerRadius = new CornerRadius(compact ? 20 : 26);
        TimeRow.Visibility = collapsedInCompact;
    }

    private void SeekArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateBuffered();
        UpdateTicks();
    }

    /// <summary>
    /// Dragging is tracked with the slider's own thumb: pointer press/release pairs can lose the
    /// release (to the overlay window, a double-click), which left the bar frozen.
    /// </summary>
    private void HookSeekThumb()
    {
        if (FindChild<Thumb>(Seek, "HorizontalThumb") is not { } thumb)
        {
            return;
        }

        thumb.DragStarted += (_, _) => _dragging = true;
        thumb.DragCompleted += (_, _) =>
        {
            _dragging = false;
            _actions.SeekTo(Seek.Value, final: true);
        };
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            if (FindChild<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>A new title is starting: forget any drag in progress and follow the new position.</summary>
    public void Reset()
    {
        _dragging = false;
        Sync();
    }

    private void Seek_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updating)
        {
            _actions.SeekTo(e.NewValue, final: !_dragging);
        }
    }

    private void Volume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updating)
        {
            _actions.SetVolume(e.NewValue);
        }
    }

    private void TracksFlyout_Opened(object sender, object e)
    {
        Flyout_Opened(sender, e);
        _filling = true;
        AudioList.ItemsSource = ViewModel.AudioTracks;
        AudioList.SelectedIndex = ViewModel.AudioTracks.ToList().FindIndex(t => t.Ordinal == ViewModel.SelectedAudio);
        var subtitles = new List<TrackOption> { new(-1, "Off", null, null) };
        subtitles.AddRange(ViewModel.SubtitleTracks);
        SubtitleList.ItemsSource = subtitles;
        SubtitleList.SelectedIndex = ViewModel.SelectedSubtitle is int s ? subtitles.FindIndex(t => t.Ordinal == s) : 0;
        SubtitleDelayBox.Value = ViewModel.SubtitleDelay;
        _filling = false;
    }

    private void SettingsFlyout_Opened(object sender, object e)
    {
        Flyout_Opened(sender, e);
        _filling = true;
        SpeedBox.SelectedItem = SpeedBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => double.Parse((string)i.Tag, System.Globalization.CultureInfo.InvariantCulture) == ViewModel.Speed);
        AudioDelayBox.Value = ViewModel.AudioDelayMs;
        _filling = false;
    }

    private void Flyout_Opened(object sender, object e) => _openFlyouts++;

    private void Flyout_Closed(object sender, object e)
    {
        _openFlyouts = Math.Max(0, _openFlyouts - 1);
        _actions.PointerActivity();
    }

    private void AudioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && AudioList.SelectedItem is TrackOption track && track.Ordinal != ViewModel.SelectedAudio)
        {
            _actions.SelectAudio(track.Ordinal);
        }
    }

    private void SubtitleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && SubtitleList.SelectedItem is TrackOption track)
        {
            _actions.SelectSubtitle(track.Ordinal < 0 ? null : track.Ordinal);
        }
    }

    private void SubtitleDelay_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_filling && !double.IsNaN(args.NewValue))
        {
            _actions.SetSubtitleDelay(args.NewValue);
        }
    }

    private void AudioDelay_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_filling && !double.IsNaN(args.NewValue))
        {
            _actions.SetAudioDelay((int)args.NewValue);
        }
    }

    private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && SpeedBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _actions.SetSpeed(double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void ChapterList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Chapter chapter)
        {
            _actions.JumpToChapter(chapter);
        }
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { Name: "Root" or "Chrome" or "TopBar" or "BottomBar" })
        {
            _actions.ToggleFullScreen();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _actions.Close();

    private void Play_Click(object sender, RoutedEventArgs e) => _actions.TogglePlay();

    private void Back10_Click(object sender, RoutedEventArgs e) => _actions.SkipBy(-10);

    private void Forward30_Click(object sender, RoutedEventArgs e) => _actions.SkipBy(30);

    private void Mute_Click(object sender, RoutedEventArgs e) => _actions.ToggleMute();

    private void Compact_Click(object sender, RoutedEventArgs e) => _actions.ToggleCompact();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => _actions.ToggleFullScreen();

    private void Skip_Click(object sender, RoutedEventArgs e) => _actions.Skip();

    private void UpNextPlay_Click(object sender, RoutedEventArgs e) => _actions.PlayUpNext();

    private void UpNextCancel_Click(object sender, RoutedEventArgs e) => _actions.CancelUpNext();

    private void LoadSubtitle_Click(object sender, RoutedEventArgs e) => _actions.LoadSubtitleFile();

    private void SwitchEngine_Click(object sender, RoutedEventArgs e) => _actions.SwitchEngine();

    private void Statistics_Click(object sender, RoutedEventArgs e) => _actions.ToggleStatistics();

    private sealed class ClockConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            Format.Clock(TimeSpan.FromSeconds(value is double d ? d : 0));

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }
}
