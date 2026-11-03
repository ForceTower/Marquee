namespace Marquee.Playback.Demuxing;

internal enum TrackKind
{
    Video = 1,
    Audio = 2,
    Subtitle = 17,
}

/// <summary>Colour code points as ISO/IEC 23091-2 numbers them (Matroska's Colour element and FFmpeg use the same).</summary>
internal sealed record ColourInfo(int? Matrix, int? Range, int? Transfer, int? Primaries, int? MaxCll, int? MaxFall,
    double? MasteringMax, double? MasteringMin);

/// <summary>DOVIDecoderConfigurationRecord from a dvcC/dvvC box or block addition mapping, or an MPEG-TS DOVI descriptor.</summary>
internal sealed record DolbyVisionConfig(int Profile, int Level, bool RpuPresent, bool ElPresent, bool BlPresent, int Compatibility)
{
    public static DolbyVisionConfig Parse(byte[] record)
    {
        int bits = (record[2] << 8) | record[3];
        return new DolbyVisionConfig(
            Profile: bits >> 9,
            Level: (bits >> 3) & 0x3F,
            RpuPresent: ((bits >> 2) & 1) == 1,
            ElPresent: ((bits >> 1) & 1) == 1,
            BlPresent: (bits & 1) == 1,
            Compatibility: record.Length > 4 ? record[4] >> 4 : 0);
    }

    public override string ToString() =>
        $"Dolby Vision profile {Profile}, level {Level}, BL compat {Compatibility}" +
        $"{(RpuPresent ? ", RPU" : "")}{(ElPresent ? ", EL" : "")}";
}

/// <summary>
/// A track whatever its container, described as Matroska would store it: Matroska's codec IDs
/// (V_MPEGH/ISO/HEVC, A_TRUEHD, S_HDMV/PGS…) and its CodecPrivate. The exception is video read from
/// MPEG-TS, which stays in Annex B, with Annex B parameter sets as its CodecPrivate.
/// </summary>
internal sealed class MediaTrack
{
    public int Number;
    public TrackKind Kind;
    public string CodecId = "";
    public byte[]? CodecPrivate;
    public string? Name;
    public string Language = "eng";
    public string? LanguageBcp47;
    public bool IsDefault = true;
    public bool IsForced;
    public long DefaultDurationNs;
    public int Width;
    public int Height;
    public double SampleRate;
    public int Channels;
    public ColourInfo? Colour;
    public DolbyVisionConfig? DolbyVision;

    public string DisplayLanguage => LanguageBcp47 ?? Language;
}

/// <summary>
/// One block's payload. Laced frames are concatenated in <see cref="Data"/>, which Windows' decoders
/// accept; <see cref="FrameSizes"/> keeps the boundaries for decoders that take one frame at a time.
/// </summary>
internal sealed record MediaPacket(int Track, long TimeTicks, long DurationTicks, bool KeyFrame, byte[] Data, int[] FrameSizes)
{
    public IEnumerable<ReadOnlyMemory<byte>> Frames()
    {
        int offset = 0;
        foreach (int size in FrameSizes)
        {
            yield return Data.AsMemory(offset, size);
            offset += size;
        }
    }
}

/// <summary>
/// Reads a file's packets track by track for Media Foundation and the players beside it (TrueHD
/// Atmos, subtitles). Times are media time from the start of the file. Thread-safe: Media
/// Foundation requests audio and video samples from different threads.
/// </summary>
internal interface IDemuxer : IDisposable
{
    /// <summary>The container, for logs ("Matroska", "MPEG-TS").</summary>
    string Container { get; }

    IReadOnlyList<MediaTrack> Tracks { get; }

    TimeSpan Duration { get; }

    bool CanSeek { get; }

    /// <summary>Lines for the log describing what the container offers for seeking.</summary>
    string SeekDescription { get; }

    /// <summary>Tracks whose packets are queued for delivery; packets for any other track are dropped.</summary>
    void Expose(int track, bool active);

    /// <summary>An inactive track's queue is capped, dropping its oldest packets, until it becomes active.</summary>
    void SetActive(int track, bool active);

    /// <summary>Drops queued packets that start before <paramref name="timeTicks"/> (used after an audio switch).</summary>
    void DropBefore(int track, long timeTicks);

    /// <summary>
    /// Hands packets of <paramref name="track"/> to <paramref name="sink"/> as reading for other
    /// tracks passes them, instead of queuing them (null stops). The sink runs under the demuxer's
    /// lock, so it must only store the packet.
    /// </summary>
    void Tap(int track, Action<MediaPacket>? sink);

    MediaPacket? Next(int track);

    /// <summary>
    /// The first <paramref name="count"/> packets of one track from the start of the file, to probe a
    /// decoder's real output format. Call it before playback starts: a demuxer that can't read out
    /// of order rewinds to the start afterwards.
    /// </summary>
    List<MediaPacket> FirstPackets(int track, int count);

    /// <summary>
    /// Positions reading at the last point at or before <paramref name="target"/> where
    /// <paramref name="track"/> can be decoded from (a keyframe for video) and returns its time.
    /// </summary>
    TimeSpan Seek(TimeSpan target, int track);
}
