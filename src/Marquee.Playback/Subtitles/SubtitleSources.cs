using System.Collections.Concurrent;
using Marquee.Playback.Matroska;

namespace Marquee.Playback.Subtitles;

/// <summary>
/// A Matroska subtitle track read alongside playback. When the file's index has an entry for each
/// of the track's blocks (mkvmerge writes them, with each block's place in its cluster), a
/// background thread reads exactly the blocks around the playing time through a small-buffered
/// demuxer of its own, so a seek or a track switch shows what should already be on screen.
/// Otherwise packets are picked up as the playback demuxer passes them, which misses lines that
/// started before a seek's keyframe or before a track switch.
/// </summary>
internal sealed class MatroskaSubtitleSource : ISubtitleSource
{
    private static readonly long Behind = TimeSpan.FromSeconds(10).Ticks;
    private static readonly long Ahead = TimeSpan.FromSeconds(60).Ticks;
    private static readonly long JumpTicks = TimeSpan.FromSeconds(5).Ticks;

    private readonly ConcurrentQueue<SubtitlePacket> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly MatroskaDemuxer? _reader;
    private readonly MatroskaDemuxer? _tapped;
    private readonly int _track;
    private readonly CuePoint[] _cues = [];
    private readonly bool[] _loaded = [];
    private readonly long _lookBack;
    private readonly HashSet<(long, int, int)> _seen = [];
    private readonly Thread? _thread;
    private long _focus = long.MinValue;
    private volatile bool _stop;
    private bool _reportedFailure;

    public bool Indexed => _reader is not null;
    public int Packets { get; private set; }

    public event Action<string>? Failed;

    public MatroskaSubtitleSource(string location, int track, MatroskaDemuxer playbackDemuxer)
    {
        _track = track;
        var cues = playbackDemuxer.Cues.Where(c => c.Track == track).OrderBy(c => c.TimeTicks).ToArray();
        if (cues.Length > 0 && cues.Count(c => c.RelativePosition > 0) >= cues.Length * 9 / 10)
        {
            _cues = cues;
            _loaded = new bool[cues.Length];
            _lookBack = Math.Max(Behind, Math.Min(cues.Max(c => c.DurationTicks), TimeSpan.FromMinutes(5).Ticks));
            _reader = MatroskaDemuxer.Open(location, 16 * 1024);
            _thread = new Thread(Load) { IsBackground = true, Name = $"Subtitle track {track} reader" };
            _thread.Start();
        }
        else
        {
            _tapped = playbackDemuxer;
            _tapped.Tap(track, OnTapped);
        }
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

    private void OnTapped(MatroskaPacket packet)
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
                if (_reader!.ReadPacketAt(_cues[next]) is { } packet)
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
        _tapped?.Tap(_track, null);
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _reader?.Dispose();
        _wake.Dispose();
    }
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
