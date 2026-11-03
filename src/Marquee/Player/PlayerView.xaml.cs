using System.Runtime.InteropServices;
using Marquee.Data;
using Marquee.Library;
using Marquee.Plex;
using Marquee.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Enumeration;
using Windows.Graphics;
using Windows.Media.Devices;
using Windows.Storage.Pickers;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace Marquee.Player;

/// <summary>
/// The full-window player. Runs one engine at a time (mpv or Media Foundation), keeps the
/// controls' view model current, reports progress to Plex and the library, and handles skip
/// markers, the next-episode countdown, full screen and picture-in-picture.
/// </summary>
public sealed partial class PlayerView : UserControl, IPlayerActions
{
    /// <summary>
    /// The one player, kept for the app's lifetime and only hidden when closed: letting it be
    /// collected releases its DispatcherQueueTimers on the finalizer thread, which crashes
    /// CoreMessagingXP with a stowed exception.
    /// </summary>
    private static PlayerView? s_instance;

    private bool _open;

    /// <summary>Set by <c>--mute</c> for unattended tests.</summary>
    public static bool StartMuted { get; set; }

    /// <summary>False for test runs (<c>--start</c>), which leave watch progress in the library and on Plex alone.</summary>
    public static bool ReportsProgress { get; set; } = true;

    private readonly PlayerViewModel _vm = new();
    private readonly PlayerControls _inline;
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _seekTimer;
    private readonly DispatcherQueueTimer _messageTimer;
    private readonly IntPtr _hwnd;
    private readonly AppSettings _settings = App.Services.Settings.Current;

    private IPlayerEngine? _engine;
    private MpvEngine? _mpv;
    private MediaFoundationEngine? _mf;
    private SubtitleOverlay? _subtitleOverlay;
    private OverlayWindow? _overlay;
    private PlayerControls? _overlayControls;
    private PlaybackRequest? _request;
    private MediaItem? _next;
    private bool _upNextCancelled;
    private long _upNextStarted;
    private long _lastActivity = Environment.TickCount64;
    private long _lastReport;
    private string? _lastState;
    private bool _markedWatched;
    private bool _cursorHidden;
    private bool _opening;
    private double? _pendingSeek;
    private int _ticks;

    private PlayerView()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(App.Window);
        _inline = new PlayerControls(_vm, this);
        InlineHost.Child = _inline;
        _vm.Volume = _settings.Volume;
        _vm.IsMuted = StartMuted;
        _vm.AudioDelayMs = _settings.AtmosLipSyncMs;

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(50);
        _tick.Tick += (_, _) => Log.Guard("Player tick", OnTick);

        _seekTimer = DispatcherQueue.CreateTimer();
        _seekTimer.Interval = TimeSpan.FromMilliseconds(150);
        _seekTimer.IsRepeating = false;
        _seekTimer.Tick += (_, _) => Log.Guard("Seek", ApplyPendingSeek);

        _messageTimer = DispatcherQueue.CreateTimer();
        _messageTimer.Interval = TimeSpan.FromSeconds(6);
        _messageTimer.IsRepeating = false;
        _messageTimer.Tick += (_, _) => _vm.Message = null;
    }

    // ---- entry point ----

    public static async Task PlayAsync(MediaItem item, bool resume, EngineMode? engine)
    {
        PlaybackPlan plan;
        try
        {
            plan = await PlaybackPlanner.PlanAsync(item, resume, engine);
        }
        catch (Exception ex)
        {
            Log.Error($"Can't play {item.Id}", ex);
            App.Window.ShowToast(ex.Message, InfoBarSeverity.Error);
            return;
        }

        s_instance ??= new PlayerView();
        await s_instance.StartAsync(plan);
    }

    private async Task StartAsync(PlaybackPlan plan)
    {
        var item = plan.Request.Item;
        Log.Info($"Play {item.Id} '{item.Title}' via {plan.Engine} from {plan.Request.Start}: {plan.Request.Location.Split('?')[0]}");
        _request = plan.Request;
        _markedWatched = false;
        _upNextCancelled = false;
        _vm.UpNext = null;
        _vm.Message = null;
        _next = item.Kind == ItemKind.Episode ? App.Services.Db.GetNextEpisode(item) : null;

        _vm.Title = item.Kind == ItemKind.Episode ? item.ShowTitle ?? item.Title : item.Title;
        _vm.Subtitle = item.Kind == ItemKind.Episode
            ? Format.Join(Format.EpisodeCode(item), item.Title)
            : Format.Join(item.Year?.ToString(), Format.Duration(item.DurationMs));
        _vm.Badges = Format.QualityBadges(item, new ItemDetails { Versions = [new VersionDetails(plan.Request.Version.Location, plan.Request.Streams)] })
            .Where(b => b is not ("HEVC" or "H.264")).Take(4).ToList();
        _vm.Chapters = plan.Request.Chapters;
        _vm.Duration = Math.Max(1, (item.DurationMs ?? 0) / 1000.0);
        _vm.Position = plan.Request.Start.TotalSeconds;
        _vm.Buffered = 0;
        _pendingSeek = null;
        _inline.Reset();
        _overlayControls?.Reset();
        PlayerTitleBar.Title = _vm.Title;
        PlayerTitleBar.Subtitle = _vm.Subtitle;

        if (!_open)
        {
            _open = true;
            Log.Info("Player opened");
            App.Window.ShowPlayerLayer(this);
            App.Window.SetTitleBar(PlayerTitleBar);
            App.Window.AppWindow.Changed += OnAppWindowChanged;
            App.Window.Activated += OnWindowActivated;
            MediaDevice.DefaultAudioRenderDeviceChanged += OnDefaultAudioChanged;
            _vm.IsFullScreen = false;
            _vm.IsCompact = false;
            _vm.ShowStatistics = false;
            PlayerTitleBar.Visibility = Visibility.Visible;
            PlayerTitleBar.IsBackButtonVisible = true;
            _tick.Start();
        }

        Root.Focus(FocusState.Programmatic);
        var request = plan.Request;
        _mpvNoSound = false;
        if (plan.Engine == EngineMode.MediaFoundation && await AudioOutputNameAsync() is null)
        {
            request = request with { WithoutSound = true };
            ShowMessage("No audio output, so this plays without sound.");
        }

        await OpenEngineAsync(plan.Engine, request);
        if (plan.Note is { } note)
        {
            ShowMessage(note);
        }
    }

    private async Task OpenEngineAsync(EngineMode kind, PlaybackRequest request)
    {
        _opening = true;
        _engine?.Pause();
        _vm.IsBuffering = true;
        ShowControls();

        if (kind == EngineMode.Mpv)
        {
            if (_mpv is null)
            {
                _mpv = new MpvEngine(_hwnd, _settings);
                Hook(_mpv);
            }

            _mf?.Pause();
            _mf?.SelectSubtitle(null);
            MfElement.Visibility = Visibility.Collapsed;
            SubtitleLayer.Visibility = Visibility.Collapsed;
            InlineHost.Visibility = Visibility.Collapsed;
            _engine = _mpv;
            PositionVideo();
            _mpv.ShowVideo(true);
            EnsureOverlay().ShowWithoutActivating();
        }
        else
        {
            if (_mf is null)
            {
                _mf = new MediaFoundationEngine(_settings);
                Hook(_mf);
                MfElement.SetMediaPlayer(_mf.Player);
                var subtitles = _subtitleOverlay ??= new SubtitleOverlay(SubtitleLayer);
                _mf.SubtitlesRendered += subtitles.Show;
            }

            if (_mpv is not null)
            {
                _mpv.Pause();
                _mpv.ShowVideo(false);
            }

            _overlay?.Hide();
            MfElement.Visibility = Visibility.Visible;
            SubtitleLayer.Visibility = Visibility.Visible;
            InlineHost.Visibility = Visibility.Visible;
            _engine = _mf;
            PositionVideo();
        }

        var engine = _engine;
        engine.Volume = _vm.Volume;
        engine.Muted = _vm.IsMuted;
        engine.AudioDelayMs = _vm.AudioDelayMs;
        _vm.CanShowSubtitles = engine.CanShowSubtitles;
        try
        {
            await engine.OpenAsync(request);
            engine.Play();
            _request = request;
            _vm.EngineText = engine.Description;
            UpdateTracks();
        }
        catch (Exception ex)
        {
            Log.Error($"{kind} open failed", ex);
            _vm.IsBuffering = false;
            ShowMessage(ex.Message);
        }
        finally
        {
            _opening = false;
        }
    }

    private void Hook(IPlayerEngine engine)
    {
        engine.Opened += () =>
        {
            if (engine == _engine)
            {
                _vm.EngineText = engine.Description;
                UpdateTracks();
            }
        };
        engine.TracksChanged += () =>
        {
            if (engine == _engine)
            {
                UpdateTracks();
            }
        };
        engine.Ended += () =>
        {
            if (engine == _engine && !_opening)
            {
                OnEnded();
            }
        };
        engine.Failed += message =>
        {
            if (engine == _engine)
            {
                Log.Error(message);
                ShowMessage(message);
            }
        };
    }

    private void UpdateTracks()
    {
        if (_engine is not { } engine)
        {
            return;
        }

        bool plexHasNames = _request?.Chapters.Any(c => c.Title is not null) == true;
        if (engine.Chapters.Count > 0 && !plexHasNames)
        {
            _vm.Chapters = engine.Chapters.Select((c, i) => c with { Thumb = _request?.Chapters.ElementAtOrDefault(i)?.Thumb }).ToList();
        }

        _vm.AudioTracks = engine.AudioTracks;
        _vm.SubtitleTracks = engine.SubtitleTracks;
        _vm.SelectedAudio = engine.SelectedAudio;
        _vm.SelectedSubtitle = engine.SelectedSubtitle;
        _vm.EngineText = engine.Description;
    }

    // ---- the 50 ms tick ----

    private void OnTick()
    {
        if (_engine is not { } engine)
        {
            return;
        }

        engine.Tick();
        if (++_ticks % 5 != 0)
        {
            return;
        }

        var position = _pendingSeek is { } pending ? TimeSpan.FromSeconds(pending) : engine.Position;
        var duration = engine.Duration > TimeSpan.Zero ? engine.Duration : TimeSpan.FromMilliseconds(_request?.Item.DurationMs ?? 0);
        _vm.Duration = Math.Max(1, duration.TotalSeconds);
        _vm.Position = position.TotalSeconds;
        _vm.Buffered = engine.Buffered.TotalSeconds;
        _vm.PositionText = Format.Clock(position);
        _vm.DurationText = Format.Clock(duration);
        _vm.IsPlaying = engine.IsPlaying;
        _vm.IsBuffering = _opening || engine.IsBuffering;
        int chapter = _vm.Chapters.Count(c => c.StartMs <= position.TotalMilliseconds) - 1;
        _vm.ChapterTitle = chapter < 0 ? "" : _vm.Chapters[chapter].Title ?? $"Chapter {chapter + 1}";

        UpdateMarkers(position, duration);
        UpdateUpNext(position, duration);
        ReportProgress(position, duration, force: false);
        DetectPointerMovement();
        AutoHide();
        WatchAudio(engine);

        if (_vm.ShowStatistics && _ticks % 20 == 0)
        {
            _vm.Statistics = string.Join("\n", engine.Statistics());
        }
    }

    private void UpdateMarkers(TimeSpan position, TimeSpan duration)
    {
        var markers = _request?.Markers ?? [];
        double ms = position.TotalMilliseconds;
        var current = markers.FirstOrDefault(m => ms >= m.StartMs && ms < m.EndMs - 2000 && m.EndMs < duration.TotalMilliseconds - 10_000);
        _vm.SkipLabel = current?.Type switch
        {
            "intro" => "Skip intro",
            "credits" => "Skip credits",
            "commercial" => "Skip",
            _ => null,
        };
    }

    /// <summary>The final credits: Plex's last credits marker that runs to the end, else the last 30 s.</summary>
    private double CreditsStartMs(TimeSpan duration) =>
        _request?.Markers.Where(m => m.Type == "credits" && m.EndMs >= duration.TotalMilliseconds - 10_000).Select(m => (double)m.StartMs).DefaultIfEmpty(duration.TotalMilliseconds - 30_000).Max()
        ?? duration.TotalMilliseconds - 30_000;

    private void UpdateUpNext(TimeSpan position, TimeSpan duration)
    {
        if (_next is null || _upNextCancelled || _settings.AutoPlayNextSeconds <= 0 || duration <= TimeSpan.FromMinutes(1))
        {
            return;
        }

        if (position.TotalMilliseconds < CreditsStartMs(duration))
        {
            _vm.UpNext = null;
            return;
        }

        if (_vm.UpNext is null)
        {
            _vm.UpNext = _next;
            _upNextStarted = Environment.TickCount64;
            ShowControls();
        }

        int left = _settings.AutoPlayNextSeconds - (int)((Environment.TickCount64 - _upNextStarted) / 1000);
        _vm.UpNextSeconds = Math.Max(0, left);
        if (left <= 0 && _engine?.IsPlaying == true)
        {
            PlayUpNext();
        }
    }

    // ---- progress ----

    private void ReportProgress(TimeSpan position, TimeSpan duration, bool force, string? state = null)
    {
        if (!ReportsProgress || _request?.Item is not { } item || duration <= TimeSpan.Zero || _opening)
        {
            return;
        }

        state ??= _engine?.IsPlaying == true ? "playing" : "paused";
        long now = Environment.TickCount64;
        bool due = force || state != _lastState || now - _lastReport > 10_000;
        if (!due)
        {
            return;
        }

        _lastReport = now;
        _lastState = state;
        long positionMs = (long)position.TotalMilliseconds;
        long durationMs = (long)duration.TotalMilliseconds;
        bool watched = position.TotalMilliseconds >= Math.Min(duration.TotalMilliseconds * 0.9, CreditsStartMs(duration));

        if (watched && !_markedWatched)
        {
            _markedWatched = true;
            App.Services.Db.UpdateProgress(item.Id, 0, watched: true);
            _ = ScrobbleAsync(item);
        }
        else if (!_markedWatched && position > TimeSpan.FromSeconds(30))
        {
            App.Services.Db.UpdateProgress(item.Id, positionMs, watched: false);
        }

        if (item.IsPlex && App.Services.Plex.Server(PlexMapper.ServerId(item.Id)) is { Server.Online: true } server)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await server.ReportTimelineAsync(PlexMapper.RatingKey(item.Id)!, state, positionMs, durationMs, CancellationToken.None);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Log.Info($"Timeline report failed: {ex.Message}");
                }
            });
        }
    }

    private static async Task ScrobbleAsync(MediaItem item)
    {
        if (item.IsPlex && App.Services.Plex.Server(PlexMapper.ServerId(item.Id)) is { Server.Online: true } server)
        {
            try
            {
                await server.ScrobbleAsync(PlexMapper.RatingKey(item.Id)!, true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Info($"Scrobble failed: {ex.Message}");
            }
        }
    }

    private void OnEnded()
    {
        Log.Info($"Ended at {_engine?.Position} of {_engine?.Duration} ({_engine?.Kind})");
        if (_engine is { } engine)
        {
            ReportProgress(engine.Duration, engine.Duration, force: true, "stopped");
        }

        if (_next is not null && !_upNextCancelled && _settings.AutoPlayNextSeconds > 0)
        {
            PlayUpNext();
        }
        else
        {
            Close();
        }
    }

    // ---- audio output ----

    private long _stuckSince;
    private TimeSpan _stuckAt;
    private bool _mpvNoSound;
    private long _mpvSoundChangeSince;

    /// <summary>
    /// Media Foundation's clock follows its audio renderer. An output that takes the samples but never
    /// plays them (an S/PDIF port with nothing on it, which Windows makes the default when a headset
    /// switches off) freezes the picture while the engine still says it's playing. Spot that and play
    /// on without sound. mpv plays on by itself when it loses its device; this only tells the user.
    /// </summary>
    private void WatchAudio(IPlayerEngine engine)
    {
        long now = Environment.TickCount64;
        bool settled = engine.IsPlaying && !engine.IsBuffering && !_opening && _pendingSeek is null;
        if (engine == _mpv && settled)
        {
            bool noSound = engine.PlaysWithoutSound;
            if (noSound == _mpvNoSound)
            {
                _mpvSoundChangeSince = 0;
            }
            else if (_mpvSoundChangeSince == 0)
            {
                _mpvSoundChangeSince = now;
            }
            else if (now - _mpvSoundChangeSince > 1500)
            {
                _mpvNoSound = noSound;
                _mpvSoundChangeSince = 0;
                ShowMessage(noSound ? "No audio output, so this plays without sound." : "Sound is back.");
            }
        }

        if (engine != _mf || engine.PlaysWithoutSound || !settled || engine.Position != _stuckAt)
        {
            _stuckAt = engine.Position;
            _stuckSince = now;
        }
        else if (now - _stuckSince > 3000)
        {
            _stuckSince = long.MaxValue;
            PlayWithoutSound();
        }
    }

    private async void PlayWithoutSound()
    {
        try
        {
            string device = await AudioOutputNameAsync() ?? "the audio output";
            Log.Info($"Media Foundation's clock is stuck at {_engine?.Position} with {device}; playing without sound");
            ShowMessage($"No sound from {device}, so this plays without it. Turn on or pick another audio output to get it back.");
            await ReopenAsync(withoutSound: true);
        }
        catch (Exception ex)
        {
            Log.Error("Playing without sound", ex);
        }
    }

    private void OnDefaultAudioChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args)
    {
        if (args.Role == AudioDeviceRole.Default)
        {
            DispatcherQueue.TryEnqueue(RestoreSound);
        }
    }

    /// <summary>A new default audio output: give it a try if playback lost its sound.</summary>
    private async void RestoreSound()
    {
        try
        {
            if (!_open || _engine is not { } engine || await AudioOutputNameAsync() is not { } device)
            {
                return;
            }

            if (engine == _mpv && _mpvNoSound)
            {
                Log.Info($"Audio output changed to {device}; mpv tries sound again");
                await _mpv.RetryAudioAsync(_request?.Audio);
            }
            else if (engine == _mf && engine.PlaysWithoutSound)
            {
                Log.Info($"Audio output changed to {device}; trying sound again");
                ShowMessage($"Sound is back on {device}.");
                await ReopenAsync(withoutSound: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Restoring sound", ex);
        }
    }

    private async Task ReopenAsync(bool withoutSound)
    {
        if (_engine is { } engine && _request is { } request)
        {
            await OpenEngineAsync(engine.Kind, request with { Start = engine.Position, WithoutSound = withoutSound });
        }
    }

    /// <summary>The output Media Foundation plays on: the one picked in Settings, else Windows' default.</summary>
    private async Task<string?> AudioOutputNameAsync()
    {
        if (_settings.AudioDevice is { Length: > 0 } chosen)
        {
            return chosen;
        }

        string? id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            return (await DeviceInformation.CreateFromIdAsync(id)).Name;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
    }

    // ---- controls visibility, cursor ----

    private POINT _lastCursor;

    /// <summary>
    /// Pointer movement over the video, whichever window receives it: mpv's child window and the
    /// overlay's transparent areas don't deliver XAML pointer events, so poll the cursor instead.
    /// </summary>
    private void DetectPointerMovement()
    {
        if (!GetCursorPos(out var cursor) || (cursor.X == _lastCursor.X && cursor.Y == _lastCursor.Y))
        {
            return;
        }

        _lastCursor = cursor;
        IntPtr root = GetAncestor(WindowFromPoint(cursor), GA_ROOT);
        if (root == _hwnd || GetWindow(root, GW_OWNER) == _hwnd)
        {
            PointerActivity();
        }
    }

    public void PointerActivity()
    {
        _lastActivity = Environment.TickCount64;
        if (!_vm.ControlsVisible)
        {
            ShowControls();
        }
    }

    private void ShowControls()
    {
        _lastActivity = Environment.TickCount64;
        _vm.ControlsVisible = true;
        SetCursorHidden(false);
    }

    private void AutoHide()
    {
        bool busy = _inline.IsBusy || _overlayControls?.IsBusy == true || _vm.UpNext is not null;
        if (_vm.ControlsVisible && _engine?.IsPlaying == true && !busy && Environment.TickCount64 - _lastActivity > 3000)
        {
            _vm.ControlsVisible = false;
            SetCursorHidden(true);
        }
    }

    private void SetCursorHidden(bool hidden)
    {
        if (hidden == _cursorHidden)
        {
            return;
        }

        _cursorHidden = hidden;
        _mpv?.SetCursorHidden(hidden);
        if (!hidden)
        {
            _cursorBlocker?.Hide();
        }
        else if (Stage.XamlRoot is not null)
        {
            if (_cursorBlocker is null)
            {
                _cursorBlocker = new CursorBlocker(_hwnd);
                _cursorBlocker.Activity += PointerActivity;
            }

            _cursorBlocker.Show(ToScreen(StageBounds()));
        }
    }

    private CursorBlocker? _cursorBlocker;

    private void ShowMessage(string message)
    {
        _vm.Message = message;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    // ---- window: full screen, compact overlay, the mpv overlay ----

    private OverlayWindow EnsureOverlay()
    {
        if (_overlay is null)
        {
            _overlayControls = new PlayerControls(_vm, this);
            _overlay = new OverlayWindow(_hwnd, _overlayControls);
        }

        PositionVideo();
        return _overlay;
    }

    /// <summary>
    /// Puts mpv's child window over the stage and the controls' overlay window over that, and keeps
    /// the cursor blocker on the stage while the cursor is hidden.
    /// </summary>
    private void PositionVideo()
    {
        if (Stage.XamlRoot is null)
        {
            return;
        }

        var client = StageBounds();
        if (_cursorHidden)
        {
            _cursorBlocker?.Show(ToScreen(client));
        }

        if (_engine == _mpv && _mpv is not null)
        {
            _mpv.SetBounds(client);
            _overlay?.Place(ToScreen(client));
        }
        else if (_engine == _mf && _mf is not null)
        {
            _mf.SetSubtitleArea(client.Width, client.Height);
        }
    }

    /// <summary>The stage in physical pixels, relative to the window's client area.</summary>
    private RectInt32 StageBounds()
    {
        double scale = Stage.XamlRoot.RasterizationScale;
        var origin = Stage.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new RectInt32(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            (int)Math.Round(Stage.ActualWidth * scale),
            (int)Math.Round(Stage.ActualHeight * scale));
    }

    private RectInt32 ToScreen(RectInt32 client)
    {
        var topLeft = new POINT { X = client.X, Y = client.Y };
        ClientToScreen(_hwnd, ref topLeft);
        return new RectInt32(topLeft.X, topLeft.Y, client.Width, client.Height);
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => PositionVideo();

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            DispatcherQueue.TryEnqueue(PositionVideo);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _settings.CompactOnFocusLoss &&
            _engine?.IsPlaying == true && !_vm.IsCompact && !_vm.IsFullScreen && _open)
        {
            ToggleCompact();
        }
    }

    public void ToggleFullScreen()
    {
        bool on = !_vm.IsFullScreen;
        if (_vm.IsCompact)
        {
            _vm.IsCompact = false;
        }

        _vm.IsFullScreen = on;
        App.Window.AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        PlayerTitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        DispatcherQueue.TryEnqueue(() =>
        {
            PositionVideo();
            Root.Focus(FocusState.Programmatic);
        });
    }

    public void ToggleCompact()
    {
        bool on = !_vm.IsCompact;
        _vm.IsFullScreen = false;
        _vm.IsCompact = on;
        if (on)
        {
            var presenter = CompactOverlayPresenter.Create();
            presenter.InitialSize = CompactOverlaySize.Medium;
            App.Window.AppWindow.SetPresenter(presenter);
        }
        else
        {
            App.Window.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        }

        PlayerTitleBar.Visibility = Visibility.Visible;
        PlayerTitleBar.IsBackButtonVisible = !on;
        DispatcherQueue.TryEnqueue(() =>
        {
            PositionVideo();
            Root.Focus(FocusState.Programmatic);
        });
    }

    // ---- IPlayerActions ----

    public void TogglePlay()
    {
        if (_engine is not { } engine)
        {
            return;
        }

        if (engine.IsPlaying)
        {
            engine.Pause();
        }
        else
        {
            engine.Play();
        }

        _vm.IsPlaying = !engine.IsPlaying;
        ShowControls();
    }

    public void SeekTo(double seconds, bool final)
    {
        _pendingSeek = seconds;
        _vm.Position = seconds;
        _vm.PositionText = Format.Clock(TimeSpan.FromSeconds(seconds));
        ShowControls();
        if (final)
        {
            _seekTimer.Stop();
            ApplyPendingSeek();
        }
        else if (!_seekTimer.IsRunning)
        {
            _seekTimer.Start();
        }
    }

    private void ApplyPendingSeek()
    {
        if (_pendingSeek is { } seconds && _engine is { } engine)
        {
            _pendingSeek = null;
            engine.Seek(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        }
    }

    public void SkipBy(double seconds)
    {
        if (_engine is { } engine)
        {
            double target = Math.Clamp((_pendingSeek ?? engine.Position.TotalSeconds) + seconds, 0, Math.Max(0, _vm.Duration - 1));
            SeekTo(target, final: true);
        }
    }

    public void SetVolume(double volume)
    {
        _vm.Volume = volume;
        if (_engine is { } engine)
        {
            engine.Volume = volume;
            if (volume > 0 && engine.Muted)
            {
                engine.Muted = false;
                _vm.IsMuted = false;
            }
        }
    }

    public void ToggleMute()
    {
        _vm.IsMuted = !_vm.IsMuted;
        if (_engine is { } engine)
        {
            engine.Muted = _vm.IsMuted;
        }

        ShowControls();
    }

    public async void SelectAudio(int ordinal)
    {
        if (_engine is not { } engine || _request is null)
        {
            return;
        }

        await engine.SelectAudioAsync(ordinal);
        _request = _request with { Audio = ordinal, WithoutSound = false, PlayTrueHdAsObjects = _settings.TrueHdAtmosObjects && engine.AudioTracks.ElementAtOrDefault(ordinal)?.IsAtmos == true };
        var prefs = TrackPreferences.Load(_request.Item.Id);
        (prefs with { Audio = ordinal }).Save(_request.Item.Id);
        UpdateTracks();
    }

    public async void SelectSubtitle(int? ordinal)
    {
        if (_engine is not { } engine || _request is null)
        {
            return;
        }

        var prefs = TrackPreferences.Load(_request.Item.Id);
        (prefs with { Subtitle = ordinal ?? TrackPreferences.SubtitlesOff }).Save(_request.Item.Id);
        if (ordinal is not int o || engine.CanShowSubtitle(o))
        {
            engine.SelectSubtitle(ordinal);
            UpdateTracks();
            return;
        }

        ShowMessage("These subtitles play through mpv, so Dolby Vision is shown as HDR10.");
        await OpenEngineAsync(EngineMode.Mpv, _request with
        {
            Start = engine.Position,
            Audio = engine.SelectedAudio ?? _request.Audio,
            Subtitle = ordinal,
        });
    }

    public void JumpToChapter(Chapter chapter) => SeekTo(chapter.StartMs / 1000.0, final: true);

    public void Skip()
    {
        if (_engine is { } engine && _request is not null)
        {
            double ms = engine.Position.TotalMilliseconds;
            if (_request.Markers.FirstOrDefault(m => ms >= m.StartMs && ms < m.EndMs) is { } marker)
            {
                SeekTo(marker.EndMs / 1000.0, final: true);
            }
        }
    }

    public async void PlayUpNext()
    {
        if (_next is not { } next)
        {
            return;
        }

        if (_engine is { } engine)
        {
            ReportProgress(engine.Position, engine.Duration, force: true, "stopped");
        }

        _vm.UpNext = null;
        try
        {
            var current = App.Services.Db.GetItem(next.Id) ?? next;
            var plan = await PlaybackPlanner.PlanAsync(current, resume: true, null);
            await StartAsync(plan);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
        }
    }

    public void CancelUpNext()
    {
        _upNextCancelled = true;
        _vm.UpNext = null;
    }

    public async void LoadSubtitleFile()
    {
        if (_engine is not { } engine || _request is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        foreach (string type in new[] { ".srt", ".ass", ".ssa", ".sub", ".sup", ".vtt" })
        {
            picker.FileTypeFilter.Add(type);
        }

        InitializeWithWindow.Initialize(picker, _hwnd);
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        if (!engine.CanShowSubtitleFile(file.Path))
        {
            ShowMessage("This subtitle file plays through mpv, so Dolby Vision is shown as HDR10.");
            await OpenEngineAsync(EngineMode.Mpv, _request with { Start = engine.Position, Audio = engine.SelectedAudio ?? _request.Audio, Subtitle = null });
        }

        _engine?.AddSubtitleFile(file.Path);
        UpdateTracks();
    }

    public void SetSpeed(double speed)
    {
        _vm.Speed = speed;
        if (_engine is { } engine)
        {
            engine.Speed = speed;
        }
    }

    public void SetAudioDelay(int milliseconds)
    {
        _vm.AudioDelayMs = milliseconds;
        if (_engine is { } engine)
        {
            engine.AudioDelayMs = milliseconds;
        }
    }

    public void SetSubtitleDelay(double seconds)
    {
        _vm.SubtitleDelay = seconds;
        _engine?.SetSubtitleDelay(seconds);
    }

    public async void SwitchEngine()
    {
        if (_engine is not { } engine || _request is null)
        {
            return;
        }

        var target = engine.Kind == EngineMode.Mpv ? EngineMode.MediaFoundation : EngineMode.Mpv;
        await OpenEngineAsync(target, _request with
        {
            Start = engine.Position,
            Audio = engine.SelectedAudio ?? _request.Audio,
            Subtitle = engine.SelectedSubtitle,
        });
    }

    public void ToggleStatistics()
    {
        _vm.ShowStatistics = !_vm.ShowStatistics;
        if (_vm.ShowStatistics && _engine is { } engine)
        {
            _vm.Statistics = string.Join("\n", engine.Statistics());
        }
    }

    public void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        Log.Info($"Player closed at {_engine?.Position}");
        if (_engine is { } engine)
        {
            ReportProgress(engine.Position, engine.Duration > TimeSpan.Zero ? engine.Duration : TimeSpan.FromMilliseconds(_request?.Item.DurationMs ?? 0), force: true, "stopped");
        }

        _tick.Stop();
        _seekTimer.Stop();
        _messageTimer.Stop();
        _vm.Message = null;
        _vm.UpNext = null;
        SetCursorHidden(false);
        if (_vm.IsFullScreen || _vm.IsCompact)
        {
            App.Window.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        }

        App.Window.AppWindow.Changed -= OnAppWindowChanged;
        App.Window.Activated -= OnWindowActivated;
        MediaDevice.DefaultAudioRenderDeviceChanged -= OnDefaultAudioChanged;
        App.Services.Settings.Update(s =>
        {
            s.Volume = _vm.Volume;
            s.AtmosLipSyncMs = _vm.AudioDelayMs;
        });

        _overlay?.Close();
        _overlay = null;
        _overlayControls = null;
        _cursorBlocker?.Dispose();
        _cursorBlocker = null;
        MfElement.SetMediaPlayer(null);
        _mf?.Dispose();
        _mf = null;
        _mpv?.Dispose();
        _mpv = null;
        _engine = null;
        _request = null;
        App.Window.HidePlayerLayer();
        App.Services.Db.NotifyChanged();
    }

    // ---- keyboard ----

    /// <summary>
    /// Player shortcuts, fed from the window's root while the player is open, so they work wherever
    /// focus is (a double-click into full screen, for one, leaves it outside the player).
    /// </summary>
    public static void HandleKey(KeyRoutedEventArgs e)
    {
        if (s_instance is { _open: true } player)
        {
            Log.Guard("Player key", () => player.OnKey(e));
        }
    }

    private void OnKey(KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or NumberBox or AutoSuggestBox)
        {
            return;
        }

        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Space or VirtualKey.K or (VirtualKey)179:
                TogglePlay();
                break;
            case VirtualKey.Left or VirtualKey.J:
                SkipBy(-10);
                break;
            case VirtualKey.Right or VirtualKey.L:
                SkipBy(30);
                break;
            case VirtualKey.Up:
                SetVolume(Math.Min(100, _vm.Volume + 5));
                ShowControls();
                break;
            case VirtualKey.Down:
                SetVolume(Math.Max(0, _vm.Volume - 5));
                ShowControls();
                break;
            case VirtualKey.M:
                ToggleMute();
                break;
            case VirtualKey.F or VirtualKey.F11:
                ToggleFullScreen();
                break;
            case VirtualKey.Escape:
                if (_vm.IsFullScreen)
                {
                    ToggleFullScreen();
                }
                else if (_vm.IsCompact)
                {
                    ToggleCompact();
                }
                else
                {
                    Close();
                }

                break;
            case VirtualKey.N when _next is not null:
                PlayUpNext();
                break;
            case VirtualKey.S when ctrl && shift:
                ToggleStatistics();
                break;
            case (VirtualKey)219:
                ShiftAudio(-10);
                break;
            case (VirtualKey)221:
                ShiftAudio(10);
                break;
            case (VirtualKey)189 or VirtualKey.Subtract:
                ShiftAudio(-1);
                break;
            case (VirtualKey)187 or VirtualKey.Add:
                ShiftAudio(1);
                break;
            case (VirtualKey)190:
                _engine?.StepFrame(forward: true);
                ShowControls();
                break;
            case (VirtualKey)188 when _engine?.Kind == EngineMode.MediaFoundation:
                // Marquee's MKV reader restarts at keyframes, so Media Foundation's step-back
                // lands a whole GOP earlier. mpv steps back exactly.
                ShowMessage("Stepping back a frame works with mpv (Switch engine in playback settings)");
                break;
            case (VirtualKey)188:
                _engine?.StepFrame(forward: false);
                ShowControls();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ShiftAudio(int milliseconds)
    {
        SetAudioDelay(_vm.AudioDelayMs + milliseconds);
        ShowMessage(_engine?.Kind == EngineMode.MediaFoundation && _request?.PlayTrueHdAsObjects != true
            ? "Audio sync only works with mpv or TrueHD Atmos objects"
            : $"Audio sync {_vm.AudioDelayMs:+0;-0;0} ms");
    }

    // Closing removes this title bar from the window, so do it after its event has returned.
    private void PlayerTitleBar_BackRequested(TitleBar sender, object args) => DispatcherQueue.TryEnqueue(() => Log.Guard("Close", Close));

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    private const uint GA_ROOT = 2;
    private const uint GW_OWNER = 4;
}
