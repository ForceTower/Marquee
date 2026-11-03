using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Marquee.Data;
using Marquee.Playback.Previews;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Marquee.Player;

/// <summary>
/// The player's overlay controls. One instance sits over Media Foundation's video in the main
/// window; another lives in a transparent window over mpv's (XAML can't draw over mpv's HWND).
/// </summary>
public sealed partial class PlayerControls : UserControl
{
    private const double PreviewWidth = 288;
    private const double MiniPreviewWidth = 176;

    /// <summary>The slider's track starts and ends this far in (the thumb's radius), as the drawn track does.</summary>
    private const double TrackInset = 10;

    private readonly IPlayerActions _actions;
    private readonly SeekPreviews _previews;
    private readonly List<Thumb> _seekThumbs = [];
    private bool _updating;
    private bool _dragging;
    private bool _filling;
    private int _openFlyouts;
    private FrameworkElement? _previewArea;
    private bool _pointerOverSeek;
    private double _previewFraction;
    private double _previewAspect = 16.0 / 9;
    private WriteableBitmap? _previewBitmap;

    internal PlayerControls(PlayerViewModel viewModel, IPlayerActions actions, SeekPreviews previews)
    {
        ViewModel = viewModel;
        _actions = actions;
        _previews = previews;
        InitializeComponent();

        // The mini player's slider gets its template only once it first shows, hence SizeChanged.
        Seek.Loaded += (_, _) => HookSeekThumb(Seek);
        MiniSeek.SizeChanged += (_, _) => HookSeekThumb(MiniSeek);
        HookSeekPreview(SeekArea, Seek);
        HookSeekPreview(MiniSeekArea, MiniSeek);
        ViewModel.PropertyChanged += OnViewModelChanged;
        _previews.Ready += OnPreviewReady;
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            _previews.Ready -= OnPreviewReady;
        };
        Sync();
    }

    internal PlayerViewModel ViewModel { get; }

    /// <summary>
    /// True while the user is using the controls (dragging, skimming the seek bar, in a menu), so they
    /// shouldn't auto-hide. Skimming doesn't count in the mini player, whose controls hide when the
    /// pointer leaves it: that's polled, because the pointer's exit can be lost between its windows.
    /// </summary>
    public bool IsBusy => _dragging || _openFlyouts > 0 || PreviewCard.Visibility == Visibility.Visible && !ViewModel.IsCompact;

    /// <summary>The bottom bar's controls in DIPs, from the time row down (not the gradient fading in above it).</summary>
    public double BottomControlsHeight => ViewModel.IsCompact ? MiniBottom.ActualHeight : BottomBar.ActualHeight - BottomBar.Padding.Top;

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
                HidePreview();
                UpdateCompact();
                break;
            case nameof(PlayerViewModel.ControlsVisible) when !ViewModel.ControlsVisible:
                HidePreview();
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
        foreach (var slider in new[] { Seek, MiniSeek })
        {
            slider.Maximum = Math.Max(1, ViewModel.Duration);
            slider.Value = Math.Clamp(ViewModel.Position, 0, slider.Maximum);
        }

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

    /// <summary>The mini player has its own controls (<c>MiniChrome</c>); the shared overlays move in below its top buttons.</summary>
    private void UpdateCompact()
    {
        bool compact = ViewModel.IsCompact;
        MessageCard.Margin = compact ? new Thickness(12, 48, 12, 0) : new Thickness(0, 80, 0, 0);
        MessageCard.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(16, 10, 16, 10);
        MessageText.FontSize = compact ? 12 : 13;
        StatisticsBox.Margin = compact ? new Thickness(8, 48, 8, 0) : new Thickness(24, 96, 0, 0);
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
    private void HookSeekThumb(Slider slider)
    {
        if (FindChild<Thumb>(slider, "HorizontalThumb") is not { } thumb || _seekThumbs.Contains(thumb))
        {
            return;
        }

        _seekThumbs.Add(thumb);
        thumb.DragStarted += (_, _) => _dragging = true;
        thumb.DragCompleted += (_, _) =>
        {
            _dragging = false;
            _actions.SeekTo(slider.Value, final: true);
            if (!_pointerOverSeek)
            {
                HidePreview();
            }
        };
    }

    /// <summary>
    /// The preview follows the pointer along a seek bar, and the thumb while it's dragged (the
    /// pointer can stray off the bar then). The slider marks pointer events handled, hence
    /// handledEventsToo.
    /// </summary>
    private void HookSeekPreview(FrameworkElement area, Slider slider)
    {
        void Track(PointerRoutedEventArgs e)
        {
            _pointerOverSeek = true;
            double trackWidth = Math.Max(1, area.ActualWidth - 2 * TrackInset);
            double fraction = _dragging
                ? slider.Value / Math.Max(1, slider.Maximum)
                : (e.GetCurrentPoint(area).Position.X - TrackInset) / trackWidth;
            ShowPreview(area, Math.Clamp(fraction, 0, 1));
        }

        area.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, e) => Track(e)), true);
        area.AddHandler(PointerMovedEvent, new PointerEventHandler((_, e) => Track(e)), true);
        area.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) =>
        {
            _pointerOverSeek = false;
            if (!_dragging)
            {
                HidePreview();
            }
        }), true);
    }

    private void ShowPreview(FrameworkElement area, double fraction)
    {
        if (!ViewModel.ControlsVisible)
        {
            HidePreview();
            return;
        }

        _previewArea = area;
        _previewFraction = fraction;
        var position = TimeSpan.FromSeconds(fraction * ViewModel.Duration);
        PreviewTime.Text = Format.Clock(position);
        string? chapter = ChapterAt(position);
        PreviewChapter.Text = chapter ?? "";
        PreviewChapter.Visibility = chapter is null ? Visibility.Collapsed : Visibility.Visible;

        double width = area == MiniSeekArea ? MiniPreviewWidth : PreviewWidth;
        bool pictures = !_previews.Unavailable;
        PreviewBox.Visibility = pictures ? Visibility.Visible : Visibility.Collapsed;
        PreviewBox.Width = width;
        PreviewBox.Height = Math.Round(width / _previewAspect);
        PreviewChapter.MaxWidth = width - 20;
        PreviewCard.Visibility = Visibility.Visible;
        PlacePreview();
        if (pictures)
        {
            _previews.Request(position, (int)Math.Round(width * (XamlRoot?.RasterizationScale ?? 1)));
        }
    }

    /// <summary>Centres the card over its point on the bar, kept inside the controls, just above the bar.</summary>
    private void PlacePreview()
    {
        if (_previewArea is not { } area || PreviewCard.Visibility != Visibility.Visible)
        {
            return;
        }

        const double Margin = 8;
        double trackWidth = Math.Max(0, area.ActualWidth - 2 * TrackInset);
        Point anchor = area.TransformToVisual(Root).TransformPoint(new Point(TrackInset + _previewFraction * trackWidth, 0));
        double width = PreviewCard.ActualWidth > 0 ? PreviewCard.ActualWidth : PreviewBox.Width;
        double height = PreviewCard.ActualHeight > 0 ? PreviewCard.ActualHeight : PreviewBox.Height + 40;
        Canvas.SetLeft(PreviewCard, Math.Clamp(anchor.X - width / 2, Margin, Math.Max(Margin, Root.ActualWidth - width - Margin)));
        Canvas.SetTop(PreviewCard, Math.Max(Margin, anchor.Y - height - 2));
    }

    private void PreviewCard_SizeChanged(object sender, SizeChangedEventArgs e) => PlacePreview();

    private void HidePreview()
    {
        PreviewCard.Visibility = Visibility.Collapsed;
        _previewArea = null;
    }

    /// <summary>Named as the bar's chapter line names them; nothing for a file without chapters.</summary>
    private string? ChapterAt(TimeSpan position)
    {
        var chapters = ViewModel.Chapters;
        if (chapters.Count < 2)
        {
            return null;
        }

        int index = chapters.Count(c => c.StartMs <= position.TotalMilliseconds) - 1;
        return index < 0 ? null : chapters[index].Title ?? $"Chapter {index + 1}";
    }

    private void OnPreviewReady(PreviewFrame frame)
    {
        if (PreviewCard.Visibility != Visibility.Visible)
        {
            return;
        }

        double aspect = (double)frame.Width / frame.Height;
        if (Math.Abs(aspect - _previewAspect) > 0.01)
        {
            _previewAspect = aspect;
            PreviewBox.Height = Math.Round(PreviewBox.Width / aspect);
        }

        if (_previewBitmap is null || _previewBitmap.PixelWidth != frame.Width || _previewBitmap.PixelHeight != frame.Height)
        {
            _previewBitmap = new WriteableBitmap(frame.Width, frame.Height);
            PreviewImage.Source = _previewBitmap;
        }

        using (var stream = _previewBitmap.PixelBuffer.AsStream())
        {
            stream.Write(frame.Pixels);
        }

        _previewBitmap.Invalidate();
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
        HidePreview();
        _previewAspect = 16.0 / 9;
        _previewBitmap = null;
        PreviewImage.Source = null;
        Sync();
    }

    private void Seek_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _actions.SeekTo(e.NewValue, final: !_dragging);
        if (_dragging && sender is Slider slider)
        {
            ShowPreview(slider == MiniSeek ? MiniSeekArea : SeekArea, e.NewValue / Math.Max(1, slider.Maximum));
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
        // The mini player spots its own double-clicks, since a press there can land in another window.
        if (!ViewModel.IsCompact && e.OriginalSource is FrameworkElement { Name: "Root" or "Chrome" or "TopBar" or "BottomBar" })
        {
            _actions.ToggleFullScreen();
        }
    }

    /// <summary>Buttons and sliders handle their own presses, so this is a press on the picture.</summary>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsCompact && e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
        {
            _actions.BeginWindowDrag();
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
}
