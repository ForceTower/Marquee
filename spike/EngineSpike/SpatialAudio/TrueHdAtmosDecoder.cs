using System.Runtime.InteropServices;
using System.Text;

namespace EngineSpike.SpatialAudio;

/// <summary>
/// TrueHD presentation 3 (Atmos) decoding through native/truehd-ffi, a C ABI over the truehd crate.
/// Bed channels come first (by label), then objects; positions arrive as metadata events.
/// </summary>
internal sealed unsafe class TrueHdAtmosDecoder : IAtmosDecoder
{
    private const string Dll = "marquee_truehd.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct MqEvent
    {
        public uint Element;
        public uint SampleOffset;
        public byte Active;
        public byte HasPosition;
        public ushort Reserved;
        public float X;
        public float Y;
        public float Z;
        public float GainDb;
        public uint Ramp;
    }

    private IntPtr _handle;
    private AtmosElement[]? _layout;
    private int _layoutChannels = -1;
    private int _layoutBeds = -1;
    private MqEvent[] _events = new MqEvent[64];

    public int Panics { get; private set; }

    public TrueHdAtmosDecoder()
    {
        _handle = mq_thd_create();
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("mq_thd_create failed");
        }
    }

    public AtmosChunk? Decode(ReadOnlySpan<byte> accessUnit)
    {
        int frames;
        fixed (byte* data = accessUnit)
        {
            frames = mq_thd_decode(_handle, data, (nuint)accessUnit.Length);
        }

        if (frames < 0)
        {
            Panics++;
            return null;
        }

        if (frames == 0)
        {
            return null;
        }

        int channels = (int)mq_thd_channels(_handle);
        int beds = (int)mq_thd_bed_count(_handle);
        if (_layout is null || channels != _layoutChannels || beds != _layoutBeds)
        {
            _layout = Enumerable.Range(0, channels)
                .Select(i => new AtmosElement(i, i < beds ? BedLabel(i) : null))
                .ToArray();
            _layoutChannels = channels;
            _layoutBeds = beds;
        }

        var pcm = new float[frames * channels];
        fixed (float* output = pcm)
        {
            mq_thd_copy_pcm(_handle, output, (nuint)pcm.Length);
        }

        var chunk = new AtmosChunk { Elements = _layout, Frames = frames, Pcm = pcm };
        uint count = mq_thd_event_count(_handle);
        if (count > _events.Length)
        {
            _events = new MqEvent[count];
        }

        fixed (MqEvent* events = _events)
        {
            count = mq_thd_copy_events(_handle, events, (uint)_events.Length);
        }

        for (int i = 0; i < count; i++)
        {
            var e = _events[i];
            chunk.Events.Add(new AtmosEvent(
                (int)e.Element,
                (int)e.SampleOffset,
                e.Active != 0,
                e.HasPosition != 0 ? (e.X, e.Y, e.Z) : null,
                float.IsNegativeInfinity(e.GainDb) ? 0 : MathF.Pow(10, e.GainDb / 20),
                (int)e.Ramp));
        }

        return chunk;
    }

    public void Reset() => mq_thd_reset(_handle);

    private string BedLabel(int index)
    {
        byte* buffer = stackalloc byte[16];
        uint length = mq_thd_bed_label(_handle, (uint)index, buffer, 16);
        return Encoding.ASCII.GetString(buffer, (int)length);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            mq_thd_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport(Dll)] private static extern IntPtr mq_thd_create();
    [DllImport(Dll)] private static extern void mq_thd_destroy(IntPtr decoder);
    [DllImport(Dll)] private static extern void mq_thd_reset(IntPtr decoder);
    [DllImport(Dll)] private static extern int mq_thd_decode(IntPtr decoder, byte* data, nuint length);
    [DllImport(Dll)] private static extern uint mq_thd_channels(IntPtr decoder);
    [DllImport(Dll)] private static extern uint mq_thd_bed_count(IntPtr decoder);
    [DllImport(Dll)] private static extern uint mq_thd_bed_label(IntPtr decoder, uint index, byte* buffer, uint capacity);
    [DllImport(Dll)] private static extern nuint mq_thd_copy_pcm(IntPtr decoder, float* output, nuint capacity);
    [DllImport(Dll)] private static extern uint mq_thd_event_count(IntPtr decoder);
    [DllImport(Dll)] private static extern uint mq_thd_copy_events(IntPtr decoder, MqEvent* output, uint capacity);
}
