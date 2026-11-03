using Marquee.Playback.MediaFoundation;

namespace Marquee.Playback.Subtitles;

/// <summary>
/// Shows one subtitle track in step with a video clock. A thread of its own feeds the track's
/// packets to its renderer and renders once per video frame (or when the layout, the delay or the
/// packets change), raising <see cref="FrameReady"/> whenever what's on screen changes.
/// </summary>
internal sealed class SubtitlePlayback : IDisposable
{
    private readonly ISubtitleSource _source;
    private readonly ISubtitleRenderer _renderer;
    private readonly Func<VideoClock> _clock;
    private readonly long _frameTicks;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _layoutLock = new();
    private SubtitleLayout _layout;
    private bool _layoutChanged;
    private long _delayTicks;
    private volatile bool _stop;

    /// <summary>Raised on the subtitle thread. The handler owns the frame and must <see cref="SubtitleFrame.Release"/> it.</summary>
    public event Action<SubtitleFrame>? FrameReady;

    public event Action<string>? Failed;

    public string Description { get; }

    /// <param name="frameRate">The video's frame rate; subtitles are rendered at most once per frame.</param>
    public SubtitlePlayback(ISubtitleSource source, ISubtitleRenderer renderer, Func<VideoClock> clock, double frameRate, string description)
    {
        _source = source;
        _renderer = renderer;
        _clock = clock;
        _frameTicks = (long)(TimeSpan.TicksPerSecond / (frameRate is > 1 and < 300 ? frameRate : 24));
        Description = description;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Subtitles" };
        _thread.Start();
    }

    public SubtitleLayout Layout
    {
        set
        {
            lock (_layoutLock)
            {
                if (value == _layout)
                {
                    return;
                }

                _layout = value;
                _layoutChanged = true;
            }

            _wake.Set();
        }
    }

    /// <summary>Positive shows subtitles later.</summary>
    public TimeSpan Delay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _delayTicks));
        set
        {
            Interlocked.Exchange(ref _delayTicks, value.Ticks);
            _wake.Set();
        }
    }

    private void Loop()
    {
        long lastSlot = long.MinValue;
        int failures = 0;
        while (!_stop)
        {
            _wake.WaitOne(5);
            bool added = false;
            while (_source.TryTake(out var packet))
            {
                _renderer.Add(packet);
                added = true;
            }

            SubtitleLayout layout;
            bool force;
            lock (_layoutLock)
            {
                layout = _layout;
                force = _layoutChanged;
                _layoutChanged = false;
            }

            VideoClock clock = _clock();
            _source.Focus(clock.Ticks);
            if (layout.IsEmpty)
            {
                continue;
            }

            long time = clock.Ticks - Interlocked.Read(ref _delayTicks);
            long slot = Math.DivRem(time, _frameTicks, out long remainder) - (remainder < 0 ? 1 : 0);
            if (!force && !added && slot == lastSlot)
            {
                continue;
            }

            lastSlot = slot;
            try
            {
                if (_renderer.Render(time, layout, force) is { } frame)
                {
                    if (FrameReady is { } handler)
                    {
                        handler(frame);
                    }
                    else
                    {
                        frame.Release();
                    }
                }

                failures = 0;
            }
            catch (Exception ex) when (!_stop)
            {
                if (++failures is 1 or 100)
                {
                    Failed?.Invoke($"rendering subtitles: {ex.Message}");
                }

                _wake.WaitOne(100);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _source.Dispose();
        _renderer.Dispose();
        _wake.Dispose();
    }
}
