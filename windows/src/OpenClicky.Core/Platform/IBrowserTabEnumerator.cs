namespace OpenClicky.Core.Platform;

/// <summary>One tab button in a browser's tab strip.</summary>
/// <param name="Title">The TabItem's UIA Name (the page title, sometimes with " - Audio playing" style suffixes).</param>
/// <param name="Bounds">The tab button in physical screen pixels.</param>
/// <param name="StripIndex">0-based position in the strip (left to right, or top to bottom for vertical tabs).</param>
public sealed record BrowserTabItem(string Title, PixelRect Bounds, int StripIndex, bool IsSelected);

/// <summary>A visible browser window with its tabs in strip order.</summary>
/// <param name="ZOrder">0 = frontmost of all visible top-level windows (comparable with <see cref="OnScreenWindowInfo.ZOrder"/>).</param>
/// <param name="ActiveTabUrl">The selected tab's URL from the address bar (scheme added when the browser hides it).</param>
public sealed record BrowserWindowTabs(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    string? WindowTitle,
    PixelRect WindowBounds,
    int ZOrder,
    bool IsForeground,
    IReadOnlyList<BrowserTabItem> Tabs,
    string? ActiveTabUrl);

/// <summary>The data BrowserTabLocator's ranking needs (it replaces the Mac's AppleScript tab list + AXTabButton frames).</summary>
public interface IBrowserTabEnumerator
{
    /// <summary>Every visible, uncloaked, unminimised browser window front to back, each walked with a 0.25 s / 600 node budget.</summary>
    Task<IReadOnlyList<BrowserWindowTabs>> EnumerateAsync(CancellationToken cancellationToken = default);
}
