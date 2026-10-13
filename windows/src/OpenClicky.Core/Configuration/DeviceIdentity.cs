// Ports DeviceIdentity.swift: one stable, anonymous id for this PC, so the backend can give each
// machine one starter allowance however often the app is reinstalled. A salted SHA-256 of the
// machine's id; the id itself never leaves the PC. The Mac hashed the hardware UUID; Windows
// hashes HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid (stable across app reinstalls, made at
// OS install). The Windows side may swap in another source through `PlatformUuidProvider`.

using System.Security.Cryptography;
using System.Text;

namespace OpenClicky.Core.Configuration;

public static class DeviceIdentity
{
    public static string Hash(string platformUuid)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"openclicky-device-v1:{platformUuid}"));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>Where the machine id comes from; replaceable by the exe or a test.</summary>
    public static Func<string?> PlatformUuidProvider { get; set; } = ReadMachineGuid;

    /// <summary>The hashed id, or null when the machine has none to read.</summary>
    public static string? Current
    {
        get
        {
            string? uuid;
            try { uuid = PlatformUuidProvider(); }
            catch { uuid = null; }
            return string.IsNullOrEmpty(uuid) ? null : Hash(uuid);
        }
    }

    private static string? ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // The 64-bit view: a 32-bit process would otherwise read a redirected key without it.
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}
