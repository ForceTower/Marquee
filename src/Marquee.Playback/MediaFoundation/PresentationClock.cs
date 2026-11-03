using System.Diagnostics;

namespace Marquee.Playback.MediaFoundation;

/// <summary>Where the video is: media time and whether it's advancing.</summary>
internal readonly record struct VideoClock(long Ticks, bool Playing);

/// <summary>
/// Media Foundation's presentation time, for whatever plays or draws outside Media Foundation in
/// step with its video (TrueHD Atmos audio, subtitles). It follows <c>MediaPlaybackSession</c> and
/// extrapolates between polls.
///
/// After a seek the session's position is unreliable twice over: it says "Playing" while the start
/// is still held (it reacts to Play(), not to frames), and once frames do flow it reports the
/// *requested* position for a while, though it presents from the actual start (the keyframe given
/// to SetActualStartPosition). So: Held until the start is released, then Armed until the next
/// "Playing" (anchored at the actual start), then Free, with polled positions ignored for a second
/// after anchoring.
/// </summary>
internal sealed class PresentationClock
{
    private static readonly long ArmedFallbackTicks = Stopwatch.Frequency * 3 / 10;

    private enum State
    {
        Free,
        Held,
        Armed,
    }

    private readonly object _lock = new();
    private State _state;
    private long _ticks;
    private long _stamp;
    private bool _playing;
    private long _armedStamp;
    private long _ignorePollsUntil;
    private TimeSpan _startPoint;

    public event Action<string>? Trace;

    public VideoClock Now()
    {
        lock (_lock)
        {
            long ticks = _ticks;
            if (_playing)
            {
                ticks += Stopwatch.GetElapsedTime(_stamp).Ticks;
            }

            return new VideoClock(ticks, _playing);
        }
    }

    /// <summary>The video (re)starts at <paramref name="start"/>; hold the clock there until frames flow.</summary>
    public void OnSeeked(TimeSpan start)
    {
        lock (_lock)
        {
            _startPoint = start;
            _ticks = start.Ticks;
            _stamp = Stopwatch.GetTimestamp();
            _playing = false;
            _state = State.Held;
        }
    }

    /// <summary>The held video start was released; frames are about to flow.</summary>
    public void OnStartReleased()
    {
        lock (_lock)
        {
            if (_state == State.Held)
            {
                _state = State.Armed;
                _armedStamp = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>The video engine reported a playback state change (play, pause, buffering).</summary>
    public void OnStateChanged(TimeSpan position, bool playing)
    {
        lock (_lock)
        {
            switch (_state)
            {
                case State.Held:
                    return;
                case State.Armed when playing:
                    Anchor(TimeSpan.Zero, $"video playing (reported {position:hh\\:mm\\:ss\\.fff})");
                    return;
                case State.Armed:
                    return;
            }

            _ticks = position.Ticks;
            _stamp = Stopwatch.GetTimestamp();
            _playing = playing;
        }
    }

    /// <summary>Called periodically with the video engine's position; <see cref="Now"/> extrapolates between calls.</summary>
    public void Update(TimeSpan position, bool playing)
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_state == State.Armed && playing && now - _armedStamp > ArmedFallbackTicks)
            {
                Anchor(Stopwatch.GetElapsedTime(_armedStamp), "no fresh Playing event after release; anchored from the release time");
                return;
            }

            if (_state != State.Free || now < _ignorePollsUntil)
            {
                return;
            }

            _ticks = position.Ticks;
            _stamp = now;
            _playing = playing;
        }
    }

    /// <summary>Starts the clock at the actual start point (plus <paramref name="elapsed"/>). Call under the lock.</summary>
    private void Anchor(TimeSpan elapsed, string why)
    {
        _ticks = (_startPoint + elapsed).Ticks;
        _stamp = Stopwatch.GetTimestamp();
        _playing = true;
        _state = State.Free;
        _ignorePollsUntil = _stamp + Stopwatch.Frequency;
        Trace?.Invoke($"clock anchored at {_startPoint + elapsed:hh\\:mm\\:ss\\.fff}: {why}");
    }
}
