namespace EngineSpike.SpatialAudio;

/// <summary>A bed channel (Name set, e.g. "LFE") or an object (Name null) of an Atmos presentation.</summary>
internal sealed record AtmosElement(int Id, string? BedChannel);

/// <summary>
/// A metadata update for one element: position (room coordinates), linear gain and active flag,
/// reached over <see cref="RampLength"/> samples starting <see cref="SampleOffset"/> samples into
/// the chunk it arrived with.
/// </summary>
internal readonly record struct AtmosEvent(int Element, int SampleOffset, bool Active, (float X, float Y, float Z)? Position, float Gain, int RampLength);

/// <summary>Decoded output of one or more access units: planar float PCM per element plus metadata events.</summary>
internal sealed class AtmosChunk
{
    public required AtmosElement[] Elements { get; init; }
    public required int Frames { get; init; }

    /// <summary>Element-major: element e's samples are Pcm[e * Frames .. (e + 1) * Frames).</summary>
    public required float[] Pcm { get; init; }

    public List<AtmosEvent> Events { get; } = [];
}

/// <summary>
/// Ring buffer between the decode thread and the render thread. Samples are addressed by an
/// absolute index counted from the last reset, which maps to media time through the time of the
/// first chunk written after that reset.
/// </summary>
internal sealed class AtmosStream
{
    public const int SampleRate = 48000;
    private const int Capacity = SampleRate * 4;

    private readonly object _gate = new();
    private float[][] _pcm = [];
    private List<(long Sample, AtmosEvent Event)>[] _events = [];
    private long _written;
    private long _baseTicks = -1;

    public AtmosElement[]? Layout { get; private set; }
    public long Written { get { lock (_gate) { return _written; } } }
    public bool HasBase { get { lock (_gate) { return _baseTicks >= 0; } } }
    public int Generation { get; private set; }

    public void Reset()
    {
        lock (_gate)
        {
            _written = 0;
            _baseTicks = -1;
            foreach (var list in _events)
            {
                list.Clear();
            }

            Generation++;
        }
    }

    /// <summary>Appends a chunk; the first chunk after a reset anchors the timeline at <paramref name="startTicks"/>.</summary>
    public void Write(AtmosChunk chunk, long startTicks)
    {
        lock (_gate)
        {
            if (Layout is null || Layout.Length != chunk.Elements.Length)
            {
                Layout = chunk.Elements;
                _pcm = chunk.Elements.Select(_ => new float[Capacity]).ToArray();
                _events = chunk.Elements.Select(_ => new List<(long, AtmosEvent)>()).ToList().ToArray();
            }

            if (_baseTicks < 0)
            {
                _baseTicks = startTicks;
            }

            for (int e = 0; e < chunk.Elements.Length; e++)
            {
                var source = chunk.Pcm.AsSpan(e * chunk.Frames, chunk.Frames);
                int at = (int)(_written % Capacity);
                int first = Math.Min(chunk.Frames, Capacity - at);
                source[..first].CopyTo(_pcm[e].AsSpan(at));
                source[first..].CopyTo(_pcm[e].AsSpan(0));
            }

            foreach (var ev in chunk.Events)
            {
                _events[ev.Element].Add((_written + ev.SampleOffset, ev));
            }

            _written += chunk.Frames;
        }
    }

    /// <summary>Absolute sample index of a media time on the current timeline (fractional).</summary>
    public double SampleAt(long ticks)
    {
        lock (_gate)
        {
            return _baseTicks < 0 ? double.NaN : (ticks - _baseTicks) * (double)SampleRate / TimeSpan.TicksPerSecond;
        }
    }

    public long TicksAt(double sample)
    {
        lock (_gate)
        {
            return _baseTicks + (long)(sample * TimeSpan.TicksPerSecond / SampleRate);
        }
    }

    /// <summary>
    /// Reads <paramref name="frames"/> output samples per element starting at absolute position
    /// <paramref name="position"/>, stepping by <paramref name="ratio"/> with linear interpolation.
    /// Returns how many input samples were consumed; positions not yet decoded read as silence.
    /// </summary>
    public double Read(double position, int frames, double ratio, float[][] output, out bool underrun)
    {
        lock (_gate)
        {
            underrun = false;
            long oldest = Math.Max(0, _written - Capacity + 1);
            for (int i = 0; i < frames; i++)
            {
                double p = position + i * ratio;
                long index = (long)Math.Floor(p);
                float t = (float)(p - index);
                bool available = index >= oldest && index + 1 < _written;
                underrun |= !available && index >= 0;
                for (int e = 0; e < output.Length && e < _pcm.Length; e++)
                {
                    output[e][i] = available
                        ? _pcm[e][index % Capacity] * (1 - t) + _pcm[e][(index + 1) % Capacity] * t
                        : 0;
                }
            }

            return frames * ratio;
        }
    }

    /// <summary>Removes and returns the element's events that start at or before <paramref name="sample"/>.</summary>
    public List<(long Sample, AtmosEvent Event)> TakeEvents(int element, long sample)
    {
        lock (_gate)
        {
            var due = new List<(long, AtmosEvent)>();
            if (element >= _events.Length)
            {
                return due;
            }

            var list = _events[element];
            int count = 0;
            while (count < list.Count && list[count].Sample <= sample)
            {
                due.Add(list[count++]);
            }

            list.RemoveRange(0, count);
            return due;
        }
    }
}
