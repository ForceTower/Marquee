using Marquee.Data;
using Marquee.Playback;
using Marquee.Playback.Demuxing;
using Marquee.Playback.Interop;
using Marquee.Playback.IO;
using Marquee.Playback.MediaFoundation;
using Marquee.Playback.SpatialAudio;
using Marquee.Playback.Subtitles;
using Marquee.Plex;
using Marquee.Shell;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage;

namespace Marquee.Player;

/// <summary>
/// Windows' own pipeline: real Dolby Vision signalling (through Marquee's own demuxing for
/// Matroska and MPEG-TS, Windows' source for MP4), DD+ Atmos objects via Windows' Dolby decoder,
/// DTS via DTS Sound Unbound, and TrueHD either as FFmpeg's 7.1 bed or, with TrueHD Atmos on, as
/// live objects through Spatial Sound. Subtitles are rendered by Marquee (libass for text, FFmpeg
/// for PGS/VobSub) into frames the player view draws over the video, timed to the video's
/// <see cref="PresentationClock"/>.
/// </summary>
internal sealed class MediaFoundationEngine : IPlayerEngine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>A subtitle the user can pick: an embedded track, a downloadable sidecar, or a file on disk.</summary>
    private sealed record SubtitleChoice(TrackOption Option, SubtitleCodec? Codec, MediaTrack? Track = null, string? Url = null, string? Path = null);

    private readonly MediaPlayer _player = new();
    private readonly AppSettings _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private List<(int? MfIndex, int? TrackNumber, TrackOption Option, string? CodecId)> _audio = [];
    private List<SubtitleChoice> _subtitleChoices = [];
    private readonly List<string> _subtitleFiles = [];
    private PlaybackRequest? _request;
    private MediaPlaybackItem? _item;
    private MediaSource? _source;
    private DemuxerMediaSource? _demuxed;
    private MediaTrack? _videoTrack;
    private PresentationClock _clock = new();
    private AtmosPlayback? _atmos;
    private SubtitlePlayback? _subtitles;
    private int _subtitleGeneration;
    private (int Width, int Height) _subtitleArea;
    private int _subtitleReserve;
    private double _subtitleDelay;
    private TaskCompletionSource? _opened;
    private TimeSpan? _pendingStart;
    private int? _pendingAudioIndex;
    private string _description = "Media Foundation";
    private int _audioDelay;

    public MediaFoundationEngine(AppSettings settings)
    {
        _settings = settings;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _player.AutoPlay = false;
        _player.Volume = settings.Volume / 100;
        _player.MediaOpened += (_, _) => _dispatcher.TryEnqueue(() => Log.Guard("MF opened", OnOpened));
        _player.MediaEnded += (_, _) => _dispatcher.TryEnqueue(() => Log.Guard("MF ended", () => Ended?.Invoke()));
        _player.MediaFailed += (_, e) => _dispatcher.TryEnqueue(() => Log.Guard("MF failed", () =>
        {
            string message = $"Media Foundation couldn't play this file: {e.ErrorMessage} (0x{e.ExtendedErrorCode?.HResult:X8})";
            Log.Error(message);
            if (_opened is { Task.IsCompleted: false } opened)
            {
                opened.TrySetException(new InvalidOperationException(message));
            }
            else
            {
                Failed?.Invoke(message);
            }
        }));
        _player.PlaybackSession.PlaybackStateChanged += (s, _) =>
            Log.Guard("MF state", () => _clock.OnStateChanged(s.Position, s.PlaybackState == MediaPlaybackState.Playing));
        _player.PlaybackSession.NaturalVideoSizeChanged += (_, _) => _dispatcher.TryEnqueue(() => Log.Guard("MF video size", ApplySubtitleLayout));
    }

    public MediaPlayer Player => _player;
    public EngineMode Kind => EngineMode.MediaFoundation;
    public string Description => _description;
    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public bool PlaysWithoutSound => _request?.WithoutSound == true;
    public bool IsBuffering => _player.PlaybackSession.PlaybackState is MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
    public TimeSpan Position => _player.PlaybackSession.Position;
    public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;
    public TimeSpan Buffered => TimeSpan.Zero;
    public bool CanShowSubtitles => true;
    public IReadOnlyList<TrackOption> AudioTracks => _audio.Select(a => a.Option).ToList();
    public IReadOnlyList<TrackOption> SubtitleTracks => _subtitleChoices.Select(s => s.Option).ToList();
    public int? SelectedAudio { get; private set; }
    public int? SelectedSubtitle { get; private set; }
    public IReadOnlyList<Chapter> Chapters => [];

    /// <summary>Raised on the subtitle thread with what to draw over the video; the handler must release each frame.</summary>
    public event Action<SubtitleFrame>? SubtitlesRendered;

    public double Volume
    {
        get => _player.Volume * 100;
        set => _player.Volume = Math.Clamp(value, 0, 100) / 100;
    }

    public bool Muted
    {
        get => _player.IsMuted;
        set
        {
            _player.IsMuted = value;
            if (_atmos is not null)
            {
                _atmos.Output.Muted = value;
            }
        }
    }

    public double Speed
    {
        get => _player.PlaybackSession.PlaybackRate;
        set => _player.PlaybackSession.PlaybackRate = value;
    }

    /// <summary>Only the TrueHD Atmos path can shift audio (Media Foundation's own renderer can't).</summary>
    public int AudioDelayMs
    {
        get => _audioDelay;
        set
        {
            _audioDelay = value;
            if (_atmos is not null)
            {
                _atmos.Output.OffsetMs = value;
            }
        }
    }

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action? TracksChanged;

    /// <summary>Whether the engine opens files of this container (Plex's name or the file's extension).</summary>
    public static bool PlaysContainer(string container) => container switch
    {
        "mkv" or "mk3d" or "mp4" or "m4v" or "mov" => true,
        "mpegts" or "ts" or "m2ts" or "mts" => FfmpegLibraries.CanDemux,
        _ => false,
    };

    /// <summary>
    /// Whether Media Foundation can show subtitle <paramref name="ordinal"/> of a request: embedded
    /// tracks need a container Marquee reads (MKV, MPEG-TS, or MP4 through libavformat), sidecars a
    /// text format.
    /// </summary>
    public static bool CanShow(PlaybackRequest request, int ordinal)
    {
        var embedded = request.Streams.Where(s => s.Kind == StreamKind.Subtitle && s.Ordinal >= 0).ToList();
        if (ordinal < embedded.Count)
        {
            return SubtitleOpener.CanShowEmbedded(request.Location, SubtitleCodecs.FromName(embedded.FirstOrDefault(s => s.Ordinal == ordinal)?.Codec));
        }

        var external = request.Streams.Where(s => s.Kind == StreamKind.Subtitle && s.ExternalKey is not null).ElementAtOrDefault(ordinal - embedded.Count);
        return SubtitleOpener.CanShowFile(SubtitleCodecs.FromName(external?.Codec));
    }

    public async Task OpenAsync(PlaybackRequest request)
    {
        if (_request?.Item.Id != request.Item.Id)
        {
            _subtitleFiles.Clear();
        }

        CloseCurrent();
        _request = request;
        _clock = new PresentationClock();
        _clock.Trace += message => Log.Info($"MF clock: {message}");
        _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string location = request.Location;

        // Windows' MP4 source doesn't hand over subtitle tracks, so list them through libavformat
        // while Media Foundation opens the file.
        Task<IReadOnlyList<MediaTrack>>? mp4Tracks = !DemuxerMediaSource.Handles(location) && SubtitleOpener.CanReadEmbedded(location)
            ? Task.Run(() => ListTracks(location))
            : null;
        _subtitleChoices = [];
        await MatchAudioDeviceAsync();

        IReadOnlyList<MediaTrack> subtitleTracks = [];
        if (DemuxerMediaSource.Handles(location))
        {
            bool windowsDts = StoreCodecRuntime.Loaded && StoreCodecRuntime.HasDtsDecoder;
            var demuxer = await Task.Run(() => Demuxer.Open(location));
            var tracks = demuxer.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
            subtitleTracks = demuxer.Tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();

            int? atmosTrack = null;
            if (!request.WithoutSound && request.PlayTrueHdAsObjects && request.Audio is int wanted && wanted < tracks.Count && tracks[wanted].CodecId == "A_TRUEHD")
            {
                atmosTrack = tracks[wanted].Number;
            }

            string reader = $"{demuxer.Container} reader";
            var demuxed = await Task.Run(() => DemuxerMediaSource.Open(demuxer, _settings.DolbyVision, windowsDts, atmosTrack, audio: !request.WithoutSound));
            demuxed.Error += message => Log.Error($"{reader}: {message}");
            demuxed.Trace += message => Log.Info($"{reader}: {message}");
            demuxed.Seeked += _clock.OnSeeked;
            demuxed.StartReleased += _clock.OnStartReleased;
            Log.Info($"{reader}:\n  " + string.Join("\n  ", demuxed.Description));
            _demuxed = demuxed;
            _videoTrack = demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
            if (atmosTrack is int track)
            {
                StartAtmos(location, demuxed, track);
            }

            _audio = tracks.Select((t, i) =>
            {
                var stream = request.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio && s.Ordinal == i);
                string label = stream is not null
                    ? Pages.DetailParts.TrackLabel(stream)
                    : Format.Join(t.DisplayLanguage, MediaLabels.Codec(CodecName(t.CodecId)) + MediaLabels.Channels(t.Channels), t.Name);
                return ((int?)null, (int?)t.Number, new TrackOption(i, label, null, Language.Key(t.DisplayLanguage), false, stream?.IsAtmos == true), (string?)t.CodecId);
            }).ToList();

            var dv = _videoTrack?.DolbyVision;
            _description = Format.Join("Media Foundation",
                dv is not null && _settings.DolbyVision ? $"Dolby Vision P{dv.Profile}{(dv.Profile == 7 ? " → 8.1" : "")}" : null,
                atmosTrack is not null ? "TrueHD Atmos objects" : null,
                request.WithoutSound ? "no sound" : null);
            _source = MediaSource.CreateFromMediaStreamSource(demuxed.Source);
        }
        else
        {
            _source = MediaFile.IsUrl(location)
                ? MediaSource.CreateFromUri(new Uri(location))
                : MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(location));
            _audio = [];
            _description = "Media Foundation";
        }

        // Not MediaPlaybackItem(source, start): that trims the item. Seek once it has opened.
        _pendingStart = request.Start > TimeSpan.Zero ? request.Start : null;
        _pendingAudioIndex = request.Audio;
        _item = new MediaPlaybackItem(_source);
        _item.AudioTracksChanged += (_, _) => _dispatcher.TryEnqueue(() => Log.Guard("MF audio tracks", MapAudioTracks));
        _player.Source = _item;
        await _opened.Task;
        if (mp4Tracks is not null)
        {
            var tracks = await mp4Tracks;
            subtitleTracks = tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
            _videoTrack = tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
        }

        _subtitleChoices = SubtitleChoices(request, subtitleTracks);
        SelectSubtitle(request.Subtitle);
    }

    /// <summary>A file's tracks as libavformat lists them, or none if it can't be read (the subtitles are then mpv's to show).</summary>
    private static IReadOnlyList<MediaTrack> ListTracks(string location)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var demuxer = Demuxer.Open(location, 16 * 1024);
            Log.Info($"{demuxer.Container} tracks listed through libavformat in {stopwatch.ElapsedMilliseconds} ms");
            return demuxer.Tracks;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't list the subtitle tracks of {location}", ex);
            return [];
        }
    }

    /// <summary>
    /// Embedded tracks first (labelled from the library's stream info when there is one), then Plex
    /// sidecars, then files loaded in the player: the same order mpv lists them in.
    /// </summary>
    private List<SubtitleChoice> SubtitleChoices(PlaybackRequest request, IReadOnlyList<MediaTrack> tracks)
    {
        var choices = new List<SubtitleChoice>();
        var streams = request.Streams.Where(s => s.Kind == StreamKind.Subtitle && s.Ordinal >= 0).ToList();
        if (tracks.Count > 0)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var stream = streams.FirstOrDefault(s => s.Ordinal == i);
                var codec = SubtitleCodecs.FromMatroska(t.CodecId);
                string label = stream is not null
                    ? Pages.DetailParts.TrackLabel(stream)
                    : Format.Join(t.DisplayLanguage, t.IsForced ? "Forced" : null, t.Name);
                string detail = stream?.Codec?.ToUpperInvariant() ?? CodecLabel(codec) ?? t.CodecId;
                choices.Add(new SubtitleChoice(new TrackOption(i, label, detail, Language.Key(t.DisplayLanguage), t.IsForced || stream?.Forced == true), codec, Track: t));
            }
        }
        else
        {
            choices.AddRange(streams.Select(s => new SubtitleChoice(
                new TrackOption(s.Ordinal, Pages.DetailParts.TrackLabel(s), s.Codec?.ToUpperInvariant(), Language.Key(s.LanguageTag), s.Forced), null)));
        }

        var externals = request.Streams.Where(s => s.Kind == StreamKind.Subtitle && s.ExternalKey is not null).ToList();
        for (int i = 0; i < request.ExternalSubtitles.Count; i++)
        {
            var (label, language, url) = request.ExternalSubtitles[i];
            string? codec = externals.ElementAtOrDefault(i)?.Codec;
            choices.Add(new SubtitleChoice(new TrackOption(choices.Count, label, codec?.ToUpperInvariant(), Language.Key(language)), SubtitleCodecs.FromName(codec), Url: url));
        }

        foreach (string path in _subtitleFiles)
        {
            choices.Add(FileChoice(path, choices.Count));
        }

        return choices;
    }

    private static SubtitleChoice FileChoice(string path, int ordinal)
    {
        var codec = SubtitleCodecs.FromName(System.IO.Path.GetExtension(path).TrimStart('.'));
        return new SubtitleChoice(new TrackOption(ordinal, System.IO.Path.GetFileNameWithoutExtension(path), CodecLabel(codec), null), codec, Path: path);
    }

    private static string? CodecLabel(SubtitleCodec? codec) => codec switch
    {
        SubtitleCodec.SubRip => "SRT",
        SubtitleCodec.WebVtt => "VTT",
        SubtitleCodec.DvbSub => "DVB",
        { } c => c.ToString().ToUpperInvariant(),
        null => null,
    };

    private static string CodecName(string codecId) => codecId switch
    {
        "A_TRUEHD" => "truehd",
        "A_EAC3" => "eac3",
        "A_AC3" => "ac3",
        "A_DTS" => "dca",
        "A_AAC" => "aac",
        _ => codecId.Replace("A_", "").ToLowerInvariant(),
    };

    private void OnOpened()
    {
        MapAudioTracks();
        if (_pendingStart is { } start)
        {
            _pendingStart = null;
            _player.PlaybackSession.Position = start;
        }

        if (_pendingAudioIndex is int ordinal && _atmos is null)
        {
            SelectMfAudio(ordinal);
        }

        _pendingAudioIndex = null;
        ApplySubtitleLayout();
        _opened?.TrySetResult();
        Opened?.Invoke();
    }

    /// <summary>The MKV reader names each audio stream "track N" (its Matroska number); MP4 tracks map by position.</summary>
    private void MapAudioTracks()
    {
        if (_item is null)
        {
            return;
        }

        if (_audio.Count == 0)
        {
            _audio = Enumerable.Range(0, _item.AudioTracks.Count)
                .Select(i => ((int?)i, (int?)null, new TrackOption(i, Format.Join(_item.AudioTracks[i].Language, _item.AudioTracks[i].Label), null, Language.Key(_item.AudioTracks[i].Language)), (string?)null))
                .ToList();
        }
        else
        {
            for (int i = 0; i < _audio.Count; i++)
            {
                int? index = null;
                for (int t = 0; t < _item.AudioTracks.Count; t++)
                {
                    if (_item.AudioTracks[t].Name == $"track {_audio[i].TrackNumber}")
                    {
                        index = t;
                    }
                }

                _audio[i] = _audio[i] with { MfIndex = index };
            }
        }

        if (_atmos is not null)
        {
            SelectedAudio = _request?.Audio;
        }
        else if (_item.AudioTracks.SelectedIndex is int selected && selected >= 0)
        {
            SelectedAudio = _audio.FirstOrDefault(a => a.MfIndex == selected).Option?.Ordinal;
        }

        TracksChanged?.Invoke();
    }

    private void SelectMfAudio(int ordinal)
    {
        if (_item is not null && ordinal < _audio.Count && _audio[ordinal].MfIndex is int index && index < _item.AudioTracks.Count)
        {
            _item.AudioTracks.SelectedIndex = index;
            SelectedAudio = ordinal;
        }
    }

    public async Task SelectAudioAsync(int ordinal)
    {
        if (_request is null || ordinal >= _audio.Count)
        {
            return;
        }

        bool wantsObjects = _request.PlayTrueHdAsObjects && _audio[ordinal].CodecId == "A_TRUEHD";
        bool hasObjects = _atmos is not null;
        if (!wantsObjects && !hasObjects && !_request.WithoutSound && _audio[ordinal].MfIndex is not null)
        {
            SelectMfAudio(ordinal);
            TracksChanged?.Invoke();
            return;
        }

        // The TrueHD Atmos path plays audio outside Media Foundation, so switching in or out reopens;
        // so does picking a track while playing without sound, which tries the audio output again.
        bool playing = IsPlaying;
        await OpenAsync(_request with { Audio = ordinal, Start = Position, WithoutSound = false });
        if (playing)
        {
            Play();
        }
    }

    public bool CanShowSubtitle(int ordinal) =>
        _subtitleChoices.ElementAtOrDefault(ordinal) is { } choice && (choice.Track is not null
            ? SubtitleOpener.CanShow(choice.Codec)
            : (choice.Url ?? choice.Path) is not null && SubtitleOpener.CanShowFile(choice.Codec));

    public bool CanShowSubtitleFile(string path) => SubtitleOpener.CanShowFile(SubtitleCodecs.FromName(System.IO.Path.GetExtension(path).TrimStart('.')));

    public void SelectSubtitle(int? ordinal)
    {
        StopSubtitles();
        SelectedSubtitle = ordinal is int o && CanShowSubtitle(o) ? o : null;
        if (_request is not null)
        {
            _request = _request with { Subtitle = SelectedSubtitle };
        }

        if (SelectedSubtitle is int selected)
        {
            _ = StartSubtitlesAsync(_subtitleChoices[selected]);
        }

        TracksChanged?.Invoke();
    }

    public void AddSubtitleFile(string path)
    {
        if (!CanShowSubtitleFile(path))
        {
            Failed?.Invoke($"Media Foundation can't show {System.IO.Path.GetFileName(path)}");
            return;
        }

        _subtitleFiles.Add(path);
        _subtitleChoices.Add(FileChoice(path, _subtitleChoices.Count));
        SelectSubtitle(_subtitleChoices.Count - 1);
    }

    public void SetSubtitleDelay(double seconds)
    {
        _subtitleDelay = seconds;
        if (_subtitles is not null)
        {
            _subtitles.Delay = TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>The overlay's size in physical pixels; the video is fitted into it as MediaPlayerElement does.</summary>
    public void SetSubtitleArea(int width, int height)
    {
        _subtitleArea = (width, height);
        ApplySubtitleLayout();
    }

    public void SetSubtitleBottomReserve(int pixels)
    {
        _subtitleReserve = pixels;
        ApplySubtitleLayout();
    }

    private void ApplySubtitleLayout()
    {
        if (_subtitles is not { } subtitles)
        {
            return;
        }

        var (width, height) = VideoSize;
        subtitles.Layout = SubtitleLayout.Fit(_subtitleArea.Width, _subtitleArea.Height, width, height) with { BottomReserve = _subtitleReserve };
    }

    public (int Width, int Height) VideoSize
    {
        get
        {
            var session = _player.PlaybackSession;
            if (session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0)
            {
                return ((int)session.NaturalVideoWidth, (int)session.NaturalVideoHeight);
            }

            return (_videoTrack?.Width ?? 0, _videoTrack?.Height ?? 0);
        }
    }

    private async Task StartSubtitlesAsync(SubtitleChoice choice)
    {
        int generation = ++_subtitleGeneration;
        string location = _request?.Location ?? "";
        var demuxer = _demuxed?.Demuxer;
        var clock = _clock;
        var video = _videoTrack;
        var (width, height) = VideoSize;
        double frameRate = video is { DefaultDurationNs: > 0 } ? 1e9 / video.DefaultDurationNs : 24;
        var style = TextStyle.Parse(_settings.SubtitleColor, _settings.SubtitleOutlineColor);
        void Report(string message) => Log.Info($"Subtitles: {message}");

        SubtitlePlayback playback;
        try
        {
            playback = await Task.Run(async () =>
            {
                if (choice.Track is { } track)
                {
                    return SubtitleOpener.OpenEmbedded(location, track, demuxer, video, clock.Now, Report, style);
                }

                byte[] content = choice.Url is { } url ? await Http.GetByteArrayAsync(url) : await File.ReadAllBytesAsync(choice.Path!);
                return SubtitleOpener.OpenFile(content, choice.Codec ?? SubtitleCodec.SubRip, choice.Option.Label, width, height, frameRate, clock.Now, Report, style);
            });
        }
        catch (Exception ex)
        {
            if (generation == _subtitleGeneration)
            {
                Log.Error($"Subtitles: couldn't show '{choice.Option.Label}'", ex);
                Failed?.Invoke($"Couldn't show the subtitles: {ex.Message}");
            }

            return;
        }

        if (generation != _subtitleGeneration)
        {
            playback.Dispose();
            return;
        }

        Log.Info($"Subtitles: {playback.Description}");
        _subtitles = playback;
        playback.Delay = TimeSpan.FromSeconds(_subtitleDelay);
        playback.FrameReady += frame =>
        {
            if (SubtitlesRendered is { } handler)
            {
                handler(frame);
            }
            else
            {
                frame.Release();
            }
        };
        ApplySubtitleLayout();
    }

    private void StopSubtitles()
    {
        _subtitleGeneration++;
        if (_subtitles is { } subtitles)
        {
            _subtitles = null;
            subtitles.Dispose();
        }

        SubtitlesRendered?.Invoke(SubtitleFrame.Empty);
    }

    public void Play() => _player.Play();

    public void Pause() => _player.Pause();

    public void Seek(TimeSpan position) => _player.PlaybackSession.Position = position;

    public void StepFrame(bool forward)
    {
        _player.Pause();
        if (forward)
        {
            _player.StepForwardOneFrame();
        }
        else
        {
            _player.StepBackwardOneFrame();
        }
    }

    public IEnumerable<string> Statistics()
    {
        var lines = _item is null ? new List<string>() : MfDescribe.Playback(_player, _item).ToList();
        if (_atmos is { } atmos)
        {
            var o = atmos.Output;
            lines.Add($"Atmos: sync {o.SyncErrorMs:F1} ms, rate {o.Ratio:0.00000}, offset {o.OffsetMs} ms, {o.Resyncs} resyncs, {o.Underruns} underruns · {o.Info}");
        }

        if (_subtitles is { } subtitles)
        {
            lines.Add($"Subtitles: {subtitles.Description}");
        }

        return lines;
    }

    public void Tick() =>
        _clock.Update(_player.PlaybackSession.Position, _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);

    private void StartAtmos(string location, DemuxerMediaSource demuxed, int track)
    {
        _atmos = new AtmosPlayback(location, track, new TrueHdAtmosDecoder(), _clock);
        _atmos.Output.OffsetMs = _audioDelay;
        _atmos.Output.Muted = _player.IsMuted;
        _atmos.Failed += message => Log.Error($"Atmos: {message}");
        demuxed.Seeked += _atmos.OnSeeked;
        demuxed.StartGate = _atmos.WaitUntilReadyAsync;
    }

    /// <summary>Follows the audio device chosen in Settings (by name), else Windows' default.</summary>
    private async Task MatchAudioDeviceAsync()
    {
        DeviceInformation? device = null;
        if (_settings.AudioDevice is { Length: > 0 } name)
        {
            var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            device = devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (device is not null)
        {
            _player.AudioDevice = device;
        }
    }

    private void CloseCurrent()
    {
        StopSubtitles();
        _atmos?.Dispose();
        _atmos = null;
        _player.Pause();
        _player.Source = null;
        _source?.Dispose();
        _source = null;
        _demuxed = null;
        _videoTrack = null;
        _item = null;
    }

    public void Dispose()
    {
        CloseCurrent();
        _player.Dispose();
    }
}
