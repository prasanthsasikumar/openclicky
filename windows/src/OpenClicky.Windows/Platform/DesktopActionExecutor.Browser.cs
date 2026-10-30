// The live half of tabs by voice (BrowserControl.swift's MARK: - Live): the web link under a click,
// loaded in the selected tab through the address bar, and `switch_tab` through the tab strip. The
// Mac's AppleScript has no Windows twin, so UI Automation reads the link and the tabs and the
// browser's own keys do the rest. The pure parts are Core/Actions/BrowserControl.cs.

using System.Windows.Automation;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

public sealed partial class DesktopActionExecutor
{
    private static readonly TimeSpan LinkLookupTimeout = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan TabReadTimeout = TimeSpan.FromSeconds(0.8);

    // MARK: - Links

    /// <summary>
    /// The http(s) link under a physical pixel, through UI Automation: the element there or one of its
    /// first few ancestors is a Hyperlink whose Value is the address (Chrome, Edge, Brave, Firefox).
    /// The walk stops at the page itself (Document), which no link contains.
    /// </summary>
    private static Task<Uri?> WebLinkAtAsync(PixelPoint point) => UiaSupport.RunAsync<Uri?>(() =>
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        var walker = TreeWalker.RawViewWalker;
        for (var level = 0; level < 6 && element is not null; level++)
        {
            var controlType = UiaSupport.Safe(() => element.Current.ControlType);
            if (controlType == ControlType.Hyperlink)
            {
                var value = UiaSupport.Safe(() => UiaSupport.ValuePatternOf(element)?.Current.Value);
                return BrowserControl.WebLink(value);
            }
            if (controlType == ControlType.Document) return null;
            var current = element;
            element = UiaSupport.Safe(() => walker.GetParent(current));
        }
        return null;
    }, LinkLookupTimeout, null);

    /// <summary>
    /// Loads <paramref name="url"/> in the browser's selected tab: Ctrl+L, the address typed the way live
    /// dictation types, Delete (drops an inline completion the address bar appended from history), Enter.
    /// Typed only once the focus moved into a new text field (the address bar), so a browser that did not
    /// take Ctrl+L gets nothing typed and the caller clicks instead.
    /// </summary>
    private async Task<bool> LoadInSelectedTabAsync(Uri url)
    {
        if (textInserter is null || focusedElements is null) return false;
        var before = await focusedElements.FocusedTextTargetAsync().ConfigureAwait(false);
        if (!SendControlShortcut(VirtualKeyL)) return false;
        var inAddressBar = false;
        for (var attempt = 0; attempt < 6 && !inAddressBar; attempt++)
        {
            await Task.Delay(60).ConfigureAwait(false);
            inAddressBar = ScreenNavigation.FocusMovedIntoNewTextField(before, await focusedElements.FocusedTextTargetAsync().ConfigureAwait(false));
        }
        if (!inAddressBar)
        {
            AppLog.Write("click: the address bar did not take the focus; clicking the link instead");
            return false;
        }
        textInserter.SelectAll();
        if (!textInserter.TypeAsKeystrokes(url.AbsoluteUri)) return false;
        await Task.Delay(80).ConfigureAwait(false);
        Send(VirtualKeyInput(VirtualKeyDelete, keyUp: false), VirtualKeyInput(VirtualKeyDelete, keyUp: true),
             VirtualKeyInput(VirtualKeys.Return, keyUp: false), VirtualKeyInput(VirtualKeys.Return, keyUp: true));
        return true;
    }

    private const int VirtualKeyL = 0x4C;
    private const int VirtualKeyDelete = 0x2E;
    private const int VirtualKey1 = 0x31;

    // MARK: - Tabs

    /// <summary>
    /// The browser window in front, else the frontmost one on screen (brought to the front once its tab
    /// is chosen); its TabItems in strip order; the target's tab selected through SelectionItem, or by
    /// keys (Ctrl+1…8, Ctrl+9, Ctrl+Tab) when the pattern did not take and the browser is in front.
    /// UI Automation knows only the selected tab's URL (the address bar), so the others are named by
    /// their titles.
    /// </summary>
    public async Task<BrowserTabSwitchResult> SwitchTabAsync(BrowserTabTarget target)
    {
        var foreground = GetForegroundWindow();
        var browsers = new WindowList().VisibleWindows()
            .Where(window => BrowserProcesses.IsBrowser(window.ProcessName) && !window.IsToolWindow && window.Title.Length > 0)
            .ToList();
        var browser = browsers.FirstOrDefault(window => window.Handle == foreground) ?? browsers.FirstOrDefault();
        if (browser is null) return new BrowserTabSwitchResult.NoBrowser();
        if (IsProcessElevated(browser.ProcessId) == true && IsProcessElevated(Environment.ProcessId) != true) return new BrowserTabSwitchResult.NotAllowed();

        var read = await UiaSupport.RunAsync(() =>
        {
            var elements = BrowserTabEnumerator.ReadTabElements(browser.Handle, TimeSpan.FromSeconds(0.4));
            var url = elements.Count == 0 ? null : BrowserAddressBar.ReadUrl(browser.Handle, TimeSpan.FromSeconds(0.25));
            return (Elements: elements, Url: url);
        }, TabReadTimeout, (Elements: (IReadOnlyList<(AutomationElement Element, string Title, PixelRect Bounds, bool IsSelected)>)[], Url: (string?)null))
            .ConfigureAwait(false);
        if (read.Elements.Count == 0)
        {
            AppLog.Write("switch_tab: the tab strip was not readable");
            return new BrowserTabSwitchResult.NoSuchTab();
        }
        var tabs = read.Elements.Select(tab => BrowserTab.From(BrowserControl.TabTitle(tab.Title), tab.IsSelected ? read.Url : null)).ToList();
        var selected = read.Elements.ToList().FindIndex(tab => tab.IsSelected);
        var activeIndex = selected >= 0 ? selected + 1 : 1;
        if (BrowserControl.TabIndex(target, tabs, activeIndex) is not { } index) return new BrowserTabSwitchResult.NoSuchTab();
        var chosen = read.Elements[index - 1];

        var selectedByPattern = index == activeIndex || await UiaSupport.RunAsync(() =>
        {
            if (!chosen.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) || pattern is not SelectionItemPattern selectionItem) return false;
            selectionItem.Select();
            Thread.Sleep(120);
            return selectionItem.Current.IsSelected;
        }, TabReadTimeout, false).ConfigureAwait(false);
        if (browser.Handle != GetForegroundWindow()) BringToFront(browser.Handle);
        if (!selectedByPattern)
        {
            await Task.Delay(100).ConfigureAwait(false);
            // Keys go to whatever is in front, so only to the browser.
            if (GetForegroundWindow() != browser.Handle || !SendTabShortcut(BrowserControl.Shortcut(activeIndex, index, tabs.Count)))
            {
                AppLog.Write("switch_tab: the tab could not be selected");
                return new BrowserTabSwitchResult.NoSuchTab();
            }
        }
        AppLog.Write($"switch_tab: tab {index} of {tabs.Count}, {(selectedByPattern ? "selected through UI Automation" : "by keys")}");
        return new BrowserTabSwitchResult.Switched(tabs[index - 1].Title);
    }

    /// <summary>Ctrl+digit, or Ctrl(+Shift)+Tab as many times as the shortcut says.</summary>
    private static bool SendTabShortcut(TabShortcut shortcut)
    {
        if (shortcut.Digit is { } digit) return SendControlShortcut(VirtualKey1 + digit - 1);
        for (var step = 0; step < Math.Abs(shortcut.Steps); step++)
        {
            var inputs = new List<INPUT>();
            FrontAppTextInserter.AddModifierRelease(inputs, FrontAppTextInserter.ModifiersReleasedBeforePaste);
            inputs.Add(VirtualKeyInput(VirtualKeys.LeftControl, keyUp: false));
            if (shortcut.Steps < 0) inputs.Add(VirtualKeyInput(VirtualKeys.LeftShift, keyUp: false));
            inputs.Add(VirtualKeyInput(VirtualKeys.Tab, keyUp: false));
            inputs.Add(VirtualKeyInput(VirtualKeys.Tab, keyUp: true));
            if (shortcut.Steps < 0) inputs.Add(VirtualKeyInput(VirtualKeys.LeftShift, keyUp: true));
            inputs.Add(VirtualKeyInput(VirtualKeys.LeftControl, keyUp: true));
            if (Send([.. inputs]) != inputs.Count) return false;
            Thread.Sleep(30);
        }
        return true;
    }
}
