using Marquee.Controls;
using Marquee.Data;
using Marquee.Library;
using Marquee.Plex;
using Marquee.Shell;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Marquee.Pages;

/// <summary>Pieces shared by the movie and show detail pages.</summary>
internal static class DetailParts
{
    private static Style S(string key) => (Style)Application.Current.Resources[key];

    private static Brush B(string key) => (Brush)Application.Current.Resources[key];

    public static void FillMeta(StackPanel row, IEnumerable<string?> parts, double? rating)
    {
        row.Children.Clear();
        bool first = true;
        foreach (string? part in parts.Where(p => !string.IsNullOrEmpty(p)))
        {
            if (!first)
            {
                row.Children.Add(new TextBlock { Text = "·", Style = S("MetaTextStyle") });
            }

            row.Children.Add(new TextBlock { Text = part, Style = S("MetaTextStyle") });
            first = false;
        }

        if (rating is > 0)
        {
            if (!first)
            {
                row.Children.Add(new TextBlock { Text = "·", Style = S("MetaTextStyle") });
            }

            var star = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            star.Children.Add(new FontIcon { Glyph = "", FontSize = 13, Foreground = B("AccentBrush") });
            star.Children.Add(new TextBlock { Text = rating.Value.ToString("0.0"), FontSize = 13, FontWeight = FontWeights.SemiBold });
            row.Children.Add(star);
        }
    }

    public static void FillBadges(StackPanel row, IEnumerable<string> badges)
    {
        row.Children.Clear();
        foreach (string badge in badges)
        {
            row.Children.Add(Badges.Create(badge));
        }

        row.Visibility = row.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static List<UIElement> CastItems(IEnumerable<Person> people) =>
        people.Take(30).Select(p =>
        {
            var picture = new PersonPicture { Width = 72, Height = 72, DisplayName = p.Name };
            if (p.Photo is { } photo)
            {
                _ = LoadPhotoAsync(picture, photo);
            }

            var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(picture);
            panel.Children.Add(new TextBlock { Text = p.Name, FontSize = 13, FontWeight = FontWeights.Medium, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
            if (p.Role is { Length: > 0 } role)
            {
                panel.Children.Add(new TextBlock { Text = role, FontSize = 12, Foreground = B("TextTertiaryBrush"), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
            }

            return (UIElement)panel;
        }).ToList();

    private static async Task LoadPhotoAsync(PersonPicture picture, string reference)
    {
        if (await App.Services.Images.GetAsync(reference, 144, 144) is { } path)
        {
            picture.ProfilePicture = new BitmapImage(new Uri(path));
        }
    }

    public static void FillDetails(Grid grid, IEnumerable<(string Label, string? Value)> rows)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        int r = 0;
        foreach (var (label, value) in rows.Where(x => !string.IsNullOrEmpty(x.Value)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, FontSize = 13, Foreground = B("TextTertiaryBrush") };
            var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(l, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            r++;
        }
    }

    /// <summary>The versions list: one row per file, the chosen one marked with the accent dot.</summary>
    public static void FillVersions(StackPanel list, MediaItem item, ItemDetails? details, TrackPreferences prefs, Action<MediaVersion> choose)
    {
        list.Children.Clear();
        var chosen = prefs.PickVersion(item);
        var library = App.Services.Db.GetLibraries().FirstOrDefault(l => l.Id == item.LibraryId);
        string source = library?.IsPlex == true ? $"Plex · {App.Services.Plex.Server(library.ServerId)?.Server.Name}" : "This PC";

        for (int i = 0; i < item.Versions.Count; i++)
        {
            var version = item.Versions[i];
            var streams = details?.Versions.FirstOrDefault(v => v.Location == version.Location)?.Streams ?? [];
            bool selected = version == chosen;

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = selected ? B("AccentBrush") : null,
                Stroke = selected ? null : B("TextMutedBrush"),
                StrokeThickness = 1.5,
            };

            string file = version.File is { } f ? Path.GetFileName(f.Replace('/', '\\')) : item.Title;
            string where = Format.Join(source, version.File is { } path && !item.IsPlex ? Path.GetDirectoryName(path) : null, Format.Size(version.Size));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = file, FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = where, FontSize = 12, Foreground = B("TextTertiaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });

            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var video = streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
            var audio = streams.Where(s => s.Kind == StreamKind.Audio).ToList();
            string?[] specs =
            [
                version.Width is { } w && version.Height is { } h ? $"{w}×{h}" : null,
                MediaLabels.Codec(version.VideoCodec) is { Length: > 0 } vc ? video?.Display?.Contains("10") == true || video?.Profile?.Contains("10") == true ? $"{vc} 10-bit" : vc : null,
                video?.DoviProfile is { } dv ? $"DV P{dv}" : null,
                audio.FirstOrDefault(a => a.IsAtmos) is { } atmos ? $"{MediaLabels.Codec(atmos.Codec, atmos.Profile)} Atmos"
                    : MediaLabels.Codec(version.AudioCodec) is { Length: > 0 } ac ? ac + MediaLabels.Channels(version.AudioChannels) : null,
                version.Container?.ToUpperInvariant(),
            ];
            foreach (string? spec in specs.Where(s => !string.IsNullOrEmpty(s)))
            {
                chips.Children.Add(Badges.Chip(spec!));
            }

            var row = new Grid { ColumnSpacing = 12, MinHeight = 56, Padding = new Thickness(16, 10, 16, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(dot);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            Grid.SetColumn(chips, 2);
            row.Children.Add(chips);

            var button = new Button
            {
                Content = row,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0, 0, 0, i < item.Versions.Count - 1 ? 1 : 0),
                BorderBrush = B("DividerBrush"),
                CornerRadius = new CornerRadius(0),
            };
            ToolTipService.SetToolTip(button, version.File);
            button.Click += (_, _) => choose(version);
            list.Children.Add(button);
        }
    }

    /// <summary>Fills the per-title audio and subtitle pickers from the chosen version's streams.</summary>
    public static void FillTrackBoxes(ComboBox audioBox, ComboBox subtitleBox, IReadOnlyList<StreamInfo> streams, TrackPreferences prefs)
    {
        audioBox.Items.Clear();
        subtitleBox.Items.Clear();
        audioBox.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = null });
        foreach (var audio in streams.Where(s => s.Kind == StreamKind.Audio))
        {
            audioBox.Items.Add(new ComboBoxItem { Content = TrackLabel(audio), Tag = audio.Ordinal });
        }

        subtitleBox.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = null });
        subtitleBox.Items.Add(new ComboBoxItem { Content = "Off", Tag = TrackPreferences.SubtitlesOff });
        foreach (var sub in streams.Where(s => s.Kind == StreamKind.Subtitle && s.Ordinal >= 0))
        {
            subtitleBox.Items.Add(new ComboBoxItem { Content = TrackLabel(sub), Tag = sub.Ordinal });
        }

        audioBox.SelectedItem = audioBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, prefs.Audio)) ?? audioBox.Items[0];
        subtitleBox.SelectedItem = subtitleBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, prefs.Subtitle)) ?? subtitleBox.Items[0];
        audioBox.IsEnabled = audioBox.Items.Count > 1;
        subtitleBox.IsEnabled = true;
    }

    public static string TrackLabel(StreamInfo s)
    {
        if (s.Kind == StreamKind.Audio)
        {
            string codec = MediaLabels.Codec(s.Codec, s.Profile) + MediaLabels.Channels(s.Channels) + (s.IsAtmos ? " Atmos" : "");
            return Format.Join(s.Language ?? "Unknown", codec, s.Title is { Length: < 40 } t && !t.Contains('.') ? t : null);
        }

        string label = Format.Join(s.Language ?? "Unknown", s.Forced ? "Forced" : null,
            s.Title is { Length: < 40 } title && !title.Contains('.') ? title : null);
        return $"{label} ({s.Codec?.ToUpperInvariant()})";
    }
}
