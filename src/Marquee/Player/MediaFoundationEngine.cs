using Marquee.Data;
using Marquee.Playback;
using Marquee.Playback.Interop;
using Marquee.Playback.IO;
using Marquee.Playback.Matroska;
using Marquee.Playback.MediaFoundation;
using Marquee.Playback.SpatialAudio;
using Marquee.Plex;
using Marquee.Shell;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage;

namespace Marquee.Player;

/// <summary>
/// Windows' own pipeline: real Dolby Vision signalling (through Marquee's MKV reader for
/// Matroska), DD+ Atmos objects via Windows' Dolby decoder, DTS via DTS Sound Unbound, and TrueHD
/// either as FFmpeg's 7.1 bed or, with TrueHD Atmos on, as live objects through Spatial Sound.
/// No subtitle rendering yet.
/// </summary>
internal sealed class MediaFoundationEngine : IPlayerEngine
{
    private readonly MediaPlayer _player = new();
    private readonly AppSettings _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private List<(int? MfIndex, int? TrackNumber, TrackOption Option, string? CodecId)> _audio = [];
    private PlaybackRequest? _request;
    private MediaPlaybackItem? _item;
    private MediaSource? _source;
    private AtmosPlayback? _atmos;
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
        _player.MediaOpened += (_, _) => _dispatcher.TryEnqueue(OnOpened);
        _player.MediaEnded += (_, _) => _dispatcher.TryEnqueue(() => Ended?.Invoke());
        _player.MediaFailed += (_, e) => _dispatcher.TryEnqueue(() =>
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
        });
        _player.PlaybackSession.PlaybackStateChanged += (s, _) =>
            _atmos?.OnVideoStateChanged(s.Position, s.PlaybackState == MediaPlaybackState.Playing);
    }

    public MediaPlayer Player => _player;
    public EngineMode Kind => EngineMode.MediaFoundation;
    public string Description => _description;
    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public bool IsBuffering => _player.PlaybackSession.PlaybackState is MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
    public TimeSpan Position => _player.PlaybackSession.Position;
    public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;
    public TimeSpan Buffered => TimeSpan.Zero;
    public bool CanShowSubtitles => false;
    public IReadOnlyList<TrackOption> AudioTracks => _audio.Select(a => a.Option).ToList();
    public IReadOnlyList<TrackOption> SubtitleTracks =>
        _request?.Streams.Where(s => s.Kind == StreamKind.Subtitle && s.Ordinal >= 0)
            .Select(s => new TrackOption(s.Ordinal, Pages.DetailParts.TrackLabel(s), s.Codec?.ToUpperInvariant(), Language.Key(s.LanguageTag), s.Forced))
            .ToList() ?? [];
    public int? SelectedAudio { get; private set; }
    public int? SelectedSubtitle => null;
    public IReadOnlyList<Chapter> Chapters => [];

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

    public async Task OpenAsync(PlaybackRequest request)
    {
        CloseCurrent();
        _request = request;
        _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await MatchAudioDeviceAsync();

        string location = request.Location;
        if (MatroskaMediaSource.Handles(location))
        {
            bool windowsDts = StoreCodecRuntime.Loaded && StoreCodecRuntime.HasDtsDecoder;
            var tracks = await Task.Run(() =>
            {
                using var probe = MatroskaDemuxer.Open(location, 64 * 1024);
                return probe.Tracks.Where(t => t.Kind == TrackKind.Audio).Select(t => (t.Number, t.CodecId, t.DisplayLanguage, t.Name, t.Channels)).ToList();
            });

            int? atmosTrack = null;
            if (request.PlayTrueHdAsObjects && request.Audio is int wanted && wanted < tracks.Count && tracks[wanted].CodecId == "A_TRUEHD")
            {
                atmosTrack = tracks[wanted].Number;
            }

            var mkv = await Task.Run(() => MatroskaMediaSource.Open(location, _settings.DolbyVision, windowsDts, atmosTrack));
            mkv.Error += message => Log.Error($"MKV reader: {message}");
            Log.Info("MKV reader:\n  " + string.Join("\n  ", mkv.Description));
            if (atmosTrack is int track)
            {
                StartAtmos(location, mkv, track);
            }

            _audio = tracks.Select((t, i) =>
            {
                var stream = request.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio && s.Ordinal == i);
                string label = stream is not null
                    ? Pages.DetailParts.TrackLabel(stream)
                    : Format.Join(t.DisplayLanguage, MediaLabels.Codec(CodecName(t.CodecId)) + MediaLabels.Channels(t.Channels), t.Name);
                return ((int?)null, (int?)t.Number, new TrackOption(i, label, null, Language.Key(t.DisplayLanguage), false, stream?.IsAtmos == true), (string?)t.CodecId);
            }).ToList();

            var dv = mkv.Demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.DolbyVision;
            _description = Format.Join("Media Foundation",
                dv is not null && _settings.DolbyVision ? $"Dolby Vision P{dv.Profile}{(dv.Profile == 7 ? " → 8.1" : "")}" : null,
                atmosTrack is not null ? "TrueHD Atmos objects" : null);
            _source = MediaSource.CreateFromMediaStreamSource(mkv.Source);
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
        _item.AudioTracksChanged += (_, _) => _dispatcher.TryEnqueue(MapAudioTracks);
        _player.Source = _item;
        await _opened.Task;
    }

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
        if (!wantsObjects && !hasObjects && _audio[ordinal].MfIndex is not null)
        {
            SelectMfAudio(ordinal);
            TracksChanged?.Invoke();
            return;
        }

        // The TrueHD Atmos path plays audio outside Media Foundation, so switching in or out reopens.
        bool playing = IsPlaying;
        await OpenAsync(_request with { Audio = ordinal, Start = Position });
        if (playing)
        {
            Play();
        }
    }

    public void SelectSubtitle(int? ordinal)
    {
    }

    public void AddSubtitleFile(string path)
    {
    }

    public void SetSubtitleDelay(double seconds)
    {
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

        return lines;
    }

    public void Tick() =>
        _atmos?.UpdateVideoClock(_player.PlaybackSession.Position, _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);

    private void StartAtmos(string location, MatroskaMediaSource mkv, int track)
    {
        _atmos = new AtmosPlayback(location, track, new TrueHdAtmosDecoder());
        _atmos.Output.OffsetMs = _audioDelay;
        _atmos.Output.Muted = _player.IsMuted;
        _atmos.Failed += message => Log.Error($"Atmos: {message}");
        mkv.Seeked += _atmos.OnSeeked;
        mkv.StartGate = _atmos.WaitUntilReadyAsync;
        mkv.StartReleased += _atmos.OnStartReleased;
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
        _atmos?.Dispose();
        _atmos = null;
        _player.Pause();
        _player.Source = null;
        _source?.Dispose();
        _source = null;
        _item = null;
    }

    public void Dispose()
    {
        CloseCurrent();
        _player.Dispose();
    }
}
