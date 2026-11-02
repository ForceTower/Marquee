using Marquee.Data;
using Marquee.Pages;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Marquee.Shell;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _libraryChangedTimer;
    private readonly DispatcherQueueTimer _toastTimer;
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Marquee";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Marquee.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 600;
        }

        PlaceWindow();

        _libraryChangedTimer = DispatcherQueue.CreateTimer();
        _libraryChangedTimer.Interval = TimeSpan.FromMilliseconds(400);
        _libraryChangedTimer.IsRepeating = false;
        _libraryChangedTimer.Tick += (_, _) =>
        {
            LibraryVersion++;
            LibraryChanged?.Invoke();
        };
        App.Services.Db.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            _libraryChangedTimer.Stop();
            _libraryChangedTimer.Start();
        });

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(5);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.IsOpen = false;

        App.Services.Sync.StatusChanged += () => DispatcherQueue.TryEnqueue(UpdateSyncIndicator);
        App.Services.Sync.Error += message => DispatcherQueue.TryEnqueue(() => ShowToast(message, InfoBarSeverity.Warning));

        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => SearchBox.Focus(FocusState.Keyboard));
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => GoBack());
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, () => _ = App.Services.Sync.SyncAllAsync());
        RootGrid.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(RootGrid).Properties.IsXButton1Pressed && GoBack())
            {
                e.Handled = true;
            }
        };

        RootGrid.Loaded += (_, _) =>
        {
            if (ContentFrame.Content is null)
            {
                Navigate("home");
                RunStartupArguments(Environment.GetCommandLineArgs().Skip(1).ToArray());
            }
        };
    }

    /// <summary>
    /// <c>--page home|movies|shows|sources|settings</c>, <c>--open "title"</c> (detail page of the
    /// best search match) and <c>--play "title"</c> (resumes it). For testing without clicking.
    /// </summary>
    private void RunStartupArguments(string[] args)
    {
        Player.PlayerView.StartMuted = args.Contains("--mute");
        args = args.Where(a => a != "--mute").ToArray();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--page":
                    Navigate(args[i + 1]);
                    break;
                case "--open" when App.Services.Db.Search(args[i + 1], 1).FirstOrDefault() is { } item:
                    OpenItem(item);
                    break;
                case "--play" when App.Services.Db.Search(args[i + 1], 1).FirstOrDefault() is { } item:
                    var playable = item.Kind == ItemKind.Show
                        ? App.Services.Db.GetEpisodes(item.Id).FirstOrDefault(e => e.InProgress) ?? App.Services.Db.GetEpisodes(item.Id).FirstOrDefault(e => !e.IsWatched)
                        : item;
                    if (playable is not null)
                    {
                        Player.PlayerLauncher.Play(playable, resume: true);
                    }

                    break;
            }
        }
    }

    /// <summary>Raised (debounced) whenever the library database changes.</summary>
    public event Action? LibraryChanged;

    /// <summary>Bumped on every <see cref="LibraryChanged"/>, so a cached page can tell whether it's stale.</summary>
    public int LibraryVersion { get; private set; }

    public void Navigate(string tag)
    {
        Type page = tag switch
        {
            "movies" or "shows" => typeof(LibraryPage),
            "sources" => typeof(SourcesPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage),
        };
        ContentFrame.Navigate(page, tag, new EntranceNavigationTransitionInfo());
    }

    public void OpenItem(MediaItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Movie:
                ContentFrame.Navigate(typeof(MovieDetailPage), item.Id, new DrillInNavigationTransitionInfo());
                break;
            case ItemKind.Show:
                ContentFrame.Navigate(typeof(ShowDetailPage), item.Id, new DrillInNavigationTransitionInfo());
                break;
            case ItemKind.Season or ItemKind.Episode when item.ShowId is not null:
                ContentFrame.Navigate(typeof(ShowDetailPage), new ShowDetailPage.Args(item.ShowId, item.SeasonNumber), new DrillInNavigationTransitionInfo());
                break;
        }
    }

    public bool GoBack()
    {
        if (PlayerLayer.Visibility == Visibility.Visible || !ContentFrame.CanGoBack)
        {
            return false;
        }

        ContentFrame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
        return true;
    }

    public void ShowToast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>The player covers the whole window, title bar included; the shell is hidden behind it.</summary>
    public void ShowPlayerLayer(UIElement player)
    {
        PlayerLayer.Children.Clear();
        PlayerLayer.Children.Add(player);
        PlayerLayer.Visibility = Visibility.Visible;
        ShellGrid.Visibility = Visibility.Collapsed;
    }

    public void HidePlayerLayer()
    {
        PlayerLayer.Visibility = Visibility.Collapsed;
        PlayerLayer.Children.Clear();
        ShellGrid.Visibility = Visibility.Visible;
        SetTitleBar(AppTitleBar);
    }

    internal Grid PlayerHost => PlayerLayer;

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            if (PlayerLayer.Visibility == Visibility.Visible)
            {
                return;
            }

            action();
            e.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void PlaceWindow()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)(area.Width * 0.85), 2400);
        int height = Math.Min((int)(area.Height * 0.85), 1500);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private void UpdateSyncIndicator()
    {
        var sync = App.Services.Sync;
        SyncIndicator.Visibility = sync.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        SyncText.Text = sync.Status ?? "";
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection || args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        Navigate(tag);
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        AppTitleBar.IsBackButtonEnabled = ContentFrame.CanGoBack;
        string? tag = e.SourcePageType == typeof(LibraryPage) ? e.Parameter as string
            : e.SourcePageType == typeof(HomePage) ? "home"
            : e.SourcePageType == typeof(SourcesPage) ? "sources"
            : e.SourcePageType == typeof(SettingsPage) ? "settings"
            : e.SourcePageType == typeof(MovieDetailPage) ? "movies"
            : e.SourcePageType == typeof(ShowDetailPage) ? "shows"
            : null;

        _syncingSelection = true;
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        _syncingSelection = false;
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args) => GoBack();

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        string text = sender.Text.Trim();
        sender.ItemsSource = text.Length < 2
            ? null
            : App.Services.Db.Search(text, 12).Select(i => new SearchSuggestion(i)).ToList();
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchSuggestion suggestion)
        {
            sender.Text = "";
            OpenItem(suggestion.Item);
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var item = (args.ChosenSuggestion as SearchSuggestion)?.Item
                   ?? App.Services.Db.Search(args.QueryText.Trim(), 1).FirstOrDefault();
        if (item is not null)
        {
            sender.Text = "";
            OpenItem(item);
        }
    }

    private sealed record SearchSuggestion(MediaItem Item)
    {
        public override string ToString() =>
            Item.Year is { } year ? $"{Item.Title} ({year})" : Item.Title;
    }
}
