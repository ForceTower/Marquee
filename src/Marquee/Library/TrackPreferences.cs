using System.Text.Json;
using Marquee.Data;

namespace Marquee.Library;

/// <summary>
/// Per-title playback choices made on a detail page: which version (file) to play, and which
/// audio and subtitle track. Ordinals index streams of that kind within the file.
/// </summary>
internal sealed record TrackPreferences(string? Version, int? Audio, int? Subtitle)
{
    public const int SubtitlesOff = -1;

    public static TrackPreferences Load(string itemId)
    {
        try
        {
            return App.Services.Db.GetMeta($"tracks:{itemId}") is { } json
                ? JsonSerializer.Deserialize<TrackPreferences>(json, LibraryDatabase.Json) ?? new(null, null, null)
                : new(null, null, null);
        }
        catch (JsonException)
        {
            return new(null, null, null);
        }
    }

    public void Save(string itemId) =>
        App.Services.Db.SetMeta($"tracks:{itemId}", JsonSerializer.Serialize(this, LibraryDatabase.Json));

    /// <summary>The version to play: the saved one if it still exists, else the highest resolution.</summary>
    public MediaVersion? PickVersion(MediaItem item) =>
        item.Versions.FirstOrDefault(v => v.Location == Version)
        ?? item.Versions.OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.Bitrate ?? 0).FirstOrDefault();
}
