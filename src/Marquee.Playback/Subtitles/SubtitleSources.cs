using System.Collections.Concurrent;
using Marquee.Playback.Demuxing;
using Marquee.Playback.Matroska;

namespace Marquee.Playback.Subtitles;

/// <summary>
/// A Matroska subtitle track whose blocks each have an index entry (mkvmerge writes them, with each
/// block's place in its cluster). A background thread reads exactly the blocks around the playing
/// time through a small-buffered demuxer of its own, so a seek or a track switch shows what should
/// already be on screen.
/// </summary>
internal sealed class MatroskaSubtitleSource : ISubtitleSource
{
    private static readonly long Behind = TimeSpan.FromSeconds(10).Ticks;
    private static readonly long Ahead = TimeSpan.FromSeconds(60).Ticks;
    private static readonly long JumpTicks = TimeSpan.FromSeconds(5).Ticks;

    private readonly ConcurrentQueue<SubtitlePacket> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly MatroskaDemuxer _reader;
    private readonly int _track;
    private readonly CuePoint[] _cues;
    private readonly bool[] _loaded;
    private readonly long _lookBack;
    private readonly Thread _thread;
    private long _focus = long.MinValue;
    private volatile bool _stop;
    private bool _reportedFailure;

    public int Packets { get; private set; }

    public event Action<string>? Failed;

    /// <summary>Whether the playback demuxer's index has an entry for (nearly) every block of the track.</summary>
    public static bool CanRead(MatroskaDemuxer playbackDemuxer, int track)
    {
        var cues = playbackDemuxer.Cues.Where(c => c.Track == track).ToList();
        return cues.Count > 0 && cues.Count(c => c.RelativePosition > 0) >= cues.Count * 9 / 10;
    }

    public MatroskaSubtitleSource(string location, int track, MatroskaDemuxer playbackDemuxer)
    {
        _track = track;
        _cues = playbackDemuxer.Cues.Where(c => c.Track == track).OrderBy(c => c.TimeTicks).ToArray();
        _loaded = new bool[_cues.Length];
        _lookBack = Math.Max(Behind, Math.Min(_cues.Max(c => c.DurationTicks), TimeSpan.FromMinutes(5).Ticks));
        _reader = MatroskaDemuxer.Open(location, 16 * 1024);
        _thread = new Thread(Load) { IsBackground = true, Name = $"Subtitle track {track} reader" };
        _thread.Start();
    }

    public bool TryTake(out SubtitlePacket packet) => _queue.TryDequeue(out packet!);

    public void Focus(long ticks)
    {
        long previous = Interlocked.Exchange(ref _focus, ticks);
        if (previous == long.MinValue || Math.Abs(ticks - previous) > JumpTicks)
        {
            _wake.Set();
        }
    }

    private void Load()
    {
        while (!_stop)
        {
            long focus = Interlocked.Read(ref _focus);
            int next = focus == long.MinValue ? -1 : NextToLoad(focus);
            if (next < 0)
            {
                _wake.WaitOne(250);
                continue;
            }

            try
            {
                if (_reader.ReadPacketAt(_cues[next]) is { } packet)
                {
                    _queue.Enqueue(new SubtitlePacket(packet.TimeTicks, packet.DurationTicks, packet.Data));
                    Packets++;
                }

                _loaded[next] = true;
            }
            catch (Exception ex) when (!_stop)
            {
                if (!_reportedFailure)
                {
                    _reportedFailure = true;
                    Failed?.Invoke($"reading subtitle track {_track}: {ex.Message}");
                }

                _wake.WaitOne(1000);
            }
        }
    }

    /// <summary>The first unread block that's on screen at <paramref name="focus"/> or starts within the next minute.</summary>
    private int NextToLoad(long focus)
    {
        int lo = 0, hi = _cues.Length;
        long from = focus - _lookBack;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_cues[mid].TimeTicks < from)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int i = lo; i < _cues.Length && _cues[i].TimeTicks <= focus + Ahead; i++)
        {
            if (!_loaded[i] && _cues[i].TimeTicks + Math.Max(_cues[i].DurationTicks, Behind) >= focus)
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _reader.Dispose();
        _wake.Dispose();
    }
}

/// <summary>
/// A subtitle track read through a demuxer of its own that can seek to any of the track's samples:
/// MP4's sample tables index every one, so this reads an MP4 track that Windows' own MP4 source
/// plays alongside without handing over. A background thread reads from just before the playing
/// time to a minute past it, and starts again from just before a jump.
/// </summary>
internal sealed class SeekingSubtitleSource : ISubtitleSource
{
    private static readonly long Behind = TimeSpan.FromSeconds(10).Ticks;
    private static readonly long Ahead = TimeSpan.FromSeconds(60).Ticks;
    private static readonly long JumpTicks = TimeSpan.FromSeconds(5).Ticks;

    private readonly ConcurrentQueue<SubtitlePacket> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly IDemuxer _reader;
    private readonly int _track;
    private readonly HashSet<(long, int)> _seen = [];
    private readonly Thread _thread;
    private long _focus = long.MinValue;
    private long _readFrom = long.MaxValue;
    private long _readTo = long.MinValue;
    private volatile bool _stop;
    private bool _reportedFailure;

    public int Packets { get; private set; }

    public event Action<string>? Failed;

    /// <param name="reader">A demuxer for this source alone, which it disposes.</param>
    public SeekingSubtitleSource(IDemuxer reader, int track)
    {
        _reader = reader;
        _track = track;
        _reader.Expose(track, active: true);
        _thread = new Thread(Load) { IsBackground = true, Name = $"Subtitle track {track} reader" };
        _thread.Start();
    }

    public bool TryTake(out SubtitlePacket packet) => _queue.TryDequeue(out packet!);

    public void Focus(long ticks)
    {
        long previous = Interlocked.Exchange(ref _focus, ticks);
        if (previous == long.MinValue || Math.Abs(ticks - previous) > JumpTicks)
        {
            _wake.Set();
        }
    }

    private void Load()
    {
        while (!_stop)
        {
            long focus = Interlocked.Read(ref _focus);
            if (focus == long.MinValue)
            {
                _wake.WaitOne(250);
                continue;
            }

            try
            {
                // Read ahead stops at the first line starting past the window, so _readTo (the
                // latest start read, or the last jump's focus) stays ahead of the focus during playback.
                bool atEnd = _readTo == long.MaxValue;
                if (focus < _readFrom || !atEnd && focus > _readTo + JumpTicks)
                {
                    _readFrom = focus - Behind;
                    _readTo = focus;
                    _reader.Seek(TimeSpan.FromTicks(Math.Max(_readFrom, 0)), _track);
                    continue;
                }

                if (_readTo < focus + Ahead)
                {
                    if (_reader.Next(_track) is { } packet)
                    {
                        if (_seen.Add((packet.TimeTicks, packet.Data.Length)))
                        {
                            _queue.Enqueue(new SubtitlePacket(packet.TimeTicks, packet.DurationTicks, packet.Data));
                            Packets++;
                        }

                        _readTo = Math.Max(_readTo, packet.TimeTicks);
                    }
                    else
                    {
                        _readTo = long.MaxValue;
                    }

                    continue;
                }

                _wake.WaitOne(250);
            }
            catch (Exception ex) when (!_stop)
            {
                if (!_reportedFailure)
                {
                    _reportedFailure = true;
                    Failed?.Invoke($"reading subtitle track {_track}: {ex.Message}");
                }

                _wake.WaitOne(1000);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _reader.Dispose();
        _wake.Dispose();
    }
}

/// <summary>
/// Packets picked up as the playback demuxer passes them, for a track the container doesn't index
/// (MPEG-TS, or an MKV without per-block cues). It misses lines that started before a seek's
/// keyframe or before a track switch.
/// </summary>
internal sealed class TappedSubtitleSource : ISubtitleSource
{
    private readonly ConcurrentQueue<SubtitlePacket> _queue = new();
    private readonly HashSet<(long, int, int)> _seen = [];
    private readonly IDemuxer _demuxer;
    private readonly int _track;

    public int Packets { get; private set; }

    public TappedSubtitleSource(IDemuxer playbackDemuxer, int track)
    {
        _demuxer = playbackDemuxer;
        _track = track;
        _demuxer.Tap(track, OnTapped);
    }

    public bool TryTake(out SubtitlePacket packet) => _queue.TryDequeue(out packet!);

    public void Focus(long ticks)
    {
    }

    private void OnTapped(MediaPacket packet)
    {
        int hash = 0;
        foreach (byte b in packet.Data.AsSpan(0, Math.Min(packet.Data.Length, 64)))
        {
            hash = hash * 31 + b;
        }

        if (_seen.Add((packet.TimeTicks, packet.Data.Length, hash)))
        {
            _queue.Enqueue(new SubtitlePacket(packet.TimeTicks, packet.DurationTicks, packet.Data));
            Packets++;
        }
    }

    public void Dispose() => _demuxer.Tap(_track, null);
}

/// <summary>A subtitle file loaded whole (sidecar, Plex external stream or one the user picked).</summary>
internal sealed class FileSubtitleSource(IEnumerable<SubtitlePacket> packets) : ISubtitleSource
{
    private readonly ConcurrentQueue<SubtitlePacket> _queue = new(packets);

    public bool TryTake(out SubtitlePacket packet) => _queue.TryDequeue(out packet!);

    public void Focus(long ticks)
    {
    }

    public void Dispose()
    {
    }
}
