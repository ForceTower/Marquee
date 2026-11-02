using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace EngineSpike.Matroska;

internal static class Ids
{
    public const uint EbmlHeader = 0x1A45DFA3;
    public const uint Segment = 0x18538067;
    public const uint SeekHead = 0x114D9B74;
    public const uint Seek = 0x4DBB;
    public const uint SeekId = 0x53AB;
    public const uint SeekPosition = 0x53AC;
    public const uint Info = 0x1549A966;
    public const uint TimestampScale = 0x2AD7B1;
    public const uint Duration = 0x4489;
    public const uint Tracks = 0x1654AE6B;
    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackType = 0x83;
    public const uint FlagDefault = 0x88;
    public const uint DefaultDuration = 0x23E383;
    public const uint Name = 0x536E;
    public const uint Language = 0x22B59C;
    public const uint LanguageBcp47 = 0x22B59D;
    public const uint CodecId = 0x86;
    public const uint CodecPrivate = 0x63A2;
    public const uint Video = 0xE0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;
    public const uint Colour = 0x55B0;
    public const uint MatrixCoefficients = 0x55B1;
    public const uint Range = 0x55B9;
    public const uint TransferCharacteristics = 0x55BA;
    public const uint Primaries = 0x55BB;
    public const uint MaxCll = 0x55BC;
    public const uint MaxFall = 0x55BD;
    public const uint MasteringMetadata = 0x55D0;
    public const uint LuminanceMax = 0x55D9;
    public const uint LuminanceMin = 0x55DA;
    public const uint Audio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint Channels = 0x9F;
    public const uint ContentEncodings = 0x6D80;
    public const uint ContentEncoding = 0x6240;
    public const uint ContentCompression = 0x5034;
    public const uint ContentCompAlgo = 0x4254;
    public const uint ContentCompSettings = 0x4255;
    public const uint BlockAdditionMapping = 0x41E4;
    public const uint BlockAddIdType = 0x41E7;
    public const uint BlockAddIdExtraData = 0x41ED;
    public const uint Cues = 0x1C53BB6B;
    public const uint CuePoint = 0xBB;
    public const uint CueTime = 0xB3;
    public const uint CueTrackPositions = 0xB7;
    public const uint CueTrack = 0xF7;
    public const uint CueClusterPosition = 0xF1;
    public const uint Cluster = 0x1F43B675;
    public const uint Timestamp = 0xE7;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;
    public const uint BlockDuration = 0x9B;
    public const uint ReferenceBlock = 0xFB;
}

/// <summary>Reads EBML element headers and values from a file.</summary>
internal sealed class EbmlStream : IDisposable
{
    public const long UnknownSize = -1;

    private readonly FileStream _stream;

    public EbmlStream(string path, int bufferSize = 1 << 20)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
    }

    public long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public long Length => _stream.Length;

    public bool TryReadHeader(out uint id, out long size)
    {
        id = 0;
        size = 0;
        int first = _stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        int length = VintLength(first);
        if (length is 0 or > 4)
        {
            throw new InvalidDataException($"Invalid EBML element ID at offset {Position - 1}");
        }

        id = (uint)first;
        for (int i = 1; i < length; i++)
        {
            id = (id << 8) | ReadByte();
        }

        size = ReadSize();
        return true;
    }

    public ulong ReadUInt(long size)
    {
        ulong value = 0;
        for (long i = 0; i < size; i++)
        {
            value = (value << 8) | ReadByte();
        }

        return value;
    }

    /// <summary>Reads a variable-length integer with its length marker removed (e.g. a block's track number).</summary>
    public long ReadVint()
    {
        int first = (int)ReadByte();
        int length = VintLength(first);
        long value = first & (0xFF >> length);
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | ReadByte();
        }

        return value;
    }

    public double ReadFloat(long size)
    {
        Span<byte> bytes = stackalloc byte[8];
        _stream.ReadExactly(bytes[..(int)size]);
        return size == 4
            ? BinaryPrimitives.ReadSingleBigEndian(bytes)
            : BinaryPrimitives.ReadDoubleBigEndian(bytes);
    }

    public string ReadString(long size) => Encoding.UTF8.GetString(ReadBytes(size)).TrimEnd('\0');

    public byte[] ReadBytes(long size)
    {
        var bytes = new byte[size];
        _stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Calls <paramref name="handler"/> for each child up to <paramref name="end"/>, then moves past the child.</summary>
    public void ForEachChild(long end, Action<uint, long> handler)
    {
        while (Position < end && TryReadHeader(out uint id, out long size))
        {
            long dataStart = Position;
            handler(id, size);
            Position = dataStart + size;
        }
    }

    private long ReadSize()
    {
        int first = (int)ReadByte();
        int length = VintLength(first);
        if (length == 0)
        {
            throw new InvalidDataException($"Invalid EBML size at offset {Position - 1}");
        }

        long mask = 0xFF >> length;
        long value = first & mask;
        bool allOnes = value == mask;
        for (int i = 1; i < length; i++)
        {
            int b = (int)ReadByte();
            value = (value << 8) | (uint)b;
            allOnes &= b == 0xFF;
        }

        return allOnes ? UnknownSize : value;
    }

    private uint ReadByte()
    {
        int b = _stream.ReadByte();
        return b < 0 ? throw new EndOfStreamException() : (uint)b;
    }

    public static int VintLength(int firstByte) =>
        firstByte == 0 ? 0 : BitOperations.LeadingZeroCount((uint)firstByte) - 23;

    public void Dispose() => _stream.Dispose();
}
