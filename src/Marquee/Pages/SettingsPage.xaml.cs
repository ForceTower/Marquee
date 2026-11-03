using System.Globalization;
using Marquee.Controls;
using Marquee.Data;
using Marquee.Library;
using Marquee.Playback.Interop;
using Marquee.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Devices.Enumeration;
using Windows.Management.Deployment;
using Windows.Media.Devices;

namespace Marquee.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private static AppSettings S => App.Services.Settings.Current;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        Select(EngineBox, S.Engine.ToString());
        DolbyVisionSwitch.IsOn = S.DolbyVision;
        Select(HwdecBox, S.HardwareDecoding);
        Select(ToneMapBox, S.ToneMapping);
        MpvConfigSwitch.IsOn = S.LoadMpvConfig;
        Select(DynamicRangeBox, S.MatchDynamicRange.ToString());
        FrameRateSwitch.IsOn = S.MatchFrameRate;
        ObjectsSwitch.IsOn = S.TrueHdAtmosObjects;
        ExclusiveSwitch.IsOn = S.ExclusiveAudio;
        LipSyncBox.Value = S.AtmosLipSyncMs;
        Select(AutoNextBox, S.AutoPlayNextSeconds.ToString(CultureInfo.InvariantCulture));
        Select(ResumeBox, S.Resume.ToString());
        CompactSwitch.IsOn = S.CompactOnFocusLoss;
        Select(SubtitleModeBox, S.Subtitles.ToString());
        FillLanguages(SubtitleLanguageBox, "Same as the audio", S.PreferredSubtitleLanguage);
        FillLanguages(AudioLanguageBox, "Original (the file's default)", S.PreferredAudioLanguage);
        FillLanguages(MetadataLanguageBox, null, S.MetadataLanguage, regional: true);
        TmdbKeyBox.Password = Tmdb.ApiKey ?? "";
        BuildAccents();
        BuildSubtitleColors();
        BuildShortcuts();
        UpdateLibraryInfo();
        VersionText.Text = $"Version {Plex.PlexClientIdentity.Version}";
        _loading = false;

        await FillDevicesAsync();
        await FillComponentsAsync();
        await ShowNvidiaOverlayAsync();
    }

    private async Task ShowNvidiaOverlayAsync()
    {
        bool? hookDisabled = await Task.Run(Player.NvidiaOverlay.IsHookDisabled);
        NvidiaOverlayCard.Visibility = hookDisabled is null ? Visibility.Collapsed : Visibility.Visible;
        NvidiaOverlayButton.Visibility = hookDisabled == true ? Visibility.Collapsed : Visibility.Visible;
        NvidiaOverlayDone.Visibility = hookDisabled == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NvidiaOverlay_Click(object sender, RoutedEventArgs e)
    {
        NvidiaOverlayButton.IsEnabled = false;
        await Player.NvidiaOverlay.DisableHookElevatedAsync();
        await ShowNvidiaOverlayAsync();
        NvidiaOverlayButton.IsEnabled = true;
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items.FirstOrDefault();

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Services.Settings.Update(s =>
        {
            s.Engine = Enum.Parse<EngineMode>(TagOf(EngineBox) ?? nameof(EngineMode.Automatic));
            s.DolbyVision = DolbyVisionSwitch.IsOn;
            s.HardwareDecoding = TagOf(HwdecBox) ?? "auto-safe";
            s.ToneMapping = TagOf(ToneMapBox) ?? "auto";
            s.LoadMpvConfig = MpvConfigSwitch.IsOn;
            s.MatchDynamicRange = Enum.Parse<DynamicRangeMatching>(TagOf(DynamicRangeBox) ?? nameof(DynamicRangeMatching.HdrVideo));
            s.MatchFrameRate = FrameRateSwitch.IsOn;
            s.AudioDevice = TagOf(DeviceBox) is { Length: > 0 } device ? device : null;
            s.TrueHdAtmosObjects = ObjectsSwitch.IsOn;
            s.ExclusiveAudio = ExclusiveSwitch.IsOn;
            s.AutoPlayNextSeconds = int.Parse(TagOf(AutoNextBox) ?? "10", CultureInfo.InvariantCulture);
            s.Resume = Enum.Parse<ResumeMode>(TagOf(ResumeBox) ?? nameof(ResumeMode.Always));
            s.CompactOnFocusLoss = CompactSwitch.IsOn;
            s.Subtitles = Enum.Parse<SubtitleMode>(TagOf(SubtitleModeBox) ?? nameof(SubtitleMode.ForcedOnly));
            s.PreferredSubtitleLanguage = TagOf(SubtitleLanguageBox) ?? "";
            s.PreferredAudioLanguage = TagOf(AudioLanguageBox) ?? "";
            s.MetadataLanguage = TagOf(MetadataLanguageBox) ?? "en";
        });
    }

    private void Setting_Changed(object sender, SelectionChangedEventArgs e) => Setting_Changed(sender, (RoutedEventArgs)e);

    private void LipSync_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loading && !double.IsNaN(args.NewValue))
        {
            App.Services.Settings.Update(s => s.AtmosLipSyncMs = (int)args.NewValue);
        }
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        string tab = sender.SelectedItem?.Tag as string ?? "playback";
        AppearancePanel.Visibility = tab == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PlaybackPanel.Visibility = tab == "playback" ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesPanel.Visibility = tab == "subtitles" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = tab == "library" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = tab == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tab == "about" ? Visibility.Visible : Visibility.Collapsed;
        Crumb.Text = SectionTitle.Text = sender.SelectedItem?.Text ?? "";
    }

    // ---- appearance ----

    private void BuildAccents()
    {
        AccentSwatches.Children.Clear();
        foreach (string accent in Theme.Accents)
        {
            bool selected = string.Equals(accent, S.Accent, StringComparison.OrdinalIgnoreCase);
            var swatch = new Button
            {
                Style = (Style)Application.Current.Resources["AccentSwatchStyle"],
                Background = new SolidColorBrush(Theme.Parse(accent)),
                BorderThickness = new Thickness(selected ? 3 : 0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"Accent {accent}");
            swatch.Click += (_, _) =>
            {
                App.Services.Settings.Update(s => s.Accent = accent);
                Theme.ApplyAccent(accent);
                if (App.Window.Content is FrameworkElement root)
                {
                    // Theme resources re-resolve on a theme change, so flip it to pick up the new accent.
                    root.RequestedTheme = ElementTheme.Light;
                    root.RequestedTheme = ElementTheme.Dark;
                }

                BuildAccents();
            };
            AccentSwatches.Children.Add(swatch);
        }
    }

    // ---- subtitle style ----

    private static readonly string[] SubtitleColors = ["#FFFFFF", "#D6D6D6", "#FFE45C", "#F5B544", "#7FE0F0", "#9BE58F"];
    private static readonly string[] OutlineColors = ["#000000", "#3A3A3A", "#6E6E6E", "#1B2A44"];

    private void BuildSubtitleColors()
    {
        BuildColorRow(SubtitleColorSwatches, SubtitleColors, S.SubtitleColor, "Text", color => App.Services.Settings.Update(s => s.SubtitleColor = color));
        BuildColorRow(SubtitleOutlineSwatches, OutlineColors, S.SubtitleOutlineColor, "Outline", color => App.Services.Settings.Update(s => s.SubtitleOutlineColor = color));
    }

    /// <summary>
    /// Preset swatches, the chosen one ringed outside (a ring inside wouldn't show on white), then
    /// "Custom…" with a color picker, which shows the color when it's the one in use.
    /// </summary>
    private void BuildColorRow(StackPanel row, string[] presets, string current, string name, Action<string> save)
    {
        void Choose(string color)
        {
            save(color);
            BuildSubtitleColors();
        }

        row.Children.Clear();
        foreach (string color in presets)
        {
            var swatch = ColorSwatch(color);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"{name} color {color}");
            swatch.Click += (_, _) => Choose(color);
            var cell = new Grid();
            if (string.Equals(color, current, StringComparison.OrdinalIgnoreCase))
            {
                cell.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Stroke = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                    StrokeThickness = 2,
                    Margin = new Thickness(-5),
                    IsHitTestVisible = false,
                });
            }

            cell.Children.Add(swatch);
            row.Children.Add(cell);
        }

        var picker = new ColorPicker
        {
            Color = Theme.Parse(current),
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
        };
        var flyout = new Flyout { Content = picker };
        flyout.Closed += (_, _) =>
        {
            string picked = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
            if (!string.Equals(picked, current, StringComparison.OrdinalIgnoreCase))
            {
                Choose(picked);
            }
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (!presets.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            content.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Theme.Parse(current)),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 255, 255, 255)),
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        content.Children.Add(new TextBlock { Text = "Custom…", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new Button { Content = content, Flyout = flyout, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
    }

    /// <summary>A round swatch with a hairline edge, so black and navy stay visible on the dark page.</summary>
    private static Button ColorSwatch(string color) => new()
    {
        Style = (Style)Application.Current.Resources["AccentSwatchStyle"],
        Background = new SolidColorBrush(Theme.Parse(color)),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 255, 255, 255)),
        BorderThickness = new Thickness(1),
    };

    // ---- languages and devices ----

    private static readonly string[] RegionalLanguages = ["en-US", "en-GB", "pt-BR", "pt-PT", "es-ES", "es-MX", "fr-FR", "de-DE", "it-IT", "ja-JP", "ko-KR", "zh-CN", "zh-TW", "nl-NL", "pl-PL", "ru-RU", "sv-SE", "tr-TR"];

    private static void FillLanguages(ComboBox box, string? emptyLabel, string selected, bool regional = false)
    {
        box.Items.Clear();
        if (emptyLabel is not null)
        {
            box.Items.Add(new ComboBoxItem { Content = emptyLabel, Tag = "" });
        }

        var cultures = regional
            ? RegionalLanguages.Select(CultureInfo.GetCultureInfo)
            : CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .Where(c => c.TwoLetterISOLanguageName.Length == 2 && c.Name == c.TwoLetterISOLanguageName)
                .DistinctBy(c => c.TwoLetterISOLanguageName);
        foreach (var culture in cultures.OrderBy(c => c.NativeName, StringComparer.CurrentCultureIgnoreCase))
        {
            string name = char.ToUpper(culture.NativeName[0]) + culture.NativeName[1..];
            box.Items.Add(new ComboBoxItem { Content = name, Tag = regional ? culture.Name : culture.TwoLetterISOLanguageName });
        }

        Select(box, selected);
    }

    private async Task FillDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        _loading = true;
        DeviceBox.Items.Clear();
        DeviceBox.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "" });
        foreach (var device in devices.OrderBy(d => d.Name))
        {
            DeviceBox.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Name });
        }

        Select(DeviceBox, S.AudioDevice ?? "");
        _loading = false;
    }

    // ---- library ----

    private void UpdateLibraryInfo()
    {
        var sync = App.Services.Sync;
        var synced = App.Services.Db.GetLibraries().Max(l => l.ScannedAt);
        SyncCard.Description = sync.IsBusy ? sync.Status : $"Plex and folders sync at startup and every 30 minutes. Last sync {Format.Ago(synced)}.";
        DatabaseCard.Description = App.Services.Db.Path;
        string cache = Path.Combine(App.Services.DataDirectory, "cache");
        long bytes = Directory.Exists(cache) ? new DirectoryInfo(cache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        CacheCard.Description = $"Posters, backdrops and stills downloaded at display size · {Format.Size(bytes)}";
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        await App.Services.Sync.SyncAllAsync();
        UpdateLibraryInfo();
    }

    private async void SaveTmdbKey_Click(object sender, RoutedEventArgs e)
    {
        string key = TmdbKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            Tmdb.ApiKey = null;
            App.Window.ShowToast("TMDB key removed.");
            return;
        }

        var error = await Tmdb.TestAsync(key);
        if (error is not null)
        {
            App.Window.ShowToast($"TMDB didn't accept that key: {error}", InfoBarSeverity.Error);
            return;
        }

        Tmdb.ApiKey = key;
        App.Window.ShowToast("TMDB key saved. Matching local files…", InfoBarSeverity.Success);
        await App.Services.Sync.SyncAllAsync(refreshServers: false);
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start("explorer.exe", $"\"{App.Services.DataDirectory}\"");

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        string cache = Path.Combine(App.Services.DataDirectory, "cache");
        try
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
        catch (IOException ex)
        {
            App.Window.ShowToast($"Some files are in use: {ex.Message}", InfoBarSeverity.Warning);
        }

        UpdateLibraryInfo();
    }

    // ---- shortcuts and about ----

    private void BuildShortcuts()
    {
        (string Keys, string Action)[] shortcuts =
        [
            ("Ctrl+F", "Search"),
            ("Alt+←, mouse back", "Go back"),
            ("F5", "Sync libraries"),
            ("Space, K", "Play / pause"),
            ("←  /  →", "Back 10 s / forward 30 s"),
            ("↑  /  ↓", "Volume"),
            ("M", "Mute"),
            ("F, F11, double-click", "Full screen"),
            ("Esc", "Leave full screen, picture in picture, or the player"),
            ("N", "Next episode"),
            ("[  /  ]", "Audio sync −10 / +10 ms"),
            ("-  /  =", "Audio sync −1 / +1 ms"),
            (",  /  .", "Previous / next frame"),
            ("Ctrl+Shift+S", "Playback statistics"),
        ];
        ShortcutGrid.Children.Clear();
        ShortcutGrid.RowDefinitions.Clear();
        for (int i = 0; i < shortcuts.Length; i++)
        {
            ShortcutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = shortcuts[i].Keys, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
            var action = new TextBlock { Text = shortcuts[i].Action, FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            ShortcutGrid.Children.Add(keys);
            ShortcutGrid.Children.Add(action);
        }
    }

    /// <summary>The Store codec packages the engines rely on, and whether this PC has them.</summary>
    private async Task FillComponentsAsync()
    {
        var installed = await Task.Run(() =>
        {
            try
            {
                return new PackageManager().FindPackagesForUser("").Select(p => p.Id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return new HashSet<string>();
            }
        });

        (string Name, string Description, Func<bool> Present)[] components =
        [
            ("HEVC Video Extensions", "HEVC decoding for Media Foundation", () => installed.Any(n => n.Contains("HEVCVideoExtension", StringComparison.OrdinalIgnoreCase))),
            ("Dolby Vision Extensions", "Real Dolby Vision output", () => installed.Any(n => n.Contains("DolbyVision", StringComparison.OrdinalIgnoreCase))),
            ("Dolby Digital Plus decoder", "DD+ and Atmos objects through Windows", () => installed.Any(n => n.Contains("DolbyDigitalPlus", StringComparison.OrdinalIgnoreCase) || n.Contains("DolbyAccess", StringComparison.OrdinalIgnoreCase))),
            ("DTS Sound Unbound", "DTS, DTS-HD MA and DTS:X through Windows (FFmpeg is used without it)", () => StoreCodecRuntime.HasDtsDecoder),
        ];

        ComponentList.Children.Clear();
        foreach (var (name, description, present) in components)
        {
            bool ok = present();
            var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            state.Children.Add(new FontIcon
            {
                Glyph = ok ? "" : "",
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources[ok ? "SuccessBrush" : "TextMutedBrush"],
            });
            state.Children.Add(new TextBlock { Text = ok ? "Installed" : "Not found", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            ComponentList.Children.Add(new SettingsCard { Header = name, Description = description, Content = state });
        }
    }
}
