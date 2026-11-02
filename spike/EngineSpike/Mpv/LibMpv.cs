using System.Runtime.InteropServices;

namespace EngineSpike.Mpv;

internal static class LibMpv
{
    private const string Dll = "libmpv-2.dll";

    public enum EventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        ClientMessage = 16,
        VideoReconfig = 17,
        AudioReconfig = 18,
        PlaybackRestart = 21,
        PropertyChange = 22,
    }

    public enum Format
    {
        None = 0,
        String = 1,
        Flag = 3,
        Int64 = 4,
        Double = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public EventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name;
        public Format Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventEndFile
    {
        public int Reason;
        public int Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventClientMessage
    {
        public int NumArgs;
        public IntPtr Args;
    }

    [DllImport(Dll)]
    public static extern IntPtr mpv_create();

    [DllImport(Dll)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Dll)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Dll)]
    public static extern int mpv_set_option_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Dll)]
    public static extern int mpv_set_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Dll)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, Format format);

    [DllImport(Dll)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Dll)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Dll)]
    public static extern void mpv_wakeup(IntPtr ctx);

    [DllImport(Dll)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Dll)]
    public static extern IntPtr mpv_error_string(int error);

    public static string ErrorString(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? error.ToString();
}
