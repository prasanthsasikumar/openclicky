// Port of macos/OpenClicky/OpenClicky/PlanCLIs.swift
//
// The two command-line tools a person may already pay for through a plan rather than a key: the
// Claude Code CLI (`claude`, a Claude plan) and Codex (`codex`, a ChatGPT plan). This file finds
// them, says whether they are signed in, and runs them; ClaudeCodeAnswerProvider and
// CodexAnswerProvider build the actual questions.
//
// Windows: an app started from the Start menu has the PATH it was started with, so a CLI installed
// since then is not on it. They are looked up in the usual install folders first (the native
// Claude Code installer's %USERPROFILE%\.local\bin, npm's %APPDATA%\npm shims, %LOCALAPPDATA%\Programs),
// then on the PATH (this process's, plus the user's and the machine's as saved, which is where a
// new install lands), trying each PATHEXT extension the way cmd.exe does. That replaces the Mac's
// login-shell lookup.
//
// npm installs a CLI as a `.cmd` shim. cmd.exe cannot carry a multi-line argument (the system
// prompt), so a shim is read and its Node script run with node directly; only a shim that cannot be
// read goes through `cmd.exe /d /s /c` with every argument escaped for cmd. A cancelled or timed-out
// run kills the whole process tree (a shim's node child included).

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Answers;

/// <summary>Whether a plan CLI can answer right now.</summary>
public enum PlanCliStatus
{
    NotInstalled,
    /// <summary>
    /// Installed, but `claude auth status` / `codex login status` says nobody is signed in (or the
    /// check could not tell within its timeout, which the person fixes the same way: open the CLI).
    /// </summary>
    InstalledSignedOut,
    Ready,
}

/// <summary>What a finished CLI run left behind.</summary>
/// <param name="TimedOut">The run was stopped because it went past its timeout; `ExitStatus` is then whatever the kill left.</param>
public sealed record PlanCliProcessOutput(int ExitStatus, byte[] StandardOutput, byte[] StandardError, bool TimedOut)
{
    public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
    public string StandardErrorText => Encoding.UTF8.GetString(StandardError);

    public bool Equals(PlanCliProcessOutput? other) =>
        other is not null && ExitStatus == other.ExitStatus && TimedOut == other.TimedOut
        && StandardOutput.AsSpan().SequenceEqual(other.StandardOutput) && StandardError.AsSpan().SequenceEqual(other.StandardError);

    public override int GetHashCode() => HashCode.Combine(ExitStatus, TimedOut, StandardOutput.Length, StandardError.Length);
}

/// <summary>
/// Runs one CLI process. The answer providers and status checks take this as a parameter so the
/// tests can hand them a fake that never starts a process.
/// </summary>
public interface IPlanCliProcessRunner
{
    /// <summary>
    /// `onStandardOutputChunk` is called on a background thread with each piece of stdout as it
    /// arrives, in order, before the returned output (which also holds all of stdout). Cancelling
    /// ends the process tree and throws OperationCanceledException.
    /// </summary>
    Task<PlanCliProcessOutput> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        double timeoutSeconds,
        Action<byte[]>? onStandardOutputChunk,
        CancellationToken cancellationToken = default);
}

public static class PlanCLIs
{
    /// <summary>
    /// Short: these run while Settings is open or before a question, and a CLI that cannot say
    /// whether it is signed in within this long is not going to answer quickly either.
    /// </summary>
    public const double StatusCheckTimeoutSeconds = 5;

    /// <summary>cmd.exe's own list when PATHEXT is not set.</summary>
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // MARK: finding the executables

    /// <summary>
    /// The folders a CLI is usually installed into, in the order they are tried: the native Claude
    /// Code installer (%USERPROFILE%\.local\bin), npm's global shims (%APPDATA%\npm), npm's suggested
    /// user prefix, Bun, Claude Code's older local install, Scoop's shims, and the per-user
    /// %LOCALAPPDATA%\Programs install of the named tool.
    /// </summary>
    public static IReadOnlyList<string> CommonInstallDirectories(string homeDirectory, string? appDataDirectory = null,
                                                                 string? localAppDataDirectory = null, string? executableName = null)
    {
        appDataDirectory ??= Path.Combine(homeDirectory, "AppData", "Roaming");
        localAppDataDirectory ??= Path.Combine(homeDirectory, "AppData", "Local");
        var directories = new List<string>
        {
            Path.Combine(homeDirectory, ".local", "bin"),
            Path.Combine(appDataDirectory, "npm"),
            Path.Combine(homeDirectory, ".npm-global"),
            Path.Combine(homeDirectory, ".npm-global", "bin"),
            Path.Combine(homeDirectory, ".bun", "bin"),
            Path.Combine(homeDirectory, ".claude", "local"),
            Path.Combine(homeDirectory, "scoop", "shims"),
        };
        if (!string.IsNullOrEmpty(executableName))
        {
            directories.Add(Path.Combine(localAppDataDirectory, "Programs", executableName));
            directories.Add(Path.Combine(localAppDataDirectory, "Programs", executableName, "bin"));
        }
        return directories;
    }

    /// <summary>The account's real home folder, where CLIs are installed (%USERPROFILE%).</summary>
    public static string RealHomeDirectory
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(profile) ? OpenClickyPaths.HomeDirectory : profile;
        }
    }

    private static string RealAppDataDirectory => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData
        ? appData : Path.Combine(RealHomeDirectory, "AppData", "Roaming");

    private static string RealLocalAppDataDirectory => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localAppData
        ? localAppData : Path.Combine(RealHomeDirectory, "AppData", "Local");

    /// <summary>The extensions a bare name may have, in PATHEXT order (".EXE" before ".CMD" by default).</summary>
    public static IReadOnlyList<string> ExecutableExtensions(string? pathExt) =>
        (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(extension => extension.StartsWith('.'))
            .ToList();

    /// <summary>`directory\name` with each extension tried, the way cmd.exe resolves a bare name.</summary>
    public static IEnumerable<string> CandidatePaths(string directory, string executableName, IReadOnlyList<string> extensions)
    {
        if (Path.HasExtension(executableName)) yield return Path.Combine(directory, executableName);
        foreach (var extension in extensions) yield return Path.Combine(directory, executableName + extension.ToLowerInvariant());
    }

    /// <summary>The first executable named `executableName` in the common install folders.</summary>
    public static string? PathInCommonInstallDirectories(string executableName, string? homeDirectory = null,
                                                         Func<string, bool>? isExecutableFile = null, string? pathExt = null,
                                                         string? appDataDirectory = null, string? localAppDataDirectory = null)
    {
        homeDirectory ??= RealHomeDirectory;
        if (homeDirectory == RealHomeDirectory)
        {
            appDataDirectory ??= RealAppDataDirectory;
            localAppDataDirectory ??= RealLocalAppDataDirectory;
        }
        return PathsInCommonInstallDirectories(executableName, homeDirectory, isExecutableFile, pathExt, appDataDirectory, localAppDataDirectory).FirstOrDefault();
    }

    /// <summary>Every executable named `executableName` in the common install folders, in order.</summary>
    public static IEnumerable<string> PathsInCommonInstallDirectories(string executableName, string homeDirectory,
                                                                      Func<string, bool>? isExecutableFile = null, string? pathExt = null,
                                                                      string? appDataDirectory = null, string? localAppDataDirectory = null)
    {
        isExecutableFile ??= File.Exists;
        var extensions = ExecutableExtensions(pathExt ?? Environment.GetEnvironmentVariable("PATHEXT"));
        foreach (var directory in CommonInstallDirectories(homeDirectory, appDataDirectory, localAppDataDirectory, executableName))
        {
            // One per folder: claude.exe and claude.cmd side by side are the same install.
            var found = CandidatePaths(directory, executableName, extensions).FirstOrDefault(isExecutableFile);
            if (found is not null) yield return found;
        }
    }

    /// <summary>
    /// Every executable named `executableName` on a PATH value, in PATH order, each folder trying
    /// the PATHEXT extensions (the Windows replacement for the Mac's login-shell `command -v`).
    /// </summary>
    public static IReadOnlyList<string> PathsOnSearchPath(string executableName, string? pathVariable, string? pathExt = null,
                                                          Func<string, bool>? isExecutableFile = null)
    {
        isExecutableFile ??= File.Exists;
        var extensions = ExecutableExtensions(pathExt ?? Environment.GetEnvironmentVariable("PATHEXT"));
        var found = new List<string>();
        foreach (var rawEntry in (pathVariable ?? "").Split(Path.PathSeparator))
        {
            var directory = Environment.ExpandEnvironmentVariables(rawEntry.Trim().Trim('"'));
            if (directory.Length == 0) continue;
            var candidate = CandidatePaths(directory, executableName, extensions).FirstOrDefault(isExecutableFile);
            if (candidate is not null && !found.Contains(candidate, PathComparer)) found.Add(candidate);
        }
        return found;
    }

    /// <summary>
    /// The PATH to search: this process's, then the user's and the machine's as saved (a CLI
    /// installed while OpenClicky runs is only on those).
    /// </summary>
    public static string SearchPathVariable()
    {
        var entries = new List<string>();
        void AddEntries(string? value)
        {
            foreach (var entry in (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!entries.Contains(entry, PathComparer)) entries.Add(entry);
            }
        }
        AddEntries(Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
        {
            AddEntries(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddEntries(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        return string.Join(Path.PathSeparator, entries);
    }

    /// <summary>Finds `executableName`: common folders first, then the PATH.</summary>
    public static string? LocateExecutable(string executableName) =>
        PathInCommonInstallDirectories(executableName) ?? PathsOnSearchPath(executableName, SearchPathVariable()).FirstOrDefault();

    /// <summary>
    /// Claude Code, the newest copy on this PC: an old npm install left in %APPDATA%\npm next to a
    /// current native one in ~\.local\bin refused options the provider passes ("unknown option").
    /// </summary>
    public static async Task<string?> ClaudeCodePathAsync(IPlanCliProcessRunner? processRunner = null, CancellationToken cancellationToken = default)
    {
        processRunner ??= new PlanCliProcessRunner();
        var candidates = PathsInCommonInstallDirectories("claude", RealHomeDirectory, appDataDirectory: RealAppDataDirectory,
                                                         localAppDataDirectory: RealLocalAppDataDirectory).ToList();
        foreach (var pathCandidate in PathsOnSearchPath("claude", SearchPathVariable()))
        {
            if (!candidates.Contains(pathCandidate, PathComparer)) candidates.Add(pathCandidate);
        }
        if (candidates.Count <= 1) return candidates.FirstOrDefault();
        var versions = new List<(string Path, string Version)>();
        foreach (var candidate in candidates)
        {
            try
            {
                var output = await processRunner.RunAsync(candidate, ["--version"], null, Path.GetTempPath(),
                                                          OpenClickyConfiguration.ProcessEnvironment(), StatusCheckTimeoutSeconds, null,
                                                          cancellationToken).ConfigureAwait(false);
                if (!output.TimedOut && output.ExitStatus == 0) versions.Add((candidate, output.StandardOutputText));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A copy that does not start is not the one to use.
            }
        }
        var newest = NewestPath(versions) ?? candidates.FirstOrDefault();
        AppLog.Write($"plans: claude code at {string.Join(", ", candidates)}; using {newest ?? "none"}");
        return newest;
    }

    /// <summary>The path whose `--version` output names the highest version ("2.1.296 (Claude Code)").</summary>
    public static string? NewestPath(IReadOnlyList<(string Path, string Version)> versions)
    {
        static int[] Numbers(string text)
        {
            var versionText = Regex.Match(text, @"[0-9.]+").Value;
            return versionText.Split('.').Select(part => int.TryParse(part, out var number) ? (int?)number : null)
                .OfType<int>().ToArray();
        }
        static int Compare(int[] first, int[] second)
        {
            for (var index = 0; index < Math.Max(first.Length, second.Length); index++)
            {
                var left = index < first.Length ? first[index] : 0;
                var right = index < second.Length ? second[index] : 0;
                if (left != right) return left.CompareTo(right);
            }
            return 0;
        }
        string? newestPath = null;
        int[]? newestNumbers = null;
        foreach (var (path, version) in versions)
        {
            var numbers = Numbers(version);
            // Swift's `max` keeps the last of equals.
            if (newestNumbers is null || Compare(newestNumbers, numbers) <= 0)
            {
                newestPath = path;
                newestNumbers = numbers;
            }
        }
        return newestPath;
    }

    public static Task<string?> CodexPathAsync() => Task.Run(() => LocateExecutable("codex"));

    // MARK: signed in or not

    /// <summary>
    /// `claude auth status` prints JSON (`{"loggedIn": true, "authMethod": "claude.ai", ...}`) and
    /// exits 1 when signed out. It reads the local credentials only — no model call, no cost, about
    /// 0.2 s — which makes it the cheapest reliable check Claude Code offers.
    /// </summary>
    public static PlanCliStatus ClaudeCodeStatus(PlanCliProcessOutput output)
    {
        if (output.TimedOut) return PlanCliStatus.InstalledSignedOut;
        try
        {
            using var document = JsonDocument.Parse(output.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return PlanCliStatus.InstalledSignedOut;
            }
            return loggedIn.GetBoolean() ? PlanCliStatus.Ready : PlanCliStatus.InstalledSignedOut;
        }
        catch (JsonException)
        {
            return PlanCliStatus.InstalledSignedOut;
        }
    }

    /// <summary>
    /// `codex login status` prints "Logged in using ChatGPT" (or "... an API key") to stderr and
    /// exits 0; signed out it prints "Not logged in" and exits 1.
    /// </summary>
    public static PlanCliStatus CodexStatus(PlanCliProcessOutput output)
    {
        if (output.TimedOut || output.ExitStatus != 0) return PlanCliStatus.InstalledSignedOut;
        var combinedText = (output.StandardOutputText + "\n" + output.StandardErrorText).ToLowerInvariant();
        if (combinedText.Contains("not logged in")) return PlanCliStatus.InstalledSignedOut;
        return combinedText.Contains("logged in") ? PlanCliStatus.Ready : PlanCliStatus.InstalledSignedOut;
    }

    public static async Task<PlanCliStatus> ClaudeCodeStatusAsync(string? executablePath, IPlanCliProcessRunner? processRunner = null,
                                                                 CancellationToken cancellationToken = default)
    {
        if (executablePath is null) return PlanCliStatus.NotInstalled;
        processRunner ??= new PlanCliProcessRunner();
        try
        {
            var output = await processRunner.RunAsync(executablePath, ["auth", "status", "--json"], null, Path.GetTempPath(),
                                                      ProcessEnvironment(executablePath), StatusCheckTimeoutSeconds, null, cancellationToken).ConfigureAwait(false);
            return ClaudeCodeStatus(output);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return PlanCliStatus.InstalledSignedOut;
        }
    }

    public static async Task<PlanCliStatus> CodexStatusAsync(string? executablePath, IPlanCliProcessRunner? processRunner = null,
                                                            CancellationToken cancellationToken = default)
    {
        if (executablePath is null) return PlanCliStatus.NotInstalled;
        processRunner ??= new PlanCliProcessRunner();
        try
        {
            var output = await processRunner.RunAsync(executablePath, ["login", "status"], null, Path.GetTempPath(),
                                                      ProcessEnvironment(executablePath), StatusCheckTimeoutSeconds, null, cancellationToken).ConfigureAwait(false);
            return CodexStatus(output);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return PlanCliStatus.InstalledSignedOut;
        }
    }

    /// <summary>Locates Claude Code and checks it, off the UI thread.</summary>
    public static async Task<PlanCliStatus> ClaudeCodeStatusAsync() =>
        await ClaudeCodeStatusAsync(await Task.Run(() => ClaudeCodePathAsync()).ConfigureAwait(false)).ConfigureAwait(false);

    /// <summary>Locates Codex and checks it, off the UI thread.</summary>
    public static async Task<PlanCliStatus> CodexStatusAsync() =>
        await CodexStatusAsync(await CodexPathAsync().ConfigureAwait(false)).ConfigureAwait(false);

    // MARK: the environment a plan CLI runs in

    /// <summary>
    /// Environment variables that would make the CLI bill something other than the person's plan,
    /// or that mark a nested Claude Code session (present when OpenClicky itself was started from a
    /// terminal inside Claude Code) and would make `claude -p` talk to the outer session.
    /// </summary>
    public static readonly IReadOnlyList<string> EnvironmentVariablesRemovedForPlanCLIs =
    [
        // An API key in the environment wins over the plan's sign-in in both CLIs.
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY",
        // Codex must use the person's own login (%USERPROFILE%\.codex), never OpenClicky's agent home.
        "CODEX_HOME",
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_CODE_EXECPATH", "CLAUDE_PID", "CLAUDE_EFFORT", "AI_AGENT",
    ];

    /// <summary>
    /// The app's environment with the plan-overriding variables removed and a PATH that can run
    /// the CLI: Codex is a Node script, so `node` must resolve, and an npm install keeps it next to
    /// `codex` — hence the executable's own folder first, and Node's usual install folder.
    /// </summary>
    public static Dictionary<string, string> ProcessEnvironment(string executablePath, IReadOnlyDictionary<string, string>? baseEnvironment = null,
                                                                string? homeDirectory = null, string? appDataDirectory = null,
                                                                string? localAppDataDirectory = null)
    {
        baseEnvironment ??= OpenClickyConfiguration.ProcessEnvironment();
        homeDirectory ??= RealHomeDirectory;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(baseEnvironment, comparer);
        foreach (var variableName in EnvironmentVariablesRemovedForPlanCLIs) environment.Remove(variableName);
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? "";
        environment.TryGetValue("PATH", out var inheritedPath);
        if (string.IsNullOrEmpty(inheritedPath)) inheritedPath = DefaultSystemPath();
        var extraDirectories = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) extraDirectories.Add(Path.Combine(programFiles, "nodejs"));
        var pathEntries = new List<string>();
        foreach (var entry in new[] { executableDirectory }
                     .Concat(CommonInstallDirectories(homeDirectory, appDataDirectory, localAppDataDirectory))
                     .Concat(extraDirectories)
                     .Concat(inheritedPath.Split(Path.PathSeparator)))
        {
            if (entry.Length > 0 && !pathEntries.Contains(entry, PathComparer)) pathEntries.Add(entry);
        }
        environment["PATH"] = string.Join(Path.PathSeparator, pathEntries);
        return environment;
    }

    private static string DefaultSystemPath()
    {
        if (!OperatingSystem.IsWindows()) return "/usr/bin:/bin:/usr/sbin:/sbin";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.Join(Path.PathSeparator, Path.Combine(windows, "System32"), windows);
    }

    /// <summary>
    /// A fresh empty folder for one run, under the temporary folder: the CLIs treat their working
    /// folder as a project and may read files there, so it must not be the person's home or
    /// Documents. The caller removes it afterwards.
    /// </summary>
    public static string MakeScratchWorkingDirectory()
    {
        var scratchDirectory = Path.Combine(Path.GetTempPath(), "openclicky-plan-answers", Guid.NewGuid().ToString().ToUpperInvariant());
        Directory.CreateDirectory(scratchDirectory);
        return scratchDirectory;
    }

    /// <summary>Removes a scratch folder, quietly (a file a dying process still holds stays behind).</summary>
    public static void RemoveScratchWorkingDirectory(string scratchDirectory)
    {
        try { Directory.Delete(scratchDirectory, recursive: true); }
        catch (Exception) { /* left for the temp folder's own clean-up */ }
    }

    /// <summary>The last non-empty line of a CLI's stderr, which is where both CLIs put a one-line reason.</summary>
    public static string? LastNonEmptyLine(string text) =>
        text.Split('\n', '\r').Select(line => line.Trim(' ', '\t')).LastOrDefault(line => line.Length > 0);
}

// MARK: - Launching on Windows

/// <summary>How a CLI path becomes a process: an .exe directly, an npm shim through its Node script, anything else through cmd.exe.</summary>
public sealed record PlanCliLaunch(string FileName, IReadOnlyList<string> ArgumentList, string? RawArguments)
{
    private static readonly Regex NpmShimScriptPattern = new(@"""%~?dp0%?\\([^""]+?\.(?:c|m)?js)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Characters cmd.exe treats specially (cross-spawn's list).</summary>
    private static readonly Regex CmdMetaCharacters = new(@"([()\][%!^""`<>&|;, *?])", RegexOptions.CultureInvariant);

    public static PlanCliLaunch Resolve(string executablePath, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
                                        Func<string, string?>? readShim = null, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        readShim ??= path =>
        {
            try { return File.ReadAllText(path); }
            catch (Exception) { return null; }
        };
        var extension = Path.GetExtension(executablePath).ToLowerInvariant();
        if (extension is not (".cmd" or ".bat")) return new PlanCliLaunch(executablePath, arguments, null);

        // An npm shim: run its script with node, which takes any argument as it is.
        if (NpmShimScript(executablePath, readShim, fileExists) is { } scriptPath)
        {
            var shimDirectory = Path.GetDirectoryName(executablePath) ?? "";
            var bundledNode = Path.Combine(shimDirectory, "node.exe");
            environment.TryGetValue("PATH", out var pathVariable);
            var node = fileExists(bundledNode)
                ? bundledNode
                : PlanCLIs.PathsOnSearchPath("node", pathVariable, ".EXE", fileExists).FirstOrDefault();
            if (node is not null) return new PlanCliLaunch(node, [scriptPath, .. arguments], null);
        }

        // Anything else: cmd.exe, every part escaped. A line break would end cmd's command line, so
        // multi-line arguments are folded onto one line (the one thing that cannot be kept).
        var commandLine = string.Join(" ", new[] { EscapeForCmd(executablePath) }.Concat(arguments.Select(argument =>
            EscapeForCmd(argument.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ')))));
        var commandInterpreter = environment.TryGetValue("ComSpec", out var comSpec) && !string.IsNullOrEmpty(comSpec) ? comSpec : "cmd.exe";
        return new PlanCliLaunch(commandInterpreter, [], $"/d /s /c \"{commandLine}\"");
    }

    /// <summary>The Node script an npm `.cmd` shim runs ("%dp0%\node_modules\…\cli.js"), when the shim names one that exists.</summary>
    public static string? NpmShimScript(string shimPath, Func<string, string?> readShim, Func<string, bool> fileExists)
    {
        if (readShim(shimPath) is not { } shimText) return null;
        var matches = NpmShimScriptPattern.Matches(shimText);
        if (matches.Count == 0) return null;
        var relativeScript = matches[^1].Groups[1].Value;
        var scriptPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shimPath) ?? "", relativeScript));
        return fileExists(scriptPath) ? scriptPath : null;
    }

    /// <summary>
    /// One argument for `cmd.exe /d /s /c "…"`: quoted the way the C runtime reads it back, then
    /// every cmd metacharacter escaped with ^ so cmd passes it through untouched.
    /// </summary>
    public static string EscapeForCmd(string argument)
    {
        // Backslashes before a quote are doubled and the quote escaped; trailing backslashes doubled.
        var quoted = Regex.Replace(argument, @"(\\*)""", match => match.Groups[1].Value + match.Groups[1].Value + "\\\"");
        quoted = Regex.Replace(quoted, @"(\\*)$", match => match.Groups[1].Value + match.Groups[1].Value);
        quoted = "\"" + quoted + "\"";
        return CmdMetaCharacters.Replace(quoted, "^$1");
    }
}

// MARK: - The real process runner

/// <summary>
/// Runs the CLI with System.Diagnostics.Process. stdout and stderr are each drained until end of
/// file (a full pipe would otherwise stall the child), stdin is written on its own task so a large
/// request (base64 screenshots) cannot block the caller, and the run is stopped (the whole process
/// tree killed) at the timeout or when the caller cancels.
/// </summary>
public sealed class PlanCliProcessRunner : IPlanCliProcessRunner
{
    public async Task<PlanCliProcessOutput> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        double timeoutSeconds,
        Action<byte[]>? onStandardOutputChunk,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var launch = PlanCliLaunch.Resolve(executablePath, arguments, environment);
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (launch.RawArguments is { } rawArguments) startInfo.Arguments = rawArguments;
        else foreach (var argument in launch.ArgumentList) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var (name, value) in environment) startInfo.Environment[name] = value;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            throw new IOException($"couldn't start {executablePath}: {error.Message}", error);
        }

        var standardOutput = new MemoryStream();
        var standardError = new MemoryStream();
        var stateLock = new object();
        var timedOut = false;
        var stopped = false;

        void Stop(bool becauseOfTimeout)
        {
            lock (stateLock)
            {
                if (stopped) return;
                try
                {
                    if (process.HasExited) return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                stopped = true;
                if (becauseOfTimeout) timedOut = true;
            }
            try { process.Kill(entireProcessTree: true); }
            catch (Exception) { /* it ended on its own meanwhile */ }
        }

        var standardOutputTask = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            var stream = process.StandardOutput.BaseStream;
            while (true)
            {
                var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                var chunk = buffer.AsSpan(0, count).ToArray();
                lock (stateLock) standardOutput.Write(chunk);
                onStandardOutputChunk?.Invoke(chunk);
            }
        });
        var standardErrorTask = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            var stream = process.StandardError.BaseStream;
            while (true)
            {
                var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                lock (stateLock) standardError.Write(buffer, 0, count);
            }
        });
        var standardInputTask = Task.Run(async () =>
        {
            var stream = process.StandardInput.BaseStream;
            try
            {
                // A child that exits before reading all of stdin makes the write fail; nothing more.
                if (standardInput is { Length: > 0 }) await stream.WriteAsync(standardInput).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception) { }
            finally
            {
                try { process.StandardInput.Close(); } catch (Exception) { }
            }
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var timeoutRegistration = timeout.Token.Register(() => Stop(becauseOfTimeout: true));
        using var cancellationRegistration = cancellationToken.Register(() => Stop(becauseOfTimeout: false));

        // Three things must finish before the result is complete: stdout reaches end of file,
        // stderr reaches end of file, and the process exits.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask, standardInputTask).ConfigureAwait(false);
        }
        catch (Exception) when (stopped)
        {
            // A killed tree can break the pipes mid-read.
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateLock)
        {
            return new PlanCliProcessOutput(process.ExitCode, standardOutput.ToArray(), standardError.ToArray(), timedOut);
        }
    }
}
