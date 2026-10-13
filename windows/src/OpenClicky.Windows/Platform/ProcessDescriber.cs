using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>pid → image name, path and display name, with the slow FileVersionInfo read cached per path.</summary>
internal static class ProcessDescriber
{
    private static readonly ConcurrentDictionary<string, string> DisplayNameByPath = new(StringComparer.OrdinalIgnoreCase);

    public sealed record ProcessDescription(int ProcessId, string? ImageName, string? ExecutablePath, string? DisplayName);

    public static ProcessDescription Describe(int processId)
    {
        var path = ProcessImagePath(processId);
        string? imageName = path is null ? null : Path.GetFileName(path);
        if (imageName is null)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                imageName = process.ProcessName + ".exe";
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
            }
        }
        return new ProcessDescription(processId, imageName, path, path is null ? StripExe(imageName) : DisplayNameOf(path));
    }

    public static string? ImageName(int processId)
    {
        var path = ProcessImagePath(processId);
        return path is null ? null : Path.GetFileName(path);
    }

    /// <summary>FileDescription ("Google Chrome"), else ProductName, else the file name without ".exe".</summary>
    public static string DisplayNameOf(string executablePath) =>
        DisplayNameByPath.GetOrAdd(executablePath, static path =>
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription)) return versionInfo.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(versionInfo.ProductName)) return versionInfo.ProductName.Trim();
            }
            catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException or IOException)
            {
            }
            return StripExe(Path.GetFileName(path)) ?? path;
        });

    private static string? StripExe(string? imageName) =>
        imageName is not null && imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;
}
