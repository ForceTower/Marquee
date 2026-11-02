using System.Runtime.InteropServices;

namespace EngineSpike.SpatialAudio;

/// <summary>Where the video is: media time and whether it's advancing.</summary>
internal readonly record struct VideoClock(long Ticks, bool Playing);

/// <summary>
/// Renders an <see cref="AtmosStream"/> through Windows Spatial Sound: bed channels with a static
/// Windows channel of their own use it, every other element is a dynamic object positioned from the
/// stream's metadata. Audio follows the video clock: drift is absorbed by resampling up to ±0.3%,
/// and anything over 40 ms (start, seek, stall) is a hard jump.
/// </summary>
internal sealed class SpatialAtmosOutput : IDisposable
{
    private const double MaxRateCorrection = 0.003;
    private const double CorrectionPerSecondOfError = 0.2;
    private const double HardResyncSeconds = 0.040;

    private sealed class State(AtmosElement element, AudioObjectType staticType)
    {
        public AtmosElement Element { get; } = element;
        public AudioObjectType StaticType { get; } = staticType;
        public IntPtr Object;
        public (float X, float Y, float Z) From = (0, 1, 0);
        public (float X, float Y, float Z) To = (0, 1, 0);
        public (float X, float Y, float Z) Current = (0, 1, 0);
        public long RampStart;
        public long RampLength;
        public float Gain = 1;
        public bool Active = true;

        /// <summary>Objects stay silent until their first position arrives (up to 32 ms after a seek).</summary>
        public bool Positioned = element.BedChannel is not null;
    }

    private readonly AtmosStream _stream;
    private readonly Func<VideoClock> _clock;
    private readonly Thread _thread;
    private volatile bool _stop;
    private State[] _states = [];
    private float[][] _buffers = [];
    private double _read = double.NaN;
    private int _generation = -1;

    /// <summary>Estimated time from handing samples to Windows until they're heard.</summary>
    public int LatencyMs { get; set; } = 40;

    /// <summary>User lip-sync trim; positive delays the audio.</summary>
    public int OffsetMs { get; set; }

    /// <summary>Renders silence but keeps decoding and syncing (unattended tests).</summary>
    public bool Muted { get; set; }

    public double ReadPosition => _read;
    public double SyncErrorMs { get; private set; }
    public double Ratio { get; private set; } = 1;
    public int Resyncs { get; private set; }
    public int Underruns { get; private set; }
    public string Info { get; private set; } = "waiting for the first decoded audio";

    public event Action<string>? Failed;
    public event Action<string>? Trace;

    private bool _startPending = true;
    private long _startStamp;

    public SpatialAtmosOutput(AtmosStream stream, Func<VideoClock> clock)
    {
        _stream = stream;
        _clock = clock;
        _thread = new Thread(Run) { IsBackground = true, Name = "Atmos output", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        var objects = new List<IntPtr>();
        SpatialAudioNative.StreamInfo? stream = null;
        try
        {
            while (_stream.Layout is null)
            {
                if (_stop)
                {
                    return;
                }

                Thread.Sleep(10);
            }

            var used = new HashSet<AudioObjectType>();
            _states = _stream.Layout
                .Select(e =>
                {
                    var type = AtmosMapping.StaticTypeFor(e.BedChannel);
                    return new State(e, type != AudioObjectType.None && used.Add(type) ? type : AudioObjectType.None);
                })
                .ToArray();
            foreach (var state in _states.Where(s => s.StaticType == AudioObjectType.None && s.Element.BedChannel is not null))
            {
                state.To = state.Current = AtmosMapping.BedPositions.GetValueOrDefault(state.Element.BedChannel!, (0, 1, 0));
            }

            _buffers = _states.Select(_ => new float[4800]).ToArray();
            var mask = used.Aggregate(AudioObjectType.None, (m, t) => m | t);
            int dynamicCount = _states.Count(s => s.StaticType == AudioObjectType.None);

            using var wake = new AutoResetEvent(false);
            stream = SpatialAudioNative.OpenDefaultEndpoint(mask, (uint)dynamicCount, wake.SafeWaitHandle.DangerousGetHandle());
            foreach (var state in _states.Where(s => s.StaticType != AudioObjectType.None))
            {
                state.Object = SpatialAudioNative.ActivateObject(stream.Stream, state.StaticType);
                objects.Add(state.Object);
            }

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
                    foreach (var state in _states.Where(s => s.StaticType == AudioObjectType.None).Take((int)available))
                    {
                        state.Object = SpatialAudioNative.ActivateObject(stream.Stream, AudioObjectType.Dynamic);
                        objects.Add(state.Object);
                    }

                    Info = $"{_states.Length} elements ({used.Count} static, {Math.Min(dynamicCount, (int)available)}/{dynamicCount} dynamic) · " +
                           $"{stream.FramesPerBuffer} frames/buffer";
                    dynamicActivated = true;
                }

                RenderPass((int)frames);
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

    private unsafe void RenderPass(int frames)
    {
        if (_buffers.Length > 0 && _buffers[0].Length < frames)
        {
            _buffers = _states.Select(_ => new float[frames]).ToArray();
        }

        if (_stream.Generation != _generation)
        {
            _generation = _stream.Generation;
            _read = double.NaN;
            _startPending = true;
            _startStamp = 0;
            foreach (var state in _states.Where(s => s.Element.BedChannel is null))
            {
                state.Positioned = false;
            }
        }

        var clock = _clock();
        if (_startPending && clock.Playing && _startStamp == 0)
        {
            _startStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        double target = _stream.HasBase
            ? _stream.SampleAt(clock.Ticks + (LatencyMs + OffsetMs) * TimeSpan.TicksPerMillisecond)
            : double.NaN;
        if (!clock.Playing || double.IsNaN(target))
        {
            WriteSilence(frames);
            return;
        }

        double ratio = 1;
        double error = double.IsNaN(_read) ? double.PositiveInfinity : _read - target;
        if (Math.Abs(error) > HardResyncSeconds * AtmosStream.SampleRate)
        {
            _read = target;
            Resyncs++;
            error = 0;
        }
        else
        {
            ratio = 1 - Math.Clamp(error / AtmosStream.SampleRate * CorrectionPerSecondOfError, -MaxRateCorrection, MaxRateCorrection);
        }

        SyncErrorMs = error * 1000.0 / AtmosStream.SampleRate;
        Ratio = ratio;

        long middle = (long)(_read + frames * ratio / 2);
        for (int e = 0; e < _states.Length; e++)
        {
            Advance(e, middle);
        }

        double consumed = _stream.Read(_read, frames, ratio, _buffers, out bool underrun);
        if (underrun)
        {
            Underruns++;
            if (_startPending)
            {
                Trace?.Invoke($"starved at start: read {_read / AtmosStream.SampleRate:0.000} s, target {target / AtmosStream.SampleRate:0.000} s, " +
                              $"decoded up to {_stream.Written / (double)AtmosStream.SampleRate:0.000} s (from the first decoded sample), video {TimeSpan.FromTicks(clock.Ticks):hh\\:mm\\:ss\\.fff}");
            }
        }
        else if (_startPending && _startStamp != 0 && _read >= 0)
        {
            _startPending = false;
            Trace?.Invoke($"audio audible {System.Diagnostics.Stopwatch.GetElapsedTime(_startStamp).TotalMilliseconds:0} ms after the video started playing");
        }

        for (int e = 0; e < _states.Length; e++)
        {
            var state = _states[e];
            if (state.Object == IntPtr.Zero)
            {
                continue;
            }

            if (state.StaticType == AudioObjectType.None)
            {
                var (x, y, z) = AtmosMapping.ToWindows(state.Current);
                SpatialAudioNative.SetPosition(state.Object, x, y, z);
            }

            SpatialAudioNative.SetVolume(state.Object, !Muted && state.Active && state.Positioned ? state.Gain : 0);
            float* buffer = SpatialAudioNative.GetBuffer(state.Object, out uint bytes);
            _buffers[e].AsSpan(0, Math.Min(frames, (int)(bytes / 4))).CopyTo(new Span<float>(buffer, (int)(bytes / 4)));
        }

        _read += consumed;
    }

    private void Advance(int element, long sample)
    {
        var state = _states[element];
        foreach (var (start, ev) in _stream.TakeEvents(element, sample))
        {
            if (ev.Position is not null && !state.Positioned)
            {
                // First position after a (re)start: jump there instead of ramping from the default.
                state.Current = ev.Position.Value;
                state.Positioned = true;
            }

            state.From = state.Current;
            state.To = ev.Position ?? state.To;
            state.RampStart = start;
            state.RampLength = ev.RampLength;
            state.Gain = ev.Gain;
            state.Active = ev.Active;
        }

        float t = state.RampLength > 0 ? Math.Clamp((sample - state.RampStart) / (float)state.RampLength, 0, 1) : 1;
        state.Current = (
            state.From.X + (state.To.X - state.From.X) * t,
            state.From.Y + (state.To.Y - state.From.Y) * t,
            state.From.Z + (state.To.Z - state.From.Z) * t);
    }

    private unsafe void WriteSilence(int frames)
    {
        foreach (var state in _states.Where(s => s.Object != IntPtr.Zero))
        {
            float* buffer = SpatialAudioNative.GetBuffer(state.Object, out uint bytes);
            new Span<float>(buffer, (int)Math.Min(frames, bytes / 4)).Clear();
        }
    }

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
