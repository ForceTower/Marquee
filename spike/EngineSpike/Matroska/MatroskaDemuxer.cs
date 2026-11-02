namespace EngineSpike.Matroska;

internal enum TrackKind
{
    Video = 1,
    Audio = 2,
    Subtitle = 17,
}

internal sealed record ColourInfo(int? Matrix, int? Range, int? Transfer, int? Primaries, int? MaxCll, int? MaxFall,
    double? MasteringMax, double? MasteringMin);

/// <summary>DOVIDecoderConfigurationRecord from a dvcC/dvvC block addition mapping.</summary>
internal sealed record DolbyVisionConfig(int Profile, int Level, bool RpuPresent, bool ElPresent, bool BlPresent, int Compatibility)
{
    public static DolbyVisionConfig Parse(byte[] record)
    {
        int bits = (record[2] << 8) | record[3];
        return new DolbyVisionConfig(
            Profile: bits >> 9,
            Level: (bits >> 3) & 0x3F,
            RpuPresent: ((bits >> 2) & 1) == 1,
            ElPresent: ((bits >> 1) & 1) == 1,
            BlPresent: (bits & 1) == 1,
            Compatibility: record.Length > 4 ? record[4] >> 4 : 0);
    }

    public override string ToString() =>
        $"Dolby Vision profile {Profile}, level {Level}, BL compat {Compatibility}" +
        $"{(RpuPresent ? ", RPU" : "")}{(ElPresent ? ", EL" : "")}";
}

internal sealed class MatroskaTrack
{
    public int Number;
    public TrackKind Kind;
    public string CodecId = "";
    public byte[]? CodecPrivate;
    public string? Name;
    public string Language = "eng";
    public string? LanguageBcp47;
    public bool IsDefault = true;
    public long DefaultDurationNs;
    public int Width;
    public int Height;
    public double SampleRate;
    public int Channels;
    public ColourInfo? Colour;
    public DolbyVisionConfig? DolbyVision;
    public int? CompressionAlgorithm;
    public byte[]? CompressionSettings;

    public string DisplayLanguage => LanguageBcp47 ?? Language;
}

/// <summary>
/// One block's payload. Laced frames are concatenated in <see cref="Data"/>, which Windows' decoders
/// accept; <see cref="FrameSizes"/> keeps the boundaries for decoders that take one frame at a time.
/// </summary>
internal sealed record MatroskaPacket(int Track, long TimeTicks, long DurationTicks, bool KeyFrame, byte[] Data, int[] FrameSizes)
{
    public IEnumerable<ReadOnlyMemory<byte>> Frames()
    {
        int offset = 0;
        foreach (int size in FrameSizes)
        {
            yield return Data.AsMemory(offset, size);
            offset += size;
        }
    }
}

/// <summary>
/// A minimal Matroska demuxer: track metadata, cues, and block reading with lacing and header
/// stripping. Clusters are read as a flat element stream so unknown-size clusters work too.
/// Thread-safe: Media Foundation requests audio and video samples from different threads.
/// </summary>
internal sealed class MatroskaDemuxer : IDisposable
{
    private const int MaxQueuedInactivePackets = 400;

    private readonly EbmlStream _s;
    private readonly object _gate = new();
    private readonly Dictionary<int, Queue<MatroskaPacket>> _queues = new();
    private readonly HashSet<int> _active = new();
    private long _segmentStart;
    private long _segmentEnd;
    private long _firstCluster = -1;
    private long _timestampScale = 1_000_000;
    private long _clusterTimestamp;
    private bool _endOfFile;

    public List<MatroskaTrack> Tracks { get; } = [];
    public List<(long TimeTicks, int Track, long ClusterPosition)> Cues { get; } = [];
    public TimeSpan Duration { get; private set; }

    private MatroskaDemuxer(string path, int bufferSize)
    {
        _s = new EbmlStream(path, bufferSize);
        ParseHeaders();
    }

    /// <param name="bufferSize">
    /// Read buffer. A reader that only wants a small track (e.g. audio) and skips video blocks
    /// should use a small one, or every skip refills a large buffer it then throws away.
    /// </param>
    public static MatroskaDemuxer Open(string path, int bufferSize = 1 << 20) => new(path, bufferSize);

    /// <summary>Tracks whose packets are queued for delivery; packets for any other track are dropped.</summary>
    public void Expose(int track, bool active)
    {
        lock (_gate)
        {
            _queues.TryAdd(track, new Queue<MatroskaPacket>());
            SetActive(track, active);
        }
    }

    public void SetActive(int track, bool active)
    {
        lock (_gate)
        {
            if (active)
            {
                _active.Add(track);
            }
            else
            {
                _active.Remove(track);
            }
        }
    }

    /// <summary>Drops queued packets that start before <paramref name="timeTicks"/> (used after an audio switch).</summary>
    public void DropBefore(int track, long timeTicks)
    {
        lock (_gate)
        {
            if (_queues.TryGetValue(track, out var queue))
            {
                while (queue.Count > 0 && queue.Peek().TimeTicks < timeTicks)
                {
                    queue.Dequeue();
                }
            }
        }
    }

    public MatroskaPacket? Next(int track)
    {
        lock (_gate)
        {
            var queue = _queues[track];
            while (queue.Count == 0)
            {
                if (_endOfFile || ReadNextPacket(_queues.ContainsKey) is not { } packet)
                {
                    _endOfFile = true;
                    return null;
                }

                if (_queues.TryGetValue(packet.Track, out var target))
                {
                    target.Enqueue(packet);
                    if (!_active.Contains(packet.Track) && target.Count > MaxQueuedInactivePackets)
                    {
                        target.Dequeue();
                    }
                }
            }

            return queue.Dequeue();
        }
    }

    /// <summary>
    /// Reads the first <paramref name="count"/> packets of one track from the start of the file,
    /// without disturbing playback state. Used to probe a decoder's real output format.
    /// </summary>
    public List<MatroskaPacket> FirstPackets(int track, int count)
    {
        lock (_gate)
        {
            long position = _s.Position;
            long clusterTimestamp = _clusterTimestamp;
            var packets = new List<MatroskaPacket>();
            try
            {
                _s.Position = _firstCluster;
                _clusterTimestamp = 0;
                while (packets.Count < count && ReadNextPacket(t => t == track) is { } packet)
                {
                    packets.Add(packet);
                }
            }
            finally
            {
                _s.Position = position;
                _clusterTimestamp = clusterTimestamp;
            }

            return packets;
        }
    }

    public int? FirstVideoTrack => Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.Number;

    /// <summary>Seeks to the last cue at or before <paramref name="target"/> and returns its time.</summary>
    public TimeSpan Seek(TimeSpan target, int videoTrack)
    {
        lock (_gate)
        {
            foreach (var queue in _queues.Values)
            {
                queue.Clear();
            }

            _endOfFile = false;
            _clusterTimestamp = 0;
            var candidates = Cues.Where(c => c.Track == videoTrack).ToList();
            if (candidates.Count == 0)
            {
                candidates = Cues;
            }

            var cue = candidates.LastOrDefault(c => c.TimeTicks <= target.Ticks);
            if (cue == default || target <= TimeSpan.Zero)
            {
                _s.Position = _firstCluster;
                return TimeSpan.Zero;
            }

            _s.Position = _segmentStart + cue.ClusterPosition;
            return TimeSpan.FromTicks(cue.TimeTicks);
        }
    }

    private void ParseHeaders()
    {
        if (!_s.TryReadHeader(out uint id, out long size) || id != Ids.EbmlHeader)
        {
            throw new InvalidDataException("Not a Matroska file");
        }

        _s.Position += size;
        if (!_s.TryReadHeader(out id, out size) || id != Ids.Segment)
        {
            throw new InvalidDataException("No Matroska segment");
        }

        _segmentStart = _s.Position;
        _segmentEnd = size == EbmlStream.UnknownSize ? _s.Length : _segmentStart + size;
        long? cuesOffset = null;
        double durationUnits = 0;

        while (_s.Position < _segmentEnd)
        {
            long elementStart = _s.Position;
            if (!_s.TryReadHeader(out id, out size))
            {
                break;
            }

            if (id == Ids.Cluster)
            {
                _firstCluster = elementStart;
                break;
            }

            long dataStart = _s.Position;
            long end = dataStart + size;
            switch (id)
            {
                case Ids.SeekHead:
                    cuesOffset ??= ParseSeekHead(end);
                    break;
                case Ids.Info:
                    _s.ForEachChild(end, (child, childSize) =>
                    {
                        if (child == Ids.TimestampScale)
                        {
                            _timestampScale = (long)_s.ReadUInt(childSize);
                        }
                        else if (child == Ids.Duration)
                        {
                            durationUnits = _s.ReadFloat(childSize);
                        }
                    });
                    break;
                case Ids.Tracks:
                    _s.ForEachChild(end, (child, childSize) =>
                    {
                        if (child == Ids.TrackEntry)
                        {
                            Tracks.Add(ParseTrack(_s.Position + childSize));
                        }
                    });
                    break;
                case Ids.Cues:
                    ParseCues(end);
                    break;
            }

            if (size == EbmlStream.UnknownSize)
            {
                throw new InvalidDataException($"Unknown-size element 0x{id:X} before the first cluster");
            }

            _s.Position = end;
        }

        if (_firstCluster < 0)
        {
            throw new InvalidDataException("No clusters found");
        }

        if (Cues.Count == 0 && cuesOffset is long offset)
        {
            _s.Position = _segmentStart + offset;
            if (_s.TryReadHeader(out id, out size) && id == Ids.Cues)
            {
                ParseCues(_s.Position + size);
            }
        }

        Duration = TimeSpan.FromTicks((long)(durationUnits * _timestampScale / 100));
        _s.Position = _firstCluster;
    }

    private long? ParseSeekHead(long end)
    {
        long? cues = null;
        _s.ForEachChild(end, (child, childSize) =>
        {
            if (child != Ids.Seek)
            {
                return;
            }

            uint seekId = 0;
            long position = -1;
            _s.ForEachChild(_s.Position + childSize, (field, fieldSize) =>
            {
                if (field == Ids.SeekId)
                {
                    seekId = (uint)_s.ReadUInt(fieldSize);
                }
                else if (field == Ids.SeekPosition)
                {
                    position = (long)_s.ReadUInt(fieldSize);
                }
            });
            if (seekId == Ids.Cues && position >= 0)
            {
                cues = position;
            }
        });
        return cues;
    }

    private MatroskaTrack ParseTrack(long end)
    {
        var track = new MatroskaTrack();
        _s.ForEachChild(end, (id, size) =>
        {
            switch (id)
            {
                case Ids.TrackNumber: track.Number = (int)_s.ReadUInt(size); break;
                case Ids.TrackType: track.Kind = (TrackKind)_s.ReadUInt(size); break;
                case Ids.FlagDefault: track.IsDefault = _s.ReadUInt(size) != 0; break;
                case Ids.DefaultDuration: track.DefaultDurationNs = (long)_s.ReadUInt(size); break;
                case Ids.Name: track.Name = _s.ReadString(size); break;
                case Ids.Language: track.Language = _s.ReadString(size); break;
                case Ids.LanguageBcp47: track.LanguageBcp47 = _s.ReadString(size); break;
                case Ids.CodecId: track.CodecId = _s.ReadString(size); break;
                case Ids.CodecPrivate: track.CodecPrivate = _s.ReadBytes(size); break;
                case Ids.Video: ParseVideo(track, _s.Position + size); break;
                case Ids.Audio: ParseAudio(track, _s.Position + size); break;
                case Ids.ContentEncodings: ParseContentEncodings(track, _s.Position + size); break;
                case Ids.BlockAdditionMapping: ParseBlockAdditionMapping(track, _s.Position + size); break;
            }
        });
        return track;
    }

    private void ParseVideo(MatroskaTrack track, long end)
    {
        _s.ForEachChild(end, (id, size) =>
        {
            switch (id)
            {
                case Ids.PixelWidth: track.Width = (int)_s.ReadUInt(size); break;
                case Ids.PixelHeight: track.Height = (int)_s.ReadUInt(size); break;
                case Ids.Colour: track.Colour = ParseColour(_s.Position + size); break;
            }
        });
    }

    private ColourInfo ParseColour(long end)
    {
        int? matrix = null, range = null, transfer = null, primaries = null, maxCll = null, maxFall = null;
        double? masteringMax = null, masteringMin = null;
        _s.ForEachChild(end, (id, size) =>
        {
            switch (id)
            {
                case Ids.MatrixCoefficients: matrix = (int)_s.ReadUInt(size); break;
                case Ids.Range: range = (int)_s.ReadUInt(size); break;
                case Ids.TransferCharacteristics: transfer = (int)_s.ReadUInt(size); break;
                case Ids.Primaries: primaries = (int)_s.ReadUInt(size); break;
                case Ids.MaxCll: maxCll = (int)_s.ReadUInt(size); break;
                case Ids.MaxFall: maxFall = (int)_s.ReadUInt(size); break;
                case Ids.MasteringMetadata:
                    _s.ForEachChild(_s.Position + size, (field, fieldSize) =>
                    {
                        if (field == Ids.LuminanceMax)
                        {
                            masteringMax = _s.ReadFloat(fieldSize);
                        }
                        else if (field == Ids.LuminanceMin)
                        {
                            masteringMin = _s.ReadFloat(fieldSize);
                        }
                    });
                    break;
            }
        });
        return new ColourInfo(matrix, range, transfer, primaries, maxCll, maxFall, masteringMax, masteringMin);
    }

    private void ParseAudio(MatroskaTrack track, long end)
    {
        _s.ForEachChild(end, (id, size) =>
        {
            if (id == Ids.SamplingFrequency)
            {
                track.SampleRate = _s.ReadFloat(size);
            }
            else if (id == Ids.Channels)
            {
                track.Channels = (int)_s.ReadUInt(size);
            }
        });
    }

    private void ParseContentEncodings(MatroskaTrack track, long end)
    {
        _s.ForEachChild(end, (id, size) =>
        {
            if (id != Ids.ContentEncoding)
            {
                return;
            }

            _s.ForEachChild(_s.Position + size, (encodingId, encodingSize) =>
            {
                if (encodingId != Ids.ContentCompression)
                {
                    return;
                }

                ulong algorithm = 0;
                byte[]? settings = null;
                _s.ForEachChild(_s.Position + encodingSize, (field, fieldSize) =>
                {
                    if (field == Ids.ContentCompAlgo)
                    {
                        algorithm = _s.ReadUInt(fieldSize);
                    }
                    else if (field == Ids.ContentCompSettings)
                    {
                        settings = _s.ReadBytes(fieldSize);
                    }
                });

                track.CompressionAlgorithm = (int)algorithm;
                track.CompressionSettings = settings;
            });
        });
    }

    private void ParseBlockAdditionMapping(MatroskaTrack track, long end)
    {
        uint type = 0;
        byte[]? extra = null;
        _s.ForEachChild(end, (id, size) =>
        {
            if (id == Ids.BlockAddIdType)
            {
                type = (uint)_s.ReadUInt(size);
            }
            else if (id == Ids.BlockAddIdExtraData)
            {
                extra = _s.ReadBytes(size);
            }
        });

        const uint dvcC = 0x64766343;
        const uint dvvC = 0x64767643;
        if (type is dvcC or dvvC && extra is { Length: >= 5 })
        {
            track.DolbyVision = DolbyVisionConfig.Parse(extra);
        }
    }

    private void ParseCues(long end)
    {
        _s.ForEachChild(end, (id, size) =>
        {
            if (id != Ids.CuePoint)
            {
                return;
            }

            long time = 0;
            _s.ForEachChild(_s.Position + size, (field, fieldSize) =>
            {
                if (field == Ids.CueTime)
                {
                    time = (long)_s.ReadUInt(fieldSize);
                }
                else if (field == Ids.CueTrackPositions)
                {
                    int cueTrack = 0;
                    long cluster = -1;
                    _s.ForEachChild(_s.Position + fieldSize, (position, positionSize) =>
                    {
                        if (position == Ids.CueTrack)
                        {
                            cueTrack = (int)_s.ReadUInt(positionSize);
                        }
                        else if (position == Ids.CueClusterPosition)
                        {
                            cluster = (long)_s.ReadUInt(positionSize);
                        }
                    });
                    if (cluster >= 0)
                    {
                        Cues.Add((time * _timestampScale / 100, cueTrack, cluster));
                    }
                }
            });
        });
    }

    private MatroskaPacket? ReadNextPacket(Func<int, bool> wanted)
    {
        while (_s.Position < _segmentEnd)
        {
            if (!_s.TryReadHeader(out uint id, out long size))
            {
                return null;
            }

            long dataStart = _s.Position;
            switch (id)
            {
                case Ids.Cluster:
                    _clusterTimestamp = 0;
                    continue;
                case Ids.Timestamp:
                    _clusterTimestamp = (long)_s.ReadUInt(size);
                    continue;
                case Ids.SimpleBlock:
                    if (wanted(PeekTrackNumber()))
                    {
                        return BuildPacket(_s.ReadBytes(size), simple: true, blockDuration: null, hasReference: false);
                    }

                    _s.Position = dataStart + size;
                    continue;
                case Ids.BlockGroup:
                    if (ReadBlockGroup(dataStart + size, wanted) is { } grouped)
                    {
                        return grouped;
                    }

                    _s.Position = dataStart + size;
                    continue;
            }

            if (size == EbmlStream.UnknownSize)
            {
                throw new InvalidDataException($"Unknown-size element 0x{id:X} inside the segment");
            }

            _s.Position = dataStart + size;
        }

        return null;
    }

    private MatroskaPacket? ReadBlockGroup(long end, Func<int, bool> wanted)
    {
        byte[]? block = null;
        long? duration = null;
        bool hasReference = false;
        _s.ForEachChild(end, (id, size) =>
        {
            switch (id)
            {
                case Ids.Block:
                    if (wanted(PeekTrackNumber()))
                    {
                        block = _s.ReadBytes(size);
                    }

                    break;
                case Ids.BlockDuration: duration = (long)_s.ReadUInt(size); break;
                case Ids.ReferenceBlock: hasReference = true; break;
            }
        });
        return block is null ? null : BuildPacket(block, simple: false, duration, hasReference);
    }

    private int PeekTrackNumber()
    {
        long start = _s.Position;
        int track = (int)_s.ReadVint();
        _s.Position = start;
        return track;
    }

    private MatroskaPacket? BuildPacket(byte[] raw, bool simple, long? blockDuration, bool hasReference)
    {
        int pos = 0;
        int trackNumber = (int)ReadVint(raw, ref pos);
        short relative = (short)((raw[pos] << 8) | raw[pos + 1]);
        int flags = raw[pos + 2];
        pos += 3;

        var track = Tracks.First(t => t.Number == trackNumber);
        List<(int Offset, int Length)> frames = SplitLaces(raw, pos, (flags >> 1) & 3);
        (byte[] data, int[] sizes) = Concatenate(raw, frames, track);

        long timeTicks = (_clusterTimestamp + relative) * _timestampScale / 100;
        long durationTicks = blockDuration is long units
            ? units * _timestampScale / 100
            : track.DefaultDurationNs * frames.Count / 100;
        bool keyFrame = simple ? (flags & 0x80) != 0 : !hasReference;
        return new MatroskaPacket(trackNumber, timeTicks, durationTicks, keyFrame, data, sizes);
    }

    private static List<(int, int)> SplitLaces(byte[] raw, int pos, int lacing)
    {
        if (lacing == 0)
        {
            return [(pos, raw.Length - pos)];
        }

        int count = raw[pos++] + 1;
        var sizes = new int[count];
        switch (lacing)
        {
            case 1:
                for (int i = 0; i < count - 1; i++)
                {
                    int b;
                    do
                    {
                        b = raw[pos++];
                        sizes[i] += b;
                    }
                    while (b == 0xFF);
                }

                break;
            case 3:
                sizes[0] = (int)ReadVint(raw, ref pos);
                for (int i = 1; i < count - 1; i++)
                {
                    int start = pos;
                    long value = ReadVint(raw, ref pos);
                    int length = pos - start;
                    sizes[i] = sizes[i - 1] + (int)(value - ((1L << (7 * length - 1)) - 1));
                }

                break;
        }

        if (lacing == 2)
        {
            Array.Fill(sizes, (raw.Length - pos) / count);
        }
        else
        {
            sizes[count - 1] = raw.Length - pos - sizes[..^1].Sum();
        }

        var frames = new List<(int, int)>(count);
        foreach (int size in sizes)
        {
            frames.Add((pos, size));
            pos += size;
        }

        return frames;
    }

    /// <summary>Joins laced frames into one sample, undoing header stripping (3) or zlib (0) compression.</summary>
    private static (byte[] Data, int[] FrameSizes) Concatenate(byte[] raw, List<(int Offset, int Length)> frames, MatroskaTrack track)
    {
        if (frames.Count == 1 && track.CompressionAlgorithm is null)
        {
            return (raw.AsSpan(frames[0].Offset, frames[0].Length).ToArray(), [frames[0].Length]);
        }

        var output = new MemoryStream();
        var sizes = new int[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            var (offset, length) = frames[i];
            long before = output.Length;
            switch (track.CompressionAlgorithm)
            {
                case null:
                    output.Write(raw, offset, length);
                    break;
                case 3:
                    output.Write(track.CompressionSettings ?? []);
                    output.Write(raw, offset, length);
                    break;
                case 0:
                    using (var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(raw, offset, length), System.IO.Compression.CompressionMode.Decompress))
                    {
                        zlib.CopyTo(output);
                    }

                    break;
                default:
                    throw new NotSupportedException($"Track {track.Number} uses content compression algorithm {track.CompressionAlgorithm}");
            }

            sizes[i] = (int)(output.Length - before);
        }

        return (output.ToArray(), sizes);
    }

    private static long ReadVint(byte[] data, ref int pos)
    {
        int first = data[pos];
        int length = EbmlStream.VintLength(first);
        long value = first & (0xFF >> length);
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | data[pos + i];
        }

        pos += length;
        return value;
    }

    public void Dispose() => _s.Dispose();
}
