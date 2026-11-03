using Marquee.Playback.Demuxing;

namespace Marquee.Playback.Matroska;

/// <summary>
/// An index entry. Entries for subtitle blocks (mkvmerge writes one per block) also locate the block
/// inside its cluster and give its duration, so a subtitle can be read without scanning the cluster.
/// </summary>
internal readonly record struct CuePoint(long TimeTicks, int Track, long ClusterPosition, long RelativePosition, long DurationTicks);

/// <summary>A file attached to the segment, such as a font an ASS subtitle track uses.</summary>
internal sealed record MatroskaAttachment(string Name, string? MimeType, byte[] Data);

/// <summary>
/// A minimal Matroska demuxer: track metadata, cues, and block reading with lacing and header
/// stripping. Clusters are read as a flat element stream so unknown-size clusters work too.
/// </summary>
internal sealed class MatroskaDemuxer : IDemuxer
{
    private const int MaxQueuedInactivePackets = 400;

    private readonly EbmlStream _s;
    private readonly object _gate = new();
    private readonly List<MediaTrack> _tracks = [];
    private readonly Dictionary<int, (int Algorithm, byte[]? Settings)> _compression = new();
    private readonly Dictionary<int, Queue<MediaPacket>> _queues = new();
    private readonly HashSet<int> _active = new();
    private readonly Dictionary<int, Action<MediaPacket>> _taps = new();
    private readonly Dictionary<long, long> _clusterDataStarts = new();
    private long _segmentStart;
    private long _segmentEnd;
    private long _firstCluster = -1;
    private long _attachments = -1;
    private long _timestampScale = 1_000_000;
    private long _clusterTimestamp;
    private bool _endOfFile;

    public string Container => "Matroska";
    public IReadOnlyList<MediaTrack> Tracks => _tracks;
    public List<CuePoint> Cues { get; } = [];
    public TimeSpan Duration { get; private set; }
    public bool CanSeek => Cues.Count > 0;
    public string SeekDescription => $"{Cues.Count} cues";

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

    public void Expose(int track, bool active)
    {
        lock (_gate)
        {
            _queues.TryAdd(track, new Queue<MediaPacket>());
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

    public void Tap(int track, Action<MediaPacket>? sink)
    {
        lock (_gate)
        {
            if (sink is null)
            {
                _taps.Remove(track);
            }
            else
            {
                _taps[track] = sink;
            }
        }
    }

    public MediaPacket? Next(int track)
    {
        lock (_gate)
        {
            var queue = _queues[track];
            while (queue.Count == 0)
            {
                if (_endOfFile || ReadNextPacket(t => _queues.ContainsKey(t) || _taps.ContainsKey(t)) is not { } packet)
                {
                    _endOfFile = true;
                    return null;
                }

                if (_taps.TryGetValue(packet.Track, out var sink))
                {
                    sink(packet);
                }
                else if (_queues.TryGetValue(packet.Track, out var target))
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

    /// <summary>Doesn't disturb playback state: reading continues where it was.</summary>
    public List<MediaPacket> FirstPackets(int track, int count)
    {
        lock (_gate)
        {
            long position = _s.Position;
            long clusterTimestamp = _clusterTimestamp;
            var packets = new List<MediaPacket>();
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

    /// <summary>
    /// Reads the block a cue entry with a <see cref="CuePoint.RelativePosition"/> points at, without
    /// disturbing playback state. Null if the entry doesn't lead to a block of its track.
    /// </summary>
    public MediaPacket? ReadPacketAt(CuePoint cue)
    {
        lock (_gate)
        {
            long position = _s.Position;
            long clusterTimestamp = _clusterTimestamp;
            try
            {
                long cluster = _segmentStart + cue.ClusterPosition;
                if (!_clusterDataStarts.TryGetValue(cluster, out long dataStart))
                {
                    _s.Position = cluster;
                    if (!_s.TryReadHeader(out uint id, out _) || id != Ids.Cluster)
                    {
                        return null;
                    }

                    dataStart = _clusterDataStarts[cluster] = _s.Position;
                }

                _s.Position = dataStart + cue.RelativePosition;
                if (!_s.TryReadHeader(out uint blockId, out long size) || blockId is not (Ids.SimpleBlock or Ids.BlockGroup))
                {
                    return null;
                }

                // The cue's time is the block's own, so the cluster's timestamp isn't needed.
                _clusterTimestamp = 0;
                MediaPacket? packet = blockId == Ids.SimpleBlock
                    ? PeekTrackNumber() == cue.Track ? BuildPacket(_s.ReadBytes(size), simple: true, blockDuration: null, hasReference: false) : null
                    : ReadBlockGroup(_s.Position + size, t => t == cue.Track);
                if (packet is null)
                {
                    return null;
                }

                long duration = packet.DurationTicks > 0 ? packet.DurationTicks : cue.DurationTicks;
                return packet with { TimeTicks = cue.TimeTicks, DurationTicks = duration };
            }
            finally
            {
                _s.Position = position;
                _clusterTimestamp = clusterTimestamp;
            }
        }
    }

    /// <summary>Reads the segment's attached files (fonts for ASS subtitles, cover art), or none.</summary>
    public List<MatroskaAttachment> ReadAttachments()
    {
        var files = new List<MatroskaAttachment>();
        lock (_gate)
        {
            if (_attachments < 0)
            {
                return files;
            }

            long position = _s.Position;
            try
            {
                _s.Position = _attachments;
                if (!_s.TryReadHeader(out uint id, out long size) || id != Ids.Attachments)
                {
                    return files;
                }

                _s.ForEachChild(_s.Position + size, (child, childSize) =>
                {
                    if (child != Ids.AttachedFile)
                    {
                        return;
                    }

                    string name = "";
                    string? mime = null;
                    byte[]? data = null;
                    _s.ForEachChild(_s.Position + childSize, (field, fieldSize) =>
                    {
                        switch (field)
                        {
                            case Ids.FileName: name = _s.ReadString(fieldSize); break;
                            case Ids.FileMimeType: mime = _s.ReadString(fieldSize); break;
                            case Ids.FileData: data = _s.ReadBytes(fieldSize); break;
                        }
                    });
                    if (data is not null)
                    {
                        files.Add(new MatroskaAttachment(name, mime, data));
                    }
                });
            }
            finally
            {
                _s.Position = position;
            }
        }

        return files;
    }

    /// <summary>
    /// Seeks to the cluster of the last cue at or before <paramref name="target"/> and returns its
    /// time. Cues are usually written for video only, so a track without its own (audio) uses the
    /// video's, whose clusters start with a keyframe.
    /// </summary>
    public TimeSpan Seek(TimeSpan target, int track)
    {
        lock (_gate)
        {
            foreach (var queue in _queues.Values)
            {
                queue.Clear();
            }

            _endOfFile = false;
            _clusterTimestamp = 0;
            int? video = Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.Number;
            var candidates = Cues.Where(c => c.Track == track).ToList();
            if (candidates.Count == 0)
            {
                candidates = Cues.Where(c => c.Track == video).ToList();
            }

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
                    var (cues, attachments) = ParseSeekHead(end);
                    cuesOffset ??= cues;
                    if (_attachments < 0 && attachments is long a)
                    {
                        _attachments = _segmentStart + a;
                    }

                    break;
                case Ids.Attachments:
                    _attachments = elementStart;
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
                            _tracks.Add(ParseTrack(_s.Position + childSize));
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

    private (long? Cues, long? Attachments) ParseSeekHead(long end)
    {
        long? cues = null;
        long? attachments = null;
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
            else if (seekId == Ids.Attachments && position >= 0)
            {
                attachments = position;
            }
        });
        return (cues, attachments);
    }

    private MediaTrack ParseTrack(long end)
    {
        var track = new MediaTrack();
        (int, byte[]?)? compression = null;
        _s.ForEachChild(end, (id, size) =>
        {
            switch (id)
            {
                case Ids.TrackNumber: track.Number = (int)_s.ReadUInt(size); break;
                case Ids.TrackType: track.Kind = (TrackKind)_s.ReadUInt(size); break;
                case Ids.FlagDefault: track.IsDefault = _s.ReadUInt(size) != 0; break;
                case Ids.FlagForced: track.IsForced = _s.ReadUInt(size) != 0; break;
                case Ids.DefaultDuration: track.DefaultDurationNs = (long)_s.ReadUInt(size); break;
                case Ids.Name: track.Name = _s.ReadString(size); break;
                case Ids.Language: track.Language = _s.ReadString(size); break;
                case Ids.LanguageBcp47: track.LanguageBcp47 = _s.ReadString(size); break;
                case Ids.CodecId: track.CodecId = _s.ReadString(size); break;
                case Ids.CodecPrivate: track.CodecPrivate = _s.ReadBytes(size); break;
                case Ids.Video: ParseVideo(track, _s.Position + size); break;
                case Ids.Audio: ParseAudio(track, _s.Position + size); break;
                case Ids.ContentEncodings: compression = ParseContentEncodings(_s.Position + size); break;
                case Ids.BlockAdditionMapping: ParseBlockAdditionMapping(track, _s.Position + size); break;
            }
        });

        if (compression is { } c)
        {
            _compression[track.Number] = c;
        }

        return track;
    }

    private void ParseVideo(MediaTrack track, long end)
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

    private void ParseAudio(MediaTrack track, long end)
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

    private (int Algorithm, byte[]? Settings)? ParseContentEncodings(long end)
    {
        (int, byte[]?)? compression = null;
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

                compression = ((int)algorithm, settings);
            });
        });
        return compression;
    }

    /// <summary>A track's content compression (0 zlib, 3 header stripping), if any.</summary>
    public (int Algorithm, byte[]? Settings)? Compression(int track) =>
        _compression.TryGetValue(track, out var compression) ? compression : null;

    private void ParseBlockAdditionMapping(MediaTrack track, long end)
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
                    long relative = 0;
                    long duration = 0;
                    _s.ForEachChild(_s.Position + fieldSize, (position, positionSize) =>
                    {
                        switch (position)
                        {
                            case Ids.CueTrack: cueTrack = (int)_s.ReadUInt(positionSize); break;
                            case Ids.CueClusterPosition: cluster = (long)_s.ReadUInt(positionSize); break;
                            case Ids.CueRelativePosition: relative = (long)_s.ReadUInt(positionSize); break;
                            case Ids.CueDuration: duration = (long)_s.ReadUInt(positionSize); break;
                        }
                    });
                    if (cluster >= 0)
                    {
                        Cues.Add(new CuePoint(time * _timestampScale / 100, cueTrack, cluster, relative, duration * _timestampScale / 100));
                    }
                }
            });
        });
    }

    private MediaPacket? ReadNextPacket(Func<int, bool> wanted)
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

    private MediaPacket? ReadBlockGroup(long end, Func<int, bool> wanted)
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

    private MediaPacket? BuildPacket(byte[] raw, bool simple, long? blockDuration, bool hasReference)
    {
        int pos = 0;
        int trackNumber = (int)ReadVint(raw, ref pos);
        short relative = (short)((raw[pos] << 8) | raw[pos + 1]);
        int flags = raw[pos + 2];
        pos += 3;

        var track = Tracks.First(t => t.Number == trackNumber);
        List<(int Offset, int Length)> frames = SplitLaces(raw, pos, (flags >> 1) & 3);
        (byte[] data, int[] sizes) = Concatenate(raw, frames, trackNumber, Compression(trackNumber));

        long timeTicks = (_clusterTimestamp + relative) * _timestampScale / 100;
        long durationTicks = blockDuration is long units
            ? units * _timestampScale / 100
            : track.DefaultDurationNs * frames.Count / 100;
        bool keyFrame = simple ? (flags & 0x80) != 0 : !hasReference;
        return new MediaPacket(trackNumber, timeTicks, durationTicks, keyFrame, data, sizes);
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
    private static (byte[] Data, int[] FrameSizes) Concatenate(byte[] raw, List<(int Offset, int Length)> frames, int track,
        (int Algorithm, byte[]? Settings)? compression)
    {
        if (frames.Count == 1 && compression is null)
        {
            return (raw.AsSpan(frames[0].Offset, frames[0].Length).ToArray(), [frames[0].Length]);
        }

        var output = new MemoryStream();
        var sizes = new int[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            var (offset, length) = frames[i];
            long before = output.Length;
            switch (compression?.Algorithm)
            {
                case null:
                    output.Write(raw, offset, length);
                    break;
                case 3:
                    output.Write(compression.Value.Settings ?? []);
                    output.Write(raw, offset, length);
                    break;
                case 0:
                    using (var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(raw, offset, length), System.IO.Compression.CompressionMode.Decompress))
                    {
                        zlib.CopyTo(output);
                    }

                    break;
                default:
                    throw new NotSupportedException($"Track {track} uses content compression algorithm {compression?.Algorithm}");
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
