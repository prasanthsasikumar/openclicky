//
//  BrowserTabLocator.cs
//  OpenClicky (Windows port of BrowserTabLocator.swift)
//
//  `point_at` for browser tabs. A tab is identified by a 16 px favicon and a truncated title, which
//  no screenshot model resolves reliably ("where is the Reddit tab" sent the Realtime model, Claude
//  and OCR to a tray item spelled "reditt"). The browser knows: on the Mac its AppleScript tab list
//  gives title + URL in strip order and Accessibility one AXTabButton per tab; on Windows UI
//  Automation over the tab strip gives the TabItems (names + frames) and the address bar's value.
//  That OS side sits behind IBrowserTabSource; the matching, pairing, mapping and the z-order rule
//  are here.
//

namespace OpenClicky.Core.Pointing;

public sealed record BrowserTab(string Title, Uri? Url)
{
    /// <summary>A tab from a raw URL string (null URL for a tab whose URL is not a URL, as the Mac's URL(string:)).</summary>
    public static BrowserTab From(string title, string? url) =>
        new(title, Uri.TryCreate(url ?? "", UriKind.Absolute, out var parsed) ? parsed : null);
}

/// <summary>One on-screen top-level window (EnumWindows + DWM extended frame bounds, cloaked ones left out):
/// owner process and bounds in global coordinates, y down.</summary>
public sealed record OnScreenWindow(int OwnerProcessId, ScreenRect Bounds);

/// <summary>A running browser that has a window on screen.</summary>
public sealed record RunningBrowser(int ProcessId, string ProcessName, bool IsActive);

/// <summary>The OS side of the tab locator (OpenClicky.Windows/Platform: UI Automation + EnumWindows).</summary>
public interface IBrowserTabSource
{
    /// <summary>Normal top-level windows currently on screen, front to back (global coordinates).</summary>
    IReadOnlyList<OnScreenWindow> OnScreenWindows();

    /// <summary>Running processes whose image name is one of <see cref="BrowserTabLocator.BrowserProcessNames"/>.</summary>
    IReadOnlyList<RunningBrowser> RunningBrowsers();

    /// <summary>The front window's tabs in strip order.</summary>
    IReadOnlyList<BrowserTab> TabList(RunningBrowser browser);

    /// <summary>Frames (global coordinates) of the tab buttons in the browser's front window.</summary>
    IReadOnlyList<ScreenRect> TabButtonFrames(RunningBrowser browser);
}

public static class BrowserTabLocator
{
    /// <summary>Browsers whose tab strip is read (the Mac's scriptable bundle ids, as Windows process image names).</summary>
    public static readonly IReadOnlySet<string> BrowserProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "vivaldi.exe", "opera.exe",
    };

    /// <summary>Words that appear in every request or describe the control rather than name the site. The Mac list, with
    /// the Windows browsers' names added next to "chrome" and "safari".</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "tab", "tabs", "browser", "window", "page", "site", "website", "open", "opened", "where", "which", "what",
        "the", "this", "that", "one", "point", "show", "find", "click", "please", "and", "for", "with", "new",
        "settings", "home", "search", "login", "sign", "dashboard", "chrome", "safari", "google", "www", "com",
        "edge", "firefox", "brave",
    };

    public sealed record LocatedTab(ScreenPoint Point, BrowserTab Tab);

    /// <summary>The screenshot-pixel centre of the tab the request is about, when the request mentions a tab and a
    /// running browser has a matching tab whose button is visible in the captured screenshot. The browser need not be the
    /// app in front: a terminal over the lower part of Chrome still leaves the tab strip showing, and that is what the
    /// user is looking at; but a browser behind the front app is only pointed at when the user's own words name the
    /// tab (see <see cref="MatchTexts"/>).</summary>
    public static LocatedTab? Locate(
        string label, string text, string? userRequest, ScreenCaptureGeometry capture, IBrowserTabSource source,
        int ownProcessId, Action<string>? log = null)
    {
        var texts = new[] { label, text, userRequest };
        if (!MentionsTab(texts)) return null;
        var windows = source.OnScreenWindows();
        var browsers = source.RunningBrowsers()
            .Where(browser => BrowserProcessNames.Contains(browser.ProcessName))
            // Only browsers with a window on screen; the active one first.
            .Where(browser => windows.Any(window => window.OwnerProcessId == browser.ProcessId))
            .OrderBy(browser => browser.IsActive ? 0 : 1)
            .ToList();
        foreach (var browser in browsers)
        {
            var tabs = source.TabList(browser);
            if (BestMatch(MatchTexts(browser.IsActive, label, text, userRequest), tabs) is not { } index) continue;
            var title = Prefix(tabs[index].Title, 40);
            if (Frame(index, source.TabButtonFrames(browser), tabs.Count) is not { } frame)
            {
                log?.Invoke($"browser tab \"{title}\" matched but its button frame could not be read");
                continue;
            }
            var globalCenter = frame.Center;
            var point = capture.ScreenshotPoint(globalCenter);
            // The screenshot is the pointer's display; the tab must be on it and not under another window.
            if (!capture.ContainsScreenshotPoint(point) || IsHidden(globalCenter, browser.ProcessId, windows, ownProcessId))
            {
                log?.Invoke($"browser tab \"{title}\" is off the capture or covered; not pointing");
                continue;
            }
            return new LocatedTab(point, tabs[index]);
        }
        return null;
    }

    private static string Prefix(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>Whether <paramref name="point"/> (global coordinates) in the window of <paramref name="ownerProcessId"/> is
    /// covered by another app's window above it, or lies in none of that app's windows. <paramref name="windows"/> is front
    /// to back. Our own windows (<paramref name="ownProcessId"/>: the overlay, the island) never hide anything.</summary>
    public static bool IsHidden(ScreenPoint point, int ownerProcessId, IReadOnlyList<OnScreenWindow> windows, int ownProcessId)
    {
        foreach (var window in windows)
        {
            if (!window.Bounds.Contains(point)) continue;
            if (window.OwnerProcessId == ownerProcessId) return false;
            if (window.OwnerProcessId != ownProcessId) return true;
        }
        return true;
    }

    // Pure parts (unit-tested)

    /// <summary>The texts a tab may be matched against. For the active browser, the model's label and text too (it read
    /// the tab strip). For a browser behind another app, the user's words alone: "minimize this tab" with a terminal in
    /// front sent the model to a Chrome tab it could see behind it, and matching its own reading of that tab made the
    /// pointer follow.</summary>
    public static string?[] MatchTexts(bool browserIsActive, string label, string text, string? userRequest) =>
        browserIsActive ? [label, text, userRequest] : [userRequest];

    public static bool MentionsTab(IEnumerable<string?> texts) =>
        texts.Any(text => Words(text ?? "").Any(word => word == "tab" || word == "tabs"));

    /// <summary>Index of the tab the request names: a word of the request in the URL's host counts most ("reddit" in
    /// www.reddit.com, ×3), a word in the title less (×1). Null when nothing distinctive matches.</summary>
    public static int? BestMatch(IEnumerable<string?> texts, IReadOnlyList<BrowserTab> tabs)
    {
        var requestWords = texts.SelectMany(text => Words(text ?? "")).ToHashSet()
            .Where(word => word.Length >= 3 && !GenericWords.Contains(word))
            .ToList();
        if (requestWords.Count == 0) return null;
        int? bestIndex = null;
        var bestScore = 0;
        for (var index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            var hostWords = Words(tab.Url?.Host ?? "").ToHashSet();
            var titleWords = Words(tab.Title).ToHashSet();
            var score = 0;
            foreach (var word in requestWords)
            {
                if (hostWords.Contains(word) || hostWords.Any(hostWord => hostWord.StartsWith(word, StringComparison.Ordinal) && hostWord.Length <= word.Length + 3)) score += 3;
                if (titleWords.Contains(word)) score += 1;
            }
            if (score > 0 && score > bestScore)
            {
                bestIndex = index;
                bestScore = score;
            }
        }
        return bestIndex;
    }

    /// <summary>Tab buttons pair with the tab list by position, left to right, and only when there is one button per tab
    /// (a mismatch means the strip is scrolled or mid-update).</summary>
    public static ScreenRect? Frame(int index, IReadOnlyList<ScreenRect> buttonFrames, int tabCount)
    {
        if (buttonFrames.Count != tabCount || index < 0 || index >= tabCount) return null;
        return buttonFrames.OrderBy(frame => frame.Left).ElementAt(index);
    }

    private static List<string> Words(string text) => AccessibleElementLocator.Words(text);
}
