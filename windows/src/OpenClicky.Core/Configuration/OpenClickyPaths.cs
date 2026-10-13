// New on Windows (no single Swift twin): every path the Mac app spelled out in place —
// ~/.openclicky (shared with the CLI), ~/Library/Application Support/OpenClicky,
// ~/Library/Logs/OpenClicky — in one class, so tests can move them all under a temp directory.

namespace OpenClicky.Core.Configuration;

public static class OpenClickyPaths
{
    private static readonly object OverrideLock = new();
    private static string? homeDirectoryOverride;
    private static string? localAppDataOverride;

    /// <summary>%USERPROFILE% (the Mac's ~), unless a test moved it.</summary>
    public static string HomeDirectory
    {
        get
        {
            lock (OverrideLock)
            {
                return homeDirectoryOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }
    }

    /// <summary>%LOCALAPPDATA%, unless a test moved it.</summary>
    public static string LocalAppDataDirectory
    {
        get
        {
            lock (OverrideLock)
            {
                return localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
        }
    }

    /// <summary>Moves the home and local app data roots (tests). Null leaves that root as it is.</summary>
    public static void OverrideRoots(string? homeDirectory, string? localAppDataDirectory)
    {
        lock (OverrideLock)
        {
            homeDirectoryOverride = homeDirectory;
            localAppDataOverride = localAppDataDirectory;
        }
    }

    public static void ResetOverrides() => OverrideRoots(null, null);

    // ~/.openclicky — the same folder and files as the CLI and the Mac app.

    /// <summary>%USERPROFILE%\.openclicky</summary>
    public static string SettingsDirectory => Path.Combine(HomeDirectory, ".openclicky");

    /// <summary>%USERPROFILE%\.openclicky\shell.json</summary>
    public static string ShellSettingsFile => Path.Combine(SettingsDirectory, "shell.json");

    /// <summary>%USERPROFILE%\.openclicky\dictation (styles, dictionary, shortcuts JSON).</summary>
    public static string DictationDirectory => Path.Combine(SettingsDirectory, "dictation");

    /// <summary>%USERPROFILE%\.openclicky\skills (the user's skill library, shared with the CLI).</summary>
    public static string UserSkillsDirectory => Path.Combine(SettingsDirectory, "skills");

    /// <summary>%USERPROFILE%\.openclicky\app-skills (the last fallback for app-teaching skills).</summary>
    public static string AppSkillsFallbackDirectory => Path.Combine(SettingsDirectory, "app-skills");

    /// <summary>%USERPROFILE%\OpenClicky — the agent's default workspace.</summary>
    public static string DefaultWorkspace => Path.Combine(HomeDirectory, "OpenClicky");

    // %LOCALAPPDATA%\OpenClicky — the Mac's Application Support and Logs folders.

    /// <summary>%LOCALAPPDATA%\OpenClicky</summary>
    public static string AppDataDirectory => Path.Combine(LocalAppDataDirectory, "OpenClicky");

    /// <summary>%LOCALAPPDATA%\OpenClicky\Logs</summary>
    public static string LogsDirectory => Path.Combine(AppDataDirectory, "Logs");

    /// <summary>%LOCALAPPDATA%\OpenClicky\Logs\app.log</summary>
    public static string LogFile => Path.Combine(LogsDirectory, "app.log");

    /// <summary>%LOCALAPPDATA%\OpenClicky\Models (Kokoro, Parakeet, …).</summary>
    public static string ModelsDirectory => Path.Combine(AppDataDirectory, "Models");

    /// <summary>%LOCALAPPDATA%\OpenClicky\dictation.sqlite (the take history).</summary>
    public static string DictationDatabaseFile => Path.Combine(AppDataDirectory, "dictation.sqlite");

    /// <summary>%LOCALAPPDATA%\OpenClicky\preferences.json (the UserDefaults replacement).</summary>
    public static string PreferencesFile => Path.Combine(AppDataDirectory, "preferences.json");

    /// <summary>
    /// "~", "~/x" and "~\x" against the home directory, and %VARIABLES% expanded — the Windows
    /// form of NSString.expandingTildeInPath.
    /// </summary>
    public static string ExpandTilde(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var expanded = path;
        if (expanded == "~")
        {
            expanded = HomeDirectory;
        }
        else if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(HomeDirectory, expanded[2..]);
        }
        if (expanded.Contains('%')) expanded = Environment.ExpandEnvironmentVariables(expanded);
        return expanded;
    }
}
