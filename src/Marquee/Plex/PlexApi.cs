using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Marquee.Data;

namespace Marquee.Plex;

/// <summary>The identity Marquee presents to plex.tv and Plex Media Server.</summary>
internal sealed class PlexClientIdentity(string clientId)
{
    public const string Product = "Marquee";

    public string ClientId { get; } = clientId;

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    public void Apply(HttpRequestHeaders headers, string? token)
    {
        headers.Accept.Clear();
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        headers.Add("X-Plex-Product", Product);
        headers.Add("X-Plex-Version", Version);
        headers.Add("X-Plex-Client-Identifier", ClientId);
        headers.Add("X-Plex-Platform", "Windows");
        headers.Add("X-Plex-Platform-Version", Environment.OSVersion.Version.ToString());
        headers.Add("X-Plex-Device", "PC");
        headers.Add("X-Plex-Device-Name", Environment.MachineName);
        if (token is not null)
        {
            headers.Add("X-Plex-Token", token);
        }
    }
}

internal sealed record PlexUser(string Name, string? Thumb);

internal sealed record PlexConnection(string Uri, bool Local, bool Relay, string Protocol);

internal sealed record PlexResource(string Id, string Name, bool Owned, string AccessToken, IReadOnlyList<PlexConnection> Connections);

/// <summary>plex.tv: PIN sign-in, the account, and the servers it can reach.</summary>
internal sealed class PlexAccountApi(HttpClient http, PlexClientIdentity identity)
{
    private const string Tv = "https://plex.tv/api/v2";

    public async Task<(long Id, string Code)> CreatePinAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{Tv}/pins?strong=true", null, ct);
        return (doc.RootElement.GetProperty("id").GetInt64(), doc.RootElement.GetProperty("code").GetString()!);
    }

    public string AuthUrl(string code) =>
        "https://app.plex.tv/auth#?" +
        $"clientID={Uri.EscapeDataString(identity.ClientId)}&code={Uri.EscapeDataString(code)}" +
        $"&context%5Bdevice%5D%5Bproduct%5D={PlexClientIdentity.Product}";

    /// <returns>The account token once the user has approved the PIN, else null.</returns>
    public async Task<string?> CheckPinAsync(long id, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{Tv}/pins/{id}", null, ct);
        return doc.RootElement.TryGetProperty("authToken", out var token) && token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;
    }

    public async Task<PlexUser> GetUserAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{Tv}/user", token, ct);
        var root = doc.RootElement;
        string name = root.Str("title") ?? root.Str("username") ?? "Plex user";
        return new PlexUser(name, root.Str("thumb"));
    }

    public async Task<List<PlexResource>> GetServersAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{Tv}/resources?includeHttps=1&includeRelay=1", token, ct);
        var servers = new List<PlexResource>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.Str("provides")?.Split(',').Contains("server") != true || r.Str("accessToken") is not { } accessToken)
            {
                continue;
            }

            var connections = r.Arr("connections")
                .Select(c => new PlexConnection(c.Str("uri")!, c.Bool("local"), c.Bool("relay"), c.Str("protocol") ?? "https"))
                .Where(c => c.Uri is not null)
                .ToList();
            servers.Add(new PlexResource(r.Str("clientIdentifier")!, r.Str("name") ?? "Plex server", r.Bool("owned"), accessToken, connections));
        }

        return servers;
    }

    /// <summary>
    /// The first connection that answers, preferring local over remote over relay, and https over
    /// http within each. Candidates are probed in parallel.
    /// </summary>
    public async Task<PlexConnection?> ConnectAsync(PlexResource server, CancellationToken ct)
    {
        var ordered = server.Connections
            .OrderBy(c => c.Relay ? 2 : c.Local ? 0 : 1)
            .ThenBy(c => c.Protocol == "https" ? 0 : 1)
            .ToList();
        var probes = ordered.Select(c => ProbeAsync(c.Uri, server.AccessToken, ct)).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (await probes[i])
            {
                return ordered[i];
            }
        }

        return null;
    }

    public async Task<bool> ProbeAsync(string uri, string token, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{uri.TrimEnd('/')}/identity");
            identity.Apply(request.Headers, token);
            using var response = await http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        identity.Apply(request.Headers, token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}

/// <summary>One Plex Media Server, reached through its chosen connection.</summary>
internal sealed class PlexServerApi(HttpClient http, PlexClientIdentity identity, ServerInfo server)
{
    public ServerInfo Server { get; } = server;

    private string BaseUri => Server.Uri?.TrimEnd('/') ?? throw new InvalidOperationException($"{Server.Name} is offline");

    public async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUri + pathAndQuery);
        identity.Apply(request.Headers, Server.Token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    public async Task SendAsync(HttpMethod method, string pathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUri + pathAndQuery);
        identity.Apply(request.Headers, Server.Token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Pages through a listing, yielding each item as it arrives.</summary>
    public async IAsyncEnumerable<JsonElement> ListAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        const int page = 300;
        char sep = path.Contains('?') ? '&' : '?';
        for (int start = 0; ; start += page)
        {
            using var doc = await GetAsync($"{path}{sep}X-Plex-Container-Start={start}&X-Plex-Container-Size={page}", ct);
            var container = doc.RootElement.GetProperty("MediaContainer");
            var items = container.Arr("Metadata").ToList();
            foreach (var item in items)
            {
                yield return item.Clone();
            }

            int total = container.Int("totalSize") ?? container.Int("size") ?? 0;
            if (items.Count < page || start + page >= total)
            {
                yield break;
            }
        }
    }

    public string MediaUrl(string partKey) => $"{BaseUri}{partKey}?X-Plex-Token={Uri.EscapeDataString(Server.Token)}";

    public string ImageUrl(string path, int width, int height) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{BaseUri}/photo/:/transcode?width={width}&height={height}&minSize=1&upscale=1" +
              $"&url={Uri.EscapeDataString(path)}&X-Plex-Token={Uri.EscapeDataString(Server.Token)}";

    public Task ReportTimelineAsync(string ratingKey, string state, long timeMs, long durationMs, CancellationToken ct) =>
        SendAsync(HttpMethod.Get,
            $"/:/timeline?ratingKey={ratingKey}&key=%2Flibrary%2Fmetadata%2F{ratingKey}&state={state}" +
            $"&time={timeMs}&duration={durationMs}&identifier=com.plexapp.plugins.library", ct);

    /// <param name="rating">0–10, or null to clear.</param>
    public Task RateAsync(string ratingKey, double? rating, CancellationToken ct) =>
        SendAsync(HttpMethod.Put,
            $"/:/rate?key={ratingKey}&identifier=com.plexapp.plugins.library" +
            $"&rating={(rating is { } r ? r.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "-1")}", ct);

    public Task ScrobbleAsync(string ratingKey, bool watched, CancellationToken ct) =>
        SendAsync(HttpMethod.Get,
            $"/:/{(watched ? "scrobble" : "unscrobble")}?key={ratingKey}&identifier=com.plexapp.plugins.library", ct);
}

internal static class JsonExtensions
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    public static int? Int(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out int i) => i,
                JsonValueKind.String when int.TryParse(v.GetString(), out int i) => i,
                _ => null,
            }
            : null;

    public static long? Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out long l) => l,
                JsonValueKind.String when long.TryParse(v.GetString(), out long l) => l,
                _ => null,
            }
            : null;

    public static double? Double(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetRawText() != "0",
            JsonValueKind.String => v.GetString() is "1" or "true",
            _ => false,
        };

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    public static string? Tags(this JsonElement e, string name, int max = int.MaxValue)
    {
        var tags = e.Arr(name).Select(t => t.Str("tag")).OfType<string>().Take(max).ToList();
        return tags.Count == 0 ? null : string.Join(", ", tags);
    }
}
