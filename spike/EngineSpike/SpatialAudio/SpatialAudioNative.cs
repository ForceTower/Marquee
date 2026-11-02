using System.Runtime.InteropServices;

namespace EngineSpike.SpatialAudio;

/// <summary>Static spatial audio channel types (spatialaudioclient.h AudioObjectType).</summary>
[Flags]
internal enum AudioObjectType : uint
{
    None = 0,
    Dynamic = 1 << 0,
    FrontLeft = 1 << 1,
    FrontRight = 1 << 2,
    FrontCenter = 1 << 3,
    LowFrequency = 1 << 4,
    SideLeft = 1 << 5,
    SideRight = 1 << 6,
    BackLeft = 1 << 7,
    BackRight = 1 << 8,
}

/// <summary>
/// Raw vtable calls into MMDevice and ISpatialAudioClient (slot numbers from mmdeviceapi.h and
/// spatialaudioclient.h). Must be used from an MTA thread.
/// </summary>
internal static unsafe class SpatialAudioNative
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_ISpatialAudioClient = new("BBF8E066-AAAA-49BE-9A4D-FD2A858EA27F");
    private static readonly Guid IID_ISpatialAudioObjectRenderStream = new("BAB5F473-B423-477B-85F5-B5A332A04153");
    private const uint CLSCTX_ALL = 0x17;
    private const uint AudioCategory_Movie = 10;
    private const ushort VT_BLOB = 65;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ActivationParams
    {
        public IntPtr ObjectFormat;
        public uint StaticObjectTypeMask;
        public uint MinDynamicObjectCount;
        public uint MaxDynamicObjectCount;
        public uint Category;
        public IntPtr EventHandle;
        public IntPtr NotifyObject;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    public sealed record StreamInfo(IntPtr Client, IntPtr Stream, string DeviceId, int SampleRate, int BitsPerSample,
        uint MaxDynamicObjects, uint FramesPerBuffer);

    /// <summary>Activates a spatial object render stream on the default render endpoint.</summary>
    public static StreamInfo OpenDefaultEndpoint(AudioObjectType staticMask, uint dynamicObjects, IntPtr eventHandle)
    {
        Guid clsid = CLSID_MMDeviceEnumerator;
        Guid iid = IID_IMMDeviceEnumerator;
        Check(CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iid, out IntPtr enumerator), "CoCreateInstance(MMDeviceEnumerator)");
        IntPtr device = IntPtr.Zero, client = IntPtr.Zero, formats = IntPtr.Zero;
        try
        {
            // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eMultimedia)
            IntPtr endpoint;
            Check(((delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)Vtbl(enumerator)[4])(enumerator, 0, 1, &endpoint), "GetDefaultAudioEndpoint");
            device = endpoint;
            IntPtr idPtr;
            Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Vtbl(device)[5])(device, &idPtr), "IMMDevice::GetId");
            string deviceId = Marshal.PtrToStringUni(idPtr) ?? "?";
            Marshal.FreeCoTaskMem(idPtr);

            Guid clientIid = IID_ISpatialAudioClient;
            Check(((delegate* unmanaged<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)Vtbl(device)[3])(device, &clientIid, CLSCTX_ALL, IntPtr.Zero, &client),
                "IMMDevice::Activate(ISpatialAudioClient)");

            uint maxDynamic;
            Check(((delegate* unmanaged<IntPtr, uint*, int>)Vtbl(client)[5])(client, &maxDynamic), "GetMaxDynamicObjectCount");
            Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Vtbl(client)[6])(client, &formats), "GetSupportedAudioObjectFormatEnumerator");
            IntPtr format;
            Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Vtbl(formats)[4])(formats, 0, &format), "IAudioFormatEnumerator::GetFormat");
            int sampleRate = Marshal.ReadInt32(format, 4);
            int bits = Marshal.ReadInt16(format, 14);

            var parameters = new ActivationParams
            {
                ObjectFormat = format,
                StaticObjectTypeMask = (uint)staticMask,
                MinDynamicObjectCount = 0,
                MaxDynamicObjectCount = Math.Min(dynamicObjects, maxDynamic),
                Category = AudioCategory_Movie,
                EventHandle = eventHandle,
                NotifyObject = IntPtr.Zero,
            };
            var blob = new PropVariantBlob { Vt = VT_BLOB, Size = (uint)sizeof(ActivationParams), Data = (IntPtr)(&parameters) };
            Guid streamIid = IID_ISpatialAudioObjectRenderStream;
            IntPtr stream;
            Check(((delegate* unmanaged<IntPtr, PropVariantBlob*, Guid*, IntPtr*, int>)Vtbl(client)[10])(client, &blob, &streamIid, &stream),
                "ActivateSpatialAudioStream");

            uint frames;
            Check(((delegate* unmanaged<IntPtr, IntPtr, uint*, int>)Vtbl(client)[7])(client, format, &frames), "GetMaxFrameCount");
            return new StreamInfo(client, stream, deviceId, sampleRate, bits, maxDynamic, frames);
        }
        catch
        {
            Release(client);
            throw;
        }
        finally
        {
            Release(formats);
            Release(device);
            Release(enumerator);
        }
    }

    // ISpatialAudioObjectRenderStreamBase: 5 Start, 6 Stop, 8 BeginUpdatingAudioObjects, 9 EndUpdatingAudioObjects.
    // ISpatialAudioObjectRenderStream: 10 ActivateSpatialAudioObject.
    public static void Start(IntPtr stream) => Check(((delegate* unmanaged<IntPtr, int>)Vtbl(stream)[5])(stream), "Start");

    public static void Stop(IntPtr stream) => ((delegate* unmanaged<IntPtr, int>)Vtbl(stream)[6])(stream);

    public static int BeginUpdating(IntPtr stream, out uint availableDynamic, out uint frames)
    {
        uint available, count;
        int hr = ((delegate* unmanaged<IntPtr, uint*, uint*, int>)Vtbl(stream)[8])(stream, &available, &count);
        availableDynamic = available;
        frames = count;
        return hr;
    }

    public static int EndUpdating(IntPtr stream) => ((delegate* unmanaged<IntPtr, int>)Vtbl(stream)[9])(stream);

    public static IntPtr ActivateObject(IntPtr stream, AudioObjectType type)
    {
        IntPtr obj;
        Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Vtbl(stream)[10])(stream, (uint)type, &obj), $"ActivateSpatialAudioObject({type})");
        return obj;
    }

    // ISpatialAudioObjectBase: 3 GetBuffer. ISpatialAudioObject: 7 SetPosition, 8 SetVolume.
    public static float* GetBuffer(IntPtr obj, out uint bytes)
    {
        byte* buffer;
        uint length;
        Check(((delegate* unmanaged<IntPtr, byte**, uint*, int>)Vtbl(obj)[3])(obj, &buffer, &length), "GetBuffer");
        bytes = length;
        return (float*)buffer;
    }

    public static void SetPosition(IntPtr obj, float x, float y, float z) =>
        ((delegate* unmanaged<IntPtr, float, float, float, int>)Vtbl(obj)[7])(obj, x, y, z);

    public static void SetVolume(IntPtr obj, float volume) =>
        ((delegate* unmanaged<IntPtr, float, int>)Vtbl(obj)[8])(obj, volume);

    public static void Release(IntPtr com)
    {
        if (com != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, uint>)Vtbl(com)[2])(com);
        }
    }

    private static void** Vtbl(IntPtr com) => *(void***)com;

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, out IntPtr result);
}
