using System.Text.Json;
using Marquee.Data;
using Marquee.Playback.Interop;
using Marquee.Playback.Matroska;
using Marquee.Shell;

namespace Marquee.Player;

/// <summary>
/// Matches the monitor the player is on to what's playing: Windows HDR on for HDR video (and, if
/// asked, off for SDR), and a refresh rate that's a whole multiple of the frame rate. It remembers
/// what it changed and puts it back when playback ends. A marker in the library database covers a
/// crash in between: the next start puts the display back.
/// </summary>
internal sealed class DisplayMatcher
{
    private const string MarkerKey = "display-restore";

    private sealed record Marker(string GdiName, uint AdapterLow, int AdapterHigh, uint TargetId, bool? Hdr, bool Mode);

    private readonly object _gate = new();
    private Task _work = Task.CompletedTask;
    private DisplayTarget? _target;
    private bool? _originalHdr;
    private bool _modeChanged;

    /// <summary>A line for the statistics overlay.</summary>
    public string? Status { get; private set; }

    /// <summary>Whether the video is HDR (or Dolby Vision) and its frame rate (0 if unknown), from the library or the file.</summary>
    public static async Task<(bool Hdr, double FrameRate)> DescribeVideoAsync(PlaybackRequest request, bool needFrameRate)
    {
        var video = request.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        bool hdr = video?.DoviProfile is not null || video?.ColorTransfer is "smpte2084" or "arib-std-b67";
        if ((!needFrameRate && video is not null) || !MatroskaMediaSource.Handles(request.Location))
        {
            return (hdr, 0);
        }

        try
        {
            var track = await Task.Run(() =>
            {
                using var demuxer = MatroskaDemuxer.Open(request.Location, 64 * 1024);
                return demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
            });
            if (track is not null)
            {
                hdr |= track.DolbyVision is not null || track.Colour?.Transfer is 16 or 18;
                return (hdr, track.DefaultDurationNs > 0 ? 1e9 / track.DefaultDurationNs : 0);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Display matching: couldn't read the video's frame rate", ex);
        }

        return (hdr, 0);
    }

    /// <summary>Sets the window's monitor up for the video. Waits for any restore still running first.</summary>
    public Task ApplyAsync(IntPtr hwnd, bool hdrVideo, double frameRate, AppSettings settings) =>
        Queue(() => Apply(hwnd, hdrVideo, frameRate, settings));

    /// <summary>Puts back whatever was changed, in the background.</summary>
    public void Restore() => Queue(RestoreNow);

    /// <summary>Waits for queued work, e.g. before the app exits.</summary>
    public void WaitForIdle()
    {
        Task work;
        lock (_gate)
        {
            work = _work;
        }

        work.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>Undoes the changes of a session that ended without restoring them (a crash).</summary>
    public static void RestoreLeftovers()
    {
        try
        {
            if (App.Services.Db.GetMeta(MarkerKey) is not { } json || JsonSerializer.Deserialize<Marker>(json) is not { } marker)
            {
                return;
            }

            var target = DisplayControl.Targets().FirstOrDefault(t => t.GdiName == marker.GdiName)
                         ?? new DisplayTarget(marker.GdiName, "", marker.AdapterLow, marker.AdapterHigh, marker.TargetId);
            if (marker.Mode)
            {
                DisplayControl.RestoreMode(target);
            }

            if (marker.Hdr is bool hdr)
            {
                DisplayControl.SetHdr(target, hdr);
            }

            Log.Info($"Display matching: restored {target.GdiName} after an unfinished session (HDR {marker.Hdr?.ToString() ?? "untouched"}, mode {(marker.Mode ? "restored" : "untouched")})");
        }
        catch (Exception ex)
        {
            Log.Error("Display matching: couldn't restore a previous session's display changes", ex);
        }
        finally
        {
            App.Services.Db.SetMeta(MarkerKey, null);
        }
    }

    private Task Queue(Action action)
    {
        lock (_gate)
        {
            _work = _work.ContinueWith(_ => Log.Guard("Display matching", action), TaskScheduler.Default);
            return _work;
        }
    }

    private void Apply(IntPtr hwnd, bool hdrVideo, double frameRate, AppSettings settings)
    {
        var target = DisplayControl.ForWindow(hwnd);
        if (target is null)
        {
            Status = "Display: couldn't find the window's monitor";
            Log.Info(Status);
            return;
        }

        if (_target is not null && _target != target)
        {
            RestoreNow();
        }

        _target = target;
        var notes = new List<string>();
        var hdr = DisplayControl.Hdr(target);
        if (settings.MatchDynamicRange != DynamicRangeMatching.Off && hdr is { Supported: true } state)
        {
            bool original = _originalHdr ?? state.Enabled;
            bool wanted = hdrVideo || (settings.MatchDynamicRange == DynamicRangeMatching.HdrVideo && original);
            if (wanted != state.Enabled)
            {
                _originalHdr ??= state.Enabled;
                SaveMarker();
                bool done = DisplayControl.SetHdr(target, wanted);
                notes.Add(done ? $"HDR switched {(wanted ? "on" : "off")}" : "couldn't switch HDR");
                hdr = DisplayControl.Hdr(target);
            }
        }

        var mode = DisplayControl.CurrentMode(target);
        if (settings.MatchFrameRate && frameRate > 0 && mode is { } current)
        {
            var offered = DisplayControl.Frequencies(target, current);
            if (DisplayControl.PickFrequency(frameRate, current, offered) is int frequency)
            {
                _modeChanged = true;
                SaveMarker();
                int result = DisplayControl.SetFrequency(target, current, frequency);
                notes.Add(result == 0
                    ? Invariant($"switched from {current.Hz:0.###} Hz for {frameRate:0.###} fps")
                    : Invariant($"couldn't switch to {DisplayControl.Nominal(frequency):0.###} Hz (error {result})"));
                mode = DisplayControl.CurrentMode(target);
            }
            else
            {
                notes.Add(Invariant($"{frameRate:0.###} fps fits"));
            }
        }

        string hdrText = hdr is { Enabled: true } ? "on" : hdr is null ? "unknown" : "off";
        Status = Invariant($"Display: {target.FriendlyName}{(mode is { } m ? Invariant($" {m.Width}x{m.Height} @ {m.Hz:0.###} Hz") : "")}, HDR {hdrText}") +
                 (notes.Count > 0 ? $" · {string.Join(", ", notes)}" : "");
        Log.Info(Status);
    }

    private void RestoreNow()
    {
        if (_target is not { } target)
        {
            return;
        }

        if (_modeChanged)
        {
            int result = DisplayControl.RestoreMode(target);
            Log.Info($"Display matching: {target.FriendlyName} back to its saved mode{(result == 0 ? "" : $" failed (error {result})")}");
        }

        if (_originalHdr is bool hdr)
        {
            bool done = DisplayControl.SetHdr(target, hdr);
            Log.Info($"Display matching: {target.FriendlyName} HDR back {(hdr ? "on" : "off")}{(done ? "" : " failed")}");
        }

        _target = null;
        _originalHdr = null;
        _modeChanged = false;
        Status = null;
        App.Services.Db.SetMeta(MarkerKey, null);
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private void SaveMarker()
    {
        if (_target is { } t)
        {
            App.Services.Db.SetMeta(MarkerKey, JsonSerializer.Serialize(new Marker(t.GdiName, t.AdapterLow, t.AdapterHigh, t.TargetId, _originalHdr, _modeChanged)));
        }
    }
}
