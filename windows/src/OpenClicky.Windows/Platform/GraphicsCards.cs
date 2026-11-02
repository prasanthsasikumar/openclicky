//
//  GraphicsCards.cs
//  OpenClicky (Windows)
//
//  The PC's graphics card with the most memory of its own, read from the display adapters'
//  registry keys (HardwareInformation.qwMemorySize, which unlike WMI's AdapterRAM isn't capped at
//  4 GB). Settings uses it to suggest a 3D piece maker (PieceMakers.GradeFor).
//

using System.IO;
using Microsoft.Win32;

namespace OpenClicky.Windows.Platform;

internal static class GraphicsCards
{
    private const string DisplayAdapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>The card with the most memory (its name and memory in GB); null when none says.</summary>
    public static (string Name, double MemoryGB)? Biggest()
    {
        (string Name, double MemoryGB)? best = null;
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapters);
            foreach (var id in adapters?.GetSubKeyNames() ?? [])
            {
                if (id.Length != 4 || !id.All(char.IsDigit)) continue;
                using var adapter = adapters!.OpenSubKey(id);
                if (adapter?.GetValue("DriverDesc") is not string name) continue;
                var bytes = Bytes(adapter.GetValue("HardwareInformation.qwMemorySize")) ?? Bytes(adapter.GetValue("HardwareInformation.MemorySize"));
                if (bytes is not > 0) continue;
                var memory = bytes.Value / (1024.0 * 1024 * 1024);
                if (best is null || memory > best.Value.MemoryGB) best = (name.Trim(), Math.Round(memory, 1));
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return best;
    }

    private static long? Bytes(object? value) => value switch
    {
        long number => number,
        int number => (uint)number,
        byte[] { Length: >= 8 } raw => BitConverter.ToInt64(raw, 0),
        byte[] { Length: >= 4 } raw => BitConverter.ToUInt32(raw, 0),
        _ => null,
    };
}
