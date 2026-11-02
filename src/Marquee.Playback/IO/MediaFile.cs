namespace Marquee.Playback.IO;

/// <summary>A media location is either a file path (local or UNC) or an http(s) URL, e.g. a Plex part.</summary>
internal static class MediaFile
{
    public static bool IsUrl(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https";

    public static Stream OpenRead(string location, int bufferSize = 1 << 20) =>
        IsUrl(location)
            ? new HttpRangeStream(new Uri(location), Math.Max(bufferSize, 256 * 1024))
            : new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);

    /// <summary>The extension of a path, or of a URL's path without its query (".mkv").</summary>
    public static string Extension(string location) =>
        (IsUrl(location) ? Path.GetExtension(new Uri(location).AbsolutePath) : Path.GetExtension(location)).ToLowerInvariant();
}
