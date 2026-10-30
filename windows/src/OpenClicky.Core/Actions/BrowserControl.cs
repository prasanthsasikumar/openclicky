// Port of macos/OpenClicky/OpenClicky/BrowserControl.swift (the pure parts).
//
// Tabs by voice. A voice click on a search result opened the page in a background tab, which is no
// use to someone who cannot see or reach the tab strip, and "show me that tab" had nothing to do it
// with (2026-10-29). So a clicked web link is followed in the tab it is in (a new tab only when
// asked), and `switch_tab` goes to a tab by name or position.
//
// The pure parts (which tab a target names, which link value may be loaded, the keys that reach a
// tab) are unit-tested; the live half is OpenClicky.Windows/Platform/DesktopActionExecutor.Browser.cs.
//
// Windows differences: the Mac drives Chrome, Brave, Edge and Safari through AppleScript; Windows has
// no scripting dictionary, so UI Automation reads the link under the point (the Hyperlink among the
// element's first few ancestors, its Value pattern being the href in Chrome, Edge, Brave and Firefox)
// and the tab strip (one TabItem per tab), and the browser's own keys do the rest: Ctrl+L or Ctrl+T,
// the address typed the way live dictation types, Enter; a tab is selected through its SelectionItem
// pattern, else Ctrl+1…8 / Ctrl+9 / Ctrl+Tab. UI Automation gives the URL of the selected tab only
// (the address bar), so a named tab is matched by its title too: the spoken words run together are
// looked for inside the title as well as the host ("you tube" is the "YouTube" tab).

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Actions;

/// <summary>Which tab `switch_tab` goes to.</summary>
public abstract record BrowserTabTarget
{
    private BrowserTabTarget() { }

    /// <summary>The tab whose site or title the person said.</summary>
    public sealed record Named(string Words) : BrowserTabTarget;
    public sealed record Next : BrowserTabTarget;
    public sealed record Previous : BrowserTabTarget;
    /// <summary>The last tab in the strip.</summary>
    public sealed record Newest : BrowserTabTarget;
    public sealed record First : BrowserTabTarget;
}

public abstract record BrowserTabSwitchResult
{
    private BrowserTabSwitchResult() { }

    public sealed record Switched(string Title) : BrowserTabSwitchResult;
    public sealed record NoBrowser : BrowserTabSwitchResult;
    public sealed record NoSuchTab : BrowserTabSwitchResult;
    /// <summary>The browser runs as administrator, so Windows drops this app's input to it (the Mac's Automation refusal).</summary>
    public sealed record NotAllowed : BrowserTabSwitchResult;
}

/// <summary>
/// The keys that select a tab when its SelectionItem pattern does not: Ctrl+<see cref="Digit"/>
/// (1…8 the tab at that place, 9 the last), else <see cref="Steps"/> presses of Ctrl+Tab (negative:
/// Ctrl+Shift+Tab). Both null/0 when the tab is already the selected one.
/// </summary>
public readonly record struct TabShortcut(int? Digit, int Steps);

public static class BrowserControl
{
    /// <summary>
    /// The 1-based index the target names among <paramref name="tabs"/>, the selected one being
    /// <paramref name="activeIndex"/>, or null when it names nothing (next past the last tab wraps, as
    /// Ctrl+Tab does).
    /// </summary>
    public static int? TabIndex(BrowserTabTarget target, IReadOnlyList<BrowserTab> tabs, int activeIndex)
    {
        if (tabs.Count == 0) return null;
        switch (target)
        {
            case BrowserTabTarget.Next: return activeIndex % tabs.Count + 1;
            case BrowserTabTarget.Previous: return activeIndex <= 1 ? tabs.Count : activeIndex - 1;
            // New tabs open beside the one they came from, but the last tab is what "the newest" means to
            // someone who cannot see the strip, and a link opened by OpenClicky goes there too.
            case BrowserTabTarget.Newest: return tabs.Count;
            case BrowserTabTarget.First: return 1;
            case BrowserTabTarget.Named named:
            {
                // "cars and bids" is carsandbids.com: the words said, run together, inside the site name.
                var joinedWords = string.Concat(ScreenNavigation.Words(named.Words));
                if (joinedWords.Length >= 4)
                {
                    for (var index = 0; index < tabs.Count; index++)
                    {
                        if ((tabs[index].Url?.Host ?? "").Contains(joinedWords, StringComparison.OrdinalIgnoreCase)) return index + 1;
                    }
                }
                if (BrowserTabLocator.BestMatch([named.Words], tabs) is { } best) return best + 1;
                // Windows only knows the selected tab's URL, so the title stands in for the rest:
                // "you tube" run together is inside "YouTube".
                if (joinedWords.Length >= 4)
                {
                    for (var index = 0; index < tabs.Count; index++)
                    {
                        if (string.Concat(ScreenNavigation.Words(tabs[index].Title)).Contains(joinedWords, StringComparison.Ordinal)) return index + 1;
                    }
                }
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>The keys from the selected tab (1-based <paramref name="fromIndex"/>) to <paramref name="toIndex"/> among <paramref name="count"/> tabs.</summary>
    public static TabShortcut Shortcut(int fromIndex, int toIndex, int count)
    {
        if (toIndex == fromIndex) return new TabShortcut(null, 0);
        if (toIndex is >= 1 and <= 8) return new TabShortcut(toIndex, 0);
        if (toIndex == count) return new TabShortcut(9, 0);
        return new TabShortcut(null, toIndex - fromIndex);
    }

    /// <summary>
    /// A tab's title as said back: Chrome and Edge append their hover card's lines to a TabItem's name
    /// (" - Memory usage - 244 MB", " - Audio playing"), which is not the page's title.
    /// </summary>
    public static string TabTitle(string accessibleName)
    {
        var title = accessibleName.Trim();
        foreach (var marker in TabNameSuffixes)
        {
            var at = title.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0) title = title[..at].TrimEnd();
        }
        return title;
    }

    private static readonly string[] TabNameSuffixes =
    [
        " - Memory usage", " - Audio playing", " - Audio muted", " - Network error", " - Crashed",
    ];

    /// <summary>
    /// A link's Value as an address the browser may load, or null: http and https only, so a
    /// `javascript:` or `mailto:` link stays a real mouse click, which is what the page expects.
    /// </summary>
    public static Uri? WebLink(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsControl)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url)) return null;
        return (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Length > 0 ? url : null;
    }
}
