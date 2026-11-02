using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace Marquee.Playback.MediaFoundation;

internal static class MfDescribe
{
    private static readonly Dictionary<Guid, string> AttributeNames = new()
    {
        [new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f")] = "MF_MT_MAJOR_TYPE",
        [new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")] = "MF_MT_SUBTYPE",
        [new("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = "MF_MT_TRANSFER_FUNCTION",
        [new("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = "MF_MT_VIDEO_PRIMARIES",
        [new("3e23d450-2c75-4d25-a00e-b91670d12327")] = "MF_MT_YUV_MATRIX",
        [new("50253128-c110-4de4-98ae-46a324fae6da")] = "MF_MT_MAX_LUMINANCE_LEVEL",
        [new("58d4bf57-6f52-4733-a195-a9e29ecf9e27")] = "MF_MT_MAX_FRAME_AVERAGE_LUMINANCE_LEVEL",
        [new("d6c6b997-272f-4ca1-8d00-8042111a0ff6")] = "MF_MT_MAX_MASTERING_LUMINANCE",
        [new("839a4460-4e7e-4b4f-ae79-cc08905c7b27")] = "MF_MT_MIN_MASTERING_LUMINANCE",
        [new("ad76a80b-2d5c-4e0b-b375-64e520137036")] = "MF_MT_VIDEO_PROFILE",
        [new("96f66574-11c5-4015-8666-bff516436da7")] = "MF_MT_VIDEO_LEVEL",
    };

    private static readonly Dictionary<uint, string> TransferFunctions = new()
    {
        [5] = "BT.709",
        [7] = "sRGB",
        [13] = "BT.2020",
        [15] = "PQ / ST 2084",
        [16] = "HLG",
    };

    private static readonly Dictionary<uint, string> Primaries = new()
    {
        [2] = "BT.709",
        [9] = "BT.2020",
        [11] = "DCI-P3",
    };

    public static IEnumerable<string> Playback(MediaPlayer player, MediaPlaybackItem item)
    {
        var session = player.PlaybackSession;
        yield return $"state = {session.PlaybackState}";
        yield return $"natural size = {session.NaturalVideoWidth}x{session.NaturalVideoHeight}";
        yield return $"duration = {session.NaturalDuration}";

        for (int i = 0; i < item.VideoTracks.Count; i++)
        {
            VideoTrack track = item.VideoTracks[i];
            VideoEncodingProperties p = track.GetEncodingProperties();
            string marker = i == item.VideoTracks.SelectedIndex ? "*" : " ";
            yield return $"{marker}video[{i}] {p.Subtype} {p.Width}x{p.Height} " +
                         $"{Fps(p.FrameRate)} fps, {p.Bitrate / 1000} kbps, profile {p.ProfileId}";
            foreach (string attr in Attributes(p.Properties))
            {
                yield return "    " + attr;
            }
        }

        for (int i = 0; i < item.AudioTracks.Count; i++)
        {
            AudioTrack track = item.AudioTracks[i];
            AudioEncodingProperties p = track.GetEncodingProperties();
            string marker = i == item.AudioTracks.SelectedIndex ? "*" : " ";
            yield return $"{marker}audio[{i}] {track.Language} '{track.Label}' {p.Subtype} " +
                         $"{p.ChannelCount}ch {p.SampleRate} Hz, {p.Bitrate / 1000} kbps, IsSpatial={p.IsSpatial}";
        }
    }

    public static List<string> AudioDevice(string deviceId, string deviceName)
    {
        var lines = new List<string> { $"device = {deviceName}" };
        try
        {
            var config = SpatialAudioDeviceConfiguration.GetForDeviceId(deviceId);
            lines.Add($"spatial supported = {config.IsSpatialAudioSupported}");
            lines.Add($"active spatial format = {SpatialFormatName(config.ActiveSpatialAudioFormat)}");
            lines.Add($"default spatial format = {SpatialFormatName(config.DefaultSpatialAudioFormat)}");
        }
        catch (Exception ex)
        {
            lines.Add($"spatial config unavailable: {ex.Message}");
        }

        return lines;
    }

    private static string SpatialFormatName(string? format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return "none";
        }

        (string Name, string Value)[] known =
        [
            ("Dolby Atmos for Headphones", SpatialAudioFormatSubtype.DolbyAtmosForHeadphones),
            ("Dolby Atmos for Home Theater", SpatialAudioFormatSubtype.DolbyAtmosForHomeTheater),
            ("Dolby Atmos for Speakers", SpatialAudioFormatSubtype.DolbyAtmosForSpeakers),
            ("Windows Sonic", SpatialAudioFormatSubtype.WindowsSonic),
        ];
        foreach (var (name, value) in known)
        {
            if (string.Equals(value, format, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return format;
    }

    private static IEnumerable<string> Attributes(MediaPropertySet properties)
    {
        foreach (var (key, value) in properties)
        {
            string name = AttributeNames.GetValueOrDefault(key, key.ToString());
            string text = value switch
            {
                Guid g => FourCc(g) is { } fourCc ? $"{g} ('{fourCc}')" : g.ToString(),
                uint u when name == "MF_MT_TRANSFER_FUNCTION" => $"{u} ({TransferFunctions.GetValueOrDefault(u, "?")})",
                uint u when name == "MF_MT_VIDEO_PRIMARIES" => $"{u} ({Primaries.GetValueOrDefault(u, "?")})",
                _ => value?.ToString() ?? "null",
            };
            yield return $"{name} = {text}";
        }
    }

    private static string? FourCc(Guid g)
    {
        byte[] b = g.ToByteArray();
        bool isFourCcBase = g.ToString().EndsWith("-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase);
        return isFourCcBase ? System.Text.Encoding.ASCII.GetString(b, 0, 4) : null;
    }

    private static string Fps(MediaRatio r) => r.Denominator == 0 ? "?" : (r.Numerator / (double)r.Denominator).ToString("0.###");
}
