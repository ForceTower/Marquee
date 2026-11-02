using System.Net.Http.Headers;
using System.Text.Json;
using Marquee.Data;
using Marquee.Plex;

namespace Marquee.Library;

internal sealed record TmdbResult(int Id, string Kind, string Title, int? Year, string? Overview, string? Poster);

internal sealed record TmdbEpisode(int Season, int Number, string? Title, string? Overview, string? Still, string? AirDate, int? Runtime);

/// <summary>Metadata for one matched movie or show, stored as JSON in the matches table.</summary>
internal sealed record TmdbMetadata
{
    public required int Id { get; init; }
    public required string Kind { get; init; }
    public string? Title { get; init; }
    public string? OriginalTitle { get; init; }
    public int? Year { get; init; }
    public string? Overview { get; init; }
    public string? Tagline { get; init; }
    public double? Rating { get; init; }
    public string? ContentRating { get; init; }
    public string? Genres { get; init; }
    public string? Studio { get; init; }
    public string? Released { get; init; }
    public string? Poster { get; init; }
    public string? Backdrop { get; init; }
    public string? Logo { get; init; }
    public int? RuntimeMinutes { get; init; }
    public List<Person> Cast { get; init; } = [];
    public List<string> Directors { get; init; } = [];
    public List<string> Writers { get; init; } = [];
    public List<string> Countries { get; init; } = [];
    public List<TmdbEpisode> Episodes { get; init; } = [];
}

/// <summary>
/// The Movie Database. Needs the user's own key (an API key, or a read access token sent as a
/// bearer token); it's stored encrypted with DPAPI like the Plex token.
/// </summary>
internal static class Tmdb
{
    private const string Api = "https://api.themoviedb.org/3";
    private const string Images = "https://image.tmdb.org/t/p";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string? ApiKey
    {
        get => App.Services.Db.GetMeta("tmdb.key") is { } protectedKey && Secrets.Unprotect(protectedKey) is { Length: > 0 } key ? key : null;
        set => App.Services.Db.SetMeta("tmdb.key", value is null ? null : Secrets.Protect(value));
    }

    public static bool IsConfigured => ApiKey is not null;

    /// <returns>Null if the key works, else the reason it doesn't.</returns>
    public static async Task<string?> TestAsync(string key)
    {
        try
        {
            using var doc = await GetAsync("/configuration", key);
            return null;
        }
        catch (HttpRequestException ex)
        {
            return ex.StatusCode is System.Net.HttpStatusCode.Unauthorized ? "unauthorized" : ex.Message;
        }
    }

    public static async Task<List<TmdbResult>> SearchAsync(string kind, string query, int? year, string language)
    {
        string type = kind == "show" ? "tv" : "movie";
        string yearParam = year is null ? "" : kind == "show" ? $"&first_air_date_year={year}" : $"&year={year}";
        using var doc = await GetAsync($"/search/{type}?query={Uri.EscapeDataString(query)}{yearParam}&language={language}&include_adult=false");
        return doc.RootElement.Arr("results").Select(r => new TmdbResult(
            r.Int("id") ?? 0,
            kind,
            r.Str("title") ?? r.Str("name") ?? "",
            YearOf(r.Str("release_date") ?? r.Str("first_air_date")),
            r.Str("overview"),
            Image(r.Str("poster_path"), "w342"))).Where(r => r.Id > 0).ToList();
    }

    public static async Task<TmdbMetadata> GetAsync(string kind, int id, string language)
    {
        string type = kind == "show" ? "tv" : "movie";
        string lang2 = language.Split('-')[0];
        string extra = kind == "show" ? "credits,images,content_ratings" : "credits,images,release_dates";
        using var doc = await GetAsync($"/{type}/{id}?language={language}&append_to_response={extra}&include_image_language={lang2},en,null");
        var m = doc.RootElement;
        string region = language.Contains('-') ? language.Split('-')[1] : "US";

        var crew = m.TryGetProperty("credits", out var credits) ? credits.Arr("crew").ToList() : [];
        var metadata = new TmdbMetadata
        {
            Id = id,
            Kind = kind,
            Title = m.Str("title") ?? m.Str("name"),
            OriginalTitle = m.Str("original_title") ?? m.Str("original_name"),
            Year = YearOf(m.Str("release_date") ?? m.Str("first_air_date")),
            Overview = NullIfEmpty(m.Str("overview")),
            Tagline = NullIfEmpty(m.Str("tagline")),
            Rating = m.Double("vote_average") is double v && v > 0 ? Math.Round(v, 1) : null,
            ContentRating = kind == "show" ? ShowRating(m, region) : MovieRating(m, region),
            Genres = string.Join(", ", m.Arr("genres").Select(g => g.Str("name")).OfType<string>().Take(3)) is { Length: > 0 } genres ? genres : null,
            Studio = m.Arr("production_companies").Select(c => c.Str("name")).FirstOrDefault() ?? m.Arr("networks").Select(n => n.Str("name")).FirstOrDefault(),
            Released = m.Str("release_date") ?? m.Str("first_air_date"),
            Poster = Image(m.Str("poster_path"), "w500"),
            Backdrop = Image(m.Str("backdrop_path"), "w1280"),
            Logo = m.TryGetProperty("images", out var images) ? Image(BestLogo(images, lang2), "w500") : null,
            RuntimeMinutes = m.Int("runtime") ?? m.Arr("episode_run_time").Select(r => r.GetInt32()).FirstOrDefault(),
            Cast = credits.ValueKind == JsonValueKind.Object
                ? credits.Arr("cast").Take(30).Select(p => new Person(p.Str("name") ?? "", p.Str("character"), Image(p.Str("profile_path"), "w185"))).ToList()
                : [],
            Directors = crew.Where(c => c.Str("job") == "Director").Select(c => c.Str("name")).OfType<string>().Distinct().ToList(),
            Writers = crew.Where(c => c.Str("department") == "Writing").Select(c => c.Str("name")).OfType<string>().Distinct().Take(4).ToList(),
            Countries = m.Arr("production_countries").Select(c => c.Str("name")).OfType<string>().ToList(),
        };

        if (kind == "show")
        {
            var episodes = new List<TmdbEpisode>();
            foreach (int season in m.Arr("seasons").Select(s => s.Int("season_number") ?? -1).Where(s => s >= 0))
            {
                using var seasonDoc = await GetAsync($"/tv/{id}/season/{season}?language={language}");
                episodes.AddRange(seasonDoc.RootElement.Arr("episodes").Select(e => new TmdbEpisode(
                    season,
                    e.Int("episode_number") ?? 0,
                    NullIfEmpty(e.Str("name")),
                    NullIfEmpty(e.Str("overview")),
                    Image(e.Str("still_path"), "w300"),
                    e.Str("air_date"),
                    e.Int("runtime"))));
            }

            metadata = metadata with { Episodes = episodes };
        }

        return metadata;
    }

    private static string? BestLogo(JsonElement images, string lang2)
    {
        var logos = images.Arr("logos").ToList();
        return (logos.FirstOrDefault(l => l.Str("iso_639_1") == lang2).Str("file_path"))
               ?? logos.FirstOrDefault(l => l.Str("iso_639_1") == "en").Str("file_path")
               ?? logos.FirstOrDefault().Str("file_path");
    }

    private static string? MovieRating(JsonElement m, string region) =>
        m.TryGetProperty("release_dates", out var rd)
            ? rd.Arr("results").Where(r => r.Str("iso_3166_1") == region || r.Str("iso_3166_1") == "US")
                .OrderBy(r => r.Str("iso_3166_1") == region ? 0 : 1)
                .SelectMany(r => r.Arr("release_dates")).Select(d => d.Str("certification")).FirstOrDefault(c => !string.IsNullOrEmpty(c))
            : null;

    private static string? ShowRating(JsonElement m, string region) =>
        m.TryGetProperty("content_ratings", out var cr)
            ? cr.Arr("results").Where(r => r.Str("iso_3166_1") == region || r.Str("iso_3166_1") == "US")
                .OrderBy(r => r.Str("iso_3166_1") == region ? 0 : 1).Select(r => r.Str("rating")).FirstOrDefault(c => !string.IsNullOrEmpty(c))
            : null;

    private static async Task<JsonDocument> GetAsync(string pathAndQuery, string? key = null)
    {
        key ??= ApiKey ?? throw new InvalidOperationException("No TMDB key set");
        bool bearer = key.Length > 40;
        string url = Api + pathAndQuery + (bearer ? "" : (pathAndQuery.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await Http.SendAsync(request);
        if ((int)response.StatusCode == 429)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            return await GetAsync(pathAndQuery, key);
        }

        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static string? Image(string? path, string size) => path is null ? null : $"{Images}/{size}{path}";

    private static int? YearOf(string? date) => date is { Length: >= 4 } && int.TryParse(date[..4], out int y) ? y : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
