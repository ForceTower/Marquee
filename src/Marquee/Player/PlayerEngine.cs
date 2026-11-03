using Marquee.Data;

namespace Marquee.Player;

/// <summary>A track the user can pick. <see cref="Ordinal"/> indexes streams of that kind in the file (0-based).</summary>
public sealed record TrackOption(int Ordinal, string Label, string? Detail, string? Language, bool Forced = false, bool IsAtmos = false);

/// <summary>Everything an engine needs to start playing one file.</summary>
internal sealed record PlaybackRequest
{
    public required MediaItem Item { get; init; }
    public required MediaVersion Version { get; init; }

    /// <summary>A file path or a direct-play URL.</summary>
    public required string Location { get; init; }

    public TimeSpan Start { get; init; }
    public IReadOnlyList<StreamInfo> Streams { get; init; } = [];
    public IReadOnlyList<Chapter> Chapters { get; init; } = [];
    public IReadOnlyList<Marker> Markers { get; init; } = [];

    /// <summary>Audio ordinal to start with, or null for the engine's default.</summary>
    public int? Audio { get; init; }

    /// <summary>Subtitle ordinal, or null for none.</summary>
    public int? Subtitle { get; init; }

    /// <summary>Plex sidecar subtitle URLs, by ordinal after the embedded ones.</summary>
    public IReadOnlyList<(string Label, string? Language, string Url)> ExternalSubtitles { get; init; } = [];

    public bool PlayTrueHdAsObjects { get; init; }

    /// <summary>
    /// Leave the audio out because no output can play it. Media Foundation's clock follows its audio
    /// renderer, so an output that never takes the samples would freeze the picture.
    /// </summary>
    public bool WithoutSound { get; init; }
}

internal interface IPlayerEngine : IDisposable
{
    EngineMode Kind { get; }

    /// <summary>"mpv · D3D11" or "Media Foundation · Dolby Vision", for the player's badge.</summary>
    string Description { get; }

    bool IsPlaying { get; }
    bool IsBuffering { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    TimeSpan Buffered { get; }
    double Volume { get; set; }
    bool Muted { get; set; }
    double Speed { get; set; }

    /// <summary>Audio delay in ms; positive delays audio. Engines that can't shift audio ignore it.</summary>
    int AudioDelayMs { get; set; }

    bool CanShowSubtitles { get; }

    /// <summary>Whether this engine can show that subtitle track (false means it plays through mpv).</summary>
    bool CanShowSubtitle(int ordinal);

    /// <summary>Whether this engine can load that subtitle file.</summary>
    bool CanShowSubtitleFile(string path);
    IReadOnlyList<TrackOption> AudioTracks { get; }
    IReadOnlyList<TrackOption> SubtitleTracks { get; }
    int? SelectedAudio { get; }
    int? SelectedSubtitle { get; }

    /// <summary>Chapters read from the file itself (empty if the engine can't read them).</summary>
    IReadOnlyList<Chapter> Chapters { get; }

    /// <summary>Playing with an audio track chosen but no audio output to play it on.</summary>
    bool PlaysWithoutSound { get; }

    event Action? Opened;
    event Action? Ended;
    event Action<string>? Failed;
    event Action? TracksChanged;

    Task OpenAsync(PlaybackRequest request);
    void Play();
    void Pause();
    void Seek(TimeSpan position);

    /// <summary>Pauses and moves one frame forward or back.</summary>
    void StepFrame(bool forward);
    Task SelectAudioAsync(int ordinal);
    void SelectSubtitle(int? ordinal);
    void AddSubtitleFile(string path);
    void SetSubtitleDelay(double seconds);

    /// <summary>
    /// Keeps bottom subtitles out of a strip this many pixels tall at the bottom of the video area
    /// (the controls, while they show); 0 puts them back. The engine eases them between the two.
    /// </summary>
    void SetSubtitleBottomReserve(int pixels);
    IEnumerable<string> Statistics();

    /// <summary>Called every 50 ms from the UI thread while open.</summary>
    void Tick();
}
