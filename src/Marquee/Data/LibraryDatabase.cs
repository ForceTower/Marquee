using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Marquee.Data;

/// <summary>
/// The local library: every Plex item and scanned file, watch progress, settings. Plex is synced
/// into it so browsing never waits on the network. Times are Unix seconds.
/// </summary>
public sealed class LibraryDatabase
{
    private const int SchemaVersion = 3;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;

    public LibraryDatabase(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Migrate();
    }

    public string Path { get; }

    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private void Migrate()
    {
        using var c = Open();
        Execute(c, "PRAGMA journal_mode = WAL;");
        long version = Scalar<long>(c, "PRAGMA user_version;");
        if (version >= SchemaVersion)
        {
            return;
        }

        // Every statement below is idempotent (IF NOT EXISTS), so older databases just gain the new tables.
        Execute(c, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS servers (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                owned INTEGER NOT NULL,
                token TEXT NOT NULL,
                uri TEXT,
                connection_kind TEXT,
                online INTEGER NOT NULL DEFAULT 0,
                synced_at INTEGER);

            CREATE TABLE IF NOT EXISTS libraries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                server_id TEXT REFERENCES servers(id) ON DELETE CASCADE,
                section_key TEXT,
                path TEXT,
                title TEXT NOT NULL,
                type TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                watch INTEGER NOT NULL DEFAULT 1,
                item_count INTEGER NOT NULL DEFAULT 0,
                scanned_at INTEGER,
                status TEXT,
                UNIQUE (server_id, section_key),
                UNIQUE (path));

            CREATE TABLE IF NOT EXISTS items (
                id TEXT PRIMARY KEY,
                library_id INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL,
                parent_id TEXT,
                show_id TEXT,
                title TEXT NOT NULL,
                sort_title TEXT,
                original_title TEXT,
                show_title TEXT,
                season_number INTEGER,
                number INTEGER,
                year INTEGER,
                summary TEXT,
                tagline TEXT,
                rating REAL,
                content_rating TEXT,
                studio TEXT,
                genres TEXT,
                duration_ms INTEGER,
                released TEXT,
                added_at INTEGER,
                updated_at INTEGER,
                poster TEXT,
                backdrop TEXT,
                logo TEXT,
                still TEXT,
                tint TEXT,
                child_count INTEGER,
                leaf_count INTEGER,
                viewed_leaf_count INTEGER,
                position_ms INTEGER NOT NULL DEFAULT 0,
                view_count INTEGER NOT NULL DEFAULT 0,
                last_viewed_at INTEGER,
                resolution TEXT,
                video_range TEXT,
                audio TEXT,
                versions TEXT,
                details TEXT,
                details_at INTEGER,
                generation INTEGER NOT NULL DEFAULT 0);

            CREATE INDEX IF NOT EXISTS items_kind_added ON items (kind, added_at DESC);
            CREATE INDEX IF NOT EXISTS items_parent ON items (parent_id, number);
            CREATE INDEX IF NOT EXISTS items_show ON items (show_id, season_number, number);
            CREATE INDEX IF NOT EXISTS items_viewed ON items (last_viewed_at DESC);
            CREATE INDEX IF NOT EXISTS items_library ON items (library_id, generation);

            CREATE TABLE IF NOT EXISTS on_deck (
                item_id TEXT PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
                server_id TEXT NOT NULL,
                rank INTEGER NOT NULL);

            -- TMDB matches for local items, kept apart from items so a rescan never loses them.
            CREATE TABLE IF NOT EXISTS matches (
                item_id TEXT PRIMARY KEY,
                tmdb_id INTEGER,
                kind TEXT NOT NULL,
                locked INTEGER NOT NULL DEFAULT 0,
                data TEXT,
                matched_at INTEGER);
            """);
        if (!Query(c, "PRAGMA table_info(items)", r => r.GetString(1)).Contains("user_rating"))
        {
            Execute(c, "ALTER TABLE items ADD COLUMN user_rating REAL");
        }

        Execute(c, $"PRAGMA user_version = {SchemaVersion};");
    }

    // ---- meta ----

    public string? GetMeta(string key)
    {
        using var c = Open();
        return Scalar<string?>(c, "SELECT value FROM meta WHERE key = $k", ("$k", key));
    }

    public void SetMeta(string key, string? value)
    {
        using var c = Open();
        if (value is null)
        {
            Execute(c, "DELETE FROM meta WHERE key = $k", ("$k", key));
        }
        else
        {
            Execute(c, "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                ("$k", key), ("$v", value));
        }
    }

    // ---- servers ----

    public List<ServerInfo> GetServers()
    {
        using var c = Open();
        return Query(c, "SELECT id, name, owned, token, uri, connection_kind, online, synced_at FROM servers ORDER BY owned DESC, name",
            r => new ServerInfo
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Owned = r.GetBoolean(2),
                Token = Secrets.Unprotect(r.GetString(3)),
                Uri = r.IsDBNull(4) ? null : r.GetString(4),
                ConnectionKind = r.IsDBNull(5) ? null : r.GetString(5),
                Online = r.GetBoolean(6),
                SyncedAt = r.IsDBNull(7) ? null : r.GetInt64(7),
            });
    }

    public void UpsertServer(ServerInfo s)
    {
        using var c = Open();
        Execute(c, """
            INSERT INTO servers (id, name, owned, token, uri, connection_kind, online)
            VALUES ($id, $name, $owned, $token, $uri, $kind, $online)
            ON CONFLICT (id) DO UPDATE SET name = excluded.name, owned = excluded.owned, token = excluded.token,
                uri = excluded.uri, connection_kind = excluded.connection_kind, online = excluded.online
            """,
            ("$id", s.Id), ("$name", s.Name), ("$owned", s.Owned), ("$token", Secrets.Protect(s.Token)),
            ("$uri", s.Uri), ("$kind", s.ConnectionKind), ("$online", s.Online));
    }

    public void SetServerSynced(string serverId, long at)
    {
        using var c = Open();
        Execute(c, "UPDATE servers SET synced_at = $at WHERE id = $id", ("$at", at), ("$id", serverId));
    }

    public void RemoveAllServers()
    {
        using var c = Open();
        Execute(c, "DELETE FROM servers");
        NotifyChanged();
    }

    // ---- libraries ----

    public List<LibraryInfo> GetLibraries()
    {
        using var c = Open();
        return Query(c, """
            SELECT id, kind, server_id, section_key, path, title, type, enabled, watch, item_count, scanned_at, status
            FROM libraries ORDER BY kind DESC, title
            """,
            r => new LibraryInfo
            {
                Id = r.GetInt64(0),
                Kind = r.GetString(1),
                ServerId = r.IsDBNull(2) ? null : r.GetString(2),
                SectionKey = r.IsDBNull(3) ? null : r.GetString(3),
                Path = r.IsDBNull(4) ? null : r.GetString(4),
                Title = r.GetString(5),
                Type = Enum.Parse<LibraryType>(r.GetString(6)),
                Enabled = r.GetBoolean(7),
                Watch = r.GetBoolean(8),
                ItemCount = r.GetInt32(9),
                ScannedAt = r.IsDBNull(10) ? null : r.GetInt64(10),
                Status = r.IsDBNull(11) ? null : r.GetString(11),
            });
    }

    public long UpsertPlexLibrary(string serverId, string sectionKey, string title, LibraryType type)
    {
        using var c = Open();
        Execute(c, """
            INSERT INTO libraries (kind, server_id, section_key, title, type) VALUES ('plex', $s, $k, $t, $type)
            ON CONFLICT (server_id, section_key) DO UPDATE SET title = excluded.title, type = excluded.type
            """, ("$s", serverId), ("$k", sectionKey), ("$t", title), ("$type", type.ToString()));
        return Scalar<long>(c, "SELECT id FROM libraries WHERE server_id = $s AND section_key = $k", ("$s", serverId), ("$k", sectionKey));
    }

    public long AddFolder(string path, LibraryType type)
    {
        using var c = Open();
        Execute(c, "INSERT OR IGNORE INTO libraries (kind, path, title, type) VALUES ('folder', $p, $p, $type)",
            ("$p", path), ("$type", type.ToString()));
        long id = Scalar<long>(c, "SELECT id FROM libraries WHERE path = $p", ("$p", path));
        NotifyChanged();
        return id;
    }

    public void RemoveLibrary(long id)
    {
        using var c = Open();
        Execute(c, "DELETE FROM libraries WHERE id = $id", ("$id", id));
        NotifyChanged();
    }

    public void SetLibraryEnabled(long id, bool enabled)
    {
        using var c = Open();
        Execute(c, "UPDATE libraries SET enabled = $e WHERE id = $id", ("$e", enabled), ("$id", id));
        NotifyChanged();
    }

    public void SetLibraryType(long id, LibraryType type)
    {
        using var c = Open();
        Execute(c, "UPDATE libraries SET type = $t WHERE id = $id", ("$t", type.ToString()), ("$id", id));
    }

    public void SetLibraryWatch(long id, bool watch)
    {
        using var c = Open();
        Execute(c, "UPDATE libraries SET watch = $w WHERE id = $id", ("$w", watch), ("$id", id));
    }

    public void SetLibraryStatus(long id, string? status, int? itemCount = null, long? scannedAt = null)
    {
        using var c = Open();
        Execute(c, """
            UPDATE libraries SET status = $s,
                item_count = COALESCE($n, item_count),
                scanned_at = COALESCE($at, scanned_at)
            WHERE id = $id
            """, ("$s", status), ("$n", itemCount), ("$at", scannedAt), ("$id", id));
    }

    // ---- items ----

    private const string ItemColumns = """
        i.id, i.library_id, i.kind, i.parent_id, i.show_id, i.title, i.sort_title, i.original_title, i.show_title,
        i.season_number, i.number, i.year, i.summary, i.tagline, i.rating, i.content_rating, i.studio, i.genres,
        i.duration_ms, i.released, i.added_at, i.updated_at, i.poster, i.backdrop, i.logo, i.still, i.tint,
        i.child_count, i.leaf_count, i.viewed_leaf_count, i.position_ms, i.view_count, i.last_viewed_at,
        i.resolution, i.video_range, i.audio, i.versions, i.details, i.details_at, i.user_rating
        """;

    private const string EnabledJoin = "JOIN libraries l ON l.id = i.library_id AND l.enabled = 1";

    private static MediaItem ReadItem(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

        string? versions = S(36);
        return new MediaItem
        {
            Id = r.GetString(0),
            LibraryId = r.GetInt64(1),
            Kind = (ItemKind)r.GetInt32(2),
            ParentId = S(3),
            ShowId = S(4),
            Title = r.GetString(5),
            SortTitle = S(6),
            OriginalTitle = S(7),
            ShowTitle = S(8),
            SeasonNumber = I(9),
            Number = I(10),
            Year = I(11),
            Summary = S(12),
            Tagline = S(13),
            Rating = r.IsDBNull(14) ? null : r.GetDouble(14),
            ContentRating = S(15),
            Studio = S(16),
            Genres = S(17),
            DurationMs = L(18),
            Released = S(19),
            AddedAt = L(20),
            UpdatedAt = L(21),
            Poster = S(22),
            Backdrop = S(23),
            Logo = S(24),
            Still = S(25),
            Tint = S(26),
            ChildCount = I(27),
            LeafCount = I(28),
            ViewedLeafCount = I(29),
            PositionMs = r.GetInt64(30),
            ViewCount = r.GetInt32(31),
            LastViewedAt = L(32),
            Resolution = S(33),
            VideoRange = S(34),
            Audio = S(35),
            Versions = versions is null ? [] : JsonSerializer.Deserialize<List<MediaVersion>>(versions, Json) ?? [],
            DetailsJson = S(37),
            DetailsAt = L(38),
            UserRating = r.IsDBNull(39) ? null : r.GetDouble(39),
        };
    }

    public MediaItem? GetItem(string id)
    {
        using var c = Open();
        return Query(c, $"SELECT {ItemColumns} FROM items i WHERE i.id = $id", ReadItem, ("$id", id)).FirstOrDefault();
    }

    public List<MediaItem> GetItems(ItemKind kind, string orderBy, int limit = -1)
    {
        using var c = Open();
        return Query(c, $"SELECT {ItemColumns} FROM items i {EnabledJoin} WHERE i.kind = $k ORDER BY {orderBy} LIMIT $n",
            ReadItem, ("$k", (int)kind), ("$n", limit));
    }

    public List<MediaItem> GetChildren(string parentId)
    {
        using var c = Open();
        return Query(c, $"SELECT {ItemColumns} FROM items i WHERE i.parent_id = $p ORDER BY i.number, i.sort_title",
            ReadItem, ("$p", parentId));
    }

    public List<MediaItem> GetEpisodes(string showId)
    {
        using var c = Open();
        return Query(c, $"SELECT {ItemColumns} FROM items i WHERE i.show_id = $s AND i.kind = $k ORDER BY i.season_number, i.number",
            ReadItem, ("$s", showId), ("$k", (int)ItemKind.Episode));
    }

    /// <summary>Movies and shows, newest first; a show counts as added when its latest episode was.</summary>
    public List<MediaItem> GetRecentlyAdded(int limit)
    {
        using var c = Open();
        return Query(c, $"""
            SELECT {ItemColumns} FROM items i {EnabledJoin}
            WHERE i.kind IN ($movie, $show)
            ORDER BY CASE WHEN i.kind = $show
                THEN COALESCE((SELECT MAX(e.added_at) FROM items e WHERE e.show_id = i.id), i.added_at)
                ELSE i.added_at END DESC
            LIMIT $n
            """, ReadItem, ("$movie", (int)ItemKind.Movie), ("$show", (int)ItemKind.Show), ("$n", limit));
    }

    /// <summary>Plex's On Deck order first, then anything else in progress, most recently watched first.</summary>
    public List<MediaItem> GetContinueWatching(int limit)
    {
        using var c = Open();
        return Query(c, $"""
            SELECT {ItemColumns} FROM items i {EnabledJoin}
            LEFT JOIN on_deck d ON d.item_id = i.id
            WHERE d.item_id IS NOT NULL OR (i.position_ms > 0 AND i.kind IN ($movie, $episode))
            ORDER BY COALESCE(i.last_viewed_at, 0) DESC, d.rank
            LIMIT $n
            """, ReadItem, ("$movie", (int)ItemKind.Movie), ("$episode", (int)ItemKind.Episode), ("$n", limit));
    }

    public List<MediaItem> Search(string text, int limit)
    {
        using var c = Open();
        string like = "%" + text.Replace("%", "").Replace("_", "") + "%";
        return Query(c, $"""
            SELECT {ItemColumns} FROM items i {EnabledJoin}
            WHERE i.kind IN ($movie, $show) AND (i.title LIKE $q OR i.original_title LIKE $q)
            ORDER BY CASE WHEN i.title LIKE $prefix THEN 0 ELSE 1 END, i.sort_title
            LIMIT $n
            """, ReadItem, ("$movie", (int)ItemKind.Movie), ("$show", (int)ItemKind.Show), ("$q", like),
            ("$prefix", text + "%"), ("$n", limit));
    }

    /// <summary>The episode after <paramref name="episode"/> in the same show, if it's in the library.</summary>
    public MediaItem? GetNextEpisode(MediaItem episode)
    {
        using var c = Open();
        return Query(c, $"""
            SELECT {ItemColumns} FROM items i
            WHERE i.show_id = $s AND i.kind = $k
              AND (i.season_number > $season OR (i.season_number = $season AND i.number > $n))
              AND i.season_number > 0
            ORDER BY i.season_number, i.number LIMIT 1
            """, ReadItem, ("$s", episode.ShowId), ("$k", (int)ItemKind.Episode),
            ("$season", episode.SeasonNumber ?? 0), ("$n", episode.Number ?? 0)).FirstOrDefault();
    }

    public int NextGeneration(long libraryId)
    {
        using var c = Open();
        return (int)Scalar<long>(c, "SELECT COALESCE(MAX(generation), 0) + 1 FROM items WHERE library_id = $l", ("$l", libraryId));
    }

    /// <param name="keepProgress">Keep stored watch progress (local files, where Marquee is the only record of it).</param>
    public void UpsertItems(IEnumerable<MediaItem> items, int generation, bool keepProgress = false)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO items (id, library_id, kind, parent_id, show_id, title, sort_title, original_title, show_title,
                season_number, number, year, summary, tagline, rating, content_rating, studio, genres, duration_ms, released,
                added_at, updated_at, poster, backdrop, logo, still, tint, child_count, leaf_count, viewed_leaf_count,
                position_ms, view_count, last_viewed_at, resolution, video_range, audio, versions, generation, user_rating)
            VALUES ($id, $lib, $kind, $parent, $show, $title, $sort, $orig, $showTitle, $season, $num, $year, $summary,
                $tagline, $rating, $cr, $studio, $genres, $dur, $released, $added, $updated, $poster, $backdrop, $logo,
                $still, $tint, $children, $leaves, $viewedLeaves, $pos, $views, $lastViewed, $res, $range, $audio,
                $versions, $gen, $userRating)
            ON CONFLICT (id) DO UPDATE SET
                library_id = excluded.library_id, kind = excluded.kind, parent_id = excluded.parent_id,
                show_id = excluded.show_id, title = excluded.title, sort_title = excluded.sort_title,
                original_title = excluded.original_title, show_title = excluded.show_title,
                season_number = excluded.season_number, number = excluded.number, year = excluded.year,
                summary = excluded.summary, tagline = excluded.tagline, rating = excluded.rating,
                content_rating = excluded.content_rating, studio = excluded.studio, genres = excluded.genres,
                duration_ms = excluded.duration_ms, released = excluded.released, added_at = excluded.added_at,
                poster = excluded.poster, backdrop = excluded.backdrop, logo = COALESCE(excluded.logo, items.logo),
                still = excluded.still, tint = excluded.tint, child_count = excluded.child_count,
                leaf_count = excluded.leaf_count, viewed_leaf_count = excluded.viewed_leaf_count,
                position_ms = CASE WHEN $keep THEN items.position_ms ELSE excluded.position_ms END,
                view_count = CASE WHEN $keep THEN items.view_count ELSE excluded.view_count END,
                last_viewed_at = CASE WHEN $keep THEN items.last_viewed_at ELSE excluded.last_viewed_at END,
                user_rating = CASE WHEN $keep THEN items.user_rating ELSE excluded.user_rating END,
                resolution = excluded.resolution,
                video_range = COALESCE(excluded.video_range, items.video_range),
                audio = COALESCE(excluded.audio, items.audio),
                versions = excluded.versions, generation = excluded.generation,
                details = CASE WHEN excluded.updated_at IS NOT items.updated_at THEN NULL ELSE items.details END,
                updated_at = excluded.updated_at
            """;

        string[] names =
        [
            "$id", "$lib", "$kind", "$parent", "$show", "$title", "$sort", "$orig", "$showTitle", "$season", "$num",
            "$year", "$summary", "$tagline", "$rating", "$cr", "$studio", "$genres", "$dur", "$released", "$added",
            "$updated", "$poster", "$backdrop", "$logo", "$still", "$tint", "$children", "$leaves", "$viewedLeaves",
            "$pos", "$views", "$lastViewed", "$res", "$range", "$audio", "$versions", "$gen", "$userRating",
        ];
        var p = names.ToDictionary(n => n, n => cmd.Parameters.AddWithValue(n, DBNull.Value));
        cmd.Parameters.AddWithValue("$keep", keepProgress);
        cmd.Prepare();

        foreach (var i in items)
        {
            object?[] values =
            [
                i.Id, i.LibraryId, (int)i.Kind, i.ParentId, i.ShowId, i.Title, i.SortTitle ?? i.Title, i.OriginalTitle,
                i.ShowTitle, i.SeasonNumber, i.Number, i.Year, i.Summary, i.Tagline, i.Rating, i.ContentRating, i.Studio,
                i.Genres, i.DurationMs, i.Released, i.AddedAt, i.UpdatedAt, i.Poster, i.Backdrop, i.Logo, i.Still, i.Tint,
                i.ChildCount, i.LeafCount, i.ViewedLeafCount, i.PositionMs, i.ViewCount, i.LastViewedAt, i.Resolution,
                i.VideoRange, i.Audio,
                i.Versions.Count == 0 ? null : JsonSerializer.Serialize(i.Versions, Json),
                generation,
                i.UserRating,
            ];
            for (int n = 0; n < names.Length; n++)
            {
                p[names[n]].Value = values[n] ?? DBNull.Value;
            }

            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public int DeleteStale(long libraryId, int generation)
    {
        using var c = Open();
        return Execute(c, "DELETE FROM items WHERE library_id = $l AND generation < $g", ("$l", libraryId), ("$g", generation));
    }

    public int CountItems(long libraryId, ItemKind kind)
    {
        using var c = Open();
        return (int)Scalar<long>(c, "SELECT COUNT(*) FROM items WHERE library_id = $l AND kind = $k", ("$l", libraryId), ("$k", (int)kind));
    }

    public void SetOnDeck(string serverId, IReadOnlyList<string> itemIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Execute(c, "DELETE FROM on_deck WHERE server_id = $s", ("$s", serverId));
        for (int i = 0; i < itemIds.Count; i++)
        {
            Execute(c, "INSERT OR IGNORE INTO on_deck (item_id, server_id, rank) SELECT $id, $s, $r WHERE EXISTS (SELECT 1 FROM items WHERE id = $id)",
                ("$id", itemIds[i]), ("$s", serverId), ("$r", i));
        }

        tx.Commit();
    }

    public void SaveDetails(string id, ItemDetails details, string? videoRange, string? audio)
    {
        using var c = Open();
        Execute(c, """
            UPDATE items SET details = $d, details_at = $at,
                video_range = COALESCE($range, video_range), audio = COALESCE($audio, audio)
            WHERE id = $id
            """,
            ("$d", JsonSerializer.Serialize(details, Json)), ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$range", videoRange), ("$audio", audio), ("$id", id));
    }

    public (int? TmdbId, bool Locked, string? Data, long? MatchedAt)? GetMatch(string itemId)
    {
        using var c = Open();
        return Query(c, "SELECT tmdb_id, locked, data, matched_at FROM matches WHERE item_id = $id",
            r => ((int?)(r.IsDBNull(0) ? null : r.GetInt32(0)), r.GetBoolean(1), r.IsDBNull(2) ? null : r.GetString(2), (long?)(r.IsDBNull(3) ? null : r.GetInt64(3))),
            ("$id", itemId)).Select(x => ((int?, bool, string?, long?)?)x).FirstOrDefault();
    }

    public void SaveMatch(string itemId, int? tmdbId, string kind, bool locked, string? data)
    {
        using var c = Open();
        Execute(c, """
            INSERT INTO matches (item_id, tmdb_id, kind, locked, data, matched_at) VALUES ($id, $t, $k, $l, $d, $at)
            ON CONFLICT (item_id) DO UPDATE SET tmdb_id = excluded.tmdb_id, kind = excluded.kind, locked = excluded.locked,
                data = excluded.data, matched_at = excluded.matched_at
            """, ("$id", itemId), ("$t", tmdbId), ("$k", kind), ("$l", locked), ("$d", data), ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    /// <summary>Overwrites an item's descriptive fields with matched metadata; nulls keep what's there.</summary>
    public void ApplyMetadata(string id, string? title, string? originalTitle, int? year, string? summary, string? tagline, double? rating,
        string? contentRating, string? genres, string? studio, string? released, string? poster, string? backdrop, string? logo, string? still, long? durationMs)
    {
        using var c = Open();
        Execute(c, """
            UPDATE items SET
                title = COALESCE($title, title), sort_title = COALESCE($title, sort_title), original_title = COALESCE($orig, original_title),
                year = COALESCE($year, year), summary = COALESCE($summary, summary), tagline = COALESCE($tagline, tagline),
                rating = COALESCE($rating, rating), content_rating = COALESCE($cr, content_rating), genres = COALESCE($genres, genres),
                studio = COALESCE($studio, studio), released = COALESCE($released, released), poster = COALESCE($poster, poster),
                backdrop = COALESCE($backdrop, backdrop), logo = COALESCE($logo, logo), still = COALESCE($still, still),
                duration_ms = COALESCE(duration_ms, $dur)
            WHERE id = $id
            """,
            ("$title", title), ("$orig", originalTitle), ("$year", year), ("$summary", summary), ("$tagline", tagline), ("$rating", rating),
            ("$cr", contentRating), ("$genres", genres), ("$studio", studio), ("$released", released), ("$poster", poster),
            ("$backdrop", backdrop), ("$logo", logo), ("$still", still), ("$dur", durationMs), ("$id", id));
    }

    /// <summary>Episodes of a show and their parents' show title, so a show's match can rename them.</summary>
    public void RenameShow(string showId, string title)
    {
        using var c = Open();
        Execute(c, "UPDATE items SET show_title = $t WHERE show_id = $s", ("$t", title), ("$s", showId));
    }

    public void SetUserRating(string id, double? rating)
    {
        using var c = Open();
        Execute(c, "UPDATE items SET user_rating = $r WHERE id = $id", ("$r", rating), ("$id", id));
    }

    public void SaveLogo(string id, string logo)
    {
        using var c = Open();
        Execute(c, "UPDATE items SET logo = $logo WHERE id = $id", ("$logo", logo), ("$id", id));
    }

    public void UpdateProgress(string id, long positionMs, bool watched)
    {
        using var c = Open();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (watched)
        {
            Execute(c, "UPDATE items SET position_ms = 0, view_count = view_count + 1, last_viewed_at = $now WHERE id = $id",
                ("$now", now), ("$id", id));
            Execute(c, "DELETE FROM on_deck WHERE item_id = $id", ("$id", id));
        }
        else
        {
            Execute(c, "UPDATE items SET position_ms = $p, last_viewed_at = $now WHERE id = $id",
                ("$p", positionMs), ("$now", now), ("$id", id));
        }

        RecountShow(c, id);
    }

    public void SetWatched(string id, bool watched)
    {
        using var c = Open();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Execute(c, """
            UPDATE items SET position_ms = 0,
                view_count = CASE WHEN $w THEN MAX(view_count, 1) ELSE 0 END,
                last_viewed_at = CASE WHEN $w THEN $now ELSE last_viewed_at END
            WHERE id = $id OR parent_id = $id OR show_id = $id
            """, ("$w", watched), ("$now", now), ("$id", id));
        RecountShow(c, id);
        NotifyChanged();
    }

    private static void RecountShow(SqliteConnection c, string itemId)
    {
        Execute(c, """
            UPDATE items SET viewed_leaf_count = (
                SELECT COUNT(*) FROM items e WHERE e.kind = 3 AND e.view_count > 0
                  AND (e.show_id = items.id OR e.parent_id = items.id))
            WHERE kind IN (1, 2) AND id IN (
                SELECT show_id FROM items WHERE id = $id UNION SELECT parent_id FROM items WHERE id = $id
                UNION SELECT $id)
            """, ("$id", itemId));
    }

    // ---- helpers ----

    private static void Bind(SqliteCommand cmd, (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static int Execute(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(read(reader));
        }

        return list;
    }
}
