// Ports AppBundleConfiguration.swift: runtime configuration of the built app. The Mac read
// Info.plist; here a value comes from the entry assembly's [AssemblyMetadata(key, value)] (set
// in the exe's csproj), else from the `Values` dictionary the app can fill at startup. Blank
// counts as missing. The PostHog key is empty by default, so analytics stays off.

using System.Reflection;

namespace OpenClicky.Core.Configuration;

public static class AppBundleConfiguration
{
    /// <summary>The embedded "appsettings": defaults the exe may replace before anything reads them.</summary>
    public static Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal)
    {
        ["PostHogAPIKey"] = "",
        ["PostHogHost"] = "",
    };

    private static Assembly? Bundle => Assembly.GetEntryAssembly();

    public static string? StringValue(string key)
    {
        var metadataValue = Bundle?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
        var trimmedMetadata = metadataValue?.Trim();
        if (!string.IsNullOrEmpty(trimmedMetadata)) return trimmedMetadata;

        lock (Values)
        {
            if (!Values.TryGetValue(key, out var value)) return null;
            var trimmedValue = value.Trim();
            return trimmedValue.Length == 0 ? null : trimmedValue;
        }
    }

    /// <summary>CFBundleShortVersionString: the app's version without build metadata ("0.8.7").</summary>
    public static string ShortVersionString
    {
        get
        {
            var informational = Bundle?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }
            return Bundle?.GetName().Version?.ToString(3) ?? "unknown";
        }
    }
}
