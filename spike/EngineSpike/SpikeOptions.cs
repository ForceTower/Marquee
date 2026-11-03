namespace EngineSpike;

public enum Engine
{
    Mpv,
    MediaFoundation,
}

public sealed record SpikeOptions(Engine? Engine, string? Path, bool Mute, int? ExitAfterSeconds, bool FullScreen, bool Probe, bool NativeMkv)
{
    public TimeSpan? Start { get; init; }

    public bool NoDolbyVision { get; init; }

    public int? AudioTrack { get; init; }

    public bool FfmpegDts { get; init; }

    /// <summary>Extract mode: write this track's raw packets to <see cref="ExtractOutput"/> and exit.</summary>
    public int? ExtractTrack { get; init; }

    public TimeSpan ExtractLength { get; init; } = TimeSpan.FromMinutes(2);

    public string? ExtractOutput { get; init; }

    /// <summary>Atmos lab mode: a truehdd .atmos file set plus the matching 7.1 CAF to A/B against.</summary>
    public string? AtmosLab { get; init; }

    public string? Bed { get; init; }

    /// <summary>Play TrueHD tracks as Atmos objects (truehd decoder + Windows Spatial Sound) instead of a 7.1 downmix.</summary>
    public bool TrueHdAtmos { get; init; }

    /// <summary>DV info mode: write the file's Dolby Vision configuration and first RPU to this path and exit.</summary>
    public string? DvInfoOutput { get; init; }

    /// <summary>Display info mode: write each monitor's HDR state, mode, offered rates and frame-rate picks to this path and exit.</summary>
    public string? DisplayInfoOutput { get; init; }

    /// <summary>Display test mode: switch the window's monitor for 23.976 fps and back, then HDR off and back on, logging each step here.</summary>
    public string? DisplayTestOutput { get; init; }

    /// <summary>Set HDR on the primary monitor ("on" or "off") and exit.</summary>
    public string? SetHdr { get; init; }

    /// <summary>Subtitle test mode: render each subtitle track around --start into PNGs in this folder, with a report, and exit.</summary>
    public string? SubtitleTestFolder { get; init; }

    public int? SubtitleTrack { get; init; }

    /// <summary>Subtitle test mode: a bottom strip in pixels (like the player controls) that bottom subtitles move out of.</summary>
    public int SubtitleReserve { get; init; }

    /// <summary>Preview test mode: render seek-bar pictures spread over the file into a contact sheet in this folder, with a report, and exit.</summary>
    public string? PreviewTestFolder { get; init; }

    public static SpikeOptions Parse(string[] args)
    {
        Engine? engine = null;
        string? path = null;
        bool mute = false;
        bool fullScreen = false;
        bool probe = false;
        bool nativeMkv = false;
        int? exitAfter = null;
        TimeSpan? start = null;
        bool noDolbyVision = false;
        int? audioTrack = null;
        bool ffmpegDts = false;
        int? extractTrack = null;
        TimeSpan extractLength = TimeSpan.FromMinutes(2);
        string? extractOutput = null;
        string? atmosLab = null;
        string? bed = null;
        bool trueHdAtmos = false;
        string? dvInfoOutput = null;
        string? subtitleTestFolder = null;
        string? displayInfoOutput = null;
        string? displayTestOutput = null;
        string? setHdr = null;
        int? subtitleTrack = null;
        int subtitleReserve = 0;
        string? previewTestFolder = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--engine" when i + 1 < args.Length:
                    engine = args[++i].ToLowerInvariant() switch
                    {
                        "mpv" => EngineSpike.Engine.Mpv,
                        "mf" => EngineSpike.Engine.MediaFoundation,
                        var other => throw new ArgumentException($"Unknown engine '{other}' (use mpv or mf)"),
                    };
                    break;
                case "--mute":
                    mute = true;
                    break;
                case "--fullscreen":
                    fullScreen = true;
                    break;
                case "--probe":
                    probe = true;
                    break;
                case "--native-mkv":
                    nativeMkv = true;
                    break;
                case "--no-dv":
                    noDolbyVision = true;
                    break;
                case "--ffmpeg-dts":
                    ffmpegDts = true;
                    break;
                case "--extract-track" when i + 1 < args.Length:
                    extractTrack = int.Parse(args[++i]);
                    break;
                case "--seconds" when i + 1 < args.Length:
                    extractLength = TimeSpan.FromSeconds(double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "--out" when i + 1 < args.Length:
                    extractOutput = args[++i];
                    break;
                case "--atmos-lab" when i + 1 < args.Length:
                    atmosLab = args[++i];
                    break;
                case "--bed" when i + 1 < args.Length:
                    bed = args[++i];
                    break;
                case "--truehd-atmos":
                    trueHdAtmos = true;
                    break;
                case "--dv-info" when i + 1 < args.Length:
                    dvInfoOutput = args[++i];
                    break;
                case "--display-info" when i + 1 < args.Length:
                    displayInfoOutput = args[++i];
                    break;
                case "--display-test" when i + 1 < args.Length:
                    displayTestOutput = args[++i];
                    break;
                case "--set-hdr" when i + 1 < args.Length:
                    setHdr = args[++i];
                    break;
                case "--sub-test" when i + 1 < args.Length:
                    subtitleTestFolder = args[++i];
                    break;
                case "--sub-track" when i + 1 < args.Length:
                    subtitleTrack = int.Parse(args[++i]);
                    break;
                case "--sub-reserve" when i + 1 < args.Length:
                    subtitleReserve = int.Parse(args[++i]);
                    break;
                case "--preview-test" when i + 1 < args.Length:
                    previewTestFolder = args[++i];
                    break;
                case "--audio-track" when i + 1 < args.Length:
                    audioTrack = int.Parse(args[++i]);
                    break;
                case "--start" when i + 1 < args.Length:
                    start = TimeSpan.FromSeconds(double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "--exit-after" when i + 1 < args.Length:
                    exitAfter = int.Parse(args[++i]);
                    break;
                default:
                    path = args[i];
                    break;
            }
        }

        return new SpikeOptions(engine, path, mute, exitAfter, fullScreen, probe, nativeMkv) {
            Start = start,
            NoDolbyVision = noDolbyVision,
            AudioTrack = audioTrack,
            FfmpegDts = ffmpegDts,
            ExtractTrack = extractTrack,
            ExtractLength = extractLength,
            ExtractOutput = extractOutput,
            AtmosLab = atmosLab,
            Bed = bed,
            TrueHdAtmos = trueHdAtmos,
            DvInfoOutput = dvInfoOutput,
            DisplayInfoOutput = displayInfoOutput,
            DisplayTestOutput = displayTestOutput,
            SetHdr = setHdr,
            SubtitleTestFolder = subtitleTestFolder,
            SubtitleTrack = subtitleTrack,
            SubtitleReserve = subtitleReserve,
            PreviewTestFolder = previewTestFolder,
        };
    }
}
