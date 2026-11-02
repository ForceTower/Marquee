using System.Text.Json;
using Marquee.Data;
using Marquee.Plex;
using Marquee.Shell;

namespace Marquee.Library;

/// <summary>
/// Full details (cast, streams, chapters, markers) for an item. Cached in the database; Plex items
/// are refetched when the cache is older than a day or the item changed on the server.
/// </summary>
internal static class DetailsLoader
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public static ItemDetails? Cached(MediaItem item)
    {
        if (item.DetailsJson is not { } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ItemDetails>(json, LibraryDatabase.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<ItemDetails?> LoadAsync(MediaItem item, bool force = false)
    {
        var cached = Cached(item);
        bool fresh = item.DetailsAt is { } at && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(at) < MaxAge;
        if (!item.IsPlex || (cached is not null && fresh && !force))
        {
            return cached;
        }

        string? serverId = PlexMapper.ServerId(item.Id);
        string? ratingKey = PlexMapper.RatingKey(item.Id);
        if (App.Services.Plex.Server(serverId) is not { Server.Online: true } server || ratingKey is null)
        {
            return cached;
        }

        try
        {
            using var doc = await server.GetAsync($"/library/metadata/{ratingKey}?includeChapters=1&includeMarkers=1", CancellationToken.None);
            var json = doc.RootElement.GetProperty("MediaContainer").Arr("Metadata").FirstOrDefault();
            if (json.ValueKind == JsonValueKind.Undefined)
            {
                return cached;
            }

            var (details, range, audio) = PlexMapper.ToDetails(json, server.Server.Id);
            App.Services.Db.SaveDetails(item.Id, details, range, audio);
            if (json.Arr("Image").FirstOrDefault(i => i.Str("type") == "clearLogo").Str("url") is { } logo)
            {
                App.Services.Db.SaveLogo(item.Id, $"plex:{server.Server.Id}:{logo}");
            }

            return details;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Error($"Details for {item.Id}", ex);
            return cached;
        }
    }
}
