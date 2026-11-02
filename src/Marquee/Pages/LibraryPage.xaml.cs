using Marquee.Data;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Marquee.Pages;

public sealed partial class LibraryPage : Page
{
    private ItemKind _kind;
    private bool _loading;
    private ItemKind? _loadedKind;
    private int _loadedVersion = -1;

    public LibraryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _kind = e.Parameter as string == "shows" ? ItemKind.Show : ItemKind.Movie;
        App.Window.LibraryChanged += Reload;
        if (_loadedKind == _kind)
        {
            if (_loadedVersion != App.Window.LibraryVersion)
            {
                Reload();
            }

            return;
        }

        Scroller.ChangeView(null, 0, null, true);
        TitleText.Text = _kind == ItemKind.Show ? "TV Shows" : "Movies";
        EmptyIcon.Glyph = _kind == ItemKind.Show ? "" : "";

        _loading = true;
        string sort = App.Services.Db.GetMeta($"sort.{_kind}") ?? "title";
        SortBox.SelectedItem = SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == sort) ?? SortBox.Items[0];
        _loading = false;
        Reload();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Window.LibraryChanged -= Reload;

    private void Reload()
    {
        string sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "title";
        string orderBy = sort switch
        {
            "added" => _kind == ItemKind.Show
                ? "COALESCE((SELECT MAX(e.added_at) FROM items e WHERE e.show_id = i.id), i.added_at) DESC"
                : "i.added_at DESC",
            "year" => "i.year DESC, i.sort_title COLLATE NOCASE",
            "rating" => "i.rating IS NULL, i.rating DESC",
            _ => "i.sort_title COLLATE NOCASE",
        };

        var items = App.Services.Db.GetItems(_kind, orderBy);
        string filter = FilterBar.SelectedItem?.Tag as string ?? "all";
        items = filter switch
        {
            "unwatched" => items.Where(i => !i.IsWatched).ToList(),
            "progress" => items.Where(i => i.InProgress || (i.Kind == ItemKind.Show && i.ViewedLeafCount > 0 && !i.IsWatched)).ToList(),
            _ => items,
        };

        double offset = Scroller.VerticalOffset;
        PosterGrid.ItemsSource = items;
        if (offset > 0)
        {
            Scroller.UpdateLayout();
            Scroller.ChangeView(null, offset, null, true);
        }

        _loadedKind = _kind;
        _loadedVersion = App.Window.LibraryVersion;
        CountText.Text = Format.Plural(items.Count, _kind == ItemKind.Show ? "show" : "movie");
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = filter == "all"
            ? $"No {(_kind == ItemKind.Show ? "TV shows" : "movies")} yet"
            : "Nothing matches this filter";
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SortBox.SelectedItem is not ComboBoxItem { Tag: string sort })
        {
            return;
        }

        App.Services.Db.SetMeta($"sort.{_kind}", sort);
        Reload();
        Scroller.ChangeView(null, 0, null, true);
    }

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_loading && IsLoaded)
        {
            Reload();
        }
    }

    private void SetUp_Click(object sender, RoutedEventArgs e) => App.Window.Navigate("sources");
}
