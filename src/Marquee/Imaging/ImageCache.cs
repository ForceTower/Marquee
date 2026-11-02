using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Marquee.Plex;

namespace Marquee.Imaging;

/// <summary>
/// Downloads artwork once, at the size it's shown, into a disk cache. References are
/// "plex:{server}:{path}" (resized by the server's photo transcoder), an http(s) URL, or a file path.
/// </summary>
internal sealed class ImageCache(PlexService plex, string directory)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();
    private readonly SemaphoreSlim _downloads = new(6);

    public Task<string?> GetAsync(string reference, int width, int height)
    {
        if (!reference.StartsWith("plex:", StringComparison.Ordinal) && !reference.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(File.Exists(reference) ? reference : null);
        }

        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{reference}|{width}x{height}"))).ToLowerInvariant();
        string path = Path.Combine(directory, key[..2], key + ".img");
        if (File.Exists(path))
        {
            return Task.FromResult<string?>(path);
        }

        return _inFlight.GetOrAdd(path, _ => DownloadAsync(reference, width, height, path));
    }

    private async Task<string?> DownloadAsync(string reference, int width, int height, string path)
    {
        try
        {
            string? url = reference.StartsWith("plex:", StringComparison.Ordinal) ? plex.ResolveImage(reference, width, height) : reference;
            if (url is null)
            {
                return null;
            }

            await _downloads.WaitAsync();
            try
            {
                byte[] bytes = await _http.GetByteArrayAsync(url);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".part";
                await File.WriteAllBytesAsync(temp, bytes);
                File.Move(temp, path, overwrite: true);
                return path;
            }
            finally
            {
                _downloads.Release();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(path, out _);
        }
    }
}
