using Marquee.Data;

namespace Marquee.Plex;

/// <summary>The signed-in Plex account and the servers it reaches.</summary>
internal sealed class PlexService
{
    private const string TokenKey = "plex.token";
    private const string UserKey = "plex.user";
    private const string ThumbKey = "plex.thumb";

    private readonly LibraryDatabase _db;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly PlexClientIdentity _identity;
    private readonly PlexAccountApi _account;
    private Dictionary<string, PlexServerApi> _servers = new();

    public PlexService(LibraryDatabase db)
    {
        _db = db;
        string clientId = db.GetMeta("plex.clientId") ?? Guid.NewGuid().ToString("N");
        db.SetMeta("plex.clientId", clientId);
        _identity = new PlexClientIdentity(clientId);
        _account = new PlexAccountApi(_http, _identity);
        ReloadServers();
    }

    public event Action? Changed;

    public bool IsSignedIn => AccountToken is { Length: > 0 };

    public string? UserName => _db.GetMeta(UserKey);

    public string? UserThumb => _db.GetMeta(ThumbKey);

    private string? AccountToken => _db.GetMeta(TokenKey) is { } protectedToken ? Secrets.Unprotect(protectedToken) : null;

    public IReadOnlyCollection<PlexServerApi> Servers => _servers.Values;

    public PlexServerApi? Server(string? id) => id is not null && _servers.TryGetValue(id, out var s) ? s : null;

    /// <summary>Runs Plex's PIN flow: opens the browser for approval, then polls until it's granted (5 minutes at most).</summary>
    public async Task SignInAsync(Func<string, Task> openBrowser, CancellationToken ct)
    {
        var (id, code) = await _account.CreatePinAsync(ct);
        await openBrowser(_account.AuthUrl(code));

        string? token = null;
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (token is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            token = await _account.CheckPinAsync(id, ct);
        }

        if (token is null)
        {
            throw new TimeoutException("Plex sign-in wasn't approved in time.");
        }

        _db.SetMeta(TokenKey, Secrets.Protect(token));
        var user = await _account.GetUserAsync(token, ct);
        _db.SetMeta(UserKey, user.Name);
        _db.SetMeta(ThumbKey, user.Thumb);
        await RefreshServersAsync(ct);
    }

    public void SignOut()
    {
        _db.SetMeta(TokenKey, null);
        _db.SetMeta(UserKey, null);
        _db.SetMeta(ThumbKey, null);
        _db.RemoveAllServers();
        ReloadServers();
        Changed?.Invoke();
    }

    /// <summary>
    /// Re-discovers the account's servers, picks a working connection for each, and registers their
    /// movie and TV libraries. Libraries on servers you own start enabled; shared ones start off.
    /// </summary>
    public async Task RefreshServersAsync(CancellationToken ct)
    {
        if (AccountToken is not { Length: > 0 } token)
        {
            return;
        }

        var resources = await _account.GetServersAsync(token, ct);
        var known = _db.GetLibraries().Where(l => l.IsPlex).Select(l => (l.ServerId, l.SectionKey)).ToHashSet();
        await Task.WhenAll(resources.Select(async r =>
        {
            var connection = await _account.ConnectAsync(r, ct);
            var info = new ServerInfo
            {
                Id = r.Id,
                Name = r.Name,
                Owned = r.Owned,
                Token = r.AccessToken,
                Uri = connection?.Uri,
                ConnectionKind = connection is null ? null : connection.Relay ? "relay" : connection.Local ? "local" : "remote",
                Online = connection is not null,
            };
            _db.UpsertServer(info);
            if (connection is null)
            {
                return;
            }

            var api = new PlexServerApi(_http, _identity, info);
            using var doc = await api.GetAsync("/library/sections", ct);
            foreach (var section in doc.RootElement.GetProperty("MediaContainer").Arr("Directory"))
            {
                LibraryType? type = section.Str("type") switch
                {
                    "movie" => LibraryType.Movies,
                    "show" => LibraryType.Shows,
                    _ => null,
                };
                if (type is null || section.Str("key") is not { } key)
                {
                    continue;
                }

                long libraryId = _db.UpsertPlexLibrary(r.Id, key, section.Str("title") ?? key, type.Value);
                if (!known.Contains((r.Id, key)) && !r.Owned)
                {
                    _db.SetLibraryEnabled(libraryId, false);
                }
            }
        }));

        ReloadServers();
        Changed?.Invoke();
    }

    /// <summary>A "plex:{server}:{part key}" location → a direct-play URL for the engines.</summary>
    public string? ResolveMedia(string location) =>
        ParseRef(location) is var (serverId, path) && Server(serverId) is { Server.Online: true } server ? server.MediaUrl(path) : null;

    public string? ResolveImage(string reference, int width, int height) =>
        ParseRef(reference) is var (serverId, path) && Server(serverId) is { Server.Online: true } server ? server.ImageUrl(path, width, height) : null;

    public static (string ServerId, string Path)? ParseRef(string reference)
    {
        if (!reference.StartsWith("plex:", StringComparison.Ordinal))
        {
            return null;
        }

        int split = reference.IndexOf(':', 5);
        return split < 0 ? null : (reference[5..split], reference[(split + 1)..]);
    }

    private void ReloadServers() =>
        _servers = _db.GetServers().ToDictionary(s => s.Id, s => new PlexServerApi(_http, _identity, s));
}
