using System.Security.Cryptography;
using System.Text;

namespace Marquee.Data;

/// <summary>Plex tokens are stored encrypted with DPAPI, readable only by this Windows user.</summary>
internal static class Secrets
{
    private static readonly byte[] Entropy = "Marquee.PlexToken"u8.ToArray();

    public static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
