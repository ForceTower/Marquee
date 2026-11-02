using System.Text.Json;
using Marquee.Data;
using Marquee.Shell;

namespace Marquee.Library;

/// <summary>
/// Matches local movies and shows with TMDB, the way Infuse does: search by the parsed title and
/// year, take the best result, then overlay its metadata on the scanned items. Matches are stored
/// apart from the items, so rescans re-apply them without calling TMDB again; a match chosen by
/// hand is locked and never replaced automatically.
/// </summary>
internal static class TmdbMatcher
{
    private static readonly TimeSpan RetryUnmatchedAfter = TimeSpan.FromDays(7);

    public static async Task MatchLibraryAsync(LibraryInfo library, Action<string>? status = null)
    {
        if (!Tmdb.IsConfigured || library.IsPlex)
        {
            return;
        }

        var db = App.Services.Db;
        string language = App.Services.Settings.Current.MetadataLanguage;
        var kind = library.Type == LibraryType.Shows ? ItemKind.Show : ItemKind.Movie;
        var items = db.GetItems(kind, "i.sort_title").Where(i => i.LibraryId == library.Id).ToList();
        int done = 0;
        foreach (var item in items)
        {
            var existing = db.GetMatch(item.Id);
            bool stale = existing is null
                         || (existing.Value.TmdbId is null && existing.Value.MatchedAt is { } at && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(at) > RetryUnmatchedAfter);
            if (stale)
            {
                status?.Invoke($"Matching · {++done} of {items.Count}");
                try
                {
                    var results = await Tmdb.SearchAsync(kind == ItemKind.Show ? "show" : "movie", item.Title, item.Year, language);
                    var best = results.FirstOrDefault(r => item.Year is null || r.Year is null || Math.Abs(r.Year.Value - item.Year.Value) <= 1)
                               ?? results.FirstOrDefault();
                    if (best is null)
                    {
                        db.SaveMatch(item.Id, null, best?.Kind ?? "movie", false, null);
                        continue;
                    }

                    var metadata = await Tmdb.GetAsync(best.Kind, best.Id, language);
                    db.SaveMatch(item.Id, best.Id, best.Kind, false, JsonSerializer.Serialize(metadata, LibraryDatabase.Json));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    Log.Error($"TMDB match for {item.Title}", ex);
                    continue;
                }
            }

            Apply(item);
        }
    }

    /// <summary>Matches an item to a TMDB entry the user picked, locking the choice.</summary>
    public static async Task SetMatchAsync(MediaItem item, TmdbResult result)
    {
        var metadata = await Tmdb.GetAsync(result.Kind, result.Id, App.Services.Settings.Current.MetadataLanguage);
        App.Services.Db.SaveMatch(item.Id, result.Id, result.Kind, true, JsonSerializer.Serialize(metadata, LibraryDatabase.Json));
        Apply(item);
        App.Services.Db.NotifyChanged();
    }

    /// <summary>Overlays a stored match on the item (and, for a show, its episodes).</summary>
    public static void Apply(MediaItem item)
    {
        var db = App.Services.Db;
        if (db.GetMatch(item.Id)?.Data is not { } json || JsonSerializer.Deserialize<TmdbMetadata>(json, LibraryDatabase.Json) is not { } m)
        {
            return;
        }

        // Artwork saved next to the files wins over TMDB's.
        static bool Local(string? image) => image is not null && !image.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        db.ApplyMetadata(item.Id, m.Title, m.OriginalTitle, m.Year, m.Overview, m.Tagline, m.Rating, m.ContentRating, m.Genres, m.Studio,
            m.Released, Local(item.Poster) ? null : m.Poster, Local(item.Backdrop) ? null : m.Backdrop, m.Logo, null,
            m.RuntimeMinutes is { } r ? r * 60_000L : null);

        var details = DetailsLoader.Cached(item) ?? new ItemDetails();
        details = details with { Cast = m.Cast, Directors = m.Directors, Writers = m.Writers, Countries = m.Countries };
        db.SaveDetails(item.Id, details, null, null);

        if (item.Kind != ItemKind.Show || m.Title is null)
        {
            return;
        }

        db.RenameShow(item.Id, m.Title);
        foreach (var episode in db.GetEpisodes(item.Id))
        {
            var e = m.Episodes.FirstOrDefault(x => x.Season == episode.SeasonNumber && x.Number == episode.Number);
            if (e is not null)
            {
                db.ApplyMetadata(episode.Id, e.Title, null, null, e.Overview, null, null, null, null, null, e.AirDate,
                    m.Poster, m.Backdrop, null, e.Still, e.Runtime is { } minutes ? minutes * 60_000L : null);
            }
        }

        foreach (var season in db.GetChildren(item.Id).Where(s => s.Kind == ItemKind.Season))
        {
            db.ApplyMetadata(season.Id, null, null, null, null, null, null, null, null, null, null, m.Poster, m.Backdrop, null, null, null);
        }
    }
}
