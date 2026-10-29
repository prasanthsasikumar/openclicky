namespace OpenClicky.Core.Platform;

/// <summary>"Open at login" (SMAppService): the HKCU Run value "OpenClicky".</summary>
public interface ILoginItem
{
    bool IsEnabled { get; }

    /// <summary>Returns the state after the change (registry access can fail; logged by the caller).</summary>
    bool SetEnabled(bool enabled);

    /// <summary>The command line written to the Run key (quoted exe path; the Velopack stub when installed by Velopack).</summary>
    string LaunchCommand { get; }
}

/// <summary>Whether there is a network path (NWPathMonitor). "Online" until the first answer, so a take is never treated as offline by mistake.</summary>
public interface INetworkReachability : IDisposable
{
    bool IsOnline { get; }

    /// <summary>The connection is billed by use (NetworkCostType Variable/Fixed).</summary>
    bool IsMetered { get; }

    /// <summary>Roaming, approaching or over the data limit (the Mac's isConstrained).</summary>
    bool IsConstrained { get; }

    /// <summary>Raised on a background thread when any of the three changes.</summary>
    event Action? Changed;
}

public enum DesktopAppTargetKind
{
    /// <summary>A Start-menu shortcut (.lnk/.url/.appref-ms).</summary>
    Shortcut,
    /// <summary>An executable path (App Paths registry).</summary>
    Executable,
    /// <summary>A packaged (UWP/MSIX) app by its AppUserModelID, launched through shell:AppsFolder.</summary>
    PackagedApp,
}

/// <summary>An app <see cref="IDesktopActionExecutor.FindApplication"/> found.</summary>
public sealed record DesktopAppTarget(string DisplayName, DesktopAppTargetKind Kind, string Target);

/// <summary>The OS side of the fast-lane actions (MacActionRunner's injected closures).</summary>
public interface IDesktopActionExecutor
{
    /// <summary>
    /// The app called <paramref name="name"/>: Start-menu shortcuts (all users + this user), App Paths,
    /// then packaged apps. Exact (case-insensitive) name first, then the name without spaces and
    /// punctuation ("vs code" ≠ "Visual Studio Code", "power point" = "PowerPoint"), then a name
    /// starting with it ("spotify" → "Spotify Premium"), then a name containing all its words
    /// ("code" → "Visual Studio Code"); the shortest name wins each step. Uninstallers are never matched.
    /// </summary>
    DesktopAppTarget? FindApplication(string name);

    bool LaunchApplication(DesktopAppTarget target);

    /// <summary>Opens an http(s) URL in the default browser (other schemes are refused).</summary>
    bool OpenUrl(Uri url);

    /// <summary>"desktop", "downloads", "documents", "home" → the known folder's real path (redirected folders included); null otherwise.</summary>
    string? KnownFolderPath(string location);

    /// <summary>explorer /select,"path".</summary>
    bool RevealInExplorer(string path);

    /// <summary>The default output device's master volume, 0–100.</summary>
    bool SetVolume(int percent);

    int? GetVolume();

    /// <summary>"playpause", "next", "previous", "mute" as media keys.</summary>
    bool SendMediaKey(string action);

    // The newer actions (MacActionRunner.Extras). Each defaults to "nothing happened", so a test
    // implements only the ones it drives.

    /// <summary>
    /// Pause or play sent to the player itself (Windows' media sessions), never the play/pause key,
    /// which toggles and so would start something when nothing plays. False when no player took it.
    /// </summary>
    Task<bool> ControlPlayerAsync(PlayerCommand command) => Task.FromResult(false);

    /// <summary>Minimizes every ordinary window on screen except the named app's; how many, or null when that app is not running.</summary>
    int? MinimizeAllWindows(string? keepApp) => 0;

    /// <summary>Brings back what the last MinimizeAllWindows minimized, or only the named app's windows; how many, or null when that app is not running.</summary>
    int? RestoreMinimizedWindows(string? app) => 0;

    bool SpotifyIsInstalled() => false;

    bool SpotifyIsRunning() => false;

    /// <summary>Starts Spotify and waits until its window is up; false when it did not come up in time.</summary>
    Task<bool> LaunchSpotifyAsync() => Task.FromResult(false);

    /// <summary>Plays a spotify:track: URI in the desktop app.</summary>
    Task<SpotifyPlayResult> PlaySpotifyTrackAsync(string trackUri, string trackName) => Task.FromResult(SpotifyPlayResult.Failed);

    /// <summary>Opens a spotify: URI (Spotify's own search page) in the desktop app.</summary>
    bool OpenSpotifyUri(string uri) => false;
}

public enum PlayerCommand
{
    Pause,
    Play,
}

public enum SpotifyPlayResult
{
    /// <summary>Spotify's media session reports the track playing.</summary>
    Playing,
    /// <summary>Spotify took the URI, but no media session confirmed playback.</summary>
    Opened,
    Failed,
}

public sealed record ProcessLaunchRequest
{
    /// <summary>A command name ("openclicky", "claude", "codex") resolved through PATH + PATHEXT and %APPDATA%\npm, or a full path.</summary>
    public required string Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    /// <summary>Variables to set (a null value removes the variable).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>A started child process. Output lines arrive through the channels; disposing kills it.</summary>
public interface IRunningProcess : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>Stdout lines (UTF-8), completed when the stream closes.</summary>
    System.Threading.Channels.ChannelReader<string> StandardOutputLines { get; }

    System.Threading.Channels.ChannelReader<string> StandardErrorLines { get; }

    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    void CloseStandardInput();

    /// <summary>Kills the process and its children.</summary>
    void Kill();

    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}

public interface IProcessRunner
{
    /// <summary>The executable a command name resolves to, or null when it is not installed.</summary>
    string? ResolveExecutable(string command);

    /// <summary>Starts the process; cancelling <paramref name="cancellationToken"/> kills it (and its children).</summary>
    IRunningProcess Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs to completion, writing <paramref name="standardInput"/> first, collecting output.</summary>
    Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunnerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The UI thread (the WPF Dispatcher). Core models post their UI-facing state changes here.</summary>
public interface IMainThread
{
    bool IsMainThread { get; }

    void Post(Action action);

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> function);

    Task InvokeAsync(Func<Task> function);
}

/// <summary>One OpenClicky per user session: a named mutex, and a named pipe that forwards a second launch's arguments.</summary>
public interface ISingleInstance : IDisposable
{
    /// <summary>
    /// True when this process is the one that runs (and starts listening for forwarded arguments).
    /// False means another instance runs: <paramref name="args"/> were forwarded to it and this one should exit.
    /// </summary>
    bool TryBecomePrimary(IReadOnlyList<string> args);

    /// <summary>A later launch's arguments (raised on a background thread).</summary>
    event Action<IReadOnlyList<string>>? ArgumentsReceived;

    /// <summary>Starts a new copy of this exe (which waits for this one to exit before taking over), then the caller shuts down.</summary>
    void Relaunch(IReadOnlyList<string>? args = null);
}
