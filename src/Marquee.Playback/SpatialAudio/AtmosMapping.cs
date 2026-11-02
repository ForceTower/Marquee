namespace Marquee.Playback.SpatialAudio;

/// <summary>Maps Atmos bed channels and room coordinates onto Windows Spatial Sound.</summary>
internal static class AtmosMapping
{
    /// <summary>Atmos bed channel names that have a static Windows object of their own.</summary>
    public static readonly Dictionary<string, AudioObjectType> StaticBedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["L"] = AudioObjectType.FrontLeft,
        ["R"] = AudioObjectType.FrontRight,
        ["C"] = AudioObjectType.FrontCenter,
        ["LFE"] = AudioObjectType.LowFrequency,
        ["Ls"] = AudioObjectType.SideLeft,
        ["Lss"] = AudioObjectType.SideLeft,
        ["Rs"] = AudioObjectType.SideRight,
        ["Rss"] = AudioObjectType.SideRight,
        ["Lrs"] = AudioObjectType.BackLeft,
        ["Rrs"] = AudioObjectType.BackRight,
        ["Lb"] = AudioObjectType.BackLeft,
        ["Rb"] = AudioObjectType.BackRight,
    };

    /// <summary>
    /// Nominal room positions for bed channels that have to be rendered as dynamic objects (tops,
    /// wides). Both DAMF names (Ltm…) and TrueHD channel labels (Tsl…) are listed.
    /// </summary>
    public static readonly Dictionary<string, (float X, float Y, float Z)> BedPositions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["L"] = (-1, 1, 0),
        ["R"] = (1, 1, 0),
        ["C"] = (0, 1, 0),
        ["Ls"] = (-1, 0, 0),
        ["Lss"] = (-1, 0, 0),
        ["Rs"] = (1, 0, 0),
        ["Rss"] = (1, 0, 0),
        ["Lrs"] = (-1, -1, 0),
        ["Rrs"] = (1, -1, 0),
        ["Ltf"] = (-1, 1, 1),
        ["Rtf"] = (1, 1, 1),
        ["Ltm"] = (-1, 0, 1),
        ["Rtm"] = (1, 0, 1),
        ["Ltr"] = (-1, -1, 1),
        ["Rtr"] = (1, -1, 1),
        ["Lb"] = (-1, -1, 0),
        ["Rb"] = (1, -1, 0),
        ["Tfl"] = (-1, 1, 1),
        ["Tfr"] = (1, 1, 1),
        ["Tsl"] = (-1, 0, 1),
        ["Tsr"] = (1, 0, 1),
        ["Tbl"] = (-1, -1, 1),
        ["Tbr"] = (1, -1, 1),
        ["Lw"] = (-1, 0.5f, 0),
        ["Rw"] = (1, 0.5f, 0),
        ["LFE2"] = (0, 1, 0),
    };

    public static AudioObjectType StaticTypeFor(string? bedChannel) =>
        bedChannel is not null && StaticBedChannels.TryGetValue(bedChannel, out var type) ? type : AudioObjectType.None;

    /// <summary>
    /// Atmos room coordinates (x: left −1 … right 1, y: back −1 … front 1, z: floor 0 … ceiling 1)
    /// to a Windows spatial direction (x right, y up, −z front), normalized to 1 m.
    /// </summary>
    public static (float X, float Y, float Z) ToWindows((float X, float Y, float Z) p)
    {
        float x = p.X, y = p.Z, z = -p.Y;
        float length = MathF.Sqrt(x * x + y * y + z * z);
        return length < 0.01f ? (0, 0, -1) : (x / length, y / length, z / length);
    }
}
