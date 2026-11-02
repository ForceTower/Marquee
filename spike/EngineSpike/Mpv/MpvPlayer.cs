using System.Globalization;
using System.Runtime.InteropServices;
using static EngineSpike.Mpv.LibMpv;

namespace EngineSpike.Mpv;

internal sealed class MpvPlayer : IDisposable
{
    private const ulong FullScreenObserver = 1;

    private static readonly string[] InfoProperties =
    [
        "filename",
        "file-format",
        "current-vo",
        "current-gpu-context",
        "hwdec-current",
        "video-codec",
        "current-tracks/video/dolby-vision-profile",
        "current-tracks/video/dolby-vision-level",
        "video-params/w",
        "video-params/h",
        "video-params/pixelformat",
        "video-params/hw-pixelformat",
        "video-params/primaries",
        "video-params/gamma",
        "video-params/colormatrix",
        "video-params/max-luma",
        "video-target-params/primaries",
        "video-target-params/gamma",
        "video-target-params/max-luma",
        "container-fps",
        "display-fps",
        "current-tracks/audio/lang",
        "audio-codec-name",
        "current-tracks/audio/codec-profile",
        "audio-params/hr-channels",
        "audio-params/format",
        "audio-out-params/hr-channels",
        "audio-out-params/format",
        "current-ao",
        "audio-device",
    ];

    // Keys pressed while mpv's window has focus never reach XAML, so mpv forwards them back to us.
    private static readonly (string Key, string Command)[] KeyBindings =
    [
        ("F2", "script-message marquee swap"),
        ("F3", "script-message marquee next-audio"),
        ("F11", "cycle fullscreen"),
    ];

    private readonly IntPtr _ctx;
    private readonly Thread _eventThread;
    private volatile bool _disposing;

    public event Action<EventId>? StateChanged;
    public event Action<bool>? FullScreenChanged;
    public event Action<string>? Message;
    public event Action<string[]>? ClientMessage;

    public MpvPlayer(IntPtr hostWindow, string logFile, bool mute)
    {
        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero)
        {
            throw new InvalidOperationException("mpv_create failed");
        }

        // Loads %APPDATA%\mpv\mpv.conf so the spike matches the standalone mpv setup.
        SetOption("config", "yes");
        SetOption("wid", hostWindow.ToInt64().ToString());
        SetOption("log-file", logFile);
        SetOption("osc", "yes");
        SetOption("input-default-bindings", "yes");
        SetOption("input-vo-keyboard", "yes");
        SetOption("keep-open", "yes");
        if (mute)
        {
            SetOption("mute", "yes");
        }

        Check(mpv_initialize(_ctx), "mpv_initialize");
        mpv_observe_property(_ctx, FullScreenObserver, "fullscreen", Format.Flag);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _eventThread.Start();
    }

    public void BindKeys()
    {
        foreach (var (key, command) in KeyBindings)
        {
            TryCommand("keybind", key, command);
        }
    }

    public void Load(string path, TimeSpan? start = null)
    {
        if (start is { } s && s > TimeSpan.Zero)
        {
            string seconds = s.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            Command("loadfile", path, "replace", "-1", $"start={seconds}");
        }
        else
        {
            Command("loadfile", path);
        }
    }

    public void Stop() => Command("stop");

    public TimeSpan? Position =>
        double.TryParse(GetProperty("time-pos"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
            ? TimeSpan.FromSeconds(s)
            : null;

    public string? AudioLanguage => Language.Key(GetProperty("current-tracks/audio/lang"));

    public string AudioSummary
    {
        get
        {
            string lang = GetProperty("current-tracks/audio/lang") ?? "?";
            string codec = GetProperty("current-tracks/audio/codec-profile") ?? GetProperty("audio-codec-name") ?? "?";
            string channels = GetProperty("current-tracks/audio/demux-channels") ?? "";
            return $"{lang} {codec} {channels}".TrimEnd();
        }
    }

    public void SelectAudioLanguage(string language)
    {
        int count = int.TryParse(GetProperty("track-list/count"), out int n) ? n : 0;
        for (int i = 0; i < count; i++)
        {
            if (GetProperty($"track-list/{i}/type") == "audio" &&
                Language.Key(GetProperty($"track-list/{i}/lang")) == language &&
                GetProperty($"track-list/{i}/id") is { } id)
            {
                SetProperty("aid", id);
                return;
            }
        }
    }

    public void CycleAudio() => Command("cycle", "audio");

    public void ShowText(string text) => TryCommand("show-text", text, "2500");

    public void SetFullScreenFlag(bool on) => SetProperty("fullscreen", on ? "yes" : "no");

    public void SetProperty(string name, string value) =>
        Check(mpv_set_property_string(_ctx, name, value), $"property {name}");

    public string? GetProperty(string name)
    {
        IntPtr value = mpv_get_property_string(_ctx, name);
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            mpv_free(value);
        }
    }

    public IEnumerable<string> Describe() =>
        InfoProperties.Select(p => $"{p} = {GetProperty(p) ?? "—"}");

    public void Command(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }

            Check(mpv_command(_ctx, ptrs), args[0]);
        }
        finally
        {
            foreach (IntPtr p in ptrs)
            {
                if (p != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(p);
                }
            }
        }
    }

    private void TryCommand(params string[] args)
    {
        try
        {
            Command(args);
        }
        catch (InvalidOperationException ex)
        {
            Message?.Invoke(ex.Message);
        }
    }

    private void SetOption(string name, string value) =>
        Check(mpv_set_option_string(_ctx, name, value), $"option {name}");

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"mpv {what}: {ErrorString(result)}");
        }
    }

    private void EventLoop()
    {
        while (true)
        {
            var ev = Marshal.PtrToStructure<Event>(mpv_wait_event(_ctx, -1));
            switch (ev.EventId)
            {
                case EventId.Shutdown:
                    return;
                case EventId.None when _disposing:
                    return;
                case EventId.FileLoaded:
                case EventId.PlaybackRestart:
                case EventId.VideoReconfig:
                case EventId.AudioReconfig:
                    StateChanged?.Invoke(ev.EventId);
                    break;
                case EventId.EndFile:
                    var end = Marshal.PtrToStructure<EventEndFile>(ev.Data);
                    Message?.Invoke(end.Reason == 4
                        ? $"mpv end-file: error {ErrorString(end.Error)}"
                        : $"mpv end-file: reason {end.Reason}");
                    break;
                case EventId.ClientMessage:
                    var message = Marshal.PtrToStructure<EventClientMessage>(ev.Data);
                    var args = new string[message.NumArgs];
                    for (int i = 0; i < args.Length; i++)
                    {
                        args[i] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(message.Args, i * IntPtr.Size)) ?? "";
                    }
                    ClientMessage?.Invoke(args);
                    break;
                case EventId.PropertyChange when ev.ReplyUserdata == FullScreenObserver:
                    var prop = Marshal.PtrToStructure<EventProperty>(ev.Data);
                    if (prop.Format == Format.Flag && prop.Data != IntPtr.Zero)
                    {
                        FullScreenChanged?.Invoke(Marshal.ReadInt32(prop.Data) != 0);
                    }
                    break;
            }
        }
    }

    public void Dispose()
    {
        _disposing = true;
        mpv_wakeup(_ctx);
        _eventThread.Join(TimeSpan.FromSeconds(2));
        mpv_terminate_destroy(_ctx);
    }
}
