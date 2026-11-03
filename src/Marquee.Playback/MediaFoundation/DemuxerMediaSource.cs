using System.Runtime.InteropServices.WindowsRuntime;
using Marquee.Playback.Demuxing;
using Marquee.Playback.Matroska;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Marquee.Playback.MediaFoundation;

/// <summary>
/// Feeds a file Marquee demuxes itself (Matroska, or MPEG-TS through libavformat) to Media
/// Foundation through a MediaStreamSource, setting the attributes Windows' own MP4 source sets and
/// its MKV and MPEG-TS sources don't: the Dolby Vision renderer-extension profile and RPU
/// forwarding, plus HDR color metadata. Audio is passed through compressed so Windows' Dolby
/// decoder (and its Atmos object output) does the decoding.
/// </summary>
internal sealed class DemuxerMediaSource
{
    // mfapi.h. The renderer-extension profile selects the Dolby Vision Extensions' video renderer
    // effect, which registers dvhe.05, dvhe.08 and dvav.09. NAL unit type 62 carries the DV RPU.
    private static readonly Guid MF_MT_VIDEO_RENDERER_EXTENSION_PROFILE = new("8437d4b9-d448-4fcd-9b6b-839bf96c7798");
    private static readonly Guid MF_MT_FORWARD_CUSTOM_NALU = new("ed336efd-244f-428d-9153-28f399458890");
    private static readonly Guid MF_DECODER_FWD_CUSTOM_SEI_DECODE_ORDER = new("f13bbe3c-36d4-410a-b985-7a951a1e6294");
    private static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid MF_MT_TRANSFER_FUNCTION = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid MF_MT_VIDEO_PRIMARIES = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid MF_MT_MAX_LUMINANCE_LEVEL = new("50253128-c110-4de4-98ae-46a324fae6da");
    private static readonly Guid MF_MT_MAX_FRAME_AVERAGE_LUMINANCE_LEVEL = new("58d4bf57-6f52-4733-a195-a9e29ecf9e27");
    private static readonly Guid MF_MT_MAX_MASTERING_LUMINANCE = new("d6c6b997-272f-4ca1-8d00-8042111a0ff6");
    private static readonly Guid MF_MT_MIN_MASTERING_LUMINANCE = new("839a4460-4e7e-4b4f-ae79-cc08905c7b27");
    private static readonly Guid MF_MT_AUDIO_CHANNEL_MASK = new("55fb5765-644a-4caf-8479-938983bb1588");
    private const uint DolbyVisionRpuNalType = 62;

    private static readonly HashSet<int> SupportedDolbyVisionProfiles = [5, 8];

    private readonly IDemuxer _demuxer;
    private readonly MediaTrack _video;
    private readonly NalConverter _converter;
    private readonly Dictionary<IMediaStreamDescriptor, MediaTrack> _tracksByDescriptor = new();
    private readonly Dictionary<int, FfmpegAudioDecoder> _decoders = new();
    private long _lastAudioTicks;

    public MediaStreamSource Source { get; }
    public List<string> Description { get; } = [];

    public event Action<string>? Error;
    public event Action<string>? Trace;

    /// <summary>Raised with the actual start time after a (re)start repositioned the demuxer, before samples resume.</summary>
    public event Action<TimeSpan>? Seeked;

    /// <summary>
    /// If set, a (re)start is held until the returned task completes (at most 1.5 s), so audio
    /// played outside Media Foundation can be ready when the first video frame shows.
    /// </summary>
    public Func<TimeSpan, Task>? StartGate { get; set; }

    /// <summary>Raised when a (re)start is released (at once, or once <see cref="StartGate"/> completes), just before samples flow.</summary>
    public event Action? StartReleased;

    internal IDemuxer Demuxer => _demuxer;

    private readonly Dictionary<int, int> _sampleCounts = new();

    private static readonly Guid MFAudioFormat_DTS_HD = new("a2e58eb7-0fa9-48bb-a40c-fa0e156d0645");

    private readonly bool _requestDolbyVision;
    private readonly bool _windowsDts;
    private readonly HashSet<int> _splitLaces = new();
    private readonly Dictionary<int, Queue<(long Ticks, long Duration, byte[] Data)>> _pendingFrames = new();

    private DemuxerMediaSource(IDemuxer demuxer, bool requestDolbyVision, bool windowsDts, int? externalAudioTrack, bool audio)
    {
        _requestDolbyVision = requestDolbyVision;
        _windowsDts = windowsDts;
        _demuxer = demuxer;
        _video = _demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)
                 ?? throw new NotSupportedException("No video track");
        _converter = NalConverter.Create(_video);

        var videoDescriptor = new VideoStreamDescriptor(VideoProperties(_video));
        Source = new MediaStreamSource(videoDescriptor);
        _tracksByDescriptor[videoDescriptor] = _video;
        _demuxer.Expose(_video.Number, active: true);
        Description.Add($"video #{_video.Number} {_video.CodecId} {_video.Width}x{_video.Height}" +
                        $"{(_video.DolbyVision is { } dv ? $" · {dv}" : "")}");

        if (externalAudioTrack is int external)
        {
            Description.Add($"audio #{external} → played by Marquee outside Media Foundation (video-only source)");
        }
        else if (!audio)
        {
            Description.Add("audio left out (video-only source)");
        }

        foreach (var track in _demuxer.Tracks.Where(t => t.Kind == TrackKind.Audio && externalAudioTrack is null && audio))
        {
            string label = track.Name ?? $"{track.CodecId[2..]} {track.Channels}ch";
            AudioEncodingProperties? properties = AudioProperties(track);
            if (properties is null && FfmpegAudioDecoder.CanDecode(track.CodecId))
            {
                properties = DecodedAudioProperties(track, label);
            }

            if (properties is null)
            {
                Description.Add($"audio #{track.Number} [{track.DisplayLanguage}] {track.CodecId} '{label}' skipped: no Windows decoder");
                continue;
            }

            var descriptor = new AudioStreamDescriptor(properties)
            {
                Name = $"track {track.Number}",
                Label = label,
                Language = track.DisplayLanguage,
            };
            Source.AddStreamDescriptor(descriptor);
            _tracksByDescriptor[descriptor] = track;
            _demuxer.Expose(track.Number, active: false);
            if (!_decoders.ContainsKey(track.Number))
            {
                string decoder = _splitLaces.Contains(track.Number) ? " (DTS Sound Unbound decoder)" : "";
                Description.Add($"audio #{track.Number} [{track.DisplayLanguage}] {track.CodecId} {track.Channels}ch '{label}' → Media Foundation{decoder}");
            }
        }

        Source.Duration = _demuxer.Duration;
        Source.CanSeek = _demuxer.CanSeek;
        Source.BufferTime = TimeSpan.FromSeconds(2);
        Source.Starting += OnStarting;
        Source.SampleRequested += OnSampleRequested;
        Source.SwitchStreamsRequested += OnSwitchStreamsRequested;
        Source.Closed += (_, _) =>
        {
            _demuxer.Dispose();
            foreach (var decoder in _decoders.Values)
            {
                decoder.Dispose();
            }
        };
        Description.Add($"{_demuxer.Container}, duration {_demuxer.Duration:hh\\:mm\\:ss}, {_demuxer.SeekDescription}");
    }

    /// <param name="demuxer">The file's reader (see <see cref="Demuxing.Demuxer.Open"/>); the source owns it from here.</param>
    /// <param name="windowsDts">
    /// Pass DTS through to Windows (DTS Sound Unbound's DTSXDecoder, which can output DTS:X objects)
    /// instead of decoding it with FFmpeg. Only works once the Store codec runtime is preloaded.
    /// </param>
    /// <param name="externalAudioTrack">
    /// Give Media Foundation video only and keep this audio track's packets in the demuxer for a
    /// player outside Media Foundation (see <see cref="Demuxer"/> and <see cref="Seeked"/>).
    /// </param>
    /// <param name="audio">False gives Media Foundation video only, with no audio at all.</param>
    public static DemuxerMediaSource Open(IDemuxer demuxer, bool requestDolbyVision = true, bool windowsDts = false, int? externalAudioTrack = null, bool audio = true)
    {
        try
        {
            return new DemuxerMediaSource(demuxer, requestDolbyVision, windowsDts, externalAudioTrack, audio);
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }

    /// <summary>Whether Marquee demuxes this file itself for Media Foundation, rather than leaving it to Windows' own source (MP4).</summary>
    public static bool Handles(string location) =>
        Demuxing.Demuxer.IsMatroska(location) || Demuxing.Demuxer.IsTransportStream(location) && FfmpegLibraries.CanDemux;

    private VideoEncodingProperties VideoProperties(MediaTrack track)
    {
        var properties = track.CodecId switch
        {
            "V_MPEGH/ISO/HEVC" => VideoEncodingProperties.CreateHevc(),
            "V_MPEG4/ISO/AVC" => VideoEncodingProperties.CreateH264(),
            _ => throw new NotSupportedException($"Video codec {track.CodecId} isn't supported by the Media Foundation engine yet"),
        };
        properties.Width = (uint)track.Width;
        properties.Height = (uint)track.Height;
        if (track.DefaultDurationNs > 0)
        {
            properties.FrameRate.Numerator = 1_000_000_000;
            properties.FrameRate.Denominator = (uint)track.DefaultDurationNs;
        }

        properties.ProfileId = _converter.ProfileId;
        if (_converter.ParameterSets.Length > 0)
        {
            properties.Properties[MF_MT_MPEG_SEQUENCE_HEADER] = _converter.ParameterSets;
        }

        ApplyColour(properties, track);
        if (track.DolbyVision is { } dv)
        {
            if (!_requestDolbyVision)
            {
                Description.Add("Dolby Vision renderer extension disabled (--no-dv)");
            }
            else if (SupportedDolbyVisionProfiles.Contains(dv.Profile))
            {
                RequestDolbyVision(properties, $"dvhe.{dv.Profile:00}");
            }
            else if (dv.Profile == 7 && track.CodecId == "V_MPEGH/ISO/HEVC" && DoviConverter.Available)
            {
                _converter.ConvertProfile7 = true;
                _converter.Log = message => Trace?.Invoke(message);
                RequestDolbyVision(properties, "dvhe.08");
                Description.Add("Dolby Vision profile 7 → 8.1, converted per frame (dovi_tool mode 2); enhancement layer dropped");
            }
            else
            {
                Description.Add($"Dolby Vision profile {dv.Profile} isn't handled by the Dolby Vision Extensions → HDR10 base layer only");
            }
        }

        return properties;
    }

    private void RequestDolbyVision(VideoEncodingProperties properties, string profile)
    {
        properties.Properties[MF_MT_VIDEO_RENDERER_EXTENSION_PROFILE] = profile;
        properties.Properties[MF_MT_FORWARD_CUSTOM_NALU] = DolbyVisionRpuNalType;
        properties.Properties[MF_DECODER_FWD_CUSTOM_SEI_DECODE_ORDER] = 1u;
        Description.Add($"Dolby Vision renderer extension requested: {profile}");
    }

    private void ApplyColour(VideoEncodingProperties properties, MediaTrack track)
    {
        ColourInfo? c = track.Colour;
        // A DV profile 5 base layer has no HDR10 fallback: it is PQ / BT.2020 in Dolby's IPT space.
        bool profile5 = track.DolbyVision?.Profile == 5;

        uint? transfer = (c?.Transfer ?? (profile5 ? 16 : null)) switch
        {
            1 or 6 => 5u,
            14 or 15 => 13u,
            16 => 15u,
            18 => 16u,
            _ => null,
        };
        uint? primaries = (c?.Primaries ?? (profile5 ? 9 : null)) switch
        {
            1 => 2u,
            9 => 9u,
            11 or 12 => 11u,
            _ => null,
        };
        uint? matrix = c?.Matrix switch
        {
            1 => 1u,
            5 or 6 => 2u,
            9 => 4u,
            10 => 5u,
            _ => null,
        };
        uint? range = c?.Range switch
        {
            1 => 2u,
            2 => 1u,
            _ => null,
        };

        SetIfPresent(properties, MF_MT_TRANSFER_FUNCTION, transfer);
        SetIfPresent(properties, MF_MT_VIDEO_PRIMARIES, primaries);
        SetIfPresent(properties, MF_MT_YUV_MATRIX, matrix);
        SetIfPresent(properties, MF_MT_VIDEO_NOMINAL_RANGE, range);
        SetIfPresent(properties, MF_MT_MAX_LUMINANCE_LEVEL, (uint?)c?.MaxCll);
        SetIfPresent(properties, MF_MT_MAX_FRAME_AVERAGE_LUMINANCE_LEVEL, (uint?)c?.MaxFall);
        SetIfPresent(properties, MF_MT_MAX_MASTERING_LUMINANCE, c?.MasteringMax is double max ? (uint)Math.Round(max) : null);
        SetIfPresent(properties, MF_MT_MIN_MASTERING_LUMINANCE, c?.MasteringMin is double min ? (uint)Math.Round(min * 10000) : null);

        Description.Add($"color (ISO/IEC 23091-2 codes): transfer {c?.Transfer?.ToString() ?? "—"}, primaries {c?.Primaries?.ToString() ?? "—"}, " +
                        $"matrix {c?.Matrix?.ToString() ?? "—"}, range {c?.Range?.ToString() ?? "—"}, MaxCLL {c?.MaxCll?.ToString() ?? "—"}, " +
                        $"mastering {c?.MasteringMin?.ToString("0.####") ?? "—"}–{c?.MasteringMax?.ToString("0") ?? "—"} nits");
    }

    private static void SetIfPresent(VideoEncodingProperties properties, Guid key, uint? value)
    {
        if (value is uint v)
        {
            properties.Properties[key] = v;
        }
    }

    private AudioEncodingProperties? AudioProperties(MediaTrack track)
    {
        uint rate = (uint)Math.Round(track.SampleRate);
        uint channels = (uint)track.Channels;
        switch (track.CodecId)
        {
            case "A_DTS" or "A_DTS/EXPRESS" or "A_DTS/LOSSLESS" when _windowsDts:
                // DTS's decoder wants one frame per sample; Matroska often laces several per block.
                _splitLaces.Add(track.Number);
                return new AudioEncodingProperties { Subtype = MFAudioFormat_DTS_HD.ToString("B"), SampleRate = rate, ChannelCount = channels };
            case "A_EAC3":
                return new AudioEncodingProperties { Subtype = MediaEncodingSubtypes.Eac3, SampleRate = rate, ChannelCount = channels };
            case "A_AC3":
                return new AudioEncodingProperties { Subtype = MediaEncodingSubtypes.Ac3, SampleRate = rate, ChannelCount = channels };
            case "A_AAC" when track.CodecPrivate is { } config:
                var aac = AudioEncodingProperties.CreateAac(rate, channels, 0);
                aac.SetFormatUserData(config);
                return aac;
            case "A_AAC":
                return AudioEncodingProperties.CreateAacAdts(rate, channels, 0);
            default:
                return null;
        }
    }

    /// <summary>
    /// Sets up FFmpeg decoding for a codec Windows can't decode, decoding the track's first packets
    /// to learn its real output format (rate, channel count and layout) before Media Foundation sees it.
    /// </summary>
    private AudioEncodingProperties? DecodedAudioProperties(MediaTrack track, string label)
    {
        string prefix = $"audio #{track.Number} [{track.DisplayLanguage}] {track.CodecId} '{label}'";
        if (!FfmpegLibraries.TryLoad(out string? error))
        {
            Description.Add($"{prefix} skipped: FFmpeg libraries unavailable ({error})");
            return null;
        }

        var decoder = new FfmpegAudioDecoder(track.CodecId, track.CodecPrivate, (int)Math.Round(track.SampleRate), track.Channels);
        foreach (var packet in _demuxer.FirstPackets(track.Number, 64))
        {
            if (packet.Frames().Any(frame => decoder.Decode(frame.Span).Length > 0))
            {
                break;
            }
        }

        decoder.Flush();
        if (decoder.Channels == 0)
        {
            Description.Add($"{prefix} skipped: FFmpeg {decoder.CodecName} produced no audio from the first packets");
            decoder.Dispose();
            return null;
        }

        var properties = AudioEncodingProperties.CreatePcm((uint)decoder.SampleRate, (uint)decoder.Channels, 32);
        properties.Subtype = MediaEncodingSubtypes.Float;
        if (decoder.ChannelMask != 0)
        {
            properties.Properties[MF_MT_AUDIO_CHANNEL_MASK] = (uint)decoder.ChannelMask;
        }

        _decoders[track.Number] = decoder;
        Description.Add($"{prefix} → decoded by Marquee (FFmpeg {decoder.CodecName}): {decoder.Channels}ch " +
                        $"{decoder.SampleRate} Hz float, mask 0x{decoder.ChannelMask:X}");
        return properties;
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        if (args.Request.StartPosition is TimeSpan position)
        {
            TimeSpan actual = _demuxer.Seek(position, _video.Number);
            foreach (var decoder in _decoders.Values)
            {
                lock (decoder)
                {
                    decoder.Flush();
                }
            }

            lock (_pendingFrames)
            {
                foreach (var queue in _pendingFrames.Values)
                {
                    queue.Clear();
                }
            }

            _lastAudioTicks = actual.Ticks;
            Seeked?.Invoke(actual);
            Trace?.Invoke($"start requested at {position:hh\\:mm\\:ss\\.fff} → keyframe {actual:hh\\:mm\\:ss\\.fff}");
            if (StartGate is { } gate)
            {
                var request = args.Request;
                var deferral = request.GetDeferral();
                _ = Task.Run(async () =>
                {
                    long held = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        await gate(actual).WaitAsync(TimeSpan.FromSeconds(1.5));
                    }
                    catch (TimeoutException)
                    {
                        Trace?.Invoke("start gate timed out after 1.5 s; starting the video anyway");
                    }

                    request.SetActualStartPosition(actual);
                    StartReleased?.Invoke();
                    deferral.Complete();
                    Trace?.Invoke($"video start held {System.Diagnostics.Stopwatch.GetElapsedTime(held).TotalMilliseconds:0} ms for the audio");
                });
                return;
            }

            args.Request.SetActualStartPosition(actual);
            StartReleased?.Invoke();
        }
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        try
        {
            var track = Resolve(args.Request.StreamDescriptor)
                        ?? throw new InvalidOperationException($"Unknown stream '{args.Request.StreamDescriptor.Name}'");
            if (track != _video)
            {
                _demuxer.SetActive(track.Number, true);
            }

            if (_decoders.TryGetValue(track.Number, out var decoder))
            {
                RequestDecodedSample(args, track, decoder);
                return;
            }

            if (_splitLaces.Contains(track.Number))
            {
                RequestSplitFrameSample(args, track);
                return;
            }

            if (_demuxer.Next(track.Number) is not { } packet)
            {
                Trace?.Invoke($"track {track.Number}: end of stream");
                return;
            }

            int count;
            lock (_sampleCounts)
            {
                count = _sampleCounts[track.Number] = _sampleCounts.GetValueOrDefault(track.Number) + 1;
            }

            if (count == 1 || count % 500 == 0)
            {
                Trace?.Invoke($"track {track.Number}: sample {count} @ {TimeSpan.FromTicks(packet.TimeTicks):hh\\:mm\\:ss\\.fff}" +
                              $"{(packet.KeyFrame ? " key" : "")}, {packet.Data.Length} B, thread {Environment.CurrentManagedThreadId}");
            }

            byte[] data = track == _video ? _converter.ToAnnexB(packet.Data, packet.KeyFrame) : packet.Data;
            var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), TimeSpan.FromTicks(packet.TimeTicks));
            sample.KeyFrame = packet.KeyFrame;
            if (packet.DurationTicks > 0)
            {
                sample.Duration = TimeSpan.FromTicks(packet.DurationTicks);
            }

            if (track != _video)
            {
                _lastAudioTicks = packet.TimeTicks;
            }

            args.Request.Sample = sample;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"{ex.GetType().Name}: {ex.Message}");
            sender.NotifyError(MediaStreamSourceErrorStatus.Other);
        }
    }

    /// <summary>
    /// Decodes packets until about 40 ms of PCM has accumulated and hands it over as one sample.
    /// TrueHD packets hold a single 40-sample access unit, so one sample per packet would mean
    /// over a thousand tiny samples a second.
    /// </summary>
    private void RequestDecodedSample(MediaStreamSourceSampleRequestedEventArgs args, MediaTrack track, FfmpegAudioDecoder decoder)
    {
        const int minimumFrames = 1920;
        var pcm = new MemoryStream();
        long? startTicks = null;
        int channels = 0;
        int rate = 0;
        int frames = 0;

        // TrueHD only starts decoding at a major sync, so right after a seek a few packets yield nothing.
        while (frames < minimumFrames && _demuxer.Next(track.Number) is { } packet)
        {
            lock (decoder)
            {
                foreach (var frame in packet.Frames())
                {
                    pcm.Write(decoder.Decode(frame.Span));
                }

                channels = decoder.Channels;
                rate = decoder.SampleRate;
            }

            if (pcm.Length > 0)
            {
                startTicks ??= packet.TimeTicks;
                frames = (int)(pcm.Length / (4 * channels));
            }
        }

        if (startTicks is not long start)
        {
            Trace?.Invoke($"track {track.Number}: end of stream");
            return;
        }

        byte[] data = pcm.ToArray();
        var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), TimeSpan.FromTicks(start));
        sample.KeyFrame = true;
        sample.Duration = TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / rate);
        _lastAudioTicks = start;

        int count;
        lock (_sampleCounts)
        {
            count = _sampleCounts[track.Number] = _sampleCounts.GetValueOrDefault(track.Number) + 1;
        }

        if (count == 1 || count % 250 == 0)
        {
            Trace?.Invoke($"track {track.Number}: decoded sample {count} @ {TimeSpan.FromTicks(start):hh\\:mm\\:ss\\.fff} + {sample.Duration.TotalMilliseconds:0.0} ms, " +
                          $"{frames} frames × {channels}ch, RMS {RmsDb(data):0.0} dBFS, {decoder.Errors} decode errors so far");
        }

        args.Request.Sample = sample;
    }

    /// <summary>Hands over laced frames one at a time, each with its own share of the block's duration.</summary>
    private void RequestSplitFrameSample(MediaStreamSourceSampleRequestedEventArgs args, MediaTrack track)
    {
        (long Ticks, long Duration, byte[] Data) next;
        lock (_pendingFrames)
        {
            var pending = _pendingFrames.TryGetValue(track.Number, out var queue) ? queue : _pendingFrames[track.Number] = new();
            if (pending.Count == 0)
            {
                if (_demuxer.Next(track.Number) is not { } packet)
                {
                    Trace?.Invoke($"track {track.Number}: end of stream");
                    return;
                }

                int count = packet.FrameSizes.Length;
                long frameDuration = count > 0 ? packet.DurationTicks / count : 0;
                int index = 0;
                foreach (var frame in packet.Frames())
                {
                    pending.Enqueue((packet.TimeTicks + index++ * frameDuration, frameDuration, frame.ToArray()));
                }
            }

            next = pending.Dequeue();
        }

        var (ticks, duration, data) = next;
        var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), TimeSpan.FromTicks(ticks));
        sample.KeyFrame = true;
        if (duration > 0)
        {
            sample.Duration = TimeSpan.FromTicks(duration);
        }

        _lastAudioTicks = ticks;
        args.Request.Sample = sample;
    }

    private static double RmsDb(byte[] pcm)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pcm);
        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }

        return samples.Length == 0 || sum == 0 ? double.NegativeInfinity : 10 * Math.Log10(sum / samples.Length);
    }

    private void OnSwitchStreamsRequested(MediaStreamSource sender, MediaStreamSourceSwitchStreamsRequestedEventArgs args)
    {
        if (Resolve(args.Request.OldStreamDescriptor) is { } oldTrack)
        {
            _demuxer.SetActive(oldTrack.Number, false);
        }

        if (Resolve(args.Request.NewStreamDescriptor) is { } newTrack)
        {
            _demuxer.SetActive(newTrack.Number, true);
            _demuxer.DropBefore(newTrack.Number, _lastAudioTicks);
            lock (_pendingFrames)
            {
                if (_pendingFrames.TryGetValue(newTrack.Number, out var queue))
                {
                    queue.Clear();
                }
            }

            if (_decoders.TryGetValue(newTrack.Number, out var decoder))
            {
                lock (decoder)
                {
                    decoder.Flush();
                }
            }
        }
    }

    // The projection may hand back a different wrapper for the same descriptor, so fall back to its Name.
    private MediaTrack? Resolve(IMediaStreamDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return null;
        }

        if (_tracksByDescriptor.TryGetValue(descriptor, out var track))
        {
            return track;
        }

        if (descriptor is VideoStreamDescriptor)
        {
            return _video;
        }

        return _tracksByDescriptor.FirstOrDefault(kv => kv.Key.Name == descriptor.Name).Value;
    }

    /// <summary>
    /// Matroska stores H.264/HEVC as length-prefixed NAL units with parameter sets in CodecPrivate;
    /// Media Foundation's decoders take Annex B start codes, with parameter sets before keyframes.
    /// MPEG-TS video is Annex B already, but goes through here too for the Dolby Vision rewrite.
    /// </summary>
    private sealed class NalConverter
    {
        private static readonly byte[] StartCode = [0, 0, 0, 1];
        private static readonly byte[] ShortStartCode = [0, 0, 1];

        /// <summary>The NAL unit length field's size in bytes, or 0 for Annex B input.</summary>
        private readonly int _lengthSize;
        private readonly bool _hevc;

        public byte[] ParameterSets { get; }
        public int ProfileId { get; }

        /// <summary>
        /// Rewrite Dolby Vision profile 7 into 8.1 on the fly: convert each RPU (NAL type 62) and
        /// drop the enhancement layer (NAL type 63). The base layer of profile 7 is HDR10 already.
        /// </summary>
        public bool ConvertProfile7 { get; set; }

        public Action<string>? Log { get; set; }

        private long _convertedRpus;
        private long _failedRpus;
        private long _droppedEnhancementUnits;

        private NalConverter(int lengthSize, byte[] parameterSets, int profileId, bool hevc)
        {
            _lengthSize = lengthSize;
            ParameterSets = parameterSets;
            ProfileId = profileId;
            _hevc = hevc;
        }

        public static NalConverter Create(MediaTrack track)
        {
            bool hevc = track.CodecId == "V_MPEGH/ISO/HEVC";
            byte[] config = track.CodecPrivate ?? [];
            if (config is [] or [0, 0, 1, ..] or [0, 0, 0, 1, ..])
            {
                return new NalConverter(0, config, AnnexBProfile(config, hevc), hevc);
            }

            var sets = new MemoryStream();
            if (hevc)
            {
                int pos = 23;
                for (int array = 0, arrays = config[22]; array < arrays; array++)
                {
                    int count = (config[pos + 1] << 8) | config[pos + 2];
                    pos += 3;
                    for (int i = 0; i < count; i++)
                    {
                        int length = (config[pos] << 8) | config[pos + 1];
                        sets.Write(StartCode);
                        sets.Write(config, pos + 2, length);
                        pos += 2 + length;
                    }
                }

                return new NalConverter((config[21] & 3) + 1, sets.ToArray(), config[1] & 0x1F, hevc: true);
            }

            int at = 6;
            int spsCount = config[5] & 0x1F;
            for (int i = 0; i < spsCount; i++)
            {
                int length = (config[at] << 8) | config[at + 1];
                sets.Write(StartCode);
                sets.Write(config, at + 2, length);
                at += 2 + length;
            }

            int ppsCount = config[at++];
            for (int i = 0; i < ppsCount; i++)
            {
                int length = (config[at] << 8) | config[at + 1];
                sets.Write(StartCode);
                sets.Write(config, at + 2, length);
                at += 2 + length;
            }

            return new NalConverter((config[4] & 3) + 1, sets.ToArray(), config[1], hevc: false);
        }

        /// <summary>general_profile_idc (HEVC) or profile_idc (H.264) from the SPS among Annex B parameter sets, else 0.</summary>
        private static int AnnexBProfile(byte[] parameterSets, bool hevc)
        {
            foreach (var unit in AnnexBUnits(parameterSets))
            {
                var nal = unit.Span;
                if (hevc && nal.Length > 3 && ((nal[0] >> 1) & 0x3F) == 33)
                {
                    return nal[3] & 0x1F;
                }

                if (!hevc && nal.Length > 1 && (nal[0] & 0x1F) == 7)
                {
                    return nal[1];
                }
            }

            return 0;
        }

        private IEnumerable<ReadOnlyMemory<byte>> Units(byte[] data) => _lengthSize == 0 ? AnnexBUnits(data) : LengthPrefixedUnits(data);

        private List<ReadOnlyMemory<byte>> LengthPrefixedUnits(byte[] data)
        {
            var units = new List<ReadOnlyMemory<byte>>();
            for (int pos = 0; pos + _lengthSize <= data.Length;)
            {
                int length = 0;
                for (int i = 0; i < _lengthSize; i++)
                {
                    length = (length << 8) | data[pos + i];
                }

                pos += _lengthSize;
                if (length <= 0 || pos + length > data.Length)
                {
                    break;
                }

                units.Add(data.AsMemory(pos, length));
                pos += length;
            }

            return units;
        }

        /// <summary>The NAL units between start codes, without the zero bytes that may pad before the next one.</summary>
        private static List<ReadOnlyMemory<byte>> AnnexBUnits(byte[] data)
        {
            var units = new List<ReadOnlyMemory<byte>>();
            int start = -1;
            int pos = 0;
            while (pos < data.Length)
            {
                int found = data.AsSpan(pos).IndexOf(ShortStartCode);
                int end = found < 0 ? data.Length : pos + found;
                if (start >= 0)
                {
                    int last = end;
                    while (last > start && data[last - 1] == 0)
                    {
                        last--;
                    }

                    if (last > start)
                    {
                        units.Add(data.AsMemory(start, last - start));
                    }
                }

                if (found < 0)
                {
                    break;
                }

                start = pos = end + ShortStartCode.Length;
            }

            return units;
        }

        public byte[] ToAnnexB(byte[] data, bool keyFrame)
        {
            var units = new List<ReadOnlyMemory<byte>>();
            bool hasParameterSets = false;
            foreach (var unit in Units(data))
            {
                byte header = unit.Span[0];
                int type = _hevc ? (header >> 1) & 0x3F : header & 0x1F;
                hasParameterSets |= _hevc ? type == 32 : type == 7;
                if (_hevc && ConvertProfile7 && type == 63)
                {
                    _droppedEnhancementUnits++;
                    continue;
                }

                units.Add(_hevc && ConvertProfile7 && type == 62 ? ConvertRpu(unit) : unit);
            }

            bool prependSets = keyFrame && !hasParameterSets;
            var output = new byte[(prependSets ? ParameterSets.Length : 0) + units.Sum(u => u.Length + StartCode.Length)];
            int at = 0;
            if (prependSets)
            {
                ParameterSets.CopyTo(output, 0);
                at = ParameterSets.Length;
            }

            foreach (var unit in units)
            {
                StartCode.CopyTo(output, at);
                unit.Span.CopyTo(output.AsSpan(at + StartCode.Length));
                at += StartCode.Length + unit.Length;
            }

            return output;
        }

        private ReadOnlyMemory<byte> ConvertRpu(ReadOnlyMemory<byte> rpu)
        {
            byte[]? converted = DoviConverter.ConvertTo81(rpu.Span, out string source);
            if (converted is null)
            {
                _failedRpus++;
                if (_failedRpus <= 3)
                {
                    Log?.Invoke($"Dolby Vision: couldn't convert an RPU ({rpu.Length} B); passing it through");
                }

                return rpu;
            }

            _convertedRpus++;
            if (_convertedRpus == 1 || _convertedRpus % 5000 == 0)
            {
                Log?.Invoke($"Dolby Vision: {_convertedRpus} RPUs converted (profile {source} → 8.1), " +
                            $"{_droppedEnhancementUnits} enhancement-layer NAL units dropped, {_failedRpus} failed");
            }

            return converted;
        }
    }
}
