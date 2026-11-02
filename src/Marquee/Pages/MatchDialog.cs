using Marquee.Data;
using Marquee.Library;
using Marquee.Shell;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Marquee.Pages;

/// <summary>"Edit match…": search TMDB and pick the right movie or show for a local item.</summary>
internal static class MatchDialog
{
    public static async Task ShowAsync(MediaItem item, XamlRoot root)
    {
        if (!Tmdb.IsConfigured)
        {
            var missing = new ContentDialog
            {
                XamlRoot = root,
                Title = "TMDB key needed",
                Content = "Matching local files uses The Movie Database. Add your free TMDB API key in Settings → Library.",
                PrimaryButtonText = "Open Settings",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await missing.ShowAsync() == ContentDialogResult.Primary)
            {
                App.Window.Navigate("settings");
            }

            return;
        }

        string kind = item.Kind == ItemKind.Show ? "show" : "movie";
        var query = new TextBox { Text = item.Title, PlaceholderText = "Title", MinWidth = 300 };
        var year = new NumberBox { Value = item.Year ?? double.NaN, PlaceholderText = "Year", Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var search = new Button { Content = "Search", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var results = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 380 };
        var status = new TextBlock { Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"], FontSize = 12 };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(query);
        bar.Children.Add(year);
        bar.Children.Add(search);
        var content = new StackPanel { Spacing = 12, Width = 560 };
        content.Children.Add(bar);
        content.Children.Add(status);
        content.Children.Add(results);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Match “{item.Title}”",
            Content = content,
            PrimaryButtonText = "Use this match",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        results.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = results.SelectedItem is ListViewItem;

        async Task RunSearch()
        {
            status.Text = "Searching…";
            try
            {
                int? y = double.IsNaN(year.Value) ? null : (int)year.Value;
                var found = await Tmdb.SearchAsync(kind, query.Text.Trim(), y, App.Services.Settings.Current.MetadataLanguage);
                results.Items.Clear();
                foreach (var r in found.Take(20))
                {
                    results.Items.Add(new ListViewItem { Content = Row(r), Tag = r, Padding = new Thickness(8) });
                }

                status.Text = found.Count == 0 ? "No results. Try the original title, or remove the year." : Format.Plural(found.Count, "result");
            }
            catch (Exception ex)
            {
                status.Text = $"Search failed: {ex.Message}";
            }
        }

        search.Click += async (_, _) => await RunSearch();
        query.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await RunSearch();
            }
        };
        dialog.Opened += async (_, _) => await RunSearch();

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && (results.SelectedItem as ListViewItem)?.Tag is TmdbResult chosen)
        {
            try
            {
                await TmdbMatcher.SetMatchAsync(App.Services.Db.GetItem(item.Id) ?? item, chosen);
                App.Window.ShowToast($"Matched to {chosen.Title}{(chosen.Year is { } cy ? $" ({cy})" : "")}.", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                App.Window.ShowToast($"Couldn't load that match: {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private static UIElement Row(TmdbResult r)
    {
        var poster = new Image { Width = 54, Height = 80, Stretch = Stretch.UniformToFill };
        if (r.Poster is { } url)
        {
            poster.Source = new BitmapImage(new Uri(url)) { DecodePixelWidth = 108 };
        }

        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Year is { } y ? $"{r.Title} ({y})" : r.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = r.Overview ?? "",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.WordEllipsis,
        });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = poster, Background = (Brush)Application.Current.Resources["SurfaceRaisedBrush"] });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}
