using System.Diagnostics;
using EngineSpike.Matroska;

namespace EngineSpike.SpatialAudio;

/// <summary>Decodes an object-audio track one access unit at a time.</summary>
internal interface IAtmosDecoder : IDisposable
{
    /// <summary>Returns the audio the access unit completes, or null (e.g. before the first sync after a reset).</summary>
    AtmosChunk? Decode(ReadOnlySpan<byte> accessUnit);

    /// <summary>Forgets all state; the next access units are a new, discontinuous stream (after a seek).</summary>
    void Reset();
}

/// <summary>
/// Plays a TrueHD Atmos track alongside video that Media Foundation renders on its own. A decode
/// thread reads the track through its own demuxer into an <see cref="AtmosStream"/>, and
/// <see cref="SpatialAtmosOutput"/> renders it locked to the video clock.
///
/// TrueHD can only start decoding at a major sync (up to ~107 ms apart) and object positions arrive
/// every ~32 ms, so after a seek decoding starts <see cref="PreRoll"/> early; the audio before the
/// start point is skipped by the output's resync. <see cref="WaitUntilReadyAsync"/> lets the video
/// start wait until audio covering the start point has been decoded.
/// </summary>
internal sealed class AtmosPlayback : IDisposable
{
    private const double DecodeAheadSeconds = 0.5;
    private static readonly TimeSpan PreRoll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadyMargin = TimeSpan.FromMilliseconds(200);

    private readonly MatroskaDemuxer _demuxer;
    private readonly int _track;
    private readonly int _seekTrack;
    private readonly IAtmosDecoder _decoder;
    private readonly AtmosStream _stream = new();
    private readonly SpatialAtmosOutput _output;
    private readonly Thread _thread;
    private readonly object _clockLock = new();
    private readonly object _seekLock = new();
    private volatile bool _stop;
    private long _clockTicks;
    private long _clockStamp;
    private bool _clockPlaying;

    // The video clock after a seek, as Media Foundation reports it, is unreliable twice over: it
    // says "Playing" while the start is still held (it reacts to Play(), not to frames), and once
    // frames do flow it reports the *requested* position for a while, though it presents from the
    // actual start (the keyframe given to SetActualStartPosition). So: Held until the start is
    // released, then Armed until the next "Playing" (anchored at the actual start), then Free —
    // with polled positions ignored for a second after anchoring.
    private enum ClockState
    {
        Free,
        Held,
        Armed,
    }

    private static readonly long ArmedFallbackTicks = Stopwatch.Frequency * 3 / 10;
    private ClockState _clockState;
    private long _armedStamp;
    private long _ignorePollsUntil;
    private TimeSpan? _pendingSeek;
    private TimeSpan _startPoint;
    private long _seekStamp;
    private long _firstChunkTicks = -1;
    private TaskCompletionSource? _ready;

    public SpatialAtmosOutput Output => _output;
    public int DecodedChunks { get; private set; }

    public event Action<string>? Failed;
    public event Action<string>? Trace;

    public AtmosPlayback(string path, int track, IAtmosDecoder decoder)
    {
        _demuxer = MatroskaDemuxer.Open(path, 64 * 1024);
        _demuxer.Expose(track, active: true);
        _track = track;
        _seekTrack = _demuxer.FirstVideoTrack ?? track;
        _decoder = decoder;
        _output = new SpatialAtmosOutput(_stream, ClockNow);
        _output.Failed += message => Failed?.Invoke(message);
        _output.Trace += message => Trace?.Invoke(message);
        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "Atmos decode" };
        _thread.Start();
    }

    /// <summary>The video (re)starts at <paramref name="start"/>: drop everything and decode from just before it.</summary>
    public void OnSeeked(TimeSpan start)
    {
        lock (_seekLock)
        {
            _pendingSeek = start;
            _startPoint = start;
            _seekStamp = Stopwatch.GetTimestamp();
            _ready?.TrySetResult();
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        lock (_clockLock)
        {
            _clockTicks = start.Ticks;
            _clockStamp = Stopwatch.GetTimestamp();
            _clockPlaying = false;
            _clockState = ClockState.Held;
        }

        _stream.Reset();
    }

    /// <summary>The held video start was released; frames are about to flow.</summary>
    public void OnStartReleased()
    {
        lock (_clockLock)
        {
            if (_clockState == ClockState.Held)
            {
                _clockState = ClockState.Armed;
                _armedStamp = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>The video engine reported a playback state change (play, pause, buffering).</summary>
    public void OnVideoStateChanged(TimeSpan position, bool playing)
    {
        lock (_clockLock)
        {
            switch (_clockState)
            {
                case ClockState.Held:
                    return;
                case ClockState.Armed when playing:
                    Anchor(TimeSpan.Zero, $"video playing (reported {position:hh\\:mm\\:ss\\.fff})");
                    return;
                case ClockState.Armed:
                    return;
            }

            _clockTicks = position.Ticks;
            _clockStamp = Stopwatch.GetTimestamp();
            _clockPlaying = playing;
        }
    }

    /// <summary>Starts the clock at the actual start point (plus <paramref name="elapsed"/>). Call under the clock lock.</summary>
    private void Anchor(TimeSpan elapsed, string why)
    {
        TimeSpan start;
        lock (_seekLock)
        {
            start = _startPoint;
        }

        _clockTicks = (start + elapsed).Ticks;
        _clockStamp = Stopwatch.GetTimestamp();
        _clockPlaying = true;
        _clockState = ClockState.Free;
        _ignorePollsUntil = _clockStamp + Stopwatch.Frequency;
        Trace?.Invoke($"clock anchored at {start + elapsed:hh\\:mm\\:ss\\.fff}: {why}");
    }

    /// <summary>Completes once audio reaching a little past the latest start point has been decoded.</summary>
    public Task WaitUntilReadyAsync(TimeSpan start)
    {
        lock (_seekLock)
        {
            return _ready?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>Called periodically with the video engine's position; the output extrapolates between calls.</summary>
    public void UpdateVideoClock(TimeSpan position, bool playing)
    {
        lock (_clockLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_clockState == ClockState.Armed && playing && now - _armedStamp > ArmedFallbackTicks)
            {
                Anchor(Stopwatch.GetElapsedTime(_armedStamp), "no fresh Playing event after release; anchored from the release time");
                return;
            }

            if (_clockState != ClockState.Free || now < _ignorePollsUntil)
            {
                return;
            }

            _clockTicks = position.Ticks;
            _clockStamp = Stopwatch.GetTimestamp();
            _clockPlaying = playing;
        }
    }

    private VideoClock ClockNow()
    {
        lock (_clockLock)
        {
            long ticks = _clockTicks;
            if (_clockPlaying)
            {
                ticks += Stopwatch.GetElapsedTime(_clockStamp).Ticks;
            }

            return new VideoClock(ticks, _clockPlaying);
        }
    }

    private bool SeekPending()
    {
        lock (_seekLock)
        {
            return _pendingSeek is not null;
        }
    }

    private void DecodeLoop()
    {
        try
        {
            while (!_stop)
            {
                TimeSpan? seek;
                lock (_seekLock)
                {
                    seek = _pendingSeek;
                    _pendingSeek = null;
                }

                if (seek is { } start)
                {
                    TimeSpan from = _demuxer.Seek(start - PreRoll, _seekTrack);
                    _decoder.Reset();
                    _firstChunkTicks = -1;
                    Trace?.Invoke($"seek to {start:hh\\:mm\\:ss\\.fff}: decoding from {from:hh\\:mm\\:ss\\.fff} ({(start - from).TotalMilliseconds:0} ms pre-roll)");
                    continue;
                }

                if (DecodedEnough())
                {
                    Thread.Sleep(5);
                    continue;
                }

                int generation = _stream.Generation;
                if (_demuxer.Next(_track) is not { } packet)
                {
                    lock (_seekLock)
                    {
                        _ready?.TrySetResult();
                    }

                    Thread.Sleep(20);
                    continue;
                }

                foreach (var frame in packet.Frames())
                {
                    AtmosChunk? chunk = _decoder.Decode(frame.Span);
                    if (chunk is null || generation != _stream.Generation || SeekPending())
                    {
                        continue;
                    }

                    if (_firstChunkTicks < 0)
                    {
                        _firstChunkTicks = packet.TimeTicks;
                    }

                    _stream.Write(chunk, packet.TimeTicks);
                    DecodedChunks++;
                    SignalIfReady();
                }
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"decode: {ex.Message}");
        }
    }

    /// <summary>Whether decoding is far enough ahead of playback (or of the start point, before playback anchors).</summary>
    private bool DecodedEnough()
    {
        if (!_stream.HasBase)
        {
            return false;
        }

        double reference = _output.ReadPosition;
        if (double.IsNaN(reference))
        {
            TimeSpan start;
            lock (_seekLock)
            {
                start = _startPoint;
            }

            reference = _stream.SampleAt(start.Ticks);
        }

        return _stream.Written - reference > DecodeAheadSeconds * AtmosStream.SampleRate;
    }

    private void SignalIfReady()
    {
        TaskCompletionSource? ready;
        TimeSpan start;
        long stamp;
        lock (_seekLock)
        {
            ready = _ready;
            start = _startPoint;
            stamp = _seekStamp;
        }

        if (ready is null || ready.Task.IsCompleted || _stream.Written < _stream.SampleAt((start + ReadyMargin).Ticks))
        {
            return;
        }

        if (ready.TrySetResult())
        {
            double lead = (start.Ticks - _firstChunkTicks) / (double)TimeSpan.TicksPerMillisecond;
            Trace?.Invoke($"audio ready {Stopwatch.GetElapsedTime(stamp).TotalMilliseconds:0} ms after the seek; " +
                          $"first decoded audio {lead:0} ms before the start point");
        }
    }

    public void Dispose()
    {
        _stop = true;
        lock (_seekLock)
        {
            _ready?.TrySetResult();
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _output.Dispose();
        _decoder.Dispose();
        _demuxer.Dispose();
    }
}
