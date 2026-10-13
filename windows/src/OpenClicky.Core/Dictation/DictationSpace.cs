// Ports Dictation/DictationSpace.swift: "your space": the styles each app is written in, the
// dictionary of names spelled your way, and the spoken shortcuts that expand into saved text.
// Three JSON files under %USERPROFILE%\.openclicky\dictation (styles.json, dictionary.json,
// shortcuts.json), readable and editable by hand, watched for changes made outside the app.
//
// The files have the Mac's JSON shape (keys sorted, dates in seconds since 1970, upper-case
// UUIDs), so a folder copied between a Mac and a PC reads on both. On Windows an app is named by
// its process image name ("Code.exe", "OUTLOOK.EXE"), stored in the same `appBundleIDs` array and
// compared without case.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Dictation;

/// <summary>How a take is written for a group of apps.</summary>
public sealed class DictationStyle
{
    // Declared in key order: the JSON comes out sorted like the Mac's (.sortedKeys).
    /// <summary>Process names (Windows) or bundle ids (from a Mac file) of the apps written in this style.</summary>
    [JsonPropertyName("appBundleIDs")] public List<string> AppProcessNames { get; set; } = [];
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Let a configured model rewrite the take with the rules below.</summary>
    [JsonPropertyName("polishWithModel")] public bool PolishWithModel { get; set; }
    /// <summary>Drop "um", "uh" and friends (the local pass).</summary>
    [JsonPropertyName("removeFillers")] public bool RemoveFillers { get; set; }
    /// <summary>Free-text rules for the model ("terse. keep code as code.").</summary>
    [JsonPropertyName("rules")] public string Rules { get; set; } = "";
    /// <summary>Start sentences with a capital letter (the local pass).</summary>
    [JsonPropertyName("sentenceCase")] public bool SentenceCase { get; set; }
    [JsonPropertyName("tagline")] public string Tagline { get; set; } = "";

    public DictationStyle() { }

    public DictationStyle(string id, string name, string tagline, string rules, bool sentenceCase, bool removeFillers,
                          bool polishWithModel, IEnumerable<string> appProcessNames)
    {
        Id = id; Name = name; Tagline = tagline; Rules = rules; SentenceCase = sentenceCase;
        RemoveFillers = removeFillers; PolishWithModel = polishWithModel; AppProcessNames = appProcessNames.ToList();
    }

    public DictationStyle Clone() => new(Id, Name, Tagline, Rules, SentenceCase, RemoveFillers, PolishWithModel, AppProcessNames);

    public bool Claims(string processName) => AppProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is DictationStyle other
        && Id == other.Id && Name == other.Name && Tagline == other.Tagline && Rules == other.Rules
        && SentenceCase == other.SentenceCase && RemoveFillers == other.RemoveFillers && PolishWithModel == other.PolishWithModel
        && AppProcessNames.SequenceEqual(other.AppProcessNames);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Rules);

    /// <summary>
    /// The five styles of a fresh install, with the Mac's default apps in their Windows forms.
    /// </summary>
    public static List<DictationStyle> Seeded() =>
    [
        new("developer", "developer", "terse, and your code stays code",
            "Terse. Keep identifiers, file names, commands and code exactly as spoken; never expand or translate them. No pleasantries. Sentence case.",
            sentenceCase: true, removeFillers: true, polishWithModel: true,
            [
                "Code.exe", "Cursor.exe", "WindowsTerminal.exe", "wt.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
                "Claude.exe", "Codex.exe", "GitHubDesktop.exe", "studio64.exe", "Zed.exe", "Warp.exe", "idea64.exe",
                "devenv.exe",
            ]),
        new("work-messaging", "work messaging", "clear, quick, and work-ready",
            "Clear and quick, as a colleague writes in a chat: short sentences, friendly, no filler, no sign-off. Keep it one message.",
            sentenceCase: true, removeFillers: true, polishWithModel: true,
            ["slack.exe", "ms-teams.exe", "Teams.exe", "Discord.exe", "Zoom.exe", "Linear.exe", "Notion.exe"]),
        new("personal-messaging", "personal messaging", "lowercase, shorthand, zero fuss",
            "Casual, lowercase, the way people text: shorthand kept (idk, lemme, ngl), light punctuation, warmth kept, nothing added.",
            sentenceCase: false, removeFillers: true, polishWithModel: true,
            ["WhatsApp.exe", "Telegram.exe", "Messenger.exe", "PhoneExperienceHost.exe"]),
        new("email", "email", "composed, complete sentences",
            "Composed, complete sentences with full punctuation and grammar; contractions are fine. Paragraphs where the thought changes. Never invent a greeting or a sign-off that was not spoken.",
            sentenceCase: true, removeFillers: true, polishWithModel: true,
            ["OUTLOOK.EXE", "olk.exe", "HxOutlook.exe", "thunderbird.exe", "Spark.exe"]),
        new("other", "other apps", "clean sentences, your voice kept",
            "Light cleanup only: punctuation, capitalisation, fillers removed, your wording kept.",
            sentenceCase: true, removeFillers: true, polishWithModel: true,
            []),
    ];
}

/// <summary>"you say X → OpenClicky writes Y": a name or term spelled the way you want it.</summary>
public sealed class DictionaryTerm
{
    [JsonPropertyName("createdAt"), JsonConverter(typeof(SecondsSince1970Converter))]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Ways it tends to be heard ("aditya shatriya"). Empty = match the written form itself.</summary>
    [JsonPropertyName("heardAs")] public List<string> HeardAs { get; set; } = [];
    [JsonPropertyName("id"), JsonConverter(typeof(UpperCaseGuidConverter))] public Guid Id { get; set; }
    /// <summary>What it should be written as.</summary>
    [JsonPropertyName("written")] public string Written { get; set; } = "";

    public DictionaryTerm() { }

    public DictionaryTerm(string written, IEnumerable<string>? heardAs = null, Guid? id = null, DateTimeOffset? createdAt = null)
    {
        Id = id ?? Guid.NewGuid();
        Written = written;
        HeardAs = heardAs?.ToList() ?? [];
        // Whole seconds: the JSON file stores seconds, and a term must read back equal to itself.
        CreatedAt = SecondsSince1970Converter.WholeSeconds(createdAt ?? DateTimeOffset.UtcNow);
    }

    public DictionaryTerm Clone() => new(Written, HeardAs, Id, CreatedAt);

    public override bool Equals(object? obj) => obj is DictionaryTerm other
        && Id == other.Id && Written == other.Written && CreatedAt == other.CreatedAt && HeardAs.SequenceEqual(other.HeardAs);

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>
/// "you say my sign-off → OpenClicky writes the saved text": expands only when the whole take is
/// the trigger.
/// </summary>
public sealed class SpokenShortcut
{
    [JsonPropertyName("createdAt"), JsonConverter(typeof(SecondsSince1970Converter))]
    public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("id"), JsonConverter(typeof(UpperCaseGuidConverter))] public Guid Id { get; set; }
    [JsonPropertyName("replacement")] public string Replacement { get; set; } = "";
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "";

    public SpokenShortcut() { }

    public SpokenShortcut(string trigger, string replacement, Guid? id = null, DateTimeOffset? createdAt = null)
    {
        Id = id ?? Guid.NewGuid();
        Trigger = trigger;
        Replacement = replacement;
        CreatedAt = SecondsSince1970Converter.WholeSeconds(createdAt ?? DateTimeOffset.UtcNow);
    }

    public SpokenShortcut Clone() => new(Trigger, Replacement, Id, CreatedAt);

    public override bool Equals(object? obj) => obj is SpokenShortcut other
        && Id == other.Id && Trigger == other.Trigger && Replacement == other.Replacement && CreatedAt == other.CreatedAt;

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>The three files, loaded together. Compared by value.</summary>
public sealed class DictationSpace
{
    public List<DictationStyle> Styles { get; set; }
    public List<DictionaryTerm> Dictionary { get; set; }
    public List<SpokenShortcut> Shortcuts { get; set; }

    public DictationSpace(List<DictationStyle> styles, List<DictionaryTerm> dictionary, List<SpokenShortcut> shortcuts)
    {
        Styles = styles;
        Dictionary = dictionary;
        Shortcuts = shortcuts;
    }

    /// <summary>A fresh install's space: the seeded styles, nothing else.</summary>
    public static DictationSpace Empty => new(DictationStyle.Seeded(), [], []);

    public DictationSpace Clone() => new(
        Styles.Select(style => style.Clone()).ToList(),
        Dictionary.Select(term => term.Clone()).ToList(),
        Shortcuts.Select(shortcut => shortcut.Clone()).ToList());

    /// <summary>The style for an app, by process name; the one with no apps (the "other apps" style) when none claims it.</summary>
    public DictationStyle Style(string? appProcessName)
    {
        if (appProcessName is not null && Styles.FirstOrDefault(style => style.Claims(appProcessName)) is { } claimed) return claimed;
        return Styles.FirstOrDefault(style => style.AppProcessNames.Count == 0) ?? Styles.FirstOrDefault() ?? DictationStyle.Seeded()[4];
    }

    /// <summary>Moves an app to a style, removing it from whichever style had it.</summary>
    public void Assign(string appProcessName, string styleId)
    {
        foreach (var style in Styles)
        {
            style.AppProcessNames.RemoveAll(name => string.Equals(name, appProcessName, StringComparison.OrdinalIgnoreCase));
            if (style.Id == styleId) style.AppProcessNames.Add(appProcessName);
        }
    }

    public override bool Equals(object? obj) => obj is DictationSpace other
        && Styles.SequenceEqual(other.Styles) && Dictionary.SequenceEqual(other.Dictionary) && Shortcuts.SequenceEqual(other.Shortcuts);

    public override int GetHashCode() => HashCode.Combine(Styles.Count, Dictionary.Count, Shortcuts.Count);
}

/// <summary>
/// Reads and writes the space under the dictation folder, and reloads when the files change.
/// PropertyChanged is raised on the thread that changed it (a reload: the watcher's); the
/// Windows side marshals to the dispatcher.
/// </summary>
public sealed class DictationSpaceStore : ObservableObject, IDisposable
{
    public const string StylesFileName = "styles.json";
    public const string DictionaryFileName = "dictionary.json";
    public const string ShortcutsFileName = "shortcuts.json";

    /// <summary>How long the watcher waits after the last change before it reloads.</summary>
    public static readonly TimeSpan ReloadDelay = TimeSpan.FromSeconds(0.3);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object stateLock = new();
    private DictationSpace space;
    private FileSystemWatcher? directoryWatcher;
    private Timer? pendingReload;
    private string lastWriteFingerprint = "";

    public string DirectoryPath { get; }

    public static string DefaultDirectoryPath => OpenClickyPaths.DictationDirectory;

    public DictationSpaceStore(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? DefaultDirectoryPath;
        space = Load(DirectoryPath);
    }

    /// <summary>A copy of the space as it is now (change it through `Update`).</summary>
    public DictationSpace Space
    {
        get { lock (stateLock) return space.Clone(); }
    }

    private string StylesPath => Path.Combine(DirectoryPath, StylesFileName);
    private string DictionaryPath => Path.Combine(DirectoryPath, DictionaryFileName);
    private string ShortcutsPath => Path.Combine(DirectoryPath, ShortcutsFileName);

    public static DictationSpace Load(string directoryPath)
    {
        T? Read<T>(string name) where T : class
        {
            try
            {
                var path = Path.Combine(directoryPath, name);
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }
        var styles = Read<List<DictationStyle>>(StylesFileName) ?? DictationStyle.Seeded();
        if (styles.Count == 0) styles = DictationStyle.Seeded();
        foreach (var style in styles) style.AppProcessNames ??= [];
        var dictionary = Read<List<DictionaryTerm>>(DictionaryFileName) ?? [];
        foreach (var term in dictionary) term.HeardAs ??= [];
        return new DictationSpace(styles, dictionary, Read<List<SpokenShortcut>>(ShortcutsFileName) ?? []);
    }

    public void Reload()
    {
        var reloaded = Load(DirectoryPath);
        lock (stateLock) space = reloaded;
        OnPropertyChanged(nameof(Space));
    }

    /// <summary>Changes the space and writes every file (small files, written whole).</summary>
    public void Update(Action<DictationSpace> change)
    {
        lock (stateLock)
        {
            var changed = space.Clone();
            change(changed);
            if (changed.Equals(space)) return;
            space = changed;
            SaveLocked();
        }
        OnPropertyChanged(nameof(Space));
    }

    public void Save()
    {
        lock (stateLock) SaveLocked();
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            AtomicFile.WriteAllBytes(StylesPath, JsonSerializer.SerializeToUtf8Bytes(space.Styles, WriteOptions));
            AtomicFile.WriteAllBytes(DictionaryPath, JsonSerializer.SerializeToUtf8Bytes(space.Dictionary, WriteOptions));
            AtomicFile.WriteAllBytes(ShortcutsPath, JsonSerializer.SerializeToUtf8Bytes(space.Shortcuts, WriteOptions));
            lastWriteFingerprint = Fingerprint(space);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"dictation space: could not write {DirectoryPath}: {exception.Message}");
        }
    }

    /// <summary>
    /// Picks up edits made by hand while the app runs. The folder is watched, not the files: an
    /// editor's atomic save replaces a file, and a watch on the old one goes quiet.
    /// </summary>
    public void StartWatching()
    {
        lock (stateLock)
        {
            if (directoryWatcher is not null) return;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var watcher = new FileSystemWatcher(DirectoryPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += (_, _) => ScheduleReload();
                watcher.Created += (_, _) => ScheduleReload();
                watcher.Deleted += (_, _) => ScheduleReload();
                watcher.Renamed += (_, _) => ScheduleReload();
                watcher.EnableRaisingEvents = true;
                directoryWatcher = watcher;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Write($"dictation space: could not watch {DirectoryPath}: {exception.Message}");
            }
        }
    }

    private void ScheduleReload()
    {
        lock (stateLock)
        {
            pendingReload?.Dispose();
            pendingReload = new Timer(_ => ReloadIfChangedOutside(), null, ReloadDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadIfChangedOutside()
    {
        var reloaded = Load(DirectoryPath);
        lock (stateLock)
        {
            // The app's own write arrives here too; only an outside change replaces the model.
            if (Fingerprint(reloaded) == lastWriteFingerprint && reloaded.Equals(space)) return;
            if (reloaded.Equals(space)) return;
            space = reloaded;
        }
        OnPropertyChanged(nameof(Space));
    }

    private static string Fingerprint(DictationSpace space) => string.Join("|",
        JsonSerializer.Serialize(space.Styles, FingerprintOptions),
        JsonSerializer.Serialize(space.Dictionary, FingerprintOptions),
        JsonSerializer.Serialize(space.Shortcuts, FingerprintOptions));

    public void Dispose()
    {
        lock (stateLock)
        {
            directoryWatcher?.Dispose();
            directoryWatcher = null;
            pendingReload?.Dispose();
            pendingReload = null;
        }
    }
}

/// <summary>Dates as JSONEncoder's .secondsSince1970 writes them: a number of seconds.</summary>
public sealed class SecondsSince1970Converter : JsonConverter<DateTimeOffset>
{
    public static DateTimeOffset WholeSeconds(DateTimeOffset date) => DateTimeOffset.FromUnixTimeSeconds(date.ToUnixTimeSeconds());

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var seconds = reader.GetDouble();
        return DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        var ticks = (value - DateTimeOffset.UnixEpoch).Ticks;
        if (ticks % TimeSpan.TicksPerSecond == 0) writer.WriteNumberValue(ticks / TimeSpan.TicksPerSecond);
        else writer.WriteNumberValue((double)ticks / TimeSpan.TicksPerSecond);
    }
}

/// <summary>UUIDs as Foundation writes them: upper case with dashes.</summary>
public sealed class UpperCaseGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("not a uuid");

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant());
}
