using Marquee.Playback.Previews;
using Marquee.Shell;
using Microsoft.UI.Dispatching;

namespace Marquee.Player;

/// <summary>
/// Pictures for the seek bar while the pointer skims it. Each file gets a thread of its own, with a
/// <see cref="FrameThumbnailer"/> opened as soon as playback starts. The thread decodes only the
/// newest position asked for, skipping those overtaken while it worked, so skimming shows the
/// latest picture it can get to. Recent pictures are kept by keyframe.
/// </summary>
internal sealed class SeekPreviews
{
    private const int CacheSize = 48;
    private const int MaxFailuresInARow = 5;

    /// <summary>Without an index to name the keyframe ahead of time, positions this close share a picture.</summary>
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(2);

    private readonly DispatcherQueue _dispatcher;
    private Session? _session;

    public SeekPreviews(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>A picture is ready, raised on the UI thread.</summary>
    public event Action<PreviewFrame>? Ready;

    /// <summary>The file can't give pictures (no index, an unsupported codec); the bar shows times only.</summary>
    public bool Unavailable => _session?.Failed == true;

    /// <summary>Starts reading <paramref name="location"/> for pictures; the same file keeps what it has (an engine switch).</summary>
    public void Load(string location)
    {
        if (_session?.Location == location)
        {
            return;
        }

        Unload();
        _session = new Session(this, location);
    }

    public void Unload()
    {
        _session?.Close();
        _session = null;
    }

    /// <param name="width">The picture's width in pixels.</param>
    public void Request(TimeSpan position, int width) => _session?.Request(position, width);

    private sealed class Session
    {
        private readonly SeekPreviews _owner;
        private readonly object _gate = new();
        private readonly Dictionary<(long Key, int Width), PreviewFrame> _cache = new();
        private readonly LinkedList<(long Key, int Width)> _recent = new();
        private (TimeSpan Position, int Width)? _pending;
        private bool _closed;
        private volatile bool _failed;

        public Session(SeekPreviews owner, string location)
        {
            _owner = owner;
            Location = location;
            new Thread(Run) { IsBackground = true, Name = "Seek previews", Priority = ThreadPriority.BelowNormal }.Start();
        }

        public string Location { get; }

        public bool Failed => _failed;

        public void Request(TimeSpan position, int width)
        {
            lock (_gate)
            {
                _pending = (position, width);
                Monitor.Pulse(_gate);
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                Monitor.Pulse(_gate);
            }
        }

        private void Run()
        {
            FrameThumbnailer thumbnailer;
            try
            {
                thumbnailer = FrameThumbnailer.Open(Location);
                Log.Info($"Seek previews: {thumbnailer.Description}");
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Info($"No seek previews: {ex.Message}");
                return;
            }

            using (thumbnailer)
            {
                bool loggedTiming = false;
                int failures = 0;
                while (Next() is var (position, width))
                {
                    var key = (thumbnailer.KeyframeAt(position)?.Ticks ?? position.Ticks / Bucket.Ticks * Bucket.Ticks, width);
                    if (_cache.TryGetValue(key, out var frame))
                    {
                        _recent.Remove(key);
                        _recent.AddFirst(key);
                        Post(frame);
                        continue;
                    }

                    try
                    {
                        frame = thumbnailer.Render(position, width);
                        failures = 0;
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Seek preview at {Format.Clock(position)} failed: {ex.Message}");
                        if (++failures >= MaxFailuresInARow)
                        {
                            _failed = true;
                            return;
                        }

                        continue;
                    }

                    if (frame is null)
                    {
                        continue;
                    }

                    if (!loggedTiming)
                    {
                        loggedTiming = true;
                        Log.Info($"Seek preview at {Format.Clock(position)}: {frame.Kind}, read {frame.ReadTime.TotalMilliseconds:0} ms, " +
                                 $"decoded in {frame.DecodeTime.TotalMilliseconds:0} ms");
                    }

                    Remember(key, frame);
                    Post(frame);
                }
            }
        }

        /// <summary>Waits for a position to decode; null once closed.</summary>
        private (TimeSpan Position, int Width)? Next()
        {
            lock (_gate)
            {
                while (!_closed && _pending is null)
                {
                    Monitor.Wait(_gate);
                }

                var next = _closed ? null : _pending;
                _pending = null;
                return next;
            }
        }

        private void Remember((long, int) key, PreviewFrame frame)
        {
            _cache[key] = frame;
            _recent.AddFirst(key);
            while (_recent.Count > CacheSize)
            {
                _cache.Remove(_recent.Last!.Value);
                _recent.RemoveLast();
            }
        }

        private void Post(PreviewFrame frame) => _owner._dispatcher.TryEnqueue(() =>
        {
            if (_owner._session == this)
            {
                _owner.Ready?.Invoke(frame);
            }
        });
    }
}
