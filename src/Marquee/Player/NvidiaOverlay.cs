using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Marquee.Shell;

namespace Marquee.Player;

/// <summary>
/// NVIDIA's overlay (the NVIDIA app) hooks every Direct3D process and takes full-screen video for a
/// game, popping up "Press Alt+Z to open the NVIDIA Overlay". The driver's application profiles
/// have a flag that keeps the overlay's capture hook out of an executable ("NVIDIA App Overlay -
/// Flags", 0x10000000), set here on a "Marquee" profile through NvAPI's DRS functions. Writing that
/// setting needs administrator rights, so Marquee runs itself elevated once with
/// <see cref="HelperArgument"/>; reading it doesn't.
/// </summary>
internal static class NvidiaOverlay
{
    public const string HelperArgument = "--nvidia-overlay-off";

    private const string ProfileName = "Marquee";
    private const uint OverlayFlagsSetting = 0x809D5F60;
    private const uint OverlayHookOff = 0x10000000;

    // nvapi_interface.h
    private const uint Initialize = 0x0150e828;
    private const uint CreateSession = 0x0694d52e;
    private const uint DestroySession = 0xdad9cff8;
    private const uint LoadSettings = 0x375dbd6b;
    private const uint SaveSettings = 0xfcbc7e14;
    private const uint FindApplicationByName = 0xeee566b2;
    private const uint FindProfileByName = 0x7e4a9a0b;
    private const uint CreateProfile = 0xcc176068;
    private const uint CreateApplication = 0x4347a9de;
    private const uint GetSetting = 0x73bf8338;
    private const uint SetSetting = 0x577dd202;

    private const int Ok = 0;
    private const int Error = -1;
    private const int ExecutableNotFound = -166;

    // NVDRS_PROFILE_V1, NVDRS_APPLICATION_V4 and NVDRS_SETTING_V1 (pack 4); strings are UTF-16[2048].
    private const int StringBytes = 4096;
    private const int ProfileSize = 4116;
    private const int ProfileNameOffset = 4;
    private const int ApplicationSize = 20492;
    private const int ApplicationNameOffset = 8;
    private const int SettingSize = 12320;
    private const int SettingIdOffset = 4100;
    private const int SettingCurrentValueOffset = 8220;

    private static QueryInterface? s_query;

    private static string ExeName => Path.GetFileName(Environment.ProcessPath) ?? "Marquee.exe";

    /// <summary>Whether the overlay's hook is kept out of Marquee; null without an NVIDIA driver.</summary>
    public static bool? IsHookDisabled()
    {
        if (!Load() || OpenSession(out IntPtr session) != Ok)
        {
            return null;
        }

        try
        {
            using var name = Text(ExeName);
            using var application = Struct(ApplicationSize, 4);
            using var profile = new Pinned(IntPtr.Size);
            if (Call<Fn4>(FindApplicationByName)(session, name.Address, profile.Address, application.Address) != Ok)
            {
                return false;
            }

            return TryGetFlags(session, profile.ReadPointer(), out uint flags) && (flags & OverlayHookOff) != 0;
        }
        finally
        {
            Call<Fn1>(DestroySession)(session);
        }
    }

    /// <summary>Sets the flag on Marquee's profile, creating it if needed. Returns the NvAPI status.</summary>
    public static int DisableHook()
    {
        if (!Load())
        {
            return Error;
        }

        int status = OpenSession(out IntPtr session);
        if (status != Ok)
        {
            return status;
        }

        try
        {
            using var name = Text(ExeName);
            using var application = Struct(ApplicationSize, 4);
            using var profile = new Pinned(IntPtr.Size);
            status = Call<Fn4>(FindApplicationByName)(session, name.Address, profile.Address, application.Address);
            if (status == ExecutableNotFound)
            {
                using var profileName = Text(ProfileName);
                if (Call<Fn3>(FindProfileByName)(session, profileName.Address, profile.Address) != Ok)
                {
                    using var info = Struct(ProfileSize, 1);
                    info.WriteString(ProfileNameOffset, ProfileName);
                    if ((status = Call<Fn3>(CreateProfile)(session, info.Address, profile.Address)) != Ok)
                    {
                        return status;
                    }
                }

                using var newApplication = Struct(ApplicationSize, 4);
                newApplication.WriteString(ApplicationNameOffset, ExeName);
                status = Call<Fn3>(CreateApplication)(session, profile.ReadPointer(), newApplication.Address);
            }

            if (status != Ok)
            {
                return status;
            }

            uint flags = TryGetFlags(session, profile.ReadPointer(), out uint current) ? current : 0;
            using var setting = Struct(SettingSize, 1);
            setting.WriteUInt32(SettingIdOffset, OverlayFlagsSetting);
            setting.WriteUInt32(SettingCurrentValueOffset, flags | OverlayHookOff);
            if ((status = Call<Fn3>(SetSetting)(session, profile.ReadPointer(), setting.Address)) != Ok)
            {
                return status;
            }

            return Call<Fn1>(SaveSettings)(session);
        }
        finally
        {
            Call<Fn1>(DestroySession)(session);
        }
    }

    /// <summary>Runs Marquee elevated with <see cref="HelperArgument"/>; false if declined or failed.</summary>
    public static async Task<bool> DisableHookElevatedAsync()
    {
        try
        {
            using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, HelperArgument)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (helper is null)
            {
                return false;
            }

            await helper.WaitForExitAsync();
            Log.Info($"NVIDIA overlay helper exited with NvAPI status {helper.ExitCode}");
            return helper.ExitCode == Ok;
        }
        catch (Win32Exception ex)
        {
            Log.Info($"NVIDIA overlay helper not run: {ex.Message}");
            return false;
        }
    }

    private static bool Load()
    {
        if (s_query is not null)
        {
            return true;
        }

        if (!NativeLibrary.TryLoad("nvapi64.dll", out IntPtr library) ||
            !NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out IntPtr query))
        {
            return false;
        }

        s_query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(query);
        return Call<Fn0>(Initialize)() == Ok;
    }

    private static int OpenSession(out IntPtr session)
    {
        using var handle = new Pinned(IntPtr.Size);
        int status = Call<Fn1>(CreateSession)(handle.Address);
        session = handle.ReadPointer();
        if (status != Ok)
        {
            return status;
        }

        status = Call<Fn1>(LoadSettings)(session);
        if (status != Ok)
        {
            Call<Fn1>(DestroySession)(session);
        }

        return status;
    }

    private static bool TryGetFlags(IntPtr session, IntPtr profile, out uint flags)
    {
        using var setting = Struct(SettingSize, 1);
        bool found = Call<FnGetSetting>(GetSetting)(session, profile, OverlayFlagsSetting, setting.Address) == Ok;
        flags = found ? setting.ReadUInt32(SettingCurrentValueOffset) : 0;
        return found;
    }

    private static T Call<T>(uint id) where T : Delegate
    {
        IntPtr function = s_query!(id);
        return function == IntPtr.Zero
            ? throw new EntryPointNotFoundException($"NvAPI function 0x{id:x8} isn't available")
            : Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static Pinned Text(string value)
    {
        var buffer = new Pinned(StringBytes);
        buffer.WriteString(0, value);
        return buffer;
    }

    /// <summary>An NvAPI struct, zeroed, with its version field (size | version &lt;&lt; 16) set.</summary>
    private static Pinned Struct(int size, int version)
    {
        var buffer = new Pinned(size);
        buffer.WriteUInt32(0, (uint)(size | (version << 16)));
        return buffer;
    }

    private sealed class Pinned : IDisposable
    {
        private readonly byte[] _bytes;
        private GCHandle _handle;

        public Pinned(int size)
        {
            _bytes = new byte[size];
            _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
        }

        public IntPtr Address => _handle.AddrOfPinnedObject();

        public void WriteString(int offset, string value) => Encoding.Unicode.GetBytes(value, 0, value.Length, _bytes, offset);

        public void WriteUInt32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(offset), value);

        public uint ReadUInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset));

        public IntPtr ReadPointer() => (IntPtr)BinaryPrimitives.ReadInt64LittleEndian(_bytes);

        public void Dispose() => _handle.Free();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Fn0();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Fn1(IntPtr a);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Fn3(IntPtr a, IntPtr b, IntPtr c);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Fn4(IntPtr a, IntPtr b, IntPtr c, IntPtr d);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnGetSetting(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);
}
