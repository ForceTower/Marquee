using System.Runtime.InteropServices;
using Windows.Management.Deployment;

namespace EngineSpike.Interop;

/// <summary>
/// Store-packaged codecs such as DTS Sound Unbound's DTSXDecoder link against the "*_app" C++
/// runtime DLLs in the Microsoft.VCLibs.140.00 framework package. Packaged apps get that package on
/// their DLL path; an unpackaged process doesn't, so activating the codec fails with
/// ERROR_MOD_NOT_FOUND. Once a DLL is loaded, later loads by the same name resolve to it, so loading
/// the runtime up front by full path lets those codecs load.
/// </summary>
internal static class StoreCodecRuntime
{
    private const string FamilyName = "Microsoft.VCLibs.140.00_8wekyb3d8bbwe";

    private static readonly string[] Libraries =
    [
        "vcruntime140_app.dll",
        "vcruntime140_1_app.dll",
        "msvcp140_app.dll",
        "vccorlib140_app.dll",
    ];

    public static bool Loaded { get; private set; }

    /// <summary>True when DTS Sound Unbound's Media Foundation decoder (DTSXDecoder) is installed.</summary>
    public static bool HasDtsDecoder =>
        new PackageManager().FindPackagesForUser(string.Empty, "DTSInc.DTSSoundUnbound_t5j2fzbtdg37r").Any();

    public static string Preload()
    {
        try
        {
            var package = new PackageManager()
                .FindPackagesForUser(string.Empty, FamilyName)
                .Where(p => p.Id.Architecture == Windows.System.ProcessorArchitecture.X64)
                .OrderByDescending(p => new Version(p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision))
                .FirstOrDefault();
            if (package is null)
            {
                return $"{FamilyName} (x64) isn't installed";
            }

            string folder = package.InstalledLocation.Path;
            var failed = Libraries.Where(name => !NativeLibrary.TryLoad(Path.Combine(folder, name), out _)).ToList();
            Loaded = failed.Count == 0;
            return failed.Count == 0
                ? $"preloaded {Libraries.Length} runtime DLLs from {package.Id.FullName}"
                : $"couldn't load {string.Join(", ", failed)} from {folder}";
        }
        catch (Exception ex)
        {
            return $"failed: {ex.Message}";
        }
    }
}
