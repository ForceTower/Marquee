using Marquee.Data;
using Marquee.Plex;
using Marquee.Shell;

namespace Marquee.Library;

/// <summary>Watched state and the user's rating, saved locally and, for Plex items, on the server too.</summary>
internal static class WatchState
{
    /// <param name="rating">0–10, or null to clear.</param>
    public static async Task RateAsync(MediaItem item, double? rating)
    {
        App.Services.Db.SetUserRating(item.Id, rating);
        if (!item.IsPlex || App.Services.Plex.Server(PlexMapper.ServerId(item.Id)) is not { Server.Online: true } server)
        {
            return;
        }

        try
        {
            await server.RateAsync(PlexMapper.RatingKey(item.Id)!, rating, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error($"Rate {item.Id}", ex);
            App.Window.ShowToast($"Couldn't update {server.Server.Name}; the rating is saved here.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        }
    }

    public static async Task SetAsync(MediaItem item, bool watched)
    {
        App.Services.Db.SetWatched(item.Id, watched);
        if (!item.IsPlex || App.Services.Plex.Server(PlexMapper.ServerId(item.Id)) is not { Server.Online: true } server)
        {
            return;
        }

        try
        {
            await server.ScrobbleAsync(PlexMapper.RatingKey(item.Id)!, watched, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error($"Scrobble {item.Id}", ex);
            App.Window.ShowToast($"Couldn't update {server.Server.Name}; the change is saved here.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        }
    }
}
