using System.Diagnostics;
using Marquee.Playback.Demuxing;
using Marquee.Playback.MediaFoundation;

namespace Marquee.Playback.SpatialAudio;

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
/// <see cref="SpatialAtmosOutput"/> renders it locked to the video's <see cref="PresentationClock"/>.
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

    private readonly IDemuxer _demuxer;
    private readonly int _track;
    private readonly IAtmosDecoder _decoder;
    private readonly AtmosStream _stream = new();
    private readonly SpatialAtmosOutput _output;
    private readonly Thread _thread;
    private readonly object _seekLock = new();
    private volatile bool _stop;
    private TimeSpan? _pendingSeek;
    private TimeSpan _startPoint;
    private long _seekStamp;
    private long _firstChunkTicks = -1;
    private TaskCompletionSource? _ready;

    public SpatialAtmosOutput Output => _output;
    public int DecodedChunks { get; private set; }

    public event Action<string>? Failed;
    public event Action<string>? Trace;

    /// <param name="clock">
    /// The video's clock. Its owner feeds it the video's seeks and state; wire its
    /// <see cref="PresentationClock.OnSeeked"/> before this instance's <see cref="OnSeeked"/>.
    /// </param>
    public AtmosPlayback(string path, int track, IAtmosDecoder decoder, PresentationClock clock)
    {
        _demuxer = Demuxer.Open(path, 64 * 1024);
        _demuxer.Expose(track, active: true);
        _track = track;
        _decoder = decoder;
        _output = new SpatialAtmosOutput(_stream, clock.Now);
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

        _stream.Reset();
    }

    /// <summary>Completes once audio reaching a little past the latest start point has been decoded.</summary>
    public Task WaitUntilReadyAsync(TimeSpan start)
    {
        lock (_seekLock)
        {
            return _ready?.Task ?? Task.CompletedTask;
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
                    TimeSpan from = _demuxer.Seek(start - PreRoll, _track);
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
