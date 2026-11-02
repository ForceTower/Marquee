using System.Runtime.InteropServices;

namespace Marquee.Playback.Matroska;

/// <summary>
/// Per-frame Dolby Vision RPU conversion to profile 8.1 through native/dovi-ffi (dovi_tool's
/// dolby_vision crate, mode 2: mapping set to no-op), so profile 7 plays through Windows' Dolby
/// Vision Extensions, which only handle profiles 5 and 8.
/// </summary>
internal static unsafe class DoviConverter
{
    private const string Dll = "marquee_dovi.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct MqDoviInfo
    {
        public byte Profile;
        public byte ElType;
    }

    private static readonly Lazy<bool> s_available = new(() => NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Dll), out _));

    public static bool Available => s_available.Value;

    /// <summary>
    /// Converts one UNSPEC62 NAL unit (with its 0x7C01 header) to profile 8.1. Returns the new NAL
    /// unit, or null if it couldn't be parsed or converted. <paramref name="source"/> describes the
    /// RPU before conversion, e.g. "7 FEL".
    /// </summary>
    public static byte[]? ConvertTo81(ReadOnlySpan<byte> nalUnit, out string source)
    {
        var output = new byte[nalUnit.Length + 1024];
        MqDoviInfo info = default;
        nint written;
        fixed (byte* input = nalUnit)
        fixed (byte* destination = output)
        {
            written = mq_dovi_convert_to_81(input, (nuint)nalUnit.Length, destination, (nuint)output.Length, &info);
        }

        string el = info.ElType switch { 1 => " MEL", 2 => " FEL", _ => "" };
        source = $"{info.Profile}{el}";
        return written > 0 ? output.AsSpan(0, (int)written).ToArray() : null;
    }

    [DllImport(Dll)]
    private static extern nint mq_dovi_convert_to_81(byte* input, nuint length, byte* output, nuint capacity, MqDoviInfo* info);
}
