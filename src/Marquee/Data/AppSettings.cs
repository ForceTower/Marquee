using System.Text.Json;

namespace Marquee.Data;

public enum EngineMode
{
    Automatic,
    Mpv,
    MediaFoundation,
}

public enum SubtitleMode
{
    Off,
    ForcedOnly,
    Always,
}

public enum ResumeMode
{
    Always,
    Never,
}

public enum DynamicRangeMatching
{
    Off,

    /// <summary>HDR and Dolby Vision video turn Windows HDR on; other video leaves it as it was.</summary>
    HdrVideo,

    /// <summary>HDR on for HDR video and off for SDR video.</summary>
    EveryVideo,
}

public sealed class AppSettings
{
    public string Accent { get; set; } = "#F5B544";

    public EngineMode Engine { get; set; } = EngineMode.Automatic;
    public bool DolbyVision { get; set; } = true;
    public bool TrueHdAtmosObjects { get; set; } = true;
    public string HardwareDecoding { get; set; } = "auto-safe";
    public string ToneMapping { get; set; } = "auto";
    public bool LoadMpvConfig { get; set; } = true;

    /// <summary>Switch the display's HDR to suit the video, and back when playback ends.</summary>
    public DynamicRangeMatching MatchDynamicRange { get; set; } = DynamicRangeMatching.HdrVideo;

    /// <summary>Switch the display to a refresh rate that's a whole multiple of the video's frame rate.</summary>
    public bool MatchFrameRate { get; set; }

    /// <summary>Audio endpoint by name (GUIDs change when Windows re-enumerates devices); null follows Windows.</summary>
    public string? AudioDevice { get; set; }
    public bool ExclusiveAudio { get; set; }
    public double Volume { get; set; } = 100;
    public int AtmosLipSyncMs { get; set; }

    public string PreferredAudioLanguage { get; set; } = "";
    public string PreferredSubtitleLanguage { get; set; } = "";
    public SubtitleMode Subtitles { get; set; } = SubtitleMode.ForcedOnly;

    /// <summary>Plain-text subtitle colours, "#RRGGBB". ASS keeps its own styles; PGS and VobSub are pictures.</summary>
    public string SubtitleColor { get; set; } = "#FFFFFF";
    public string SubtitleOutlineColor { get; set; } = "#000000";

    public int AutoPlayNextSeconds { get; set; } = 10;
    public ResumeMode Resume { get; set; } = ResumeMode.Always;
    public bool CompactOnFocusLoss { get; set; }

    public string MetadataLanguage { get; set; } = "en";
}

public sealed class SettingsStore
{
    private const string Key = "settings";
    private readonly LibraryDatabase _db;

    public SettingsStore(LibraryDatabase db)
    {
        _db = db;
        Current = TryLoad() ?? new AppSettings();
    }

    public AppSettings Current { get; }

    public event Action? Changed;

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        _db.SetMeta(Key, JsonSerializer.Serialize(Current, LibraryDatabase.Json));
        Changed?.Invoke();
    }

    private AppSettings? TryLoad()
    {
        try
        {
            return _db.GetMeta(Key) is { } json ? JsonSerializer.Deserialize<AppSettings>(json, LibraryDatabase.Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
