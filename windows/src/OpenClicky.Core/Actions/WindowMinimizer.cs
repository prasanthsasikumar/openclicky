// Port of the pure half of macos/OpenClicky/OpenClicky/WindowMinimizer.swift.
//
// "Minimize everything": every ordinary window on screen is minimized, and "bring them back"
// restores exactly those. "Minimize everything but Terminal" keeps one app up, and "show just the
// terminal" brings back that app's windows alone. OpenClicky's own windows (the overlay, the island,
// the orb), the taskbar and the desktop, and windows on other virtual desktops are never touched.
// The Win32 half is OpenClicky.Windows/Platform/DesktopActionExecutor.Windows.cs.

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Actions;

/// <summary>A running app as the name matching sees it: its window's process, the name Windows shows for it
/// (the executable's description, "Windows Terminal") and its image name ("WindowsTerminal.exe").</summary>
public sealed record RunningAppName(int ProcessId, string DisplayName, string? ImageName);

public static class WindowMinimizer
{
    /// <summary>Smaller than this is a tooltip or a palette, not a window anyone wants minimized.</summary>
    public const int SmallestWindowSide = 50;

    /// <summary>The desktop and the taskbars: top-level windows, but never a "window" to minimize.</summary>
    private static readonly HashSet<string> ShellClassNames = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>
    /// Ordinary windows (not always-on-top, not a tool window, big enough, with a title) of apps other
    /// than OpenClicky and the apps asked to stay up. Cloaked windows (another virtual desktop) are left
    /// alone: minimizing one would not clear this screen and restoring it would pull it across.
    /// </summary>
    public static IReadOnlyList<OnScreenWindowInfo> WindowsToMinimize(IReadOnlyList<OnScreenWindowInfo> onScreen, int ownProcessId,
                                                                      IReadOnlySet<int>? keptProcessIds = null) =>
        onScreen.Where(window =>
                !window.IsCloaked && !window.IsMinimized && !window.IsTopMost && !window.IsToolWindow
                && window.ProcessId != ownProcessId
                && keptProcessIds?.Contains(window.ProcessId) != true
                && !ShellClassNames.Contains(window.ClassName)
                && window.Title.Trim().Length > 0
                && window.Bounds.Width >= SmallestWindowSide && window.Bounds.Height >= SmallestWindowSide)
            .ToList();

    /// <summary>
    /// The running apps the spoken name means: an exact name or image name first ("Notepad",
    /// "notepad.exe", "WindowsTerminal"), else every app whose name holds it as whole words ("Chrome"
    /// for Google Chrome, "terminal" for Windows Terminal). Case and a leading "the" do not matter.
    /// Empty when nothing matches.
    /// </summary>
    public static IReadOnlySet<int> ProcessIdsOfAppsNamed(string spokenName, IReadOnlyList<RunningAppName> runningApps)
    {
        var wanted = spokenName.Trim().ToLowerInvariant();
        if (wanted.StartsWith("the ", StringComparison.Ordinal)) wanted = wanted[4..];
        if (wanted.Length == 0) return new HashSet<int>();
        static string WithoutExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var exact = runningApps.Where(app =>
            app.DisplayName.ToLowerInvariant() == wanted
            || (app.ImageName is { } image && (image.ToLowerInvariant() == wanted || WithoutExe(image).ToLowerInvariant() == WithoutExe(wanted)))).ToList();
        if (exact.Count > 0) return exact.Select(app => app.ProcessId).ToHashSet();
        var wantedWords = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return runningApps.Where(app =>
        {
            var nameWords = app.DisplayName.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nameWords.Length < wantedWords.Length) return false;
            for (var start = 0; start + wantedWords.Length <= nameWords.Length; start++)
            {
                if (nameWords.Skip(start).Take(wantedWords.Length).SequenceEqual(wantedWords)) return true;
            }
            return false;
        }).Select(app => app.ProcessId).ToHashSet();
    }
}
