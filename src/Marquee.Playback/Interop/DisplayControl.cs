using System.Runtime.InteropServices;
using System.Text;

namespace Marquee.Playback.Interop;

/// <summary>A monitor: its GDI name (for mode changes) and its DisplayConfig target (for HDR).</summary>
internal sealed record DisplayTarget(string GdiName, string FriendlyName, uint AdapterLow, int AdapterHigh, uint TargetId);

/// <summary>A display mode. <see cref="Frequency"/> is Windows' integer rate (23 means 23.976 Hz); <see cref="Hz"/> the actual one.</summary>
internal readonly record struct DisplayMode(int Width, int Height, int BitsPerPixel, int Frequency, double Hz);

internal readonly record struct HdrState(bool Supported, bool Enabled);

/// <summary>
/// Reads and switches a monitor's HDR state and refresh rate, as a normal user.
///
/// HDR goes through DisplayConfig: Windows 11 24H2's HDR state (device info 15/16) where it
/// exists, else the older advanced color state (9/10). Refresh rates go through the legacy mode
/// API, as Kodi does: modes are listed with integer rates, where a rate one below a multiple of 6
/// (23, 29, 59, 119…) is the NTSC variant (23.976, 29.97, 59.94, 119.88 Hz). Changes are made
/// with CDS_FULLSCREEN, so nothing is saved, restoring means returning to the saved mode, and
/// Windows reverts the mode itself if the process dies. HDR changes stay until undone.
/// </summary>
internal static unsafe class DisplayControl
{
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int GetAdvancedColorInfo = 9;
    private const int SetAdvancedColorState = 10;
    private const int GetAdvancedColorInfo2 = 15;
    private const int SetHdrState = 16;
    private const uint QdcOnlyActivePaths = 2;
    private const int EnumCurrentSettings = -1;
    private const uint CdsFullscreen = 4;
    private const uint DmBitsPerPel = 0x40000, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;

    /// <summary>Two rates match when one is a whole multiple of the other to within this fraction (0.05%).</summary>
    private const double Tolerance = 0.0005;

    /// <summary>The monitor showing most of <paramref name="hwnd"/>.</summary>
    public static DisplayTarget? ForWindow(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, 2);
        var info = new MONITORINFOEX { Size = sizeof(MONITORINFOEX) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, &info))
        {
            return null;
        }

        string gdi = new string(info.Device).TrimEnd('\0');
        return Targets().FirstOrDefault(t => string.Equals(t.GdiName, gdi, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every active monitor.</summary>
    public static List<DisplayTarget> Targets()
    {
        var targets = new List<DisplayTarget>();
        foreach (var path in ActivePaths())
        {
            var source = new SOURCE_DEVICE_NAME { Header = Header(GetSourceName, sizeof(SOURCE_DEVICE_NAME), path.SourceAdapter, path.SourceId) };
            var target = new TARGET_DEVICE_NAME { Header = Header(GetTargetName, sizeof(TARGET_DEVICE_NAME), path.TargetAdapter, path.TargetId) };
            if (DisplayConfigGetDeviceInfo(&source.Header) != 0)
            {
                continue;
            }

            DisplayConfigGetDeviceInfo(&target.Header);
            targets.Add(new DisplayTarget(new string(source.GdiName).TrimEnd('\0'), new string(target.FriendlyName).TrimEnd('\0'),
                path.TargetAdapter.Low, path.TargetAdapter.High, path.TargetId));
        }

        return targets;
    }

    public static HdrState? Hdr(DisplayTarget target)
    {
        var info2 = new ADVANCED_COLOR_INFO_2 { Header = Header(GetAdvancedColorInfo2, sizeof(ADVANCED_COLOR_INFO_2), target) };
        if (DisplayConfigGetDeviceInfo(&info2.Header) == 0)
        {
            return new HdrState(Supported: (info2.Value >> 4 & 1) == 1, Enabled: (info2.Value >> 5 & 1) == 1);
        }

        var info = new ADVANCED_COLOR_INFO { Header = Header(GetAdvancedColorInfo, sizeof(ADVANCED_COLOR_INFO), target) };
        return DisplayConfigGetDeviceInfo(&info.Header) == 0
            ? new HdrState(Supported: (info.Value & 1) == 1, Enabled: (info.Value >> 1 & 1) == 1)
            : null;
    }

    public static bool SetHdr(DisplayTarget target, bool enabled)
    {
        var state = new SET_STATE { Header = Header(SetHdrState, sizeof(SET_STATE), target), Value = enabled ? 1u : 0u };
        if (DisplayConfigSetDeviceInfo(&state.Header) == 0)
        {
            return true;
        }

        state.Header = Header(SetAdvancedColorState, sizeof(SET_STATE), target);
        return DisplayConfigSetDeviceInfo(&state.Header) == 0;
    }

    /// <summary>The current mode, with its exact rate from DisplayConfig (e.g. 239.991 for Windows' "240").</summary>
    public static DisplayMode? CurrentMode(DisplayTarget target)
    {
        var mode = NewDevMode();
        if (!EnumDisplaySettingsEx(target.GdiName, EnumCurrentSettings, ref mode, 0))
        {
            return null;
        }

        var path = ActivePaths().FirstOrDefault(p => p.TargetAdapter.Low == target.AdapterLow && p.TargetAdapter.High == target.AdapterHigh && p.TargetId == target.TargetId);
        double hz = path.RefreshDenominator > 0 ? path.RefreshNumerator / (double)path.RefreshDenominator : Nominal((int)mode.DisplayFrequency);
        return new DisplayMode((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.BitsPerPel, (int)mode.DisplayFrequency, hz);
    }

    /// <summary>The integer rates offered at <paramref name="mode"/>'s resolution and depth.</summary>
    public static SortedSet<int> Frequencies(DisplayTarget target, DisplayMode mode)
    {
        var rates = new SortedSet<int>();
        for (int i = 0; ; i++)
        {
            var candidate = NewDevMode();
            if (!EnumDisplaySettingsEx(target.GdiName, i, ref candidate, 0))
            {
                break;
            }

            if (candidate.PelsWidth == mode.Width && candidate.PelsHeight == mode.Height && candidate.BitsPerPel == mode.BitsPerPixel && candidate.DisplayFrequency > 1)
            {
                rates.Add((int)candidate.DisplayFrequency);
            }
        }

        return rates;
    }

    /// <summary>The actual rate behind one of Windows' integer rates: 23 → 23.976, 119 → 119.88, 120 → 120.</summary>
    public static double Nominal(int frequency) =>
        frequency >= 23 && (frequency + 1) % 6 == 0 ? (frequency + 1) * 1000.0 / 1001 : frequency;

    /// <summary>Whether <paramref name="hz"/> shows every frame of <paramref name="fps"/> video for the same number of refreshes.</summary>
    public static bool Fits(double hz, double fps)
    {
        double multiple = Math.Round(hz / fps);
        return multiple >= 1 && Math.Abs(hz - multiple * fps) <= hz * Tolerance;
    }

    /// <summary>
    /// The rate to switch to for <paramref name="fps"/> video, or null to stay: the highest offered
    /// rate that's a whole multiple of the frame rate, unless the current one already is.
    /// </summary>
    public static int? PickFrequency(double fps, DisplayMode current, IEnumerable<int> offered)
    {
        if (fps <= 0 || Fits(current.Hz, fps))
        {
            return null;
        }

        return offered.Where(f => Fits(Nominal(f), fps)).OrderByDescending(Nominal).Select(f => (int?)f).FirstOrDefault();
    }

    /// <summary>Switches to <paramref name="frequency"/> at the current resolution, without saving it. Returns a DISP_CHANGE code (0 = done).</summary>
    public static int SetFrequency(DisplayTarget target, DisplayMode current, int frequency)
    {
        var mode = NewDevMode();
        mode.PelsWidth = (uint)current.Width;
        mode.PelsHeight = (uint)current.Height;
        mode.BitsPerPel = (uint)current.BitsPerPixel;
        mode.DisplayFrequency = (uint)frequency;
        mode.Fields = DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency;
        return ChangeDisplaySettingsEx(target.GdiName, ref mode, IntPtr.Zero, CdsFullscreen, IntPtr.Zero);
    }

    /// <summary>Returns the monitor to its saved mode, undoing <see cref="SetFrequency"/>.</summary>
    public static int RestoreMode(DisplayTarget target) =>
        ChangeDisplaySettingsEx(target.GdiName, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    /// <summary>Every monitor's HDR state, mode and offered rates, and the rates common frame rates would get. Changes nothing.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        foreach (var target in Targets())
        {
            sb.AppendLine($"{target.GdiName} '{target.FriendlyName}'");
            sb.AppendLine($"  HDR: {Hdr(target)?.ToString() ?? "unknown"}");
            if (CurrentMode(target) is not { } mode)
            {
                continue;
            }

            var offered = Frequencies(target, mode);
            sb.AppendLine($"  mode: {mode.Width}x{mode.Height} {mode.BitsPerPixel} bpp, {mode.Frequency} ({mode.Hz:0.000} Hz)");
            sb.AppendLine($"  offered: {string.Join(", ", offered.Select(f => $"{f} ({Nominal(f):0.###})"))}");
            foreach (double fps in new[] { 23.976, 24, 25, 29.97, 30, 50, 59.94, 60 })
            {
                int? pick = PickFrequency(fps, mode, offered);
                sb.AppendLine($"  {fps} fps → {(pick is int f ? $"{f} ({Nominal(f):0.###} Hz)" : "stay")}");
            }
        }

        return sb.ToString();
    }

    private static List<PATH_INFO> ActivePaths()
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
        {
            return [];
        }

        var paths = new PATH_INFO[pathCount];
        var modes = new byte[modeCount * 64];
        fixed (PATH_INFO* p = paths)
        fixed (byte* m = modes)
        {
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, IntPtr.Zero) != 0)
            {
                return [];
            }
        }

        return paths.Take((int)pathCount).ToList();
    }

    private static DEVICE_INFO_HEADER Header(int type, int size, DisplayTarget target) =>
        Header(type, size, new LUID { Low = target.AdapterLow, High = target.AdapterHigh }, target.TargetId);

    private static DEVICE_INFO_HEADER Header(int type, int size, LUID adapter, uint id) =>
        new() { Type = type, Size = size, Adapter = adapter, Id = id };

    private static DEVMODE NewDevMode() => new() { Size = (ushort)sizeof(DEVMODE) };

    // wingdi.h / winuser.h layouts (Windows SDK 10.0.26100).
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO
    {
        public LUID SourceAdapter;
        public uint SourceId, SourceModeIndex, SourceStatus;
        public LUID TargetAdapter;
        public uint TargetId, TargetModeIndex, OutputTechnology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatus, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER
    {
        public int Type, Size;
        public LUID Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER Header;
        public fixed char GdiName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER Header;
        public uint Flags, OutputTechnology;
        public ushort ManufacturerId, ProductCode;
        public uint ConnectorInstance;
        public fixed char FriendlyName[64];
        public fixed char DevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADVANCED_COLOR_INFO
    {
        public DEVICE_INFO_HEADER Header;
        public uint Value, ColorEncoding, BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADVANCED_COLOR_INFO_2
    {
        public DEVICE_INFO_HEADER Header;
        public uint Value, ColorEncoding, BitsPerColorChannel, ActiveColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SET_STATE
    {
        public DEVICE_INFO_HEADER Header;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFOEX
    {
        public int Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        public fixed char Device[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, PATH_INFO* paths, ref uint modeCount, byte* modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(DEVICE_INFO_HEADER* request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(DEVICE_INFO_HEADER* request);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    // By pointer: passed by ref, the fixed char buffer would be marshalled as ANSI.
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, MONITORINFOEX* info);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string device, int modeIndex, ref DEVMODE mode, uint flags);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE mode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, IntPtr mode, IntPtr hwnd, uint flags, IntPtr param);
}
