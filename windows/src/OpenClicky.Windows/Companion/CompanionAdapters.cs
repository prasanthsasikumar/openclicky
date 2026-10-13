// The OS sides of two Core seams the companion uses, over the Platform services that already exist:
//   WriteInPlaceHost  — IWriteInPlaceHost over UI Automation (IFocusedElementReader) and the text inserter
//   BrowserTabSource  — IBrowserTabSource (BrowserTabLocator's view) over IBrowserTabEnumerator + IWindowList

using OpenClicky.Core.Answers;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Companion;

public sealed class WriteInPlaceHost(IFocusedElementReader reader, ITextInserter inserter) : IWriteInPlaceHost
{
    /// <summary>UI Automation needs no permission on Windows.</summary>
    public bool CanReadFocusedFields => true;

    public bool IsOwnProcessInFront()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0) return false;
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    public WritingFieldSnapshot SnapshotFocusedField()
    {
        // Off the caller's thread: the reader runs its own UIA thread with a timeout.
        var snapshot = Task.Run(() => reader.SnapshotAsync()).GetAwaiter().GetResult();
        return new WritingFieldSnapshot(snapshot.AppProcessName, snapshot.AppName, snapshot.Role, snapshot.IsEditable, snapshot.IsSecure, snapshot.Value, snapshot.SelectedText);
    }

    public void SelectAllInFocusedField() => inserter.SelectAll();

    public void Insert(string text) => inserter.Insert(text);

    public async Task<WriteInPlaceLanding> VerifyLandingAsync(string text, WritingFieldSnapshot before)
    {
        var platformBefore = new FocusedFieldSnapshot
        {
            AppProcessName = before.AppProcessName,
            AppName = before.AppName,
            Role = before.Role,
            IsEditable = before.IsEditable,
            IsSecure = before.IsSecure,
            Value = before.Value,
            SelectedText = before.SelectedText,
        };
        var landing = await PasteLanding.VerifyAsync(text, platformBefore, reader, inserter).ConfigureAwait(false);
        return landing switch
        {
            PasteLandingResult.Verified => WriteInPlaceLanding.Verified,
            PasteLandingResult.Posted => WriteInPlaceLanding.Posted,
            PasteLandingResult.NoTarget => WriteInPlaceLanding.NoTarget,
            _ => WriteInPlaceLanding.LeftOnPasteboard,
        };
    }

    public void CopyToClipboard(string text) => inserter.CopyToClipboard(text);
}

public sealed class BrowserTabSource(IBrowserTabEnumerator enumerator, IWindowList windowList) : IBrowserTabSource
{
    private readonly object cacheLock = new();
    private IReadOnlyList<BrowserWindowTabs>? cachedWindows;
    private DateTimeOffset cachedAt;

    /// <summary>One enumeration serves a whole point_at lookup (a few calls within a second).</summary>
    private IReadOnlyList<BrowserWindowTabs> Windows()
    {
        lock (cacheLock)
        {
            if (cachedWindows is not null && DateTimeOffset.UtcNow - cachedAt < TimeSpan.FromSeconds(2)) return cachedWindows;
        }
        IReadOnlyList<BrowserWindowTabs> windows;
        try { windows = enumerator.EnumerateAsync().GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            Core.Configuration.AppLog.Write($"point_at: browser tabs unreadable ({error.GetType().Name})");
            windows = [];
        }
        lock (cacheLock)
        {
            cachedWindows = windows;
            cachedAt = DateTimeOffset.UtcNow;
        }
        return windows;
    }

    private static ScreenRect Rect(PixelRect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    public IReadOnlyList<OnScreenWindow> OnScreenWindows() =>
        windowList.VisibleWindows().Select(window => new OnScreenWindow(window.ProcessId, Rect(window.Bounds))).ToList();

    public IReadOnlyList<RunningBrowser> RunningBrowsers() =>
        Windows().GroupBy(window => window.ProcessId)
            .Select(group => new RunningBrowser(group.Key, group.First().ProcessName, group.Any(window => window.IsForeground)))
            .ToList();

    private BrowserWindowTabs? FrontWindow(RunningBrowser browser) =>
        Windows().Where(window => window.ProcessId == browser.ProcessId).OrderBy(window => window.ZOrder).FirstOrDefault();

    public IReadOnlyList<BrowserTab> TabList(RunningBrowser browser)
    {
        if (FrontWindow(browser) is not { } window) return [];
        return window.Tabs.OrderBy(tab => tab.StripIndex)
            .Select(tab => new BrowserTab(tab.Title, tab.IsSelected && Uri.TryCreate(window.ActiveTabUrl, UriKind.Absolute, out var url) ? url : null))
            .ToList();
    }

    public IReadOnlyList<ScreenRect> TabButtonFrames(RunningBrowser browser) =>
        FrontWindow(browser)?.Tabs.OrderBy(tab => tab.StripIndex).Select(tab => Rect(tab.Bounds)).ToList() ?? [];
}
