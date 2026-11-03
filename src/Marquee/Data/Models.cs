namespace Marquee.Data;

public enum ItemKind
{
    Movie,
    Show,
    Season,
    Episode,
}

public enum LibraryType
{
    Movies,
    Shows,
    Other,
}

/// <summary>
/// One movie, show, season or episode, from Plex or a local folder. Image fields hold image
/// references (see <see cref="Imaging.ImageCache"/>), not URLs.
/// </summary>
public sealed record MediaItem
{
    public string Id { get; init; } = "";
    public long LibraryId { get; init; }
    public ItemKind Kind { get; init; }
    public string? ParentId { get; init; }
    public string? ShowId { get; init; }
    public string Title { get; init; } = "";
    public string? SortTitle { get; init; }
    public string? OriginalTitle { get; init; }
    public string? ShowTitle { get; init; }
    public int? SeasonNumber { get; init; }
    public int? Number { get; init; }
    public int? Year { get; init; }
    public string? Summary { get; init; }
    public string? Tagline { get; init; }
    public double? Rating { get; init; }

    /// <summary>The user's own rating, 0–10 (Plex's scale; five stars in half steps).</summary>
    public double? UserRating { get; init; }

    public string? ContentRating { get; init; }
    public string? Studio { get; init; }
    public string? Genres { get; init; }
    public long? DurationMs { get; init; }
    public string? Released { get; init; }
    public long? AddedAt { get; init; }
    public long? UpdatedAt { get; init; }
    public string? Poster { get; init; }
    public string? Backdrop { get; init; }
    public string? Logo { get; init; }
    public string? Still { get; init; }
    public string? Tint { get; init; }
    public int? ChildCount { get; init; }
    public int? LeafCount { get; init; }
    public int? ViewedLeafCount { get; init; }
    public long PositionMs { get; init; }
    public int ViewCount { get; init; }
    public long? LastViewedAt { get; init; }
    public string? Resolution { get; init; }
    public string? VideoRange { get; init; }
    public string? Audio { get; init; }
    public IReadOnlyList<MediaVersion> Versions { get; init; } = [];
    public string? DetailsJson { get; init; }
    public long? DetailsAt { get; init; }

    public bool IsPlex => Id.StartsWith("p:", StringComparison.Ordinal);
    public bool IsPlayable => Kind is ItemKind.Movie or ItemKind.Episode;
    public bool IsWatched => Kind is ItemKind.Show or ItemKind.Season
        ? LeafCount > 0 && ViewedLeafCount >= LeafCount
        : ViewCount > 0 && PositionMs == 0;
    public bool InProgress => PositionMs > 0 && DurationMs > 0;
    public double Progress => DurationMs is > 0 ? Math.Clamp(PositionMs / (double)DurationMs.Value, 0, 1) : 0;
}

/// <param name="Location">A Plex part ("plex:{server}:{part key}") or a file path.</param>
public sealed record MediaVersion(
    string Location,
    string? File,
    long? Size,
    string? Container,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? Resolution,
    string? AudioCodec,
    int? AudioChannels,
    long? Bitrate,
    long? DurationMs);

public sealed record Person(string Name, string? Role, string? Photo);

public sealed record Chapter(long StartMs, long EndMs, string? Title, string? Thumb)
{
    public bool HasThumb => !string.IsNullOrEmpty(Thumb);
}

public sealed record Marker(string Type, long StartMs, long EndMs);

public enum StreamKind
{
    Video = 1,
    Audio = 2,
    Subtitle = 3,
}

/// <param name="Ordinal">Position among streams of the same kind in the file (0-based).</param>
/// <param name="ExternalKey">Plex key of a sidecar subtitle file, if this stream isn't in the container.</param>
public sealed record StreamInfo(
    StreamKind Kind,
    int Ordinal,
    string? Language,
    string? LanguageTag,
    string? Title,
    string? Display,
    string? Codec,
    string? Profile,
    int? Channels,
    bool Forced,
    bool Default,
    int? DoviProfile,
    string? ColorTransfer,
    string? ExternalKey)
{
    public bool IsAtmos =>
        Profile?.Contains("atmos", StringComparison.OrdinalIgnoreCase) == true ||
        Display?.Contains("Atmos", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsDtsX => Profile?.Contains("dts:x", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record VersionDetails(string Location, IReadOnlyList<StreamInfo> Streams);

/// <summary>Everything only the full metadata fetch returns, cached as JSON on the item.</summary>
public sealed record ItemDetails
{
    public IReadOnlyList<Person> Cast { get; init; } = [];
    public IReadOnlyList<string> Directors { get; init; } = [];
    public IReadOnlyList<string> Writers { get; init; } = [];
    public IReadOnlyList<string> Countries { get; init; } = [];
    public IReadOnlyList<Chapter> Chapters { get; init; } = [];
    public IReadOnlyList<Marker> Markers { get; init; } = [];
    public IReadOnlyList<VersionDetails> Versions { get; init; } = [];
}

public sealed record LibraryInfo
{
    public required long Id { get; init; }
    public required string Kind { get; init; }
    public string? ServerId { get; init; }
    public string? SectionKey { get; init; }
    public string? Path { get; init; }
    public required string Title { get; init; }
    public required LibraryType Type { get; init; }
    public bool Enabled { get; init; }
    public bool Watch { get; init; }
    public int ItemCount { get; init; }
    public long? ScannedAt { get; init; }
    public string? Status { get; init; }

    public bool IsPlex => Kind == "plex";
}

public sealed record ServerInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Owned { get; init; }
    public required string Token { get; init; }
    public string? Uri { get; init; }
    public string? ConnectionKind { get; init; }
    public bool Online { get; init; }
    public long? SyncedAt { get; init; }
}
