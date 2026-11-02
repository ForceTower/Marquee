using System.Runtime.InteropServices;

namespace EngineSpike.SpatialAudio;

/// <summary>
/// Plays a decoded Atmos presentation (objects + positions) and its 7.1 downmix through one Windows
/// Spatial Sound stream, sample-locked, so the two can be A/B'd instantly. Atmos objects become
/// dynamic spatial objects; the 7.1 bed uses the static channel objects. The Atmos LFE shares the
/// static LFE object with the bed.
/// </summary>
internal sealed class AtmosLabRenderer : IDisposable
{
    // TrueHD 7.1 channel order: L, R, C, LFE, Ls, Rs, Lb, Rb.
    public static readonly AudioObjectType[] BedTypes =
    [
        AudioObjectType.FrontLeft, AudioObjectType.FrontRight, AudioObjectType.FrontCenter, AudioObjectType.LowFrequency,
        AudioObjectType.SideLeft, AudioObjectType.SideRight, AudioObjectType.BackLeft, AudioObjectType.BackRight,
    ];

    public static readonly (float X, float Y, float Z)[] BedPositions =
    [
        (-1, 1, 0), (1, 1, 0), (0, 1, 0), (0, 0.6f, 0), (-1, 0, 0), (1, 0, 0), (-1, -1, 0), (1, -1, 0),
    ];

    private static readonly Dictionary<string, AudioObjectType> AtmosBedChannels = new()
    {
        ["L"] = AudioObjectType.FrontLeft,
        ["R"] = AudioObjectType.FrontRight,
        ["C"] = AudioObjectType.FrontCenter,
        ["LFE"] = AudioObjectType.LowFrequency,
        ["Ls"] = AudioObjectType.SideLeft,
        ["Lss"] = AudioObjectType.SideLeft,
        ["Rs"] = AudioObjectType.SideRight,
        ["Rss"] = AudioObjectType.SideRight,
        ["Lrs"] = AudioObjectType.BackLeft,
        ["Rrs"] = AudioObjectType.BackRight,
    };

    public sealed class ElementState(DamfElement element, AudioObjectType staticType)
    {
        public DamfElement Element { get; } = element;
        public AudioObjectType StaticType { get; } = staticType;
        public IntPtr Object;
        public int EventIndex;
        public (float X, float Y, float Z) From = (0, 1, 0);
        public (float X, float Y, float Z) To = (0, 1, 0);
        public (float X, float Y, float Z) Current = (0, 1, 0);
        public long RampStart;
        public long RampLength;
        public float Gain = 1;
        public bool Active = true;
        public volatile float LevelDb = -100;
    }

    private readonly DamfPresentation _atmos;
    private readonly CafAudio _bed;
    private readonly Thread _thread;
    private readonly long _totalFrames;
    private volatile bool _stop;
    private long _position;
    private long _seekTo = -1;
    private float _mix = 1;

    public ElementState[] Elements { get; }
    public float[] BedLevelsDb { get; } = Enumerable.Repeat(-100f, 8).ToArray();
    public volatile bool Playing = true;
    public volatile bool AtmosMode = true;
    public volatile bool TestMode;
    public (float X, float Y, float Z) TestPosition { get; private set; }
    public string TestPhase { get; private set; } = "";
    private long _testFrame;
    private uint _noiseState = 0x12345678;
    public long Position => Interlocked.Read(ref _position);
    public long TotalFrames => _totalFrames;
    public int SampleRate => _atmos.Audio.SampleRate;
    public string Info { get; private set; } = "starting…";

    public event Action<string>? Failed;

    public AtmosLabRenderer(DamfPresentation atmos, CafAudio bed)
    {
        _atmos = atmos;
        _bed = bed;
        _totalFrames = Math.Min(atmos.Audio.Frames, bed.Frames);
        Elements = atmos.Elements
            .Select(e => new ElementState(e, e.BedChannel is { } channel && AtmosBedChannels.TryGetValue(channel, out var type) ? type : AudioObjectType.None))
            .ToArray();
        _thread = new Thread(Run) { IsBackground = true, Name = "Atmos lab render", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Seek(TimeSpan position) =>
        Interlocked.Exchange(ref _seekTo, Math.Clamp((long)(position.TotalSeconds * SampleRate), 0, _totalFrames - 1));

    private void Run()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        var objects = new List<IntPtr>();
        SpatialAudioNative.StreamInfo? stream = null;
        try
        {
            using var wake = new AutoResetEvent(false);
            var dynamicElements = Elements.Where(e => e.StaticType == AudioObjectType.None).ToArray();
            AudioObjectType mask = BedTypes.Aggregate(AudioObjectType.None, (m, t) => m | t);
            stream = SpatialAudioNative.OpenDefaultEndpoint(mask, (uint)dynamicElements.Length, wake.SafeWaitHandle.DangerousGetHandle());
            Info = $"{stream.DeviceId} · objects {stream.SampleRate} Hz {stream.BitsPerSample}-bit · " +
                   $"{stream.FramesPerBuffer} frames/buffer · up to {stream.MaxDynamicObjects} dynamic objects";
            if (stream.SampleRate != SampleRate)
            {
                throw new NotSupportedException($"Spatial objects run at {stream.SampleRate} Hz but the audio is {SampleRate} Hz");
            }

            var bedObjects = BedTypes.Select(type => SpatialAudioNative.ActivateObject(stream.Stream, type)).ToArray();
            objects.AddRange(bedObjects);
            SpatialAudioNative.Start(stream.Stream);

            bool dynamicActivated = false;
            while (!_stop)
            {
                wake.WaitOne(200);
                if (SpatialAudioNative.BeginUpdating(stream.Stream, out uint available, out uint frames) < 0)
                {
                    continue;
                }

                if (!dynamicActivated)
                {
                    foreach (var element in dynamicElements.Take((int)available))
                    {
                        element.Object = SpatialAudioNative.ActivateObject(stream.Stream, AudioObjectType.Dynamic);
                        objects.Add(element.Object);
                    }

                    Info += $" · {Math.Min(available, (uint)dynamicElements.Length)}/{dynamicElements.Length} objects active";
                    dynamicActivated = true;
                }

                RenderBlock(bedObjects, (int)frames);
                SpatialAudioNative.EndUpdating(stream.Stream);
            }

            SpatialAudioNative.Stop(stream.Stream);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            objects.ForEach(SpatialAudioNative.Release);
            if (stream is not null)
            {
                SpatialAudioNative.Release(stream.Stream);
                SpatialAudioNative.Release(stream.Client);
            }

            CoUninitialize();
        }
    }

    /// <summary>
    /// The 20 s position test path, in Atmos room coordinates: two laps at ear level, a rise from
    /// the front to straight overhead and back, then alternating front / overhead.
    /// </summary>
    private static ((float X, float Y, float Z) Position, string Phase) TestPath(double t)
    {
        if (t < 8)
        {
            double azimuth = t / 4 * 2 * Math.PI;
            return (((float)Math.Sin(azimuth), (float)Math.Cos(azimuth), 0), "circling at ear level: front → right → back → left");
        }

        if (t < 14)
        {
            double elevation = Math.Sin((t - 8) / 6 * Math.PI) * Math.PI / 2;
            return ((0, (float)Math.Cos(elevation), (float)Math.Sin(elevation)), "rising from the front to straight overhead, and back");
        }

        bool overhead = (int)((t - 14) / 1.5) % 2 == 1;
        return (overhead ? (0, 0, 1) : (0, 1, 0), overhead ? "OVERHEAD" : "FRONT (ear level)");
    }

    /// <summary>Plays broadband noise bursts on one dynamic object along <see cref="TestPath"/>; everything else is silent.</summary>
    private unsafe void RenderTest(IntPtr[] bedObjects, int frames)
    {
        var carrier = Elements.FirstOrDefault(e => e.Object != IntPtr.Zero && e.StaticType == AudioObjectType.None);
        (TestPosition, TestPhase) = TestPath(_testFrame / (double)SampleRate % 20);

        foreach (IntPtr bed in bedObjects)
        {
            float* silent = SpatialAudioNative.GetBuffer(bed, out uint silentBytes);
            new Span<float>(silent, (int)Math.Min(frames, silentBytes / 4)).Clear();
        }

        foreach (var element in Elements.Where(e => e.Object != IntPtr.Zero))
        {
            float* buffer = SpatialAudioNative.GetBuffer(element.Object, out uint bytes);
            int count = Math.Min(frames, (int)(bytes / 4));
            if (element != carrier)
            {
                SpatialAudioNative.SetVolume(element.Object, 0);
                new Span<float>(buffer, count).Clear();
                continue;
            }

            var (x, y, z) = ToWindows(TestPosition);
            SpatialAudioNative.SetPosition(element.Object, x, y, z);
            SpatialAudioNative.SetVolume(element.Object, 1);
            for (int i = 0; i < count; i++)
            {
                bool burst = (_testFrame + i) % 9600 < 7200;
                _noiseState ^= _noiseState << 13;
                _noiseState ^= _noiseState >> 17;
                _noiseState ^= _noiseState << 5;
                buffer[i] = burst ? (_noiseState / (float)uint.MaxValue - 0.5f) * 0.25f : 0;
            }
        }

        _testFrame += frames;
    }

    private unsafe void RenderBlock(IntPtr[] bedObjects, int frames)
    {
        if (TestMode)
        {
            RenderTest(bedObjects, frames);
            return;
        }

        long seek = Interlocked.Exchange(ref _seekTo, -1);
        if (seek >= 0)
        {
            Interlocked.Exchange(ref _position, seek);
            ResetStates(seek);
        }

        bool playing = Playing;
        float fromMix = _mix;
        float toMix = AtmosMode ? 1 : 0;
        _mix = toMix;
        long start = Position;

        foreach (var element in Elements.Where(e => e.Object != IntPtr.Zero))
        {
            Advance(element, start + frames / 2);
            var (x, y, z) = ToWindows(element.Current);
            SpatialAudioNative.SetPosition(element.Object, x, y, z);
            SpatialAudioNative.SetVolume(element.Object, element.Active ? element.Gain : 0);
        }

        for (int b = 0; b < BedTypes.Length; b++)
        {
            var atmosElement = Elements.FirstOrDefault(e => e.StaticType == BedTypes[b]);
            float* buffer = SpatialAudioNative.GetBuffer(bedObjects[b], out uint bytes);
            int count = Math.Min(frames, (int)(bytes / 4));
            double atmosSum = 0, bedSum = 0;
            for (int i = 0; i < count; i++)
            {
                long frame = start + i;
                bool inRange = playing && frame < _totalFrames;
                float mix = fromMix + (toMix - fromMix) * i / count;
                float bedSample = inRange ? _bed.Sample(frame, b) : 0;
                float atmosSample = inRange && atmosElement is not null ? _atmos.Audio.Sample(frame, atmosElement.Element.Channel) : 0;
                buffer[i] = atmosSample * mix + bedSample * (1 - mix);
                atmosSum += atmosSample * atmosSample;
                bedSum += bedSample * bedSample;
            }

            BedLevelsDb[b] = Db(bedSum, count);
            if (atmosElement is not null)
            {
                atmosElement.LevelDb = Db(atmosSum, count);
            }
        }

        foreach (var element in Elements.Where(e => e.Object != IntPtr.Zero))
        {
            float* buffer = SpatialAudioNative.GetBuffer(element.Object, out uint bytes);
            int count = Math.Min(frames, (int)(bytes / 4));
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                long frame = start + i;
                float sample = playing && frame < _totalFrames ? _atmos.Audio.Sample(frame, element.Element.Channel) : 0;
                sum += sample * sample;
                buffer[i] = sample * (fromMix + (toMix - fromMix) * i / count);
            }

            element.LevelDb = Db(sum, count);
        }

        if (playing)
        {
            long next = start + frames;
            if (next >= _totalFrames)
            {
                next = 0;
                ResetStates(0);
            }

            Interlocked.Exchange(ref _position, next);
        }
    }

    private void Advance(ElementState state, long sample)
    {
        if (!_atmos.Events.TryGetValue(state.Element.Id, out var events))
        {
            return;
        }

        while (state.EventIndex < events.Count && events[state.EventIndex].SamplePos <= sample)
        {
            var e = events[state.EventIndex++];
            state.From = state.Current;
            state.To = e.Position ?? state.To;
            state.RampStart = e.SamplePos;
            state.RampLength = e.RampLength;
            state.Gain = float.IsNegativeInfinity(e.GainDb) ? 0 : MathF.Pow(10, e.GainDb / 20);
            state.Active = e.Active;
        }

        float t = state.RampLength > 0 ? Math.Clamp((sample - state.RampStart) / (float)state.RampLength, 0, 1) : 1;
        state.Current = (
            state.From.X + (state.To.X - state.From.X) * t,
            state.From.Y + (state.To.Y - state.From.Y) * t,
            state.From.Z + (state.To.Z - state.From.Z) * t);
    }

    private void ResetStates(long sample)
    {
        foreach (var state in Elements)
        {
            state.EventIndex = 0;
            state.RampLength = 0;
            Advance(state, sample);
            state.From = state.To;
            state.Current = state.To;
        }
    }

    /// <summary>
    /// Atmos room coordinates (x: left −1 … right 1, y: back −1 … front 1, z: floor 0 … ceiling 1)
    /// to a Windows spatial direction (x right, y up, −z front), normalized to 1 m.
    /// </summary>
    public static (float X, float Y, float Z) ToWindows((float X, float Y, float Z) p)
    {
        float x = p.X, y = p.Z, z = -p.Y;
        float length = MathF.Sqrt(x * x + y * y + z * z);
        return length < 0.01f ? (0, 0, -1) : (x / length, y / length, z / length);
    }

    private static float Db(double sum, int count) =>
        count == 0 || sum <= 0 ? -100 : (float)(10 * Math.Log10(sum / count));

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
