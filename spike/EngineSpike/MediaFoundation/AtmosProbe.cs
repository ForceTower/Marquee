using System.Runtime.InteropServices;
using System.Text;

namespace EngineSpike.MediaFoundation;

/// <summary>
/// Answers "is this track really delivering Atmos objects?" without relying on ears:
/// 1. reads the E-AC-3 JOC flag from the container (dec3 box) when the source exposes it;
/// 2. lists what Windows' audio decoders can output for the stream;
/// 3. decodes a few seconds through the Dolby decoder as spatial objects and measures which
///    object channels actually carry signal.
/// COM is called through raw vtable slots to avoid declaring the IMFAttributes hierarchy.
/// </summary>
internal static unsafe class AtmosProbe
{
    private const uint MF_VERSION = 0x00020070;
    private const uint COINIT_MULTITHREADED = 0x0;
    private const int MF_E_INVALIDSTREAMNUMBER = unchecked((int)0xC00D36B3);
    private const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const uint MFT_ENUM_FLAG_ALL = 0x3F;
    private const uint MFT_ENUM_FLAG_UNTRUSTED_STOREMFT = 0x400;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    private const uint MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
    private const ushort VT_I8 = 20;
    private const double SilenceDb = -90;

    private static readonly Guid MFT_CATEGORY_AUDIO_DECODER = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
    private static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_Dolby_DDPlus = new("a7fb87af-2d02-42fb-a4d4-05cd93843bdd");
    private static readonly Guid MFAudioFormat_Float_SpatialObjects = new("fa39cd94-bc64-4ab1-9b71-dcd09d5a7e7a");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_MPEG4_SAMPLE_DESCRIPTION = new("261e9d83-9529-4b8f-a111-8b9c950a81a9");
    private static readonly Guid MF_MT_SPATIAL_AUDIO_MAX_DYNAMIC_OBJECTS = new("dcfba24a-2609-4240-a721-3faea76a4df9");
    private static readonly Guid MF_MT_SPATIAL_AUDIO_OBJECT_METADATA_FORMAT_ID = new("2ab71bc0-6223-4ba7-ad64-7b94b47ae792");
    private static readonly Guid MF_SD_LANGUAGE = new("00af2180-bdc2-423c-abca-f503593bc121");

    private static readonly Dictionary<Guid, string> SubtypeNames = new()
    {
        [new("00000001-0000-0010-8000-00aa00389b71")] = "PCM",
        [new("00000003-0000-0010-8000-00aa00389b71")] = "Float",
        [new("00000092-0000-0010-8000-00aa00389b71")] = "AC-3 S/PDIF",
        [new("0000000a-0cea-0010-8000-00aa00389b71")] = "E-AC-3 S/PDIF",
        [new("e06d802c-db46-11cf-b4d1-00805f6cbbea")] = "Dolby AC-3",
        [MFAudioFormat_Dolby_DDPlus] = "Dolby Digital Plus",
        [MFAudioFormat_Float_SpatialObjects] = "SpatialObjects",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterTypeInfo
    {
        public Guid Major;
        public Guid Sub;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputStreamInfo
    {
        public uint Flags;
        public uint Size;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public ulong UInt64;
        [FieldOffset(8)] public long Int64;
        [FieldOffset(8)] public double Double;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(16)] public IntPtr BlobData;
    }

    public static List<string> Run(string path, TimeSpan from, TimeSpan duration)
    {
        var lines = new List<string>();
        int coInit = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        int mfStart = MFStartup(MF_VERSION, 0);
        IntPtr reader = IntPtr.Zero;
        try
        {
            if (mfStart < 0)
            {
                lines.Add($"MFStartup failed 0x{mfStart:X8}");
                return lines;
            }

            int hr = MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader);
            if (hr < 0)
            {
                lines.Add($"MFCreateSourceReaderFromURL failed 0x{hr:X8}");
                return lines;
            }

            lines.Add($"decode window: {from:hh\\:mm\\:ss} + {duration.TotalSeconds:0} s");
            for (uint stream = 0; ; stream++)
            {
                IntPtr native;
                hr = ReaderGetNativeMediaType(reader, stream, 0, &native);
                if (hr == MF_E_INVALIDSTREAMNUMBER)
                {
                    break;
                }

                if (hr < 0)
                {
                    continue;
                }

                try
                {
                    if (GetGuid(native, MF_MT_MAJOR_TYPE) == MFMediaType_Audio)
                    {
                        ProbeStream(reader, stream, native, from, duration, lines);
                    }
                }
                finally
                {
                    Release(native);
                }
            }
        }
        finally
        {
            Release(reader);
            if (mfStart >= 0)
            {
                MFShutdown();
            }

            if (coInit >= 0)
            {
                CoUninitialize();
            }
        }

        return lines;
    }

    private static void ProbeStream(IntPtr reader, uint stream, IntPtr native, TimeSpan from, TimeSpan duration, List<string> lines)
    {
        Guid subtype = GetGuid(native, MF_MT_SUBTYPE) ?? Guid.Empty;
        string language = ReaderGetStreamString(reader, stream, MF_SD_LANGUAGE) ?? "?";
        uint bitrate = GetUInt32(native, MF_MT_AVG_BITRATE) ?? 0;
        lines.Add("");
        lines.Add($"stream {stream} [{language}] {SubtypeName(subtype)} {GetUInt32(native, MF_MT_AUDIO_NUM_CHANNELS)}ch {bitrate / 1000} kbps");

        if (subtype == MFAudioFormat_Dolby_DDPlus)
        {
            lines.Add("  container: " + DescribeJoc(GetBlob(native, MF_MT_MPEG4_SAMPLE_DESCRIPTION)));
        }

        var input = new RegisterTypeInfo { Major = MFMediaType_Audio, Sub = subtype };
        IntPtr* activates;
        uint count;
        int hr = MFTEnumEx(MFT_CATEGORY_AUDIO_DECODER, MFT_ENUM_FLAG_ALL | MFT_ENUM_FLAG_UNTRUSTED_STOREMFT, &input, null, &activates, &count);
        if (hr < 0)
        {
            hr = MFTEnumEx(MFT_CATEGORY_AUDIO_DECODER, MFT_ENUM_FLAG_ALL, &input, null, &activates, &count);
        }

        if (hr < 0)
        {
            lines.Add($"  MFTEnumEx failed 0x{hr:X8}");
            return;
        }

        if (count == 0)
        {
            lines.Add("  no Windows decoder for this format");
        }

        try
        {
            for (uint i = 0; i < count; i++)
            {
                ProbeDecoder(activates[i], reader, stream, native, from, duration, lines);
            }
        }
        finally
        {
            CoTaskMemFree((IntPtr)activates);
        }
    }

    private static void ProbeDecoder(IntPtr activate, IntPtr reader, uint stream, IntPtr native, TimeSpan from, TimeSpan duration, List<string> lines)
    {
        string name = GetString(activate, MFT_FRIENDLY_NAME_Attribute) ?? "(unnamed)";
        IntPtr mft = IntPtr.Zero;
        try
        {
            Guid iid = IID_IMFTransform;
            int hr = ActivateObject(activate, &iid, &mft);
            if (hr < 0)
            {
                lines.Add($"  {name}: ActivateObject failed 0x{hr:X8}");
                return;
            }

            hr = TransformSetInputType(mft, 0, native, 0);
            if (hr < 0)
            {
                lines.Add($"  {name}: rejects this stream (0x{hr:X8})");
                return;
            }

            var outputs = new List<string>();
            uint? maxObjects = null;
            for (uint t = 0; ; t++)
            {
                IntPtr type;
                if (TransformGetOutputAvailableType(mft, 0, t, &type) < 0)
                {
                    break;
                }

                Guid outSubtype = GetGuid(type, MF_MT_SUBTYPE) ?? Guid.Empty;
                outputs.Add(outSubtype == MFAudioFormat_Float_SpatialObjects
                    ? "SpatialObjects"
                    : $"{SubtypeName(outSubtype)} {GetUInt32(type, MF_MT_AUDIO_NUM_CHANNELS)}ch");
                if (outSubtype == MFAudioFormat_Float_SpatialObjects)
                {
                    maxObjects = GetUInt32(type, MF_MT_SPATIAL_AUDIO_MAX_DYNAMIC_OBJECTS);
                }

                Release(type);
            }

            string spatial = maxObjects is uint m ? $" (up to {m} dynamic objects)" : "";
            lines.Add($"  {name}: {string.Join(", ", outputs.Distinct())}{spatial}");
            if (maxObjects is not null)
            {
                DecodeAsObjects(reader, stream, mft, from, duration, lines);
            }
        }
        finally
        {
            if (mft != IntPtr.Zero)
            {
                ShutdownObject(activate);
                Release(mft);
            }

            Release(activate);
        }
    }

    private sealed class ChannelStats
    {
        public double[] Planar = [];
        public double[] Interleaved = [];
        public long Count;
    }

    private enum Verdict
    {
        Unknown,
        ObjectsPresent,
        NoObjects,
    }

    private sealed class DecodeStats
    {
        public Verdict Verdict;
        public int Samples;
        public int StreamChanges;
        public long Frames;
        public string? Shape;
        public List<string> SampleAttributes = [];
        public readonly Dictionary<int, ChannelStats> Buffers = new();
    }

    private static void DecodeAsObjects(IntPtr reader, uint stream, IntPtr mft, TimeSpan from, TimeSpan duration, List<string> lines)
    {
        Guid metadataFormat = Guid.Empty;
        IntPtr spatialType = FindSpatialType(mft, null);
        if (spatialType == IntPtr.Zero)
        {
            return;
        }

        try
        {
            metadataFormat = GetGuid(spatialType, MF_MT_SPATIAL_AUDIO_OBJECT_METADATA_FORMAT_ID) ?? Guid.Empty;
            int hr = TransformSetOutputType(mft, 0, spatialType, 0);
            if (hr < 0)
            {
                lines.Add($"    SetOutputType(SpatialObjects) failed 0x{hr:X8}");
                return;
            }
        }
        finally
        {
            Release(spatialType);
        }

        OutputStreamInfo info;
        TransformGetOutputStreamInfo(mft, 0, &info);
        bool mftAllocates = (info.Flags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;

        ReaderSetStreamSelection(reader, MF_SOURCE_READER_ALL_STREAMS, 0);
        ReaderSetStreamSelection(reader, stream, 1);
        var position = new PropVariant { Vt = VT_I8, Int64 = from.Ticks };
        Guid timeFormat = Guid.Empty;
        ReaderSetCurrentPosition(reader, &timeFormat, &position);
        TransformProcessMessage(mft, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        TransformProcessMessage(mft, MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

        var stats = new DecodeStats();
        long? firstTimestamp = null;
        string? failure = null;
        while (failure is null)
        {
            uint actual, flags;
            long timestamp;
            IntPtr sample;
            int hr = ReaderReadSample(reader, stream, 0, &actual, &flags, &timestamp, &sample);
            if (hr < 0)
            {
                failure = $"ReadSample failed 0x{hr:X8}";
                break;
            }

            if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                break;
            }

            if (sample == IntPtr.Zero)
            {
                continue;
            }

            firstTimestamp ??= timestamp;
            try
            {
                hr = TransformProcessInput(mft, 0, sample, 0);
                if (hr == MF_E_NOTACCEPTING)
                {
                    failure = Drain(mft, info, mftAllocates, metadataFormat, stats);
                    hr = TransformProcessInput(mft, 0, sample, 0);
                }

                if (hr < 0)
                {
                    failure ??= $"ProcessInput failed 0x{hr:X8}";
                }
            }
            finally
            {
                Release(sample);
            }

            failure ??= Drain(mft, info, mftAllocates, metadataFormat, stats);
            if (timestamp - firstTimestamp >= duration.Ticks)
            {
                break;
            }
        }

        Report(stats, metadataFormat, failure, lines);
    }

    private static string? Drain(IntPtr mft, OutputStreamInfo info, bool mftAllocates, Guid metadataFormat, DecodeStats stats)
    {
        while (true)
        {
            IntPtr provided = IntPtr.Zero;
            if (!mftAllocates)
            {
                IntPtr buffer;
                MFCreateSample(&provided);
                MFCreateMemoryBuffer(Math.Max(info.Size, 1u << 20), &buffer);
                SampleAddBuffer(provided, buffer);
                Release(buffer);
            }

            var output = new OutputDataBuffer { StreamId = 0, Sample = provided };
            uint status;
            int hr = TransformProcessOutput(mft, 0, 1, &output, &status);
            if (output.Events != IntPtr.Zero)
            {
                Release(output.Events);
            }

            try
            {
                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    return null;
                }

                // Only the audio renderer can allocate IMFSpatialAudioSample (there is no public factory),
                // so a decoder that is actually emitting objects rejects our plain sample with E_NOINTERFACE.
                if (hr == E_NOINTERFACE)
                {
                    stats.Verdict = Verdict.ObjectsPresent;
                    return "decoder tried to write spatial objects into a plain sample (E_NOINTERFACE)";
                }

                if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    stats.StreamChanges++;
                    IntPtr type = FindSpatialType(mft, metadataFormat);
                    if (type == IntPtr.Zero)
                    {
                        stats.Verdict = Verdict.NoObjects;
                        return "after reading the audio, the decoder withdrew object output";
                    }

                    hr = TransformSetOutputType(mft, 0, type, 0);
                    Release(type);
                    if (hr < 0)
                    {
                        return $"stream change: SetOutputType failed 0x{hr:X8}";
                    }

                    continue;
                }

                if (hr < 0)
                {
                    return $"ProcessOutput failed 0x{hr:X8}";
                }

                if (output.Sample != IntPtr.Zero)
                {
                    Measure(output.Sample, stats);
                }
            }
            finally
            {
                if (output.Sample != IntPtr.Zero)
                {
                    Release(output.Sample);
                }
                else if (provided != IntPtr.Zero)
                {
                    Release(provided);
                }
            }
        }
    }

    private static void Measure(IntPtr sample, DecodeStats stats)
    {
        long durationTicks;
        int frames = SampleGetSampleDuration(sample, &durationTicks) >= 0 && durationTicks > 0
            ? (int)Math.Round(durationTicks * 48000.0 / TimeSpan.TicksPerSecond)
            : 1536;
        uint bufferCount;
        SampleGetBufferCount(sample, &bufferCount);
        stats.Samples++;
        stats.Frames += frames;
        if (stats.Samples == 1)
        {
            stats.SampleAttributes = ExtraAttributes(sample);
        }

        var shape = new StringBuilder($"{bufferCount} buffer(s), {frames} frames:");
        for (int b = 0; b < bufferCount; b++)
        {
            IntPtr buffer;
            if (SampleGetBufferByIndex(sample, (uint)b, &buffer) < 0)
            {
                continue;
            }

            try
            {
                byte* data;
                uint max, length;
                if (BufferLock(buffer, &data, &max, &length) < 0)
                {
                    continue;
                }

                try
                {
                    int floats = (int)(length / 4);
                    int channels = frames > 0 && floats % frames == 0 ? floats / frames : 0;
                    shape.Append($" [{b}] {length} B" + (channels > 0 ? $" = {channels}ch" : " (not audio-shaped)"));
                    if (channels == 0)
                    {
                        continue;
                    }

                    if (!stats.Buffers.TryGetValue(b, out ChannelStats? channelStats) || channelStats.Planar.Length != channels)
                    {
                        channelStats = new ChannelStats { Planar = new double[channels], Interleaved = new double[channels] };
                        stats.Buffers[b] = channelStats;
                    }

                    // The object layout isn't documented, so measure it both ways. Read with the wrong
                    // layout, every "channel" mixes all of them and nothing looks silent; the right
                    // layout is the one where individual channels separate out.
                    float* values = (float*)data;
                    for (int i = 0; i < floats; i++)
                    {
                        float v = values[i];
                        if (float.IsFinite(v))
                        {
                            channelStats.Planar[i / frames] += v * v;
                            channelStats.Interleaved[i % channels] += v * v;
                        }
                    }

                    channelStats.Count += frames;
                }
                finally
                {
                    BufferUnlock(buffer);
                }
            }
            finally
            {
                Release(buffer);
            }
        }

        stats.Shape ??= shape.ToString();
    }

    private static void Report(DecodeStats stats, Guid metadataFormat, string? failure, List<string> lines)
    {
        lines.Add(stats.Verdict switch
        {
            Verdict.ObjectsPresent => "    ✔ ATMOS OBJECTS: the decoder kept object output after reading the audio",
            Verdict.NoObjects => "    ✘ NO OBJECTS: the decoder fell back to plain channels after reading the audio",
            _ => "    ? inconclusive",
        });
        if (failure is not null)
        {
            lines.Add($"      ({failure})");
        }

        if (stats.Samples == 0)
        {
            return;
        }

        lines.Add($"    decoded as objects (metadata {metadataFormat.ToString()[..8]}…): {stats.Samples} samples, " +
                  $"{stats.Frames / 48000.0:0.0} s, {stats.StreamChanges} format change(s)");

        if (stats.Shape is not null)
        {
            lines.Add($"    first sample: {stats.Shape}");
        }

        foreach (string attribute in stats.SampleAttributes)
        {
            lines.Add($"    sample attr {attribute}");
        }

        foreach (var (index, channelStats) in stats.Buffers.OrderBy(kv => kv.Key))
        {
            lines.Add($"    buffer {index}, {channelStats.Planar.Length} channels:");
            AddLevels("planar     ", channelStats.Planar, channelStats.Count, lines);
            AddLevels("interleaved", channelStats.Interleaved, channelStats.Count, lines);
        }
    }

    private static void AddLevels(string layout, double[] sumSq, long count, List<string> lines)
    {
        double[] levels = sumSq.Select(s => Db(s, count)).ToArray();
        int active = levels.Count(l => l > SilenceDb);
        string text = string.Join(" ", levels.Select(l => l <= SilenceDb ? "·" : l.ToString("0")));
        lines.Add($"      {layout} {active,2} active │ {text}");
    }

    private static double Db(double sumSq, long count) =>
        count == 0 || sumSq <= 0 ? double.NegativeInfinity : 10 * Math.Log10(sumSq / count);

    private static IntPtr FindSpatialType(IntPtr mft, Guid? metadataFormat)
    {
        IntPtr fallback = IntPtr.Zero;
        for (uint t = 0; ; t++)
        {
            IntPtr type;
            if (TransformGetOutputAvailableType(mft, 0, t, &type) < 0)
            {
                return fallback;
            }

            if (GetGuid(type, MF_MT_SUBTYPE) != MFAudioFormat_Float_SpatialObjects)
            {
                Release(type);
                continue;
            }

            if (metadataFormat is null || GetGuid(type, MF_MT_SPATIAL_AUDIO_OBJECT_METADATA_FORMAT_ID) == metadataFormat)
            {
                Release(fallback);
                return type;
            }

            if (fallback == IntPtr.Zero)
            {
                fallback = type;
            }
            else
            {
                Release(type);
            }
        }
    }

    /// <summary>
    /// Finds the dec3 box in an MP4 sample description and reads the E-AC-3 JOC extension
    /// (ETSI TS 102 366 Annex F): flag_ec3_extension_type_a plus complexity_index_type_a.
    /// </summary>
    private static string DescribeJoc(byte[]? sampleDescription)
    {
        if (sampleDescription is null)
        {
            return "no sample description exposed (not MP4?), JOC flag unknown";
        }

        int at = sampleDescription.AsSpan().IndexOf("dec3"u8);
        if (at < 4)
        {
            return "no dec3 box";
        }

        int boxSize = (sampleDescription[at - 4] << 24) | (sampleDescription[at - 3] << 16) | (sampleDescription[at - 2] << 8) | sampleDescription[at - 1];
        int start = at + 4;
        int end = Math.Min(sampleDescription.Length, at - 4 + boxSize);
        var bits = new BitReader(sampleDescription, start, end);
        bits.Read(13);
        int independentSubstreams = bits.Read(3) + 1;
        for (int i = 0; i < independentSubstreams; i++)
        {
            bits.Read(2 + 5 + 1 + 1 + 3 + 3 + 1 + 3);
            int dependent = bits.Read(4);
            bits.Read(dependent > 0 ? 9 : 1);
        }

        string payload = Convert.ToHexString(sampleDescription, start, end - start);
        if (bits.Remaining < 8)
        {
            return $"dec3 {payload}: no JOC extension field (muxer didn't write it; says nothing either way)";
        }

        bits.Read(7);
        bool joc = bits.Read(1) == 1;
        if (!joc)
        {
            return $"dec3 {payload}: JOC flag clear";
        }

        return bits.Remaining >= 8
            ? $"dec3 {payload}: JOC (Atmos) flagged, complexity index {bits.Read(8)}"
            : $"dec3 {payload}: JOC (Atmos) flagged";
    }

    private ref struct BitReader(byte[] data, int start, int end)
    {
        private int _bit = start * 8;

        public readonly int Remaining => end * 8 - _bit;

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++, _bit++)
            {
                value = (value << 1) | ((data[_bit / 8] >> (7 - _bit % 8)) & 1);
            }

            return value;
        }
    }

    private static string SubtypeName(Guid subtype) => SubtypeNames.GetValueOrDefault(subtype, subtype.ToString());

    private static List<string> ExtraAttributes(IntPtr attributes)
    {
        var result = new List<string>();
        uint count;
        if (((delegate* unmanaged<IntPtr, uint*, int>)Vtbl(attributes)[30])(attributes, &count) < 0)
        {
            return result;
        }

        for (uint i = 0; i < count; i++)
        {
            Guid key;
            PropVariant value;
            if (((delegate* unmanaged<IntPtr, uint, Guid*, PropVariant*, int>)Vtbl(attributes)[31])(attributes, i, &key, &value) < 0)
            {
                continue;
            }

            result.Add($"{key} = {Format(&value)}");
            PropVariantClear(&value);
        }

        return result;
    }

    private static string Format(PropVariant* v) => v->Vt switch
    {
        19 => v->UInt32.ToString(),
        20 or 21 => v->UInt64.ToString(),
        5 => v->Double.ToString("0.###"),
        72 => (*(Guid*)v->Pointer).ToString(),
        31 => Marshal.PtrToStringUni(v->Pointer) ?? "",
        13 => "IUnknown",
        0x1011 => $"blob[{v->UInt32}]",
        _ => $"vt={v->Vt}",
    };

    private static void** Vtbl(IntPtr com) => *(void***)com;

    private static void Release(IntPtr com)
    {
        if (com != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, uint>)Vtbl(com)[2])(com);
        }
    }

    // IMFAttributes: 7 GetUINT32, 10 GetGUID, 13 GetAllocatedString, 16 GetAllocatedBlob, 30 GetCount, 31 GetItemByIndex.
    private static uint? GetUInt32(IntPtr attributes, Guid key)
    {
        uint value;
        int hr = ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Vtbl(attributes)[7])(attributes, &key, &value);
        return hr >= 0 ? value : null;
    }

    private static Guid? GetGuid(IntPtr attributes, Guid key)
    {
        Guid value;
        int hr = ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Vtbl(attributes)[10])(attributes, &key, &value);
        return hr >= 0 ? value : null;
    }

    private static string? GetString(IntPtr attributes, Guid key)
    {
        IntPtr value;
        uint length;
        int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, uint*, int>)Vtbl(attributes)[13])(attributes, &key, &value, &length);
        if (hr < 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(value, (int)length);
        }
        finally
        {
            CoTaskMemFree(value);
        }
    }

    private static byte[]? GetBlob(IntPtr attributes, Guid key)
    {
        IntPtr value;
        uint size;
        int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, uint*, int>)Vtbl(attributes)[16])(attributes, &key, &value, &size);
        if (hr < 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(value, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            CoTaskMemFree(value);
        }
    }

    // IMFSourceReader: 4 SetStreamSelection, 5 GetNativeMediaType, 8 SetCurrentPosition, 9 ReadSample, 12 GetPresentationAttribute.
    private static int ReaderSetStreamSelection(IntPtr reader, uint stream, int selected) =>
        ((delegate* unmanaged<IntPtr, uint, int, int>)Vtbl(reader)[4])(reader, stream, selected);

    private static int ReaderGetNativeMediaType(IntPtr reader, uint stream, uint index, IntPtr* type) =>
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Vtbl(reader)[5])(reader, stream, index, type);

    private static int ReaderSetCurrentPosition(IntPtr reader, Guid* timeFormat, PropVariant* position) =>
        ((delegate* unmanaged<IntPtr, Guid*, PropVariant*, int>)Vtbl(reader)[8])(reader, timeFormat, position);

    private static int ReaderReadSample(IntPtr reader, uint stream, uint control, uint* actual, uint* flags, long* timestamp, IntPtr* sample) =>
        ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Vtbl(reader)[9])(reader, stream, control, actual, flags, timestamp, sample);

    private static string? ReaderGetStreamString(IntPtr reader, uint stream, Guid key)
    {
        PropVariant value;
        int hr = ((delegate* unmanaged<IntPtr, uint, Guid*, PropVariant*, int>)Vtbl(reader)[12])(reader, stream, &key, &value);
        if (hr < 0)
        {
            return null;
        }

        string? text = value.Vt == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
        PropVariantClear(&value);
        return text;
    }

    // IMFActivate (after the 30 IMFAttributes methods): 33 ActivateObject, 34 ShutdownObject.
    private static int ActivateObject(IntPtr activate, Guid* iid, IntPtr* result) =>
        ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Vtbl(activate)[33])(activate, iid, result);

    private static int ShutdownObject(IntPtr activate) =>
        ((delegate* unmanaged<IntPtr, int>)Vtbl(activate)[34])(activate);

    // IMFTransform: 7 GetOutputStreamInfo, 14 GetOutputAvailableType, 15 SetInputType, 16 SetOutputType,
    // 23 ProcessMessage, 24 ProcessInput, 25 ProcessOutput.
    private static int TransformGetOutputStreamInfo(IntPtr mft, uint stream, OutputStreamInfo* info) =>
        ((delegate* unmanaged<IntPtr, uint, OutputStreamInfo*, int>)Vtbl(mft)[7])(mft, stream, info);

    private static int TransformGetOutputAvailableType(IntPtr mft, uint stream, uint index, IntPtr* type) =>
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Vtbl(mft)[14])(mft, stream, index, type);

    private static int TransformSetInputType(IntPtr mft, uint stream, IntPtr type, uint flags) =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Vtbl(mft)[15])(mft, stream, type, flags);

    private static int TransformSetOutputType(IntPtr mft, uint stream, IntPtr type, uint flags) =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Vtbl(mft)[16])(mft, stream, type, flags);

    private static int TransformProcessMessage(IntPtr mft, uint message, nuint param) =>
        ((delegate* unmanaged<IntPtr, uint, nuint, int>)Vtbl(mft)[23])(mft, message, param);

    private static int TransformProcessInput(IntPtr mft, uint stream, IntPtr sample, uint flags) =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Vtbl(mft)[24])(mft, stream, sample, flags);

    private static int TransformProcessOutput(IntPtr mft, uint flags, uint count, OutputDataBuffer* buffers, uint* status) =>
        ((delegate* unmanaged<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)Vtbl(mft)[25])(mft, flags, count, buffers, status);

    // IMFSample (after the 30 IMFAttributes methods): 37 GetSampleDuration, 39 GetBufferCount,
    // 40 GetBufferByIndex, 42 AddBuffer.
    private static int SampleGetSampleDuration(IntPtr sample, long* duration) =>
        ((delegate* unmanaged<IntPtr, long*, int>)Vtbl(sample)[37])(sample, duration);

    private static int SampleGetBufferCount(IntPtr sample, uint* count) =>
        ((delegate* unmanaged<IntPtr, uint*, int>)Vtbl(sample)[39])(sample, count);

    private static int SampleGetBufferByIndex(IntPtr sample, uint index, IntPtr* buffer) =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Vtbl(sample)[40])(sample, index, buffer);

    private static int SampleAddBuffer(IntPtr sample, IntPtr buffer) =>
        ((delegate* unmanaged<IntPtr, IntPtr, int>)Vtbl(sample)[42])(sample, buffer);

    // IMFMediaBuffer: 3 Lock, 4 Unlock.
    private static int BufferLock(IntPtr buffer, byte** data, uint* max, uint* length) =>
        ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Vtbl(buffer)[3])(buffer, data, max, length);

    private static int BufferUnlock(IntPtr buffer) =>
        ((delegate* unmanaged<IntPtr, int>)Vtbl(buffer)[4])(buffer);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr p);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(PropVariant* value);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(IntPtr* sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(uint maxLength, IntPtr* buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid category, uint flags, RegisterTypeInfo* input, RegisterTypeInfo* output, IntPtr** activates, uint* count);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
}
