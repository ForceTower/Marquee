using Marquee.Controls;
using Marquee.Data;
using Marquee.Plex;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Marquee.Pages;

public sealed partial class SourcesPage : Page
{
    private CancellationTokenSource? _signIn;
    private readonly HashSet<string> _expandedServers = [];

    public SourcesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Window.LibraryChanged += Reload;
        App.Services.Plex.Changed += OnPlexChanged;
        Reload();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Window.LibraryChanged -= Reload;
        App.Services.Plex.Changed -= OnPlexChanged;
    }

    private void OnPlexChanged() => DispatcherQueue.TryEnqueue(Reload);

    private void Reload()
    {
        var libraries = App.Services.Db.GetLibraries();
        BuildFolders(libraries.Where(l => !l.IsPlex).ToList());
        BuildPlex(libraries.Where(l => l.IsPlex).ToList());
    }

    private void BuildFolders(List<LibraryInfo> folders)
    {
        FolderList.Children.Clear();
        NoFolders.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var folder in folders)
        {
            var type = new ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (label, value) in new[] { ("Movies", LibraryType.Movies), ("TV Shows", LibraryType.Shows), ("Other videos", LibraryType.Other) })
            {
                type.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            }

            type.SelectedIndex = (int)folder.Type;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(type, "Library type");
            type.SelectionChanged += async (_, _) =>
            {
                if (type.SelectedItem is ComboBoxItem { Tag: LibraryType t } && t != folder.Type)
                {
                    App.Services.Db.SetLibraryType(folder.Id, t);
                    await App.Services.Sync.SyncLibraryAsync(folder with { Type = t });
                }
            };

            var watch = new ToggleSwitch { IsOn = folder.Watch, OnContent = "Watching", OffContent = "Not watching", MinWidth = 0 };
            ToolTipService.SetToolTip(watch, "Rescan automatically when files are added or removed");
            watch.Toggled += (_, _) =>
            {
                App.Services.Db.SetLibraryWatch(folder.Id, watch.IsOn);
                _ = App.Services.Sync.SyncLibraryAsync(folder with { Watch = watch.IsOn });
            };

            var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 16 }, Width = 36, Height = 36, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, $"More options for {folder.Path}");
            var menu = new MenuFlyout();
            var rescan = new MenuFlyoutItem { Text = "Rescan", Icon = new FontIcon { Glyph = "" } };
            rescan.Click += async (_, _) => await App.Services.Sync.SyncLibraryAsync(folder);
            var open = new MenuFlyoutItem { Text = "Open in File Explorer", Icon = new FontIcon { Glyph = "" } };
            open.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"\"{folder.Path}\"");
            var remove = new MenuFlyoutItem { Text = "Remove from library", Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) => App.Services.Db.RemoveLibrary(folder.Id);
            menu.Items.Add(rescan);
            menu.Items.Add(open);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(remove);
            more.Flyout = menu;

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            controls.Children.Add(type);
            controls.Children.Add(watch);
            controls.Children.Add(more);

            string status = folder.Status ?? Format.Join(
                Format.Plural(folder.ItemCount, folder.Type == LibraryType.Shows ? "show" : folder.Type == LibraryType.Movies ? "movie" : "video"),
                folder.Path!.StartsWith(@"\\", StringComparison.Ordinal) ? "network share" : null,
                $"scanned {Format.Ago(folder.ScannedAt)}");
            var card = new SettingsCard { Glyph = "", Header = folder.Path, Description = status, Content = controls };
            FolderList.Children.Add(card);
        }
    }

    private void BuildPlex(List<LibraryInfo> plexLibraries)
    {
        var plex = App.Services.Plex;
        bool signedIn = plex.IsSignedIn;
        SignInCard.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountCard.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        ServerList.Children.Clear();
        if (!signedIn)
        {
            return;
        }

        AccountName.Text = $"Signed in as {plex.UserName}";
        AccountPicture.DisplayName = plex.UserName;
        AccountPicture.ProfilePicture = plex.UserThumb is { } thumb ? new BitmapImage(new Uri(thumb)) : null;
        AccountDetail.Text = Format.Join("Linked with plex.tv", Format.Plural(plex.Servers.Count, "server"));

        foreach (var api in plex.Servers)
        {
            var server = api.Server;
            var libraries = plexLibraries.Where(l => l.ServerId == server.Id).ToList();
            ServerList.Children.Add(BuildServer(server, libraries));
        }
    }

    private Expander BuildServer(ServerInfo server, List<LibraryInfo> libraries)
    {
        string connection = server.Online
            ? Format.Join("Online", server.ConnectionKind switch
            {
                "local" => "local network, direct play",
                "remote" => "remote",
                "relay" => "relay (bandwidth-limited)",
                _ => null,
            }, server.Owned ? null : "shared with you")
            : "Offline";

        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = (Brush)Application.Current.Resources[server.Online ? "SuccessBrush" : "TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        status.Children.Add(dot);
        status.Children.Add(new TextBlock { Text = connection, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = server.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        text.Children.Add(status);

        var header = new Grid { ColumnSpacing = 16, MinHeight = 44 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 22,
            Foreground = (Brush)Application.Current.Resources[server.Online ? "AccentBrush" : "TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        var synced = new TextBlock
        {
            Text = server.Online ? $"Synced {Format.Ago(server.SyncedAt)}" : "",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(synced, 2);
        header.Children.Add(synced);

        var rows = new StackPanel();
        foreach (var library in libraries)
        {
            var toggle = new ToggleSwitch { IsOn = library.Enabled, MinWidth = 0 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"Include {library.Title}");
            toggle.Toggled += async (_, _) =>
            {
                App.Services.Db.SetLibraryEnabled(library.Id, toggle.IsOn);
                if (toggle.IsOn)
                {
                    await App.Services.Sync.SyncLibraryAsync(library with { Enabled = true });
                }
            };
            string count = library.Status ?? (library.ScannedAt is null ? "Not synced yet" : Format.Plural(library.ItemCount, library.Type == LibraryType.Shows ? "show" : "item"));
            rows.Children.Add(new SettingsCard
            {
                IsNested = true,
                Header = library.Title,
                Description = count,
                Content = toggle,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = (Brush)Application.Current.Resources["DividerBrush"],
                CornerRadius = new CornerRadius(0),
            });
        }

        var expander = new Expander
        {
            Header = header,
            Content = rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _expandedServers.Contains(server.Id) || (server.Owned && server.Online && _expandedServers.Count == 0),
            Padding = new Thickness(0),
        };
        expander.Expanding += (_, _) => _expandedServers.Add(server.Id);
        expander.Collapsed += (_, _) => _expandedServers.Remove(server.Id);
        if (expander.IsExpanded)
        {
            _expandedServers.Add(server.Id);
        }

        return expander;
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        _signIn = new CancellationTokenSource();
        SignInButton.Visibility = Visibility.Collapsed;
        SignInWaiting.Visibility = Visibility.Visible;
        try
        {
            await App.Services.Plex.SignInAsync(
                async url => await Windows.System.Launcher.LaunchUriAsync(new Uri(url)), _signIn.Token);
            App.Window.ShowToast("Signed in to Plex. Syncing your libraries…", InfoBarSeverity.Success);
            _ = App.Services.Sync.SyncAllAsync(refreshServers: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Window.ShowToast($"Plex sign-in failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SignInButton.Visibility = Visibility.Visible;
            SignInWaiting.Visibility = Visibility.Collapsed;
            Reload();
        }
    }

    private void CancelSignIn_Click(object sender, RoutedEventArgs e) => _signIn?.Cancel();

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Sign out of Plex?",
            Content = "Your Plex libraries are removed from Marquee. Watch progress stays on your Plex server.",
            PrimaryButtonText = "Sign out",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            App.Services.Plex.SignOut();
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
        if (await picker.PickSingleFolderAsync() is not { } folder)
        {
            return;
        }

        string name = folder.Name.ToLowerInvariant();
        var type = name.Contains("tv") || name.Contains("show") || name.Contains("series") || name.Contains("anime")
            ? LibraryType.Shows
            : LibraryType.Movies;
        long id = App.Services.Db.AddFolder(folder.Path, type);
        if (App.Services.Db.GetLibraries().FirstOrDefault(l => l.Id == id) is { } library)
        {
            await App.Services.Sync.SyncLibraryAsync(library);
        }
    }

    private void TmdbSettings_Click(object sender, RoutedEventArgs e) => App.Window.Navigate("settings");

    private async void ScanAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var folder in App.Services.Db.GetLibraries().Where(l => !l.IsPlex && l.Enabled))
        {
            await App.Services.Sync.SyncLibraryAsync(folder);
        }
    }
}
