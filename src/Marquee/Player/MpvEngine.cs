using System.Globalization;
using Marquee.Data;
using Marquee.Playback;
using Marquee.Playback.Interop;
using Marquee.Playback.Mpv;
using Marquee.Plex;
using Marquee.Shell;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace Marquee.Player;

/// <summary>
/// libmpv in a Win32 child window (its own D3D11 swap chain, so HDR works). Plays everything;
/// Dolby Vision is tone-mapped to HDR10 by libplacebo. Marquee draws the controls, so mpv's own
/// OSC, OSD and key bindings are off.
/// </summary>
internal sealed class MpvEngine : IPlayerEngine
{
    private readonly DispatcherQueue _dispatcher;
    private readonly VideoHostWindow _host;
    private readonly MpvPlayer _mpv;
    private List<(string Id, TrackOption Option)> _audio = [];
    private List<(string Id, TrackOption Option)> _subtitles = [];
    private PlaybackRequest? _request;
    private TaskCompletionSource? _loaded;
    private TimeSpan _position;
    private TimeSpan _duration;
    private TimeSpan _buffered;
    private bool _paused;
    private bool _buffering;
    private int _audioDelay;
    private string _description = "mpv";

    public MpvEngine(IntPtr parentWindow, AppSettings settings)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _host = new VideoHostWindow(parentWindow);
        string log = Path.Combine(App.Services.DataDirectory, "mpv.log");
        _mpv = new MpvPlayer(_host.Handle, log,
        [
            ("config", settings.LoadMpvConfig ? "yes" : "no"),
            ("osc", "no"),
            ("osd-level", "0"),
            ("input-default-bindings", "no"),
            ("input-vo-keyboard", "no"),
            ("input-cursor", "no"),
            ("cursor-autohide", "no"),
            ("keep-open", "no"),
            ("idle", "yes"),
            ("force-window", "yes"),
            ("vo", "gpu-next"),
            ("gpu-api", "d3d11"),
            ("target-colorspace-hint", "yes"),
            ("sub-auto", "fuzzy"),
            ("cache", "yes"),
            ("demuxer-max-bytes", "512MiB"),
            ("demuxer-readahead-secs", "30"),
            ("network-timeout", "30"),
        ]);
        _mpv.Message += message => Log.Info($"mpv: {message}");
        _mpv.StateChanged += id => _dispatcher.TryEnqueue(() => Log.Guard($"mpv {id}", () => OnStateChanged(id)));
        _mpv.FileEnded += error => _dispatcher.TryEnqueue(() => Log.Guard("mpv ended", () =>
        {
            _loaded?.TrySetException(new InvalidOperationException(error ?? "ended"));
            if (error is null)
            {
                Ended?.Invoke();
            }
            else
            {
                Failed?.Invoke($"mpv couldn't play this file: {error}");
            }
        }));

        // Marquee's own settings win over mpv.conf, which loads during initialization.
        TrySet("hwdec", settings.HardwareDecoding);
        if (settings.ToneMapping != "auto")
        {
            TrySet("tone-mapping", settings.ToneMapping);
        }

        if (settings.AudioDevice is { Length: > 0 } device)
        {
            TrySet("audio-device", $"wasapi/{device}");
        }

        TrySet("audio-exclusive", settings.ExclusiveAudio ? "yes" : "no");
        TrySet("volume", settings.Volume.ToString(CultureInfo.InvariantCulture));
    }

    public EngineMode Kind => EngineMode.Mpv;
    public string Description => _description;
    public bool IsPlaying => !_paused && !_buffering;
    public bool IsBuffering => _buffering;
    public TimeSpan Position => _position;
    public TimeSpan Duration => _duration;
    public TimeSpan Buffered => _buffered;
    public bool CanShowSubtitles => true;

    public bool CanShowSubtitle(int ordinal) => true;

    public bool CanShowSubtitleFile(string path) => true;
    public IReadOnlyList<TrackOption> AudioTracks => _audio.Select(a => a.Option).ToList();
    public IReadOnlyList<TrackOption> SubtitleTracks => _subtitles.Select(s => s.Option).ToList();
    public int? SelectedAudio { get; private set; }
    public int? SelectedSubtitle { get; private set; }
    public IReadOnlyList<Chapter> Chapters { get; private set; } = [];

    public double Volume
    {
        get => double.TryParse(_mpv.GetProperty("volume"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 100;
        set => TrySet("volume", Math.Clamp(value, 0, 100).ToString(CultureInfo.InvariantCulture));
    }

    public bool Muted
    {
        get => _mpv.GetProperty("mute") == "yes";
        set => TrySet("mute", value ? "yes" : "no");
    }

    public double Speed
    {
        get => double.TryParse(_mpv.GetProperty("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 1;
        set => TrySet("speed", value.ToString(CultureInfo.InvariantCulture));
    }

    public int AudioDelayMs
    {
        get => _audioDelay;
        set
        {
            _audioDelay = value;
            TrySet("audio-delay", (value / 1000.0).ToString(CultureInfo.InvariantCulture));
        }
    }

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action? TracksChanged;

    public VideoHostWindow Host => _host;

    public void SetBounds(RectInt32 bounds) => _host.SetBounds(bounds);

    public void ShowVideo(bool visible) => _host.Show(visible);

    /// <summary>mpv's video window belongs to mpv's thread, so it hides the cursor itself.</summary>
    public void SetCursorHidden(bool hidden) => TrySet("cursor-autohide", hidden ? "always" : "no");

    /// <summary>
    /// mpv plays on without sound when it can't open its audio device, dropping the audio track
    /// ("aid" becomes "no"; Marquee never turns audio off itself).
    /// </summary>
    public bool PlaysWithoutSound =>
        AudioTracks.Count > 0 && (_mpv.GetProperty("aid") is null or "no" || string.IsNullOrEmpty(_mpv.GetProperty("current-ao")));

    /// <summary>Tries the audio device again, for when one has just appeared.</summary>
    public async Task RetryAudioAsync(int? ordinal)
    {
        if (_mpv.GetProperty("aid") is null or "no")
        {
            await SelectAudioAsync(ordinal ?? 0);
            return;
        }

        try
        {
            _mpv.Command("ao-reload");
        }
        catch (InvalidOperationException ex)
        {
            Log.Info($"mpv: {ex.Message}");
        }
    }

    public async Task OpenAsync(PlaybackRequest request)
    {
        _request = request;
        _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mpv.Load(request.Location, request.Start > TimeSpan.Zero ? request.Start : null);
        await _loaded.Task;
    }

    private void OnStateChanged(LibMpv.EventId id)
    {
        switch (id)
        {
            case LibMpv.EventId.FileLoaded:
                ReadChapters();
                ReadTracks();
                ApplyInitialTracks();
                _loaded?.TrySetResult();
                Opened?.Invoke();
                break;
            case LibMpv.EventId.VideoReconfig or LibMpv.EventId.AudioReconfig:
                UpdateDescription();
                break;
        }
    }

    private void ReadTracks()
    {
        var audio = new List<(string, TrackOption)>();
        var subtitles = new List<(string, TrackOption)>();
        int count = int.TryParse(_mpv.GetProperty("track-list/count"), out int n) ? n : 0;
        for (int i = 0; i < count; i++)
        {
            string? type = _mpv.GetProperty($"track-list/{i}/type");
            string? id = _mpv.GetProperty($"track-list/{i}/id");
            if (id is null || type is not ("audio" or "sub"))
            {
                continue;
            }

            string? lang = _mpv.GetProperty($"track-list/{i}/lang");
            string? title = _mpv.GetProperty($"track-list/{i}/title");
            string? codec = _mpv.GetProperty($"track-list/{i}/codec");
            bool forced = _mpv.GetProperty($"track-list/{i}/forced") == "yes";
            string language = LanguageName(lang);
            if (type == "audio")
            {
                int ordinal = audio.Count;
                var stream = _request?.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio && s.Ordinal == ordinal);
                string channels = MediaLabels.Channels(int.TryParse(_mpv.GetProperty($"track-list/{i}/demux-channel-count"), out int ch) ? ch : null);
                string label = stream is not null
                    ? Pages.DetailParts.TrackLabel(stream)
                    : Format.Join(language, MediaLabels.Codec(codec, _mpv.GetProperty($"track-list/{i}/codec-profile")) + channels, Short(title));
                audio.Add((id, new TrackOption(ordinal, label, null, Language.Key(lang), false, stream?.IsAtmos == true)));
            }
            else
            {
                int ordinal = subtitles.Count;
                string label = Format.Join(language, forced ? "Forced" : null, Short(title));
                subtitles.Add((id, new TrackOption(ordinal, label, codec?.ToUpperInvariant(), Language.Key(lang), forced)));
            }
        }

        _audio = audio;
        _subtitles = subtitles;
        SelectedAudio = _audio.FindIndex(a => a.Id == _mpv.GetProperty("aid")) is int ai and >= 0 ? ai : null;
        SelectedSubtitle = _subtitles.FindIndex(s => s.Id == _mpv.GetProperty("sid")) is int si and >= 0 ? si : null;
        TracksChanged?.Invoke();
    }

    private void ReadChapters()
    {
        int count = int.TryParse(_mpv.GetProperty("chapter-list/count"), out int n) ? n : 0;
        var chapters = new List<Chapter>();
        for (int i = 0; i < count; i++)
        {
            long start = (long)((Seconds($"chapter-list/{i}/time") ?? TimeSpan.Zero).TotalMilliseconds);
            string? title = _mpv.GetProperty($"chapter-list/{i}/title");
            chapters.Add(new Chapter(start, 0, string.IsNullOrWhiteSpace(title) ? null : title, null));
        }

        for (int i = 0; i < chapters.Count - 1; i++)
        {
            chapters[i] = chapters[i] with { EndMs = chapters[i + 1].StartMs };
        }

        Chapters = chapters;
    }

    private void ApplyInitialTracks()
    {
        if (_request is null)
        {
            return;
        }

        if (_request.Audio is int audio && audio < _audio.Count)
        {
            TrySet("aid", _audio[audio].Id);
            SelectedAudio = audio;
        }

        foreach (var external in _request.ExternalSubtitles)
        {
            _mpv.Command("sub-add", external.Url, "auto", external.Label, external.Language ?? "");
        }

        if (_request.ExternalSubtitles.Count > 0)
        {
            ReadTracks();
        }

        SelectSubtitle(_request.Subtitle);
    }

    private void UpdateDescription()
    {
        string? hwdec = _mpv.GetProperty("hwdec-current");
        string? gamma = _mpv.GetProperty("video-params/gamma");
        string? dv = _mpv.GetProperty("current-tracks/video/dolby-vision-profile");
        string range = dv is { Length: > 0 } && dv != "0" ? "DV → HDR10" : gamma switch
        {
            "pq" => "HDR10",
            "hlg" => "HLG",
            _ => "",
        };
        _description = Format.Join("mpv", hwdec is null or "no" ? "software" : hwdec, range);
    }

    public void Play() => TrySet("pause", "no");

    public void Pause() => TrySet("pause", "yes");

    public void Seek(TimeSpan position)
    {
        _position = position;
        try
        {
            _mpv.Command("seek", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void StepFrame(bool forward)
    {
        try
        {
            _mpv.Command(forward ? "frame-step" : "frame-back-step");
        }
        catch (InvalidOperationException)
        {
        }
    }

    public Task SelectAudioAsync(int ordinal)
    {
        if (ordinal < _audio.Count)
        {
            TrySet("aid", _audio[ordinal].Id);
            SelectedAudio = ordinal;
            TracksChanged?.Invoke();
        }

        return Task.CompletedTask;
    }

    public void SelectSubtitle(int? ordinal)
    {
        if (ordinal is int o && o >= 0 && o < _subtitles.Count)
        {
            TrySet("sid", _subtitles[o].Id);
            SelectedSubtitle = o;
        }
        else
        {
            TrySet("sid", "no");
            SelectedSubtitle = null;
        }

        TracksChanged?.Invoke();
    }

    public void AddSubtitleFile(string path)
    {
        try
        {
            _mpv.Command("sub-add", path, "select");
            ReadTracks();
        }
        catch (InvalidOperationException ex)
        {
            Failed?.Invoke($"Couldn't load {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    public void SetSubtitleDelay(double seconds) => TrySet("sub-delay", seconds.ToString(CultureInfo.InvariantCulture));

    public IEnumerable<string> Statistics() => _mpv.Describe();

    public void Tick()
    {
        _position = Seconds("time-pos") ?? _position;
        _duration = Seconds("duration") ?? _duration;
        _buffered = Seconds("demuxer-cache-time") ?? _buffered;
        _paused = _mpv.GetProperty("pause") == "yes";
        _buffering = _mpv.GetProperty("paused-for-cache") == "yes" || _mpv.GetProperty("seeking") == "yes";
    }

    private TimeSpan? Seconds(string property) =>
        double.TryParse(_mpv.GetProperty(property), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
            ? TimeSpan.FromSeconds(s)
            : null;

    private void TrySet(string property, string value)
    {
        try
        {
            _mpv.SetProperty(property, value);
        }
        catch (InvalidOperationException ex)
        {
            Log.Info($"mpv: {ex.Message}");
        }
    }

    private static string? Short(string? title) => title is { Length: > 0 and < 40 } t && !t.Contains('.') ? t : null;

    private static string LanguageName(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || tag == "und")
        {
            return "Unknown";
        }

        try
        {
            string name = CultureInfo.GetCultureInfo(Language.Key(tag) ?? tag).NativeName;
            return char.ToUpper(name[0]) + name[1..];
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    public void Dispose()
    {
        _mpv.Dispose();
        _host.Dispose();
    }
}
