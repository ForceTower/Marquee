using System.Net;
using System.Net.Http.Headers;

namespace Marquee.Playback.IO;

/// <summary>
/// A read-only, seekable stream over an HTTP resource that supports range requests (a Plex part,
/// for instance). Reading is sequential from one open-ended <c>Range: bytes=N-</c> response; a
/// short forward seek skips within it, anything else reopens the response at the new offset.
/// Not thread-safe: the demuxer serializes access.
/// </summary>
internal sealed class HttpRangeStream : Stream
{
    private const long ForwardSkipLimit = 2 << 20;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly Uri _uri;
    private readonly byte[] _buffer;
    private long _bufferStart;
    private int _bufferLength;
    private long _position;
    private HttpResponseMessage? _response;
    private Stream? _body;
    private long _bodyPosition;

    public HttpRangeStream(Uri uri, int bufferSize)
    {
        _uri = uri;
        _buffer = new byte[bufferSize];
        Length = OpenAt(0);
    }

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override int ReadByte()
    {
        if (!EnsureBuffered())
        {
            return -1;
        }

        return _buffer[(int)(_position++ - _bufferStart)];
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        int total = 0;
        while (destination.Length > 0 && EnsureBuffered())
        {
            int available = (int)(_bufferStart + _bufferLength - _position);
            int n = Math.Min(available, destination.Length);
            _buffer.AsSpan((int)(_position - _bufferStart), n).CopyTo(destination);
            destination = destination[n..];
            _position += n;
            total += n;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    private bool EnsureBuffered()
    {
        if (_position >= _bufferStart && _position < _bufferStart + _bufferLength)
        {
            return true;
        }

        if (_position >= Length)
        {
            return false;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Fill();
                return _bufferLength > 0;
            }
            catch (Exception ex) when (attempt < 2 && ex is HttpRequestException or IOException)
            {
                CloseBody();
            }
        }
    }

    private void Fill()
    {
        long skip = _position - _bodyPosition;
        if (_body is null || skip < 0 || skip > ForwardSkipLimit)
        {
            OpenAt(_position);
        }
        else
        {
            while (_bodyPosition < _position)
            {
                int n = _body.Read(_buffer, 0, (int)Math.Min(_buffer.Length, _position - _bodyPosition));
                if (n == 0)
                {
                    throw new IOException("Unexpected end of HTTP response");
                }

                _bodyPosition += n;
            }
        }

        int filled = 0;
        while (filled < _buffer.Length)
        {
            int n = _body!.Read(_buffer, filled, _buffer.Length - filled);
            if (n == 0)
            {
                break;
            }

            filled += n;
        }

        _bufferStart = _bodyPosition;
        _bufferLength = filled;
        _bodyPosition += filled;
    }

    /// <returns>The resource's total length.</returns>
    private long OpenAt(long offset)
    {
        CloseBody();
        var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(offset, null);
        _response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        _response.EnsureSuccessStatusCode();
        if (offset > 0 && _response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new NotSupportedException($"{_uri.Host} doesn't support range requests");
        }

        _body = _response.Content.ReadAsStream();
        _bodyPosition = offset;
        return _response.Content.Headers.ContentRange?.Length
               ?? _response.Content.Headers.ContentLength
               ?? throw new NotSupportedException("The server didn't report the file's length");
    }

    private void CloseBody()
    {
        _body?.Dispose();
        _response?.Dispose();
        _body = null;
        _response = null;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseBody();
        }

        base.Dispose(disposing);
    }
}
