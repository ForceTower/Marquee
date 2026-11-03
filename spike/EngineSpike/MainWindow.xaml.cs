using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Enumeration;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EngineSpike;

public sealed partial class MainWindow : Window
{
    private readonly SpikeOptions _options;
    private readonly DiagLog _log;
    private readonly IntPtr _hwnd;
    private readonly VideoHostWindow _videoHost;
    private readonly MpvPlayer _mpv;
    private readonly MediaPlayer _mf = new();
    private readonly DispatcherQueueTimer _mpvInfoTimer;
    private readonly DispatcherQueueTimer _badgeTimer;

    // Held in fields: an unreferenced DispatcherQueueTimer can be garbage collected and stop firing.
    private DispatcherQueueTimer? _exitTimer;
    private DispatcherQueueTimer? _heartbeatTimer;
    private DispatcherQueueTimer? _atmosClockTimer;
    private AtmosPlayback? _atmos;
    private PresentationClock? _clock;
    private MediaPlaybackItem? _mfItem;
    private MediaSource? _mfSource;
    private DeviceInformation? _mfAudioDevice;
    private Engine? _active;
    private string? _currentPath;
    private string? _pendingMpvAudio;
    private string? _pendingMfAudio;
    private TimeSpan? _pendingMfStart;
    private int? _pendingMfTrackIndex;
    private bool _announceMpv;
    private RectInt32 _hostBounds;
    private bool _fullScreen;

    public MainWindow(SpikeOptions options)
    {
        InitializeComponent();
        _options = options;
        Title = "Marquee · engine spike";
        AppWindow.Resize(new SizeInt32(1800, 1100));

        _log = new DiagLog(InfoText, LogText, DispatcherQueue);
        _log.Line($"logs: {_log.Directory} (UI thread {Environment.CurrentManagedThreadId})");
        _log.Line($"Store codec runtime: {StoreCodecRuntime.Preload()}");

        _mpvInfoTimer = DispatcherQueue.CreateTimer();
        _mpvInfoTimer.Interval = TimeSpan.FromSeconds(1.5);
        _mpvInfoTimer.IsRepeating = false;
        _mpvInfoTimer.Tick += (_, _) => ShowMpvInfo();

        _badgeTimer = DispatcherQueue.CreateTimer();
        _badgeTimer.Interval = TimeSpan.FromSeconds(2.5);
        _badgeTimer.IsRepeating = false;
        _badgeTimer.Tick += (_, _) => EngineBadge.Visibility = Visibility.Collapsed;

        _hwnd = WindowNative.GetWindowHandle(this);
        _videoHost = new VideoHostWindow(_hwnd);
        _mpv = new MpvPlayer(_videoHost.Handle, _log.MpvLogPath, options.Mute);
        _mpv.StateChanged += id => DispatcherQueue.TryEnqueue(() => OnMpvStateChanged(id));
        _mpv.ClientMessage += args => DispatcherQueue.TryEnqueue(async () => await OnMpvClientMessageAsync(args));
        _mpv.Message += message => _log.Line(message);
        _mpv.FullScreenChanged += on => DispatcherQueue.TryEnqueue(() =>
        {
            if (_active == Engine.Mpv)
            {
                SetFullScreen(on);
            }
        });
        _mpv.BindKeys();

        MfElement.SetMediaPlayer(_mf);
        _mf.IsMuted = options.Mute;
        _mf.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(OnMfOpened);
        _mf.MediaFailed += (_, e) => _log.Line(
            $"MF MediaFailed: {e.Error} 0x{e.ExtendedErrorCode?.HResult:X8} {e.ErrorMessage} {e.ExtendedErrorCode?.Message}".TrimEnd());
        _mf.PlaybackSession.PlaybackStateChanged += (s, _) =>
        {
            _log.Line($"MF state {s.PlaybackState}");
            _clock?.OnStateChanged(s.Position, s.PlaybackState == MediaPlaybackState.Playing);
        };

        VideoArea.LayoutUpdated += (_, _) => SyncVideoHostBounds();
        RootGrid.Loaded += async (_, _) => await StartAsync();
        Closed += OnClosed;
    }

    private async Task StartAsync()
    {
        LoadSamples();
        NativeMkvBox.IsChecked = _options.NativeMkv;
        TrueHdAtmosBox.IsChecked = _options.TrueHdAtmos;
        foreach (var (key, shift) in new[] { ((Windows.System.VirtualKey)219, -10), ((Windows.System.VirtualKey)221, 10) })
        {
            var accelerator = new KeyboardAccelerator { Key = key };
            accelerator.Invoked += (_, e) =>
            {
                ShiftLipSync(shift);
                e.Handled = true;
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
        await MatchMfAudioDeviceAsync();

        if (_options.ExitAfterSeconds is int seconds)
        {
            _exitTimer = DispatcherQueue.CreateTimer();
            _exitTimer.Interval = TimeSpan.FromSeconds(seconds);
            _exitTimer.IsRepeating = false;
            _exitTimer.Tick += (_, _) =>
            {
                _log.Line($"exit: {_active?.ToString() ?? "idle"} at {CurrentPosition?.ToString(@"hh\:mm\:ss\.fff") ?? "—"}");
                Close();
            };
            _exitTimer.Start();
        }

        if (_options.FullScreen)
        {
            SetFullScreen(true);
        }

        if (_options.ExitAfterSeconds is not null)
        {
            _heartbeatTimer = DispatcherQueue.CreateTimer();
            _heartbeatTimer.Interval = TimeSpan.FromSeconds(3);
            _heartbeatTimer.Tick += (_, _) => _log.Line(
                $"heartbeat: {_active?.ToString() ?? "idle"} {CurrentPosition?.ToString(@"hh\:mm\:ss\.fff")}" +
                (_atmos is { } atmos ? " · " + AtmosStatus(atmos) : ""));
            _heartbeatTimer.Start();
        }

        if (_options.Path is not { } path)
        {
            return;
        }

        PathBox.Text = path;
        if (_options.Probe)
        {
            await ProbeAsync(path);
        }

        if (_options.Engine is { } engine)
        {
            _pendingMfTrackIndex = _options.AudioTrack;
            await PlayAsync(engine, path, _options.Start);
        }
    }

    private void LoadSamples()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "samples.txt");
        if (!File.Exists(file))
        {
            return;
        }

        foreach (string line in File.ReadAllLines(file))
        {
            int split = line.IndexOf('|');
            if (split > 0)
            {
                SamplesBox.Items.Add(new ComboBoxItem { Content = line[..split], Tag = line[(split + 1)..] });
            }
        }
    }

    /// <summary>
    /// mpv.conf pins a specific WASAPI endpoint, by endpoint GUID or by name; point Media Foundation
    /// at the same one so the comparison isn't skewed by MF landing on a different device. A pin that
    /// no longer resolves (GUIDs change when Windows re-enumerates a device) falls back to the
    /// system default for both.
    /// </summary>
    private async Task MatchMfAudioDeviceAsync()
    {
        string? mpvDevice = _mpv.GetProperty("audio-device");
        string? endpoint = mpvDevice is not null && mpvDevice.StartsWith("wasapi/") ? mpvDevice["wasapi/".Length..] : null;

        DeviceInformation? device = null;
        if (endpoint is not null)
        {
            var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            device = devices.FirstOrDefault(d =>
                d.Id.Contains(endpoint, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Name, endpoint, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                _log.Line($"⚠ mpv.conf audio-device {mpvDevice} no longer exists; using the system default");
                _mpv.SetProperty("audio-device", "auto");
            }
        }

        bool fallback = device is null;
        device ??= await DeviceInformation.CreateFromIdAsync(MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default));
        _mfAudioDevice = device;
        _mf.AudioDevice = device;
        _log.Line($"mpv audio-device = {_mpv.GetProperty("audio-device") ?? "—"}");
        _log.Line($"MF audio device = {device.Name}{(fallback ? " (system default)" : "")}");
    }

    private async Task PlayAsync(Engine engine, string path, TimeSpan? start = null, string? audioLanguage = null)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
        {
            return;
        }

        StopAll();
        _active = engine;
        _currentPath = path;
        _log.Line($"▶ {engine}: {path}{(start is { } s ? $" @ {s:hh\\:mm\\:ss}" : "")}");

        if (engine == Engine.Mpv)
        {
            MfElement.Visibility = Visibility.Collapsed;
            _videoHost.Show(true);
            _pendingMpvAudio = audioLanguage;
            _announceMpv = true;
            _mpv.Load(path, start);
            return;
        }

        _videoHost.Show(false);
        MfElement.Visibility = Visibility.Visible;
        VideoHostWindow.Focus(_hwnd);
        MfElement.Focus(FocusState.Programmatic);
        _pendingMfAudio = audioLanguage;
        try
        {
            if (DemuxerMediaSource.Handles(path) && NativeMkvBox.IsChecked != true)
            {
                bool windowsDts = StoreCodecRuntime.Loaded && StoreCodecRuntime.HasDtsDecoder && !_options.FfmpegDts;
                int? atmosTrack = TrueHdAtmosBox.IsChecked == true ? await Task.Run(() => FindTrueHdTrack(path, audioLanguage)) : null;
                DemuxerMediaSource demuxed = await Task.Run(() => DemuxerMediaSource.Open(Demuxer.Open(path), !_options.NoDolbyVision, windowsDts, atmosTrack));
                string reader = $"{demuxed.Demuxer.Container} reader";
                demuxed.Error += message => _log.Line($"{reader} error: {message}");
                demuxed.Trace += message => _log.Line($"{reader}: {message}");
                _log.Block($"Marquee {reader}", demuxed.Description);
                if (atmosTrack is int track)
                {
                    StartAtmos(path, demuxed, track);
                }

                _mfSource = MediaSource.CreateFromMediaStreamSource(demuxed.Source);
            }
            else
            {
                _mfSource = Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
                    ? MediaSource.CreateFromUri(uri)
                    : MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path));
            }

            // Not MediaPlaybackItem(source, start): that trims the item, so positions become relative
            // to the start and nothing before it can be reached. Seek once the media has opened instead.
            _pendingMfStart = start;
            _mfItem = new MediaPlaybackItem(_mfSource);
            _mf.Source = _mfItem;
            _mf.Play();
        }
        catch (Exception ex)
        {
            _log.Line($"MF open failed: 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private async Task SwapEngineAsync()
    {
        if (_active is not Engine current || _currentPath is null)
        {
            return;
        }

        TimeSpan? position = CurrentPosition;
        string? language = current == Engine.Mpv ? _mpv.AudioLanguage : SelectedMfTrack()?.Language;
        Engine target = current == Engine.Mpv ? Engine.MediaFoundation : Engine.Mpv;
        _log.Line($"⇄ {current} → {target}, audio {Language.Key(language) ?? "default"}");
        await PlayAsync(target, _currentPath, position, Language.Key(language));
    }

    private void NextAudio()
    {
        if (_active == Engine.Mpv)
        {
            _mpv.CycleAudio();
            Announce($"mpv · {_mpv.AudioSummary}");
        }
        else if (_active == Engine.MediaFoundation && _mfItem is { AudioTracks.Count: > 0 } item)
        {
            item.AudioTracks.SelectedIndex = (item.AudioTracks.SelectedIndex + 1) % item.AudioTracks.Count;
            Announce($"Media Foundation · {MfAudioSummary()}");
        }
    }

    /// <summary>The TrueHD track to play as Atmos objects: the preferred language's if there is one, else the first.</summary>
    private static int? FindTrueHdTrack(string path, string? language)
    {
        using var demuxer = Demuxer.Open(path, 64 * 1024);
        var trueHd = demuxer.Tracks.Where(t => t.Kind == TrackKind.Audio && t.CodecId == "A_TRUEHD").ToList();
        return (trueHd.FirstOrDefault(t => language is not null && Language.Key(t.DisplayLanguage) == language) ?? trueHd.FirstOrDefault())?.Number;
    }

    private void StartAtmos(string path, DemuxerMediaSource demuxed, int track)
    {
        _clock = new PresentationClock();
        _clock.Trace += message => _log.Line($"Atmos: {message}");
        _atmos = new AtmosPlayback(path, track, new TrueHdAtmosDecoder(), _clock);
        _atmos.Output.Muted = _options.Mute;
        _atmos.Failed += message => _log.Line($"Atmos: {message}");
        _atmos.Trace += message => _log.Line($"Atmos: {message}");
        demuxed.Seeked += _clock.OnSeeked;
        demuxed.Seeked += _atmos.OnSeeked;
        demuxed.StartGate = _atmos.WaitUntilReadyAsync;
        demuxed.StartReleased += _clock.OnStartReleased;
        _atmosClockTimer = DispatcherQueue.CreateTimer();
        _atmosClockTimer.Interval = TimeSpan.FromMilliseconds(50);
        _atmosClockTimer.Tick += (_, _) => _clock?.Update(
            _mf.PlaybackSession.Position, _mf.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
        _atmosClockTimer.Start();
        _log.Line($"Atmos: TrueHD track #{track} → truehd decoder → Windows Spatial Sound ([ / ] shift lip-sync)");
    }

    private string AtmosStatus(AtmosPlayback atmos)
    {
        var output = atmos.Output;
        return $"Atmos: sync {output.SyncErrorMs:F1} ms, rate {output.Ratio:0.00000}, offset {output.OffsetMs} ms, " +
               $"{output.Resyncs} resyncs, {output.Underruns} underruns, {atmos.DecodedChunks} chunks · {output.Info}";
    }

    private void ShiftLipSync(int milliseconds)
    {
        if (_atmos is { } atmos)
        {
            atmos.Output.OffsetMs += milliseconds;
            Announce($"Atmos audio offset {atmos.Output.OffsetMs:+0;-0;0} ms");
        }
    }

    private void StopAll()
    {
        _atmosClockTimer?.Stop();
        _atmosClockTimer = null;
        _atmos?.Dispose();
        _atmos = null;
        _clock = null;
        _mpvInfoTimer.Stop();
        _mpv.Stop();
        _videoHost.Show(false);

        _mf.Pause();
        _mf.Source = null;
        _mfSource?.Dispose();
        _mfSource = null;
        _mfItem = null;
        MfElement.Visibility = Visibility.Collapsed;

        _active = null;
        NowPlayingText.Text = "";
    }

    private void OnMpvStateChanged(LibMpv.EventId id)
    {
        _log.Line($"mpv {id}");
        if (id == LibMpv.EventId.FileLoaded && _pendingMpvAudio is { } language)
        {
            _pendingMpvAudio = null;
            _mpv.SelectAudioLanguage(language);
        }

        if (id == LibMpv.EventId.VideoReconfig && _active == Engine.Mpv)
        {
            _videoHost.FocusChild();
        }

        if (id == LibMpv.EventId.PlaybackRestart && _announceMpv)
        {
            _announceMpv = false;
            Announce($"mpv · {_mpv.AudioSummary}");
        }

        _mpvInfoTimer.Stop();
        _mpvInfoTimer.Start();
    }

    private async Task OnMpvClientMessageAsync(string[] args)
    {
        if (args is not ["marquee", var action])
        {
            return;
        }

        switch (action)
        {
            case "swap":
                await SwapEngineAsync();
                break;
            case "next-audio":
                NextAudio();
                break;
        }
    }

    private void OnMfOpened()
    {
        _log.Line("MF MediaOpened");
        if (_pendingMfStart is { } start)
        {
            _pendingMfStart = null;
            _mf.PlaybackSession.Position = start;
        }

        if (_pendingMfTrackIndex is int index && _mfItem is not null && index < _mfItem.AudioTracks.Count)
        {
            _pendingMfTrackIndex = null;
            _mfItem.AudioTracks.SelectedIndex = index;
        }

        if (_pendingMfAudio is { } language && _mfItem is not null)
        {
            _pendingMfAudio = null;
            for (int i = 0; i < _mfItem.AudioTracks.Count; i++)
            {
                if (Language.Key(_mfItem.AudioTracks[i].Language) == language)
                {
                    _mfItem.AudioTracks.SelectedIndex = i;
                    break;
                }
            }
        }

        Announce($"Media Foundation · {MfAudioSummary()}");
        ShowMfInfo();
    }

    private AudioTrack? SelectedMfTrack() =>
        _mfItem is { } item && item.AudioTracks.SelectedIndex is int i && i >= 0 && i < item.AudioTracks.Count
            ? item.AudioTracks[i]
            : null;

    private string MfAudioSummary()
    {
        if (SelectedMfTrack() is not { } track)
        {
            return "no audio";
        }

        var p = track.GetEncodingProperties();
        return $"{track.Language} {p.Subtype} {p.ChannelCount}ch {p.Bitrate / 1000} kbps";
    }

    /// <summary>
    /// Says which engine and track are playing: an mpv OSD message over mpv's window, or a XAML
    /// badge over Media Foundation's (XAML can't draw over the mpv window).
    /// </summary>
    private void Announce(string text)
    {
        NowPlayingText.Text = text;
        _log.Line($"now: {text}");
        if (_active == Engine.Mpv)
        {
            _mpv.ShowText(text);
            return;
        }

        EngineBadgeText.Text = text;
        EngineBadge.Visibility = Visibility.Visible;
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    private async Task ProbeAsync(string path)
    {
        path = path.Trim().Trim('"');
        TimeSpan from = CurrentPosition ?? TimeSpan.FromMinutes(10);
        _log.Line($"probing Windows audio decoders: {Path.GetFileName(path)}");
        List<string> lines = await Task.Run(() => AtmosProbe.Run(path, from, TimeSpan.FromSeconds(20)));
        _log.Block("Atmos probe", lines);
    }

    private void ShowMpvInfo() => _log.Info("mpv", _mpv.Describe());

    private void ShowMfInfo()
    {
        if (_mfItem is null)
        {
            return;
        }

        var lines = MfDescribe.Playback(_mf, _mfItem).ToList();
        if (_mfAudioDevice is not null)
        {
            lines.Add("");
            lines.AddRange(MfDescribe.AudioDevice(_mfAudioDevice.Id, _mfAudioDevice.Name));
        }

        _log.Info("Media Foundation", lines);
    }

    private void SyncVideoHostBounds()
    {
        if (VideoArea.XamlRoot is null)
        {
            return;
        }

        double scale = VideoArea.XamlRoot.RasterizationScale;
        var origin = VideoArea.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var bounds = new RectInt32(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            (int)Math.Round(VideoArea.ActualWidth * scale),
            (int)Math.Round(VideoArea.ActualHeight * scale));
        if (bounds.Equals(_hostBounds))
        {
            return;
        }

        _hostBounds = bounds;
        _videoHost.SetBounds(bounds);
    }

    private void SetFullScreen(bool on)
    {
        if (_fullScreen == on)
        {
            return;
        }

        _fullScreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusPane.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusColumn.Width = on ? new GridLength(0) : new GridLength(460);
        FullScreenToggle.IsChecked = on;
        _mpv.SetFullScreenFlag(on);
    }

    private string CurrentPath => PathBox.Text;

    private TimeSpan? CurrentPosition => _active switch
    {
        Engine.Mpv => _mpv.Position,
        Engine.MediaFoundation => _mf.PlaybackSession.Position,
        _ => null,
    };

    private async void PlayMpv_Click(object sender, RoutedEventArgs e) => await PlayAsync(Engine.Mpv, CurrentPath);

    private async void PlayMf_Click(object sender, RoutedEventArgs e) => await PlayAsync(Engine.MediaFoundation, CurrentPath);

    private async void Swap_Click(object sender, RoutedEventArgs e) => await SwapEngineAsync();

    private void NextAudio_Click(object sender, RoutedEventArgs e) => NextAudio();

    private void Stop_Click(object sender, RoutedEventArgs e) => StopAll();

    private async void ProbeAtmos_Click(object sender, RoutedEventArgs e) => await ProbeAsync(_currentPath ?? CurrentPath);

    private void RefreshInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_active == Engine.Mpv)
        {
            ShowMpvInfo();
        }
        else if (_active == Engine.MediaFoundation)
        {
            ShowMfInfo();
        }
    }

    private void FullScreenToggle_Click(object sender, RoutedEventArgs e) => SetFullScreen(FullScreenToggle.IsChecked == true);

    private async void Swap_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SwapEngineAsync();
    }

    private void NextAudio_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        NextAudio();
    }

    private void ToggleFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFullScreen(!_fullScreen);
        args.Handled = true;
    }

    private void ExitFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFullScreen(false);
        args.Handled = true;
    }

    private async void SamplesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SamplesBox.SelectedItem is not ComboBoxItem { Tag: string path })
        {
            return;
        }

        PathBox.Text = path;
        if (_active is Engine engine)
        {
            await PlayAsync(engine, path);
        }
    }

    // XAML popups can't draw over the mpv child window, so get it out of the way while the list is open.
    private void SamplesBox_DropDownOpened(object sender, object e) => _videoHost.Show(false);

    private void SamplesBox_DropDownClosed(object sender, object e) => _videoHost.Show(_active == Engine.Mpv);

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hwnd);
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            PathBox.Text = file.Path;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        StopAll();
        _mpv.Dispose();
        _mf.Dispose();
        _videoHost.Dispose();
    }
}
