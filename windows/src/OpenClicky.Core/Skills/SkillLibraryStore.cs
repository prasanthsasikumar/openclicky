// Port of macos/OpenClicky/OpenClicky/SkillLibraryStore.swift
//
// The user's skill library as the island HUD and the talk lanes see it. Reads and writes the same
// files as the CLI (`agent/src/skillsLibrary.ts`):
//
//   <userSkillsDirectory>/library/<id>/SKILL.md   one folder per skill (created here or dropped in by hand)
//   <userSkillsDirectory>/activations.json        { "active": [ids], "updatedAt": ISO-8601 }
//   <userSkillsDirectory>/active/<id> → library/<id>   one entry per activated skill; Codex loads this dir
//
// App-teaching skills (`app-skills/` in the checkout) are read-only here and matched by frontmost app.
//
// Windows note on `active/`: the Mac (and the CLI) use symlinks. On Windows a directory symlink
// needs either Developer Mode or an elevated process, which most people do not have. So each entry
// in `active/` is made by the first of these that works:
//   1. a directory symlink (Directory.CreateSymbolicLink) — works with Developer Mode / admin;
//   2. a directory junction — needs no privilege, but creating one needs P/Invoke (FSCTL_SET_REPARSE_POINT)
//      or `mklink /J`, and Core may not P/Invoke; the Windows side passes a `createJunction` delegate
//      (and without one we shell out to `cmd /c mklink /J`, which needs no P/Invoke);
//   3. a plain copy of the skill folder — always works; it is refreshed whenever the library's SKILL.md
//      differs from the copy's, so it reads the same as a link for Codex.
// Both symlinks and junctions report their target through FileSystemInfo.LinkTarget.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClicky.Core.Skills;

public enum SkillLibraryErrorKind
{
    InvalidMarkdown,
}

public sealed class SkillLibraryException : Exception
{
    public SkillLibraryErrorKind Kind { get; }

    public SkillLibraryException(SkillLibraryErrorKind kind, string? detail = null)
        : base(DescribeError(kind, detail))
    {
        Kind = kind;
    }

    public static SkillLibraryException InvalidMarkdown() => new(SkillLibraryErrorKind.InvalidMarkdown);

    private static string DescribeError(SkillLibraryErrorKind kind, string? detail) => kind switch
    {
        SkillLibraryErrorKind.InvalidMarkdown => "The skill file needs frontmatter with a name and a description.",
        _ => detail ?? string.Empty,
    };
}

public sealed class SkillLibraryStore : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private IReadOnlyList<SkillFile> librarySkills = Array.Empty<SkillFile>();
    private IReadOnlySet<string> activeIds = new HashSet<string>();
    private IReadOnlyList<SkillFile> appSkills = Array.Empty<SkillFile>();
    private string? lastError;

    public IReadOnlyList<SkillFile> LibrarySkills { get => librarySkills; private set => Set(ref librarySkills, value); }
    public IReadOnlySet<string> ActiveIds { get => activeIds; private set => Set(ref activeIds, value); }
    public IReadOnlyList<SkillFile> AppSkills { get => appSkills; private set => Set(ref appSkills, value); }
    public string? LastError { get => lastError; set => Set(ref lastError, value); }

    public string UserSkillsDirectory { get; }
    public string AppSkillsDirectory { get; }

    public string LibraryDirectory => Path.Combine(UserSkillsDirectory, "library");
    public string ActiveDirectory => Path.Combine(UserSkillsDirectory, "active");
    public string ActivationsPath => Path.Combine(UserSkillsDirectory, "activations.json");

    /// <summary>Activated skills that apply to the voice / teacher prompts.</summary>
    public IReadOnlyList<SkillFile> ActiveTalkSkills =>
        LibrarySkills.Where(skill => ActiveIds.Contains(skill.Id) && skill.IsForTalk).ToList();

    private readonly Func<DateTimeOffset> now;
    private readonly Func<string, string, bool>? createJunction;
    private readonly Action<Action> post;
    private readonly object reloadLock = new();

    /// <summary>Watches `library/` (skills added or removed) and the root (activations.json rewritten by the CLI).</summary>
    private readonly List<FileSystemWatcher> watchers = new();
    private Timer? reloadDebounce;

    /// <param name="userSkillsDirectory">`%USERPROFILE%\.openclicky\skills` in the app.</param>
    /// <param name="appSkillsDirectory">`app-skills/` of the checkout.</param>
    /// <param name="watch">Watch the directories for changes made by the CLI or by hand.</param>
    /// <param name="now">Clock for activations.json's `updatedAt`.</param>
    /// <param name="createJunction">Creates a directory junction (link path, target path) → success. Optional; see the file header.</param>
    /// <param name="post">Runs a background reload (directory watcher) where the UI may observe it — the
    /// app passes its dispatcher. Defaults to running inline on the timer thread.</param>
    public SkillLibraryStore(
        string userSkillsDirectory,
        string appSkillsDirectory,
        bool watch = true,
        Func<DateTimeOffset>? now = null,
        Func<string, string, bool>? createJunction = null,
        Action<Action>? post = null)
    {
        UserSkillsDirectory = userSkillsDirectory;
        AppSkillsDirectory = appSkillsDirectory;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.createJunction = createJunction;
        this.post = post ?? (action => action());
        EnsureDirectories();
        Reload();
        if (watch) StartWatching();
    }

    public void Dispose()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        reloadDebounce?.Dispose();
    }

    // MARK: - Reading

    /// <summary>
    /// Re-reads the library, the activations, and the app skills; drops stale `active/` entries.
    /// `clearingErrors`: an explicit reload (launch, user action) drops a stale LastError from an
    /// earlier failure; a background reload (directory watcher, the reload after a failed save) keeps
    /// it so the HUD still shows what went wrong. SyncActiveDirectory sets it again if linking fails.
    /// </summary>
    public void Reload(bool clearingErrors = true)
    {
        lock (reloadLock)
        {
            EnsureDirectories();
            LibrarySkills = SkillFile.Load(LibraryDirectory);
            var known = LibrarySkills.Select(skill => skill.Id).ToHashSet(StringComparer.Ordinal);
            ActiveIds = ReadActivations().Where(known.Contains).ToHashSet(StringComparer.Ordinal);
            AppSkills = SkillFile.Load(AppSkillsDirectory);
            if (clearingErrors) LastError = null;
            SyncActiveDirectory();
        }
    }

    private List<string> ReadActivations()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(ActivationsPath, Encoding.UTF8));
            if (node is not JsonObject json || json["active"] is not JsonArray active) return new List<string>();
            return active
                .OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var text) ? text : null)
                .OfType<string>()
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new List<string>();
        }
    }

    // MARK: - Activation

    public void SetActive(string id, bool on)
    {
        var ids = ReadActivations().Where(existing => existing != id).ToList();
        if (on) ids.Add(id);
        try
        {
            WriteActivations(ids);
            Reload();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not save activations: {exception.Message}";
            Reload(clearingErrors: false);
        }
    }

    private void WriteActivations(List<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = ids.Where(seen.Add).ToList();
        // Keys sorted as the Mac writes them ("active" < "updatedAt"); ISO-8601 without fractions, UTC.
        var payload = new JsonObject
        {
            ["active"] = new JsonArray(unique.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["updatedAt"] = now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        var text = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        WriteAtomically(ActivationsPath, text);
    }

    /// <summary>`active/` holds exactly one entry per activated existing skill (mirrors `syncActiveDir` in the CLI).</summary>
    private void SyncActiveDirectory()
    {
        var wanted = LibrarySkills.Select(skill => skill.Id).Where(ActiveIds.Contains).ToList();
        string[] existing;
        try { existing = Directory.GetFileSystemEntries(ActiveDirectory).Select(path => Path.GetFileName(path)).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { existing = Array.Empty<string>(); }

        foreach (var entry in existing.Where(entry => !wanted.Contains(entry)))
        {
            TryRemoveEntry(Path.Combine(ActiveDirectory, entry));
        }
        foreach (var id in wanted)
        {
            var link = Path.Combine(ActiveDirectory, id);
            var target = Path.Combine(LibraryDirectory, id);
            if (IsCurrentActiveEntry(link, target)) continue;
            TryRemoveEntry(link);
            try
            {
                CreateActiveEntry(link, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(link)) continue; // another writer (the CLI) linked it first
                LastError = $"Could not link {id} into active/: {exception.Message}";
            }
        }
    }

    /// <summary>A link (symlink or junction) to the library folder, or a copy whose SKILL.md matches it.</summary>
    private static bool IsCurrentActiveEntry(string link, string target)
    {
        var info = new DirectoryInfo(link);
        if (!info.Exists && info.LinkTarget is null) return false;
        if (info.LinkTarget is not null)
        {
            return string.Equals(Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(link)!).TrimEnd('\\', '/'),
                Path.GetFullPath(target).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        try
        {
            return File.ReadAllText(Path.Combine(link, "SKILL.md")) == File.ReadAllText(Path.Combine(target, "SKILL.md"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Symlink → junction → copy; see the file header.</summary>
    private void CreateActiveEntry(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(link) || new DirectoryInfo(link).LinkTarget is not null) throw;
        }
        if (OperatingSystem.IsWindows())
        {
            var junctionMade = createJunction is not null ? createJunction(link, target) : TryMklinkJunction(link, target);
            if (junctionMade && Directory.Exists(link)) return;
        }
        CopyDirectory(target, link);
    }

    private static bool TryMklinkJunction(string link, string target)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(link);
            startInfo.ArgumentList.Add(target);
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            if (!process.WaitForExit(5000)) { process.Kill(); return false; }
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>Removes a link without following it, a copied folder with its contents, or a file.</summary>
    private static void TryRemoveEntry(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null)
            {
                // A symlink or junction: deleting it non-recursively removes only the link.
                if (info.Attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(path, recursive: false);
                else File.Delete(path);
            }
            else if (info.Exists)
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Same as the Mac's `try?`: a stale entry left behind is retried on the next reload.
        }
    }

    // MARK: - Removing

    /// <summary>Deletes a library skill: its activation, its `active/` entry and its folder.</summary>
    public void RemoveSkill(string id)
    {
        var ids = ReadActivations().Where(existing => existing != id).ToList();
        try
        {
            WriteActivations(ids);
            TryRemoveEntry(Path.Combine(ActiveDirectory, id));
            var folder = Path.Combine(LibraryDirectory, id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Reload();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not remove {id}: {exception.Message}";
            Reload(clearingErrors: false);
        }
    }

    // MARK: - Creating

    /// <summary>Writes a SKILL.md into the library under a unique id derived from its name and activates it.</summary>
    public SkillFile ImportSkill(string markdown)
    {
        var parsed = SkillFile.Parse(markdown, "pending") ?? throw SkillLibraryException.InvalidMarkdown();
        EnsureDirectories();
        var baseId = Slugify(parsed.Name);
        // Claim the id atomically: .NET's CreateDirectory succeeds on an existing folder, so build the
        // folder under a temporary name and rename it into place; the rename fails when another writer
        // (the CLI, a hand-dropped folder) owns the id, so try the next suffix. Never write SKILL.md
        // into a folder we did not create.
        var id = baseId;
        var suffix = 2;
        var staging = Path.Combine(LibraryDirectory, $".claim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var text = markdown.EndsWith('\n') ? markdown : markdown + "\n";
            File.WriteAllText(Path.Combine(staging, "SKILL.md"), text, new UTF8Encoding(false));
            while (true)
            {
                var folder = Path.Combine(LibraryDirectory, id);
                if (!Directory.Exists(folder) && !File.Exists(folder))
                {
                    try
                    {
                        Directory.Move(staging, folder);
                        break;
                    }
                    catch (IOException) when (Directory.Exists(folder) || File.Exists(folder))
                    {
                        // Lost a race to another writer; fall through to the next suffix.
                    }
                }
                id = $"{baseId}-{suffix}";
                suffix += 1;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        SetActive(id, true);
        return LibrarySkills.FirstOrDefault(skill => skill.Id == id) ?? throw SkillLibraryException.InvalidMarkdown();
    }

    // MARK: - Helpers

    /// <summary>Records the error for the HUD and returns it for throwing.</summary>
    private SkillLibraryException Fail(SkillLibraryException error)
    {
        LastError = error.Message;
        return error;
    }

    private void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(LibraryDirectory);
            Directory.CreateDirectory(ActiveDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void WriteAtomically(string path, string text)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Mirrors `slugify` in agent/src/skillMarkdown.ts: lowercase, non-alphanumerics → "-", trimmed, never empty.</summary>
    public static string Slugify(string name)
    {
        var output = new StringBuilder();
        var pendingDash = false;
        foreach (var character in name.ToLowerInvariant())
        {
            var isAlphanumeric = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (isAlphanumeric)
            {
                if (pendingDash && output.Length > 0) output.Append('-');
                pendingDash = false;
                output.Append(character);
            }
            else
            {
                pendingDash = true;
            }
        }
        return output.Length == 0 ? "skill" : output.ToString();
    }

    private void StartWatching()
    {
        // library/ for skills added or removed; the root for activations.json rewritten by the CLI
        // (`openclicky skills activate|deactivate`) so the HUD toggles follow.
        TryWatch(LibraryDirectory, name => !name.StartsWith(".claim-", StringComparison.Ordinal));
        TryWatch(UserSkillsDirectory, name => name.StartsWith("activations.json", StringComparison.OrdinalIgnoreCase));
    }

    private void TryWatch(string directory, Func<string, bool> relevant)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler onChange = (_, args) => { if (relevant(args.Name ?? string.Empty)) ScheduleReload(); };
            watcher.Created += onChange;
            watcher.Changed += onChange;
            watcher.Deleted += onChange;
            watcher.Renamed += (_, args) =>
            {
                if (relevant(args.Name ?? string.Empty) || relevant(args.OldName ?? string.Empty)) ScheduleReload();
            };
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    /// <summary>One reload per burst of file events (both watchers share the 300 ms debounce).</summary>
    private void ScheduleReload()
    {
        lock (watchers)
        {
            reloadDebounce ??= new Timer(_ => post(() => Reload(clearingErrors: false)));
            reloadDebounce.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
