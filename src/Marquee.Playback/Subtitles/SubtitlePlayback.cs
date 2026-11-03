using System.Diagnostics;
using Marquee.Playback.MediaFoundation;

namespace Marquee.Playback.Subtitles;

/// <summary>
/// Shows one subtitle track in step with a video clock. A thread of its own feeds the track's
/// packets to its renderer and renders once per video frame (or when the layout, the delay or the
/// packets change), raising <see cref="FrameReady"/> whenever what's on screen changes. Changes to
/// the layout's bottom reserve are eased, so subtitles glide out of the controls' way.
/// </summary>
internal sealed class SubtitlePlayback : IDisposable
{
    private const double ReserveEaseSeconds = 0.06;

    private readonly ISubtitleSource _source;
    private readonly ISubtitleRenderer _renderer;
    private readonly Func<VideoClock> _clock;
    private readonly long _frameTicks;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _layoutLock = new();
    private SubtitleLayout _layout;
    private bool _layoutChanged;
    private SubtitleLayout _rendered;
    private double _reserve;
    private long _reserveStamp;
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

            SubtitleLayout target;
            bool force;
            lock (_layoutLock)
            {
                target = _layout;
                force = _layoutChanged;
                _layoutChanged = false;
            }

            var layout = target with { BottomReserve = EaseReserve(target.BottomReserve) };
            if (layout != _rendered)
            {
                _rendered = layout;
                force = true;
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

    /// <summary>Moves the reserve in use exponentially towards <paramref name="goal"/>, about 95% of the way in 0.18 s.</summary>
    private int EaseReserve(int goal)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _reserveStamp == 0 ? 0 : Stopwatch.GetElapsedTime(_reserveStamp, now).TotalSeconds;
        _reserveStamp = now;
        _reserve += (goal - _reserve) * Math.Min(1, elapsed / ReserveEaseSeconds);
        if (Math.Abs(goal - _reserve) < 1)
        {
            _reserve = goal;
        }

        return (int)Math.Round(_reserve);
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
