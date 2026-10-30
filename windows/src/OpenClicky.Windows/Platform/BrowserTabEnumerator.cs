using System.Diagnostics;
using System.Windows.Automation;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// The tab strip of every visible browser window through UI Automation (Chrome, Edge, Brave, Opera,
/// Vivaldi, Arc and Firefox all expose one TabItem per tab with its title and frame), plus the
/// selected tab's URL from the address bar. Replaces the Mac's AppleScript tab list + AXTabButton walk.
/// </summary>
public sealed class BrowserTabEnumerator(IWindowList windowList) : IBrowserTabEnumerator
{
    private static readonly TimeSpan PerWindowBudget = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan PerWindowTimeout = TimeSpan.FromSeconds(0.6);

    public async Task<IReadOnlyList<BrowserWindowTabs>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var foreground = GetForegroundWindow();
        var browserWindows = windowList.VisibleWindows()
            .Where(window => BrowserProcesses.IsBrowser(window.ProcessName) && !window.IsToolWindow && window.Title.Length > 0)
            .ToList();
        var results = new List<BrowserWindowTabs>();
        foreach (var window in browserWindows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var walk = await UiaSupport.RunAsync(() => ReadTabs(window.Handle), PerWindowTimeout, (Tabs: (IReadOnlyList<BrowserTabItem>)[], Url: (string?)null), cancellationToken)
                .ConfigureAwait(false);
            if (walk.Tabs.Count == 0) continue;
            results.Add(new BrowserWindowTabs(window.Handle, window.ProcessId, window.ProcessName!, window.Title, window.Bounds, window.ZOrder,
                window.Handle == foreground, walk.Tabs, walk.Url));
        }
        return results;
    }

    private static (IReadOnlyList<BrowserTabItem> Tabs, string? Url) ReadTabs(nint windowHandle)
    {
        var stopwatch = Stopwatch.StartNew();
        var found = ReadTabElements(windowHandle, PerWindowBudget);
        if (found.Count == 0) return ([], null);
        var tabs = found.Select((tab, index) => new BrowserTabItem(tab.Title, tab.Bounds, index, tab.IsSelected)).ToList();

        var remaining = PerWindowBudget - stopwatch.Elapsed;
        var url = BrowserAddressBar.ReadUrl(windowHandle, remaining > TimeSpan.FromMilliseconds(80) ? remaining : TimeSpan.FromMilliseconds(80));
        return (tabs, url);
    }

    /// <summary>
    /// The window's TabItems in strip order with their UIA elements (switch_tab selects one through
    /// them). Call on a UIA thread (UiaSupport.RunAsync).
    /// </summary>
    internal static IReadOnlyList<(AutomationElement Element, string Title, PixelRect Bounds, bool IsSelected)> ReadTabElements(nint windowHandle, TimeSpan budget)
    {
        var window = UiaSupport.Safe(() => AutomationElement.FromHandle(windowHandle));
        if (window is null) return [];
        var request = UiaSupport.WalkCacheRequest();
        var root = UiaSupport.Safe(() => window.GetUpdatedCache(request));
        if (root is null) return [];

        var found = new List<(AutomationElement Element, string Title, PixelRect Bounds, bool IsSelected)>();
        var stopwatch = Stopwatch.StartNew();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < 600 && stopwatch.Elapsed < budget)
        {
            var (element, depth) = queue.Dequeue();
            visited++;
            var controlType = UiaSupport.Cached<ControlType>(element, AutomationElement.ControlTypeProperty);
            if (controlType == ControlType.Document) continue; // web content never holds the browser's own tabs
            if (controlType == ControlType.TabItem)
            {
                var bounds = UiaSupport.ToPixelRect(UiaSupport.Cached<System.Windows.Rect>(element, AutomationElement.BoundingRectangleProperty));
                var title = UiaSupport.Cached<string>(element, AutomationElement.NameProperty) ?? string.Empty;
                if (!bounds.IsEmpty && bounds.Height < 120)
                {
                    found.Add((element, title, bounds, UiaSupport.Cached<bool>(element, SelectionItemPattern.IsSelectedProperty)));
                }
                continue;
            }
            if (depth >= 12) continue;
            foreach (var child in UiaSupport.CachedChildren(element, request)) queue.Enqueue((child, depth + 1));
        }
        if (found.Count == 0) return [];

        // Strip order: left to right, or top to bottom for vertical tabs (Edge, Vivaldi, Arc).
        var isVertical = found.Count > 1 && found.Max(tab => tab.Bounds.Left) - found.Min(tab => tab.Bounds.Left) < found.Max(tab => tab.Bounds.Top) - found.Min(tab => tab.Bounds.Top);
        var ordered = isVertical
            ? found.OrderBy(tab => tab.Bounds.Top).ThenBy(tab => tab.Bounds.Left)
            : found.OrderBy(tab => tab.Bounds.Left).ThenBy(tab => tab.Bounds.Top);
        return ordered.ToList();
    }
}
