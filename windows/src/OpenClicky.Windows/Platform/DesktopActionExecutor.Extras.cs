// The OS side of the newer fast-lane actions (MacActionRunner.live's Extras): minimizing and
// restoring windows (WindowMinimizer.swift's MinimizedWindowsMemory), pause and play through Windows'
// media sessions (the Mac scripts Music and Spotify), and Spotify's desktop app (the Mac's
// SpotifyPlayer, which plays a URI by AppleScript; Windows opens the spotify: URI and checks the
// media session to know whether it really started).

using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using Windows.Media.Control;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

public sealed partial class DesktopActionExecutor
{
    private readonly WindowList windowList = new();
    private readonly object minimizedLock = new();
    /// <summary>
    /// What "minimize everything" minimized, newest set first, each with the window that was in front so
    /// it comes back in front. Held by handle: restoring by place also brought back a window the person
    /// had minimized themselves (the Mac's 2026-10-28 fix).
    /// </summary>
    private readonly List<(List<MinimizedWindow> Windows, nint FrontWindow)> minimizedSets = [];

    private sealed record MinimizedWindow(nint Handle, int ProcessId);

    // MARK: - Windows

    public int? MinimizeAllWindows(string? keepApp)
    {
        IReadOnlySet<int> keptProcessIds = new HashSet<int>();
        if (keepApp is not null)
        {
            keptProcessIds = WindowMinimizer.ProcessIdsOfAppsNamed(keepApp, RunningAppNames());
            if (keptProcessIds.Count == 0) return null;
        }
        var frontWindow = GetForegroundWindow();
        var minimized = new List<MinimizedWindow>();
        foreach (var window in WindowMinimizer.WindowsToMinimize(windowList.VisibleWindows(), Environment.ProcessId, keptProcessIds))
        {
            // Async: a hung app's window must not hold the voice turn up.
            if (ShowWindowAsync(window.Handle, SW_MINIMIZE)) minimized.Add(new MinimizedWindow(window.Handle, window.ProcessId));
        }
        lock (minimizedLock)
        {
            if (minimized.Count > 0) minimizedSets.Add((minimized, frontWindow));
        }
        if (keptProcessIds.Count > 0) BringToFront(TopLevelWindowsOf(keptProcessIds, minimizedOnly: false).FirstOrDefault());
        return minimized.Count;
    }

    /// <summary>
    /// With no app: brings back the last set. With an app: brings back that app's minimized windows only
    /// (every one, the person named the app), takes them out of what "bring them back" would restore
    /// later, and brings the app to the front. Null when the named app is not running.
    /// </summary>
    public int? RestoreMinimizedWindows(string? app)
    {
        if (app is null)
        {
            (List<MinimizedWindow> Windows, nint FrontWindow) last;
            lock (minimizedLock)
            {
                if (minimizedSets.Count == 0) return 0;
                last = minimizedSets[^1];
                minimizedSets.RemoveAt(minimizedSets.Count - 1);
            }
            var restored = last.Windows.Count(window => IsWindow(window.Handle) && IsIconic(window.Handle) && ShowWindowAsync(window.Handle, SW_RESTORE));
            BringToFront(last.FrontWindow);
            return restored;
        }
        var processIds = WindowMinimizer.ProcessIdsOfAppsNamed(app, RunningAppNames());
        if (processIds.Count == 0) return null;
        lock (minimizedLock)
        {
            for (var index = minimizedSets.Count - 1; index >= 0; index--)
            {
                minimizedSets[index].Windows.RemoveAll(window => processIds.Contains(window.ProcessId));
                if (minimizedSets[index].Windows.Count == 0) minimizedSets.RemoveAt(index);
            }
        }
        var appWindows = TopLevelWindowsOf(processIds, minimizedOnly: true);
        var count = appWindows.Count(handle => ShowWindowAsync(handle, SW_RESTORE));
        BringToFront(TopLevelWindowsOf(processIds, minimizedOnly: false).FirstOrDefault());
        return count;
    }

    /// <summary>The apps with a window the person can see or has minimized: what a spoken app name can mean.</summary>
    private static List<RunningAppName> RunningAppNames()
    {
        var processIds = new HashSet<int>();
        EnumWindows((windowHandle, _) =>
        {
            if (IsWindowVisible(windowHandle) && ((long)GetWindowLongPtr(windowHandle, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0)
            {
                GetWindowThreadProcessId(windowHandle, out var processId);
                if (processId != 0 && processId != Environment.ProcessId) processIds.Add((int)processId);
            }
            return true;
        }, 0);
        return processIds.Select(processId =>
        {
            var description = ProcessDescriber.Describe(processId);
            return new RunningAppName(processId, description.DisplayName ?? description.ImageName ?? "", description.ImageName);
        }).ToList();
    }

    /// <summary>The visible top-level windows of these processes, front to back (only the minimized ones when asked).</summary>
    private static List<nint> TopLevelWindowsOf(IReadOnlySet<int> processIds, bool minimizedOnly)
    {
        var windows = new List<nint>();
        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle) || ((long)GetWindowLongPtr(windowHandle, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (minimizedOnly && !IsIconic(windowHandle)) return true;
            GetWindowThreadProcessId(windowHandle, out var processId);
            if (processIds.Contains((int)processId)) windows.Add(windowHandle);
            return true;
        }, 0);
        return windows;
    }

    /// <summary>Best effort: Windows may refuse the foreground to a background app and flash the taskbar button instead.</summary>
    private static void BringToFront(nint window)
    {
        if (window == 0 || !IsWindow(window)) return;
        SetForegroundWindow(window);
    }

    // MARK: - Pause and play

    public async Task<bool> ControlPlayerAsync(PlayerCommand command)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            // The player with the track: the one playing for pause; for play, the current session (the
            // one Windows' own media flyout shows), else Spotify's, else any.
            GlobalSystemMediaTransportControlsSession? Playing() => sessions.FirstOrDefault(session =>
                session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            var session = command == PlayerCommand.Pause
                ? Playing()
                : Playing() ?? manager.GetCurrentSession() ?? sessions.FirstOrDefault(IsSpotify) ?? sessions.FirstOrDefault();
            if (session is null) return false;
            return command == PlayerCommand.Pause ? await session.TryPauseAsync() : await session.TryPlayAsync();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Write($"media session: {command} failed ({error.GetType().Name})");
            return false;
        }
    }

    private static bool IsSpotify(GlobalSystemMediaTransportControlsSession session) =>
        session.SourceAppUserModelId?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true;

    // MARK: - Spotify

    public bool SpotifyIsInstalled() => FindApplication("Spotify") is not null;

    public bool SpotifyIsRunning() => Process.GetProcessesByName("Spotify").Any(process => process.MainWindowHandle != 0);

    /// <summary>Starts Spotify and waits until it has a window, or <paramref name="seconds"/> pass.</summary>
    public async Task<bool> LaunchSpotifyAsync() => await LaunchSpotifyAsync(seconds: 6);

    private async Task<bool> LaunchSpotifyAsync(double seconds)
    {
        if (FindApplication("Spotify") is not { } spotify || !LaunchApplication(spotify)) return false;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (SpotifyIsRunning()) return true;
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// Opens the track's URI in Spotify, then watches Spotify's media session for up to four seconds:
    /// the track playing is Playing; the track up but paused gets a play; nothing confirming it is
    /// Opened, so the sentence never claims a song that is not playing.
    /// </summary>
    public async Task<SpotifyPlayResult> PlaySpotifyTrackAsync(string trackUri, string trackName)
    {
        if (!OpenSpotifyUri(trackUri)) return SpotifyPlayResult.Failed;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                if (manager.GetSessions().FirstOrDefault(IsSpotify) is not { } session) continue;
                var properties = await session.TryGetMediaPropertiesAsync();
                if (!string.Equals(properties?.Title?.Trim(), trackName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return SpotifyPlayResult.Playing;
                return await session.TryPlayAsync() ? SpotifyPlayResult.Playing : SpotifyPlayResult.Opened;
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Write($"media session: spotify check failed ({error.GetType().Name})");
        }
        return SpotifyPlayResult.Opened;
    }

    /// <summary>Only spotify: URIs, which Spotify registered; anything else is not this action's to open.</summary>
    public bool OpenSpotifyUri(string uri)
    {
        if (!uri.StartsWith("spotify:", StringComparison.Ordinal)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // MARK: - Win32

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
