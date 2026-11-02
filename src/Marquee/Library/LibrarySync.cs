using Marquee.Data;
using Marquee.Plex;

namespace Marquee.Library;

/// <summary>
/// Keeps the local database in step with Plex and with folders on this PC: a full pass at
/// startup and every half hour, plus a rescan when a watched folder changes.
/// </summary>
internal sealed class LibrarySync : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly LibraryDatabase _db;
    private readonly PlexService _plex;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<long, CancellationTokenSource> _pendingRescans = new();
    private readonly Timer _timer;

    public LibrarySync(LibraryDatabase db, PlexService plex)
    {
        _db = db;
        _plex = plex;
        _timer = new Timer(_ => _ = SyncAllAsync(), null, Interval, Interval);
    }

    public bool IsBusy { get; private set; }

    public string? Status { get; private set; }

    public event Action? StatusChanged;

    public event Action<string>? Error;

    public async Task SyncAllAsync(bool refreshServers = true)
    {
        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            SetStatus(true, "Syncing…");
            if (refreshServers && _plex.IsSignedIn)
            {
                try
                {
                    await _plex.RefreshServersAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Error?.Invoke($"Couldn't reach plex.tv: {ex.Message}");
                }
            }

            foreach (var library in _db.GetLibraries().Where(l => l.Enabled))
            {
                await SyncLibraryCoreAsync(library);
            }

            foreach (var server in _plex.Servers.Where(s => s.Server.Online))
            {
                await SyncOnDeckAsync(server);
            }

            UpdateWatchers();
        }
        finally
        {
            SetStatus(false, null);
            _gate.Release();
            _db.NotifyChanged();
        }
    }

    public async Task SyncLibraryAsync(LibraryInfo library)
    {
        await _gate.WaitAsync();
        try
        {
            SetStatus(true, $"Syncing {library.Title}…");
            await SyncLibraryCoreAsync(library);
            UpdateWatchers();
        }
        finally
        {
            SetStatus(false, null);
            _gate.Release();
            _db.NotifyChanged();
        }
    }

    private async Task SyncLibraryCoreAsync(LibraryInfo library)
    {
        try
        {
            if (library.IsPlex)
            {
                await SyncPlexAsync(library);
            }
            else
            {
                await ScanFolderAsync(library);
            }
        }
        catch (Exception ex)
        {
            _db.SetLibraryStatus(library.Id, $"Failed: {ex.Message}");
            Error?.Invoke($"{library.Title}: {ex.Message}");
        }

        _db.NotifyChanged();
    }

    private async Task SyncPlexAsync(LibraryInfo library)
    {
        if (_plex.Server(library.ServerId) is not { Server.Online: true } server)
        {
            _db.SetLibraryStatus(library.Id, "Server offline");
            return;
        }

        int generation = _db.NextGeneration(library.Id);
        int[] types = library.Type == LibraryType.Shows ? [2, 3, 4] : [1];
        var batch = new List<MediaItem>();
        int count = 0;
        foreach (int type in types)
        {
            await foreach (var json in server.ListAsync($"/library/sections/{library.SectionKey}/all?type={type}&includeGuids=0", CancellationToken.None))
            {
                if (PlexMapper.ToItem(json, server.Server.Id, library.Id) is not { } item)
                {
                    continue;
                }

                batch.Add(item);
                if (type is 1 or 2)
                {
                    count++;
                }

                if (batch.Count >= 300)
                {
                    _db.UpsertItems(batch, generation);
                    batch.Clear();
                    _db.SetLibraryStatus(library.Id, $"Syncing · {count} so far");
                    _db.NotifyChanged();
                }
            }
        }

        _db.UpsertItems(batch, generation);
        _db.DeleteStale(library.Id, generation);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _db.SetLibraryStatus(library.Id, null, count, now);
        _db.SetServerSynced(server.Server.Id, now);
    }

    private async Task SyncOnDeckAsync(PlexServerApi server)
    {
        try
        {
            var ids = new List<string>();
            await foreach (var json in server.ListAsync("/library/onDeck", CancellationToken.None))
            {
                if (json.Str("ratingKey") is { } key)
                {
                    ids.Add(PlexMapper.ItemId(server.Server.Id, key));
                }
            }

            _db.SetOnDeck(server.Server.Id, ids);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"{server.Server.Name} On Deck: {ex.Message}");
        }
    }

    private async Task ScanFolderAsync(LibraryInfo library)
    {
        if (!Directory.Exists(library.Path))
        {
            _db.SetLibraryStatus(library.Id, "Folder not found");
            return;
        }

        var probed = new HashSet<string>();
        foreach (var kind in new[] { ItemKind.Movie, ItemKind.Episode })
        {
            probed.UnionWith(_db.GetItems(kind, "i.id").Where(i => i.LibraryId == library.Id && i.DetailsJson is not null).Select(i => i.Id));
        }

        int lastReported = -1;
        var result = await Task.Run(() => FolderScanner.Scan(library, probed, (n, total) =>
        {
            if (n / 25 != lastReported)
            {
                lastReported = n / 25;
                _db.SetLibraryStatus(library.Id, $"Scanning · {n} of {total} files");
                _db.NotifyChanged();
            }
        }, CancellationToken.None));

        int generation = _db.NextGeneration(library.Id);
        _db.UpsertItems(result.Items, generation, keepProgress: true);
        foreach (var (id, details) in result.Probes)
        {
            var item = result.Items.First(i => i.Id == id);
            _db.SaveDetails(id, details, item.VideoRange, item.Audio);
        }

        _db.DeleteStale(library.Id, generation);
        int count = result.Items.Count(i => i.Kind is ItemKind.Movie or ItemKind.Show);
        await TmdbMatcher.MatchLibraryAsync(library, status =>
        {
            _db.SetLibraryStatus(library.Id, status);
            _db.NotifyChanged();
        });
        _db.SetLibraryStatus(library.Id, null, count, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private void UpdateWatchers()
    {
        var wanted = _db.GetLibraries().Where(l => !l.IsPlex && l.Enabled && l.Watch && Directory.Exists(l.Path)).ToDictionary(l => l.Id);
        foreach (long id in _watchers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _watchers[id].Dispose();
            _watchers.Remove(id);
        }

        foreach (var library in wanted.Values.Where(l => !_watchers.ContainsKey(l.Id)))
        {
            try
            {
                var watcher = new FileSystemWatcher(library.Path!) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                FileSystemEventHandler changed = (_, _) => ScheduleRescan(library.Id);
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, _) => ScheduleRescan(library.Id);
                watcher.EnableRaisingEvents = true;
                _watchers[library.Id] = watcher;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                Error?.Invoke($"Can't watch {library.Path}: {ex.Message}");
            }
        }
    }

    private void ScheduleRescan(long libraryId)
    {
        lock (_pendingRescans)
        {
            if (_pendingRescans.Remove(libraryId, out var previous))
            {
                previous.Cancel();
            }

            var cts = new CancellationTokenSource();
            _pendingRescans[libraryId] = cts;
            _ = Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(async t =>
            {
                if (!t.IsCanceled && _db.GetLibraries().FirstOrDefault(l => l.Id == libraryId) is { } library)
                {
                    await SyncLibraryAsync(library);
                }
            }, TaskScheduler.Default);
        }
    }

    private void SetStatus(bool busy, string? status)
    {
        IsBusy = busy;
        Status = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
    }
}
