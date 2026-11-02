using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EngineSpike.AtmosLab;

/// <summary>Linear PCM from a Core Audio Format file, as written by truehdd (24-bit big-endian).</summary>
internal sealed class CafAudio
{
    private readonly byte[] _data;
    private readonly int _bytesPerSample;
    private readonly bool _float;
    private readonly bool _littleEndian;

    public int Channels { get; }
    public int SampleRate { get; }
    public long Frames { get; }

    private CafAudio(byte[] data, int channels, int sampleRate, int bits, bool isFloat, bool littleEndian)
    {
        _data = data;
        Channels = channels;
        SampleRate = sampleRate;
        _bytesPerSample = bits / 8;
        _float = isFloat;
        _littleEndian = littleEndian;
        Frames = data.Length / (_bytesPerSample * channels);
    }

    public static CafAudio Load(string path)
    {
        using var stream = File.OpenRead(path);
        var reader = new BinaryReader(stream);
        reader.ReadBytes(8);
        int channels = 0, bits = 0, sampleRate = 0;
        bool isFloat = false, littleEndian = false;
        while (stream.Position < stream.Length)
        {
            string type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(8));
            if (type == "desc")
            {
                byte[] desc = reader.ReadBytes((int)size);
                sampleRate = (int)BinaryPrimitives.ReadDoubleBigEndian(desc);
                uint flags = BinaryPrimitives.ReadUInt32BigEndian(desc.AsSpan(12));
                channels = (int)BinaryPrimitives.ReadUInt32BigEndian(desc.AsSpan(24));
                bits = (int)BinaryPrimitives.ReadUInt32BigEndian(desc.AsSpan(28));
                isFloat = (flags & 1) != 0;
                littleEndian = (flags & 2) != 0;
            }
            else if (type == "data")
            {
                reader.ReadBytes(4);
                long length = size < 0 ? stream.Length - stream.Position : size - 4;
                return new CafAudio(reader.ReadBytes((int)length), channels, sampleRate, bits, isFloat, littleEndian);
            }
            else
            {
                stream.Seek(size, SeekOrigin.Current);
            }
        }

        throw new InvalidDataException($"No data chunk in {path}");
    }

    public float Sample(long frame, int channel)
    {
        int offset = (int)((frame * Channels + channel) * _bytesPerSample);
        var bytes = _data.AsSpan(offset, _bytesPerSample);
        if (_float)
        {
            return _littleEndian ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : BinaryPrimitives.ReadSingleBigEndian(bytes);
        }

        int value = _bytesPerSample switch
        {
            3 when _littleEndian => (bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8),
            3 => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8),
            4 when _littleEndian => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            4 => BinaryPrimitives.ReadInt32BigEndian(bytes),
            _ => _littleEndian ? BinaryPrimitives.ReadInt16LittleEndian(bytes) << 16 : BinaryPrimitives.ReadInt16BigEndian(bytes) << 16,
        };
        return value / 2147483648f;
    }
}

/// <summary>An object's position/gain change, ramped over <see cref="RampLength"/> samples.</summary>
internal sealed record DamfEvent(long SamplePos, bool Active, (float X, float Y, float Z)? Position, float GainDb, long RampLength);

/// <summary>One audio channel of the presentation: a bed channel (e.g. LFE) or an object.</summary>
internal sealed record DamfElement(int Id, int Channel, string? BedChannel)
{
    public bool IsObject => BedChannel is null;
}

/// <summary>
/// A Dolby Atmos master file set as written by truehdd: a YAML header naming the bed channels and
/// objects (in audio-channel order), a YAML event list with positions, and CAF audio. Only the
/// fields needed for rendering are parsed.
/// </summary>
internal sealed class DamfPresentation
{
    public List<DamfElement> Elements { get; } = [];
    public Dictionary<int, List<DamfEvent>> Events { get; } = new();
    public required CafAudio Audio { get; init; }

    public static DamfPresentation Load(string atmosPath)
    {
        string directory = Path.GetDirectoryName(atmosPath)!;
        string? metadataFile = null, audioFile = null, section = null, pendingBedChannel = null;
        var elements = new List<DamfElement>();
        foreach (string raw in File.ReadLines(atmosPath))
        {
            string line = raw.Trim();
            if (line.StartsWith("metadata:"))
            {
                metadataFile = Value(line);
            }
            else if (line.StartsWith("audio:"))
            {
                audioFile = Value(line);
            }
            else if (line == "bedInstances:" || line == "objects:")
            {
                section = line;
            }
            else if (section == "bedInstances:" && line.StartsWith("- channel:"))
            {
                pendingBedChannel = Value(line);
            }
            else if (section == "bedInstances:" && line.StartsWith("ID:") && pendingBedChannel is not null)
            {
                elements.Add(new DamfElement(int.Parse(Value(line)), elements.Count, pendingBedChannel));
                pendingBedChannel = null;
            }
            else if (section == "objects:" && line.StartsWith("- ID:"))
            {
                elements.Add(new DamfElement(int.Parse(Value(line)), elements.Count, null));
            }
        }

        var presentation = new DamfPresentation
        {
            Audio = CafAudio.Load(Path.Combine(directory, audioFile ?? throw new InvalidDataException("No audio file in header"))),
        };
        presentation.Elements.AddRange(elements);
        presentation.LoadEvents(Path.Combine(directory, metadataFile ?? throw new InvalidDataException("No metadata file in header")));
        return presentation;
    }

    private void LoadEvents(string metadataPath)
    {
        int? id = null;
        long samplePos = 0, ramp = 0;
        bool active = true;
        float gain = 0;
        (float, float, float)? position = null;

        void Flush()
        {
            if (id is int current)
            {
                if (!Events.TryGetValue(current, out var list))
                {
                    Events[current] = list = [];
                }

                list.Add(new DamfEvent(samplePos, active, position, gain, ramp));
            }
        }

        foreach (string raw in File.ReadLines(metadataPath))
        {
            string line = raw.Trim();
            if (line.StartsWith("- ID:"))
            {
                Flush();
                id = int.Parse(Value(line));
                samplePos = 0;
                ramp = 0;
                active = true;
                gain = 0;
                position = null;
            }
            else if (line.StartsWith("samplePos:"))
            {
                samplePos = long.Parse(Value(line));
            }
            else if (line.StartsWith("active:"))
            {
                active = Value(line) == "true";
            }
            else if (line.StartsWith("rampLength:"))
            {
                ramp = long.Parse(Value(line));
            }
            else if (line.StartsWith("gain:"))
            {
                string value = Value(line);
                gain = value.Contains("inf") ? float.NegativeInfinity : float.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("pos:"))
            {
                float[] xyz = Value(line).Trim('[', ']').Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                position = (xyz[0], xyz[1], xyz[2]);
            }
        }

        Flush();
    }

    private static string Value(string line) => line[(line.IndexOf(':') + 1)..].Trim();
}
