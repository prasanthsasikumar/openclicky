// Ports OpenClickyConfiguration.swift: where the backend lives, the user's token, and how to run
// the `openclicky` CLI. Read from %USERPROFILE%\.openclicky\shell.json (shared with the CLI) with
// environment overrides. The app never holds provider keys for the backend — only the user's
// OpenClicky token, which the backend verifies; own keys go straight to their vendors.

using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Configuration;

public static class OpenClickyConfiguration
{
    /// <summary>Where the app talks to unless shell.json says otherwise.</summary>
    public const string HostedBackendUrl = "https://api.openclicky.org";

    /// <summary>
    /// Connectors (Composio app connections). Off for 0.8.0; a later release turns them back on by
    /// flipping this.
    /// </summary>
    public const bool ConnectorsEnabled = false;

    /// <summary>The Composio MCP URL from shell.json, or null while connectors are off.</summary>
    public static string? ActiveComposioMcpUrl
    {
        get
        {
#pragma warning disable CS0162 // ConnectorsEnabled is a switch flipped by hand.
            if (!ConnectorsEnabled) return null;
            var url = Settings.ComposioMcpUrl;
            return string.IsNullOrEmpty(url) ? null : url;
#pragma warning restore CS0162
        }
    }

    public static string SettingsFilePath => OpenClickyPaths.ShellSettingsFile;

    private static readonly object SettingsLock = new();
    private static OpenClickyShellSettings? settings;

    /// <summary>
    /// The settings now (file, then environment overrides). A snapshot: never mutate it, change
    /// shell.json through `Update`.
    /// </summary>
    public static OpenClickyShellSettings Settings
    {
        get
        {
            lock (SettingsLock)
            {
                return settings ??= Load();
            }
        }
    }

    /// <summary>The Swift name for `Settings`.</summary>
    public static OpenClickyShellSettings Current => Settings;

    /// <summary>Raised after every reload (the app's own writes and edits made by hand).</summary>
    public static event Action? SettingsChanged;

    /// <summary>
    /// Raised when shell.json changed on disk in a way that matters to a live connection: the token,
    /// the backend, or one of the user's own keys.
    /// </summary>
    public static event Action? CredentialsChanged;

    /// <summary>Raised when shell.json stops reading; the argument is the sentence to show.</summary>
    public static event Action<string>? SettingsFileUnreadable;

    public static void Reload()
    {
        var loaded = Load();
        lock (SettingsLock) settings = loaded;
        SettingsChanged?.Invoke();
    }

    /// <summary>What a live session was opened with. When this changes, the session is stale.</summary>
    public static IReadOnlyList<string> CredentialFingerprint(OpenClickyShellSettings shellSettings) =>
        [shellSettings.BackendUrl, shellSettings.Token, shellSettings.OpenaiApiKey ?? "", shellSettings.AnthropicApiKey ?? "",
         shellSettings.GeminiApiKey ?? "", shellSettings.RealtimeProvider ?? ""];

    // MARK: - Watching the file

    private static FileSystemWatcher? settingsDirectoryWatcher;
    private static Timer? pendingSettingsReload;
    private static SynchronizationContext? watcherContext;
    /// <summary>How long the folder must be quiet before an edit is read (editors write in bursts).</summary>
    public static TimeSpan SettingsReloadDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Picks up shell.json edits made while the app runs. The folder is watched, not the file: an
    /// editor's atomic save replaces the file. The events are raised on `context` (default: the
    /// caller's SynchronizationContext, the UI thread when called from it).
    /// </summary>
    public static void StartWatchingSettingsFile(SynchronizationContext? context = null)
    {
        lock (SettingsLock)
        {
            if (settingsDirectoryWatcher is not null) return;
        }
        EnsureSettingsFileExists();
        var directoryPath = Path.GetDirectoryName(SettingsFilePath)!;
        if (!Directory.Exists(directoryPath)) return;
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(directoryPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
        }
        catch (ArgumentException)
        {
            return;
        }
        lock (SettingsLock)
        {
            if (settingsDirectoryWatcher is not null) { watcher.Dispose(); return; }
            watcherContext = context ?? SynchronizationContext.Current;
            settingsDirectoryWatcher = watcher;
        }
        FileSystemEventHandler onChange = (_, _) => ScheduleReloadAfterExternalChange();
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += (_, _) => ScheduleReloadAfterExternalChange();
        watcher.EnableRaisingEvents = true;
    }

    public static void StopWatchingSettingsFile()
    {
        lock (SettingsLock)
        {
            settingsDirectoryWatcher?.Dispose();
            settingsDirectoryWatcher = null;
            pendingSettingsReload?.Dispose();
            pendingSettingsReload = null;
        }
    }

    private static void ScheduleReloadAfterExternalChange()
    {
        lock (SettingsLock)
        {
            // Editors write in bursts (temp file, rename, attributes); act once they are done.
            pendingSettingsReload?.Dispose();
            pendingSettingsReload = new Timer(_ =>
            {
                var context = watcherContext;
                if (context is null) ReloadAfterExternalChange();
                else context.Post(_ => ReloadAfterExternalChange(), null);
            }, null, SettingsReloadDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private static void ReloadAfterExternalChange()
    {
        var fingerprintBefore = CredentialFingerprint(Settings);
        Reload();
        if (CredentialFingerprint(Settings).SequenceEqual(fingerprintBefore)) return;
        CredentialsChanged?.Invoke();
    }

    // MARK: - Reading

    /// <summary>
    /// File settings, then environment overrides: OPENCLICKY_SHELL_CLI ("node C:\path\cli.js"),
    /// BACKEND_URL, OPENCLICKY_TOKEN, OPENCLICKY_WORKSPACE, OPENCLICKY_MODEL,
    /// OPENCLICKY_TRANSCRIPTION_PROVIDER. The overrides live only in memory: `Update` starts from
    /// the file alone, so it never writes them.
    /// </summary>
    public static OpenClickyShellSettings Load() => Load(SettingsFilePath, ProcessEnvironment());

    public static OpenClickyShellSettings Load(string filePath, IReadOnlyDictionary<string, string> environment)
    {
        var loadedSettings = new OpenClickyShellSettings();
        var reading = ReadSettingsFile(filePath);
        switch (reading)
        {
            case SettingsFileReading.Readable readable:
                loadedSettings = readable.Settings;
                if (IsRealSettingsFile(filePath)) SettingsFileProblem = null;
                break;
            case SettingsFileReading.Missing:
                if (IsRealSettingsFile(filePath)) SettingsFileProblem = null;
                break;
            case SettingsFileReading.Unreadable unreadable:
                // The defaults stand in for this launch; the file itself is left for the person to fix.
                NoteUnreadableSettingsFile(unreadable.Reason, filePath);
                break;
        }
        if (environment.TryGetValue("OPENCLICKY_SHELL_CLI", out var cli) && cli.Length > 0)
            loadedSettings.CliCommand = cli.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (environment.TryGetValue("BACKEND_URL", out var backendUrl) && backendUrl.Length > 0) loadedSettings.BackendUrl = backendUrl;
        if (environment.TryGetValue("OPENCLICKY_TOKEN", out var token) && token.Length > 0) loadedSettings.Token = token;
        if (environment.TryGetValue("OPENCLICKY_WORKSPACE", out var workspace) && workspace.Length > 0) loadedSettings.Workspace = workspace;
        if (environment.TryGetValue("OPENCLICKY_MODEL", out var model) && model.Length > 0) loadedSettings.Model = model;
        if (environment.TryGetValue("OPENCLICKY_TRANSCRIPTION_PROVIDER", out var provider) && provider.Length > 0) loadedSettings.TranscriptionProvider = provider;
        return loadedSettings;
    }

    /// <summary>
    /// What is in shell.json: nothing yet, settings (with the file's own JSON object, so a write can
    /// keep what this build does not know), or a file that does not read, and why.
    /// </summary>
    public abstract record SettingsFileReading
    {
        public sealed record Missing : SettingsFileReading;
        public sealed record Readable(OpenClickyShellSettings Settings, JsonObject JsonObject) : SettingsFileReading;
        public sealed record Unreadable(string Reason) : SettingsFileReading;
    }

    public static SettingsFileReading ReadSettingsFile(string filePath)
    {
        if (!File.Exists(filePath)) return new SettingsFileReading.Missing();
        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SettingsFileReading.Unreadable($"it can't be opened ({exception.Message})");
        }
        // An empty file has nothing in it to lose.
        if (string.IsNullOrWhiteSpace(text)) return new SettingsFileReading.Readable(new OpenClickyShellSettings(), new JsonObject());
        JsonObject? jsonObject;
        try
        {
            jsonObject = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            jsonObject = null;
        }
        if (jsonObject is null) return new SettingsFileReading.Unreadable("it isn't valid json");
        try
        {
            return new SettingsFileReading.Readable(OpenClickyShellSettings.Decode(text), jsonObject);
        }
        catch (JsonException exception) when (!string.IsNullOrEmpty(exception.Path))
        {
            var fieldPath = exception.Path.StartsWith("$.", StringComparison.Ordinal) ? exception.Path[2..] : exception.Path.TrimStart('$');
            return new SettingsFileReading.Unreadable($"\"{fieldPath}\" has the wrong kind of value");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new SettingsFileReading.Unreadable($"it doesn't read ({exception.Message})");
        }
    }

    /// <summary>The sentence saying why shell.json is neither used nor written, while it does not read.</summary>
    public static string? SettingsFileProblem { get; private set; }

    public static string UnreadableSettingsFileSentence(string reason) =>
        $"your settings file (%USERPROFILE%\\.openclicky\\shell.json) has a mistake in it: {reason}. openclicky won't change it until it's fixed.";

    private static bool IsRealSettingsFile(string filePath) =>
        string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(SettingsFilePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Logs and shows the problem once per distinct problem (every refused write would repeat it).</summary>
    private static void NoteUnreadableSettingsFile(string reason, string filePath)
    {
        AppLog.Write($"settings: {filePath} does not read, so nothing is written to it: {reason}");
        // Only the real settings file is the person's to hear about; tests use temporary files.
        if (!IsRealSettingsFile(filePath)) return;
        var sentence = UnreadableSettingsFileSentence(reason);
        if (SettingsFileProblem == sentence) return;
        SettingsFileProblem = sentence;
        // AccountProfileStore listens and shows it as the quiet caption (it may not exist yet: this
        // can run during the very first load).
        SettingsFileUnreadable?.Invoke(sentence);
    }

    public static void EnsureSettingsFileExists()
    {
        if (File.Exists(SettingsFilePath))
        {
            TightenSettingsDirectoryPermissionsIfNeeded(Path.GetDirectoryName(SettingsFilePath)!);
            return;
        }
        try
        {
            WriteShellSettingsData(SortedIndentedJson(JsonNode.Parse(new OpenClickyShellSettings().Encode())!.AsObject()), SettingsFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"settings: could not create {SettingsFilePath}: {exception.Message}");
        }
    }

    // MARK: - Writing

    /// <summary>Changes shell.json in place (sign-in writes the session here) and reloads the settings.</summary>
    public static void Update(Action<OpenClickyShellSettings> change)
    {
        Update(SettingsFilePath, change);
        Reload();
    }

    /// <summary>
    /// Starts from the file alone (never the environment overrides) and writes back only the fields
    /// the change touched, merged into the file's own JSON object: fields this build does not know,
    /// and values the change left alone, stay exactly as they were written. A file that exists but
    /// does not read is never written. Returns whether the file was written.
    /// </summary>
    public static bool Update(string filePath, Action<OpenClickyShellSettings> change)
    {
        var fileSettings = new OpenClickyShellSettings();
        var fileJsonObject = new JsonObject();
        switch (ReadSettingsFile(filePath))
        {
            case SettingsFileReading.Missing:
                break;
            case SettingsFileReading.Readable readable:
                fileSettings = readable.Settings;
                fileJsonObject = readable.JsonObject;
                break;
            case SettingsFileReading.Unreadable unreadable:
                NoteUnreadableSettingsFile(unreadable.Reason, filePath);
                return false;
        }
        var changedSettings = fileSettings.Clone();
        change(changedSettings);
        try
        {
            var encodedBefore = JsonNode.Parse(fileSettings.Encode())!.AsObject();
            var encodedAfter = JsonNode.Parse(changedSettings.Encode())!.AsObject();
            var fieldNames = encodedBefore.Select(pair => pair.Key).Union(encodedAfter.Select(pair => pair.Key)).ToList();
            foreach (var fieldName in fieldNames)
            {
                encodedBefore.TryGetPropertyValue(fieldName, out var valueBefore);
                var hasAfter = encodedAfter.TryGetPropertyValue(fieldName, out var valueAfter);
                if (JsonNode.DeepEquals(valueBefore, valueAfter)) continue;
                // An optional set back to null is not encoded at all, so it leaves the file too.
                if (hasAfter) fileJsonObject[fieldName] = valueAfter?.DeepClone();
                else fileJsonObject.Remove(fieldName);
            }
            WriteShellSettingsData(SortedIndentedJson(fileJsonObject), filePath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write($"settings: could not write {filePath}: {exception.Message}");
            return false;
        }
    }

    private static string SortedIndentedJson(JsonObject jsonObject)
    {
        static JsonNode? Sorted(JsonNode? node) => node switch
        {
            JsonObject objectNode => new JsonObject(objectNode
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value)))),
            JsonArray arrayNode => new JsonArray(arrayNode.Select(Sorted).ToArray()),
            _ => node?.DeepClone(),
        };
        return Sorted(jsonObject)!.ToJsonString(new JsonSerializerOptions(OpenClickyShellSettings.JsonOptions) { WriteIndented = true });
    }

    /// <summary>
    /// shell.json holds the session token, the refresh token, and the user's own keys, so it must
    /// never be readable by anyone but the owner. On Windows the profile folder's inherited ACL
    /// already keeps it to the owner (plus SYSTEM and Administrators); elsewhere the directory is
    /// 0700 and the file 0600 after every write. The write is atomic (temp file, then rename).
    /// </summary>
    public static void WriteShellSettingsData(string json, string filePath)
    {
        var directoryPath = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(directoryPath);
        TightenSettingsDirectoryPermissionsIfNeeded(directoryPath);
        AtomicFile.WriteAllText(filePath, json);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TightenSettingsDirectoryPermissionsIfNeeded(string directoryPath)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(directoryPath)) return;
        try
        {
            File.SetUnixFileMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    // MARK: - Derived values

    /// <summary>Backend base URL without a trailing slash.</summary>
    public static string BackendBaseUrl => TrimmedBackendUrl(Settings.BackendUrl);

    private static string TrimmedBackendUrl(string backendUrl)
    {
        var url = backendUrl.Trim().TrimEnd('/');
        return url.Length == 0 ? HostedBackendUrl : url;
    }

    public static string BackendHostDescription =>
        Uri.TryCreate(BackendBaseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : BackendBaseUrl;

    public static string? Token => Cleaned(Settings.Token);

    /// <summary>True when this PC is signed in to an OpenClicky account (a token in shell.json).</summary>
    public static bool HasAccountToken => Token is not null;

    /// <summary>True when model calls can be made at all: an own key, a plan, or an account.</summary>
    public static bool IsConfigured => IsConfiguredFor(Settings);

    public static bool IsConfiguredFor(OpenClickyShellSettings shellSettings)
    {
        var credentials = ProviderCredentials.FromSettings(shellSettings);
        // An account, an OpenAI key (it also covers speech and dictation), or anything that can answer.
        return credentials.AccountToken is not null || credentials.OpenAIKey is not null || credentials.AnswerSourcesInOrder.Count > 0;
    }

    /// <summary>
    /// Where cua-driver is, mirroring the CLI's precedence (`resolveCuaDriverBin` in
    /// agent/src/config.ts): an explicit `cuaDriverBin` — or else `CUA_DRIVER_BIN` — wins outright,
    /// an explicit empty string disables it; only when neither is set are the install paths tried.
    /// </summary>
    public static string? ResolvedCuaDriverBin => ResolveCuaDriverBin(Settings, ProcessEnvironment());

    public static IReadOnlyList<string> CuaDriverCandidates =>
    [
        Path.Combine(OpenClickyPaths.HomeDirectory, ".local", "bin", "cua-driver.exe"),
        Path.Combine(OpenClickyPaths.LocalAppDataDirectory, "Programs", "CuaDriver", "cua-driver.exe"),
    ];

    public static string? ResolveCuaDriverBin(OpenClickyShellSettings shellSettings, IReadOnlyDictionary<string, string> environment,
                                              Func<string, bool>? isExecutable = null)
    {
        if (shellSettings.CuaDriverBin is { } configured)
        {
            var trimmed = configured.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        if (environment.TryGetValue("CUA_DRIVER_BIN", out var environmentValue))
        {
            var trimmed = environmentValue.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        var check = isExecutable ?? File.Exists;
        return CuaDriverCandidates.FirstOrDefault(check);
    }

    public static IReadOnlyList<string> CliCommand => Settings.CliCommand;
    public static string WorkspacePath => OpenClickyPaths.ExpandTilde(Settings.Workspace);
    public static string? AgentModelOverride => string.IsNullOrEmpty(Settings.Model) ? null : Settings.Model;

    /// <summary>The fast voice vendor the person picked in Settings → Voice; null is automatic.</summary>
    public static Speech.RealtimeVoiceProvider? PreferredRealtimeProvider =>
        RawValues.FromRawValue<Speech.RealtimeVoiceProvider>(Settings.RealtimeProvider);

    /// <summary>
    /// The user's skill library: `library/&lt;id&gt;/SKILL.md`, `active/`, `activations.json`. Same layout
    /// as the CLI's agent/src/skillsLibrary.ts, so both sides see the same skills.
    /// </summary>
    public static string UserSkillsDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("OPENCLICKY_USER_SKILLS_DIR");
            if (!string.IsNullOrEmpty(overridePath)) return OpenClickyPaths.ExpandTilde(overridePath);
            return OpenClickyPaths.UserSkillsDirectory;
        }
    }

    /// <summary>
    /// App-teaching skills: `appSkillsPath` from shell.json, else derived from the CLI path
    /// (`…\agent\dist\cli.js` → `…\app-skills`), else %USERPROFILE%\.openclicky\app-skills.
    /// </summary>
    public static string AppSkillsDirectory => AppSkillsDirectoryFor(Settings);

    public static string AppSkillsDirectoryFor(OpenClickyShellSettings shellSettings)
    {
        var configured = shellSettings.AppSkillsPath?.Trim();
        if (!string.IsNullOrEmpty(configured)) return OpenClickyPaths.ExpandTilde(configured);
        var cliPath = shellSettings.CliCommand.LastOrDefault();
        if (cliPath is not null && cliPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            var cliFullPath = Path.GetFullPath(OpenClickyPaths.ExpandTilde(cliPath));
            // agent\dist\cli.js → agent\dist → agent → repo root
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cliFullPath)));
            if (root is not null)
            {
                var candidate = Path.Combine(root, "app-skills");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return OpenClickyPaths.AppSkillsFallbackDirectory;
    }

    // MARK: - Bring your own key

    internal static string? Cleaned(string? value)
    {
        var trimmedValue = (value ?? "").Trim();
        return trimmedValue.Length == 0 ? null : trimmedValue;
    }

    /// <summary>True when this PC pays its own provider bills: an OpenAI key is set in shell.json.</summary>
    public static bool UsesOwnKeysFor(OpenClickyShellSettings shellSettings) => Cleaned(shellSettings.OpenaiApiKey) is not null;
    public static bool UsesOwnKeys => UsesOwnKeysFor(Settings);

    /// <summary>
    /// Adds the account's bearer token to a backend request. Never the person's own provider keys:
    /// those go straight to their vendor (ProviderRouter). No token, no header: the backend refuses
    /// an empty `Authorization: Bearer `.
    /// </summary>
    public static void Authorize(HttpRequestMessage request) => Authorize(request, Token);

    public static void Authorize(HttpRequestMessage request, string? token)
    {
        if (token is null) return;
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    }

    /// <summary>
    /// Environment for the `openclicky` CLI subprocess. Provider keys are stripped on purpose; the
    /// user's own keys travel under OpenClicky names so the CLI forwards them as headers.
    /// </summary>
    public static Dictionary<string, string> CliProcessEnvironment => CliProcessEnvironmentFor(Settings);

    public static Dictionary<string, string> CliProcessEnvironmentFor(OpenClickyShellSettings shellSettings,
                                                                      IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        var environment = new Dictionary<string, string>(baseEnvironment ?? ProcessEnvironment(), EnvironmentComparer);
        environment["BACKEND_URL"] = TrimmedBackendUrl(shellSettings.BackendUrl);
        if (Cleaned(shellSettings.Token) is { } token) environment["OPENCLICKY_TOKEN"] = token;
        if (Cleaned(shellSettings.Model) is { } model) environment["OPENCLICKY_MODEL"] = model;
        environment["OPENCLICKY_WORKSPACE"] = OpenClickyPaths.ExpandTilde(shellSettings.Workspace);
        if (shellSettings.WritableRoots is { } writableRoots) environment["OPENCLICKY_WRITABLE_ROOTS"] = string.Join(",", writableRoots);
#pragma warning disable CS0162
        if (ConnectorsEnabled && !string.IsNullOrEmpty(shellSettings.ComposioMcpUrl)) environment["COMPOSIO_MCP_URL"] = shellSettings.ComposioMcpUrl;
        if (ConnectorsEnabled && !string.IsNullOrEmpty(shellSettings.ComposioApiKey)) environment["COMPOSIO_API_KEY"] = shellSettings.ComposioApiKey;
#pragma warning restore CS0162
        if (!string.IsNullOrEmpty(shellSettings.CuaDriverBin)) environment["CUA_DRIVER_BIN"] = shellSettings.CuaDriverBin;
        if (Cleaned(shellSettings.OpenaiApiKey) is { } openaiApiKey) environment["OPENCLICKY_OPENAI_KEY"] = openaiApiKey;
        if (Cleaned(shellSettings.AnthropicApiKey) is { } anthropicApiKey) environment["OPENCLICKY_ANTHROPIC_KEY"] = anthropicApiKey;
        environment.Remove("OPENAI_API_KEY");
        environment.Remove("ANTHROPIC_API_KEY");
        // An app started from the Start menu may miss per-user tool folders; add the usual CLI
        // locations so `openclicky`, `codex`, `node`, `ffmpeg` resolve.
        var homeDirectory = OpenClickyPaths.HomeDirectory;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var extraPathEntries = new List<string>
        {
            Path.Combine(homeDirectory, ".local", "bin"),
            Path.Combine(appData, "npm"),
            Path.Combine(homeDirectory, ".npm-global"),
            Path.Combine(homeDirectory, ".npm-global", "bin"),
        };
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) extraPathEntries.Add(Path.Combine(programFiles, "nodejs"));
        environment.TryGetValue("PATH", out var existingPath);
        extraPathEntries.Add(string.IsNullOrEmpty(existingPath) ? DefaultSystemPath() : existingPath);
        environment["PATH"] = string.Join(Path.PathSeparator, extraPathEntries);
        return environment;
    }

    private static string DefaultSystemPath()
    {
        if (!OperatingSystem.IsWindows()) return "/usr/bin:/bin";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.Join(Path.PathSeparator, Path.Combine(windows, "System32"), windows);
    }

    private static StringComparer EnvironmentComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>This process's environment as a dictionary (case-insensitive keys on Windows).</summary>
    public static IReadOnlyDictionary<string, string> ProcessEnvironment()
    {
        var environment = new Dictionary<string, string>(EnvironmentComparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }
        return environment;
    }
}
