// The live half of moving around by voice (ScreenNavigation.swift's MARK: - Live): the wheel
// for `scroll`, File Explorer for `open_folder` and `find_files` (the Windows Search index, else a
// bounded walk), Ctrl+F and live dictation's typing for `find_on_page`, and the Realtime `click`.
// Every event goes through SendInput, so an app that runs as administrator (UIPI) is refused up
// front rather than silently ignoring the input. The pure parts are Core/Actions/ScreenNavigation.cs.

using System.Diagnostics;
using System.IO;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

public sealed partial class DesktopActionExecutor
{
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(2.5);

    private readonly ITextInserter? textInserter;
    private readonly IFocusedElementReader? focusedElements;

    /// <param name="textInserter">Live dictation's typing, for the find bar; null → find_on_page types nothing.</param>
    /// <param name="focusedElements">UI Automation's focus, to know Ctrl+F opened a find bar; null → find_on_page types nothing.</param>
    public DesktopActionExecutor(ITextInserter? textInserter = null, IFocusedElementReader? focusedElements = null)
    {
        this.textInserter = textInserter;
        this.focusedElements = focusedElements;
    }

    // MARK: - Scroll

    public ScrollResult ScrollFrontWindow(ScrollDirection direction, ScrollAmount amount)
    {
        var window = GetForegroundWindow();
        if (window == 0 || IsIconic(window) || ProcessIdOf(window) == Environment.ProcessId) return ScrollResult.NoWindow;
        var frame = VisibleFrame(window);
        var width = frame.Right - frame.Left;
        var height = frame.Bottom - frame.Top;
        if (width <= 50 || height <= 50) return ScrollResult.NoWindow;
        if (!FrontAppTextInserter.CanSendInputToForeground()) return ScrollResult.NotAllowed;

        // The wheel goes to the window under the pointer: when the pointer is on the island, the
        // taskbar or another app, it moves to the middle of the window in front first.
        GetCursorPos(out var pointer);
        var pointerIsOnIt = pointer.X >= frame.Left && pointer.X < frame.Right && pointer.Y >= frame.Top && pointer.Y < frame.Bottom
            && GetAncestor(WindowFromPoint(pointer), GA_ROOT) == window;
        if (!pointerIsOnIt) Send(AbsoluteMoveInput(frame.Left + width / 2, frame.Top + height / 2));

        var dpi = GetDpiForWindow(window);
        var steps = ScreenNavigation.ScrollSteps(direction, amount, width, height, dpi == 0 ? 1 : dpi / 96.0);
        foreach (var (vertical, horizontal) in steps)
        {
            var input = vertical != 0 ? MouseInput(MOUSEEVENTF_WHEEL, data: vertical) : MouseInput(MOUSEEVENTF_HWHEEL, data: horizontal);
            if (Send(input) != 1) return ScrollResult.NotAllowed;
            // "To the bottom" is ten turns; a moment between them lets a smooth-scrolling page keep up.
            if (steps.Count > 1) Thread.Sleep(20);
        }
        return ScrollResult.Scrolled;
    }

    // MARK: - Folders and files

    public bool OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The Windows Search index first (every file under the home folder it has indexed, in a few
    /// milliseconds); when it is off, slow or has nothing worth showing, a bounded walk of the home
    /// folder. The log says how many and from where, never a name.
    /// </summary>
    public Task<IReadOnlyList<string>> FindFilesAsync(string words) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var home = KnownFolderPath("home") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var started = Stopwatch.StartNew();
        IReadOnlyList<string> ranked = [];
        var source = "index";
        if (ScreenNavigation.WindowsSearchQuery(words, home) is { } query && QueryWindowsSearch(query) is { } indexed)
        {
            ranked = ScreenNavigation.RankFoundFiles(indexed, words);
        }
        if (ranked.Count == 0)
        {
            source = "walk";
            // The known folders too, for a Desktop or Documents redirected outside the profile.
            var roots = new[] { home, KnownFolderPath("desktop"), KnownFolderPath("documents"), KnownFolderPath("downloads") }
                .OfType<string>().Where(root => root.Length > 0);
            ranked = ScreenNavigation.RankFoundFiles(ScreenNavigation.WalkForFiles(roots, words, WalkBudget), words);
        }
        AppLog.Write($"find_files: {ranked.Count} shown, from the {source}, in {started.ElapsedMilliseconds} ms");
        return ranked;
    });

    /// <summary>
    /// SystemIndex through ADO over the Search.CollatorDSO OLE DB provider (both part of Windows, so no
    /// package), on its own STA thread with a deadline. Null when the index did not answer: the
    /// Windows Search service off, or a query it refused.
    /// </summary>
    private static List<string>? QueryWindowsSearch(string query)
    {
        List<string>? paths = null;
        var thread = new Thread(() =>
        {
            dynamic? connection = null;
            dynamic? records = null;
            try
            {
                var connectionType = Type.GetTypeFromProgID("ADODB.Connection");
                if (connectionType is null) return;
                connection = Activator.CreateInstance(connectionType)!;
                connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
                records = connection.Execute(query);
                var found = new List<string>();
                while (!(bool)records.EOF)
                {
                    if (records.Fields.Item(0).Value is string path && path.Length > 0) found.Add(path);
                    records.MoveNext();
                }
                paths = found;
            }
            catch (Exception error)
            {
                AppLog.Write($"find_files: Windows Search did not answer ({error.GetType().Name})");
            }
            finally
            {
                try { records?.Close(); } catch (Exception) { }
                try { connection?.Close(); } catch (Exception) { }
            }
        })
        { IsBackground = true, Name = "OpenClicky file search" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(IndexTimeout) ? paths : null;
    }

    // MARK: - Find on the page

    /// <summary>
    /// Ctrl+F in the app in front, then the words and Enter. The words are typed only once the focus
    /// has moved into a text field that was not focused before (the find bar); an app without find
    /// would otherwise take them into its document. Typed the way live dictation types (unicode
    /// keystrokes, the clipboard untouched).
    /// </summary>
    public async Task<FindOnPageResult> FindOnPageAsync(string text)
    {
        var window = GetForegroundWindow();
        if (window == 0 || ProcessIdOf(window) == Environment.ProcessId) return FindOnPageResult.NoApp;
        if (textInserter is null || focusedElements is null) return FindOnPageResult.NoFindField;
        if (!FrontAppTextInserter.CanSendInputToForeground()) return FindOnPageResult.NotAllowed;
        if (ProcessDescriber.ImageName(ProcessIdOf(window)) is { } imageName && ScreenNavigation.CtrlFIsNotFindProcessNames.Contains(imageName))
        {
            return FindOnPageResult.NoFindField;
        }
        var before = await focusedElements.FocusedTextTargetAsync().ConfigureAwait(false);
        if (!SendControlShortcut(VirtualKeyF)) return FindOnPageResult.NotAllowed;
        var foundFindField = false;
        for (var attempt = 0; attempt < 8 && !foundFindField; attempt++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            var now = await focusedElements.FocusedTextTargetAsync().ConfigureAwait(false);
            foundFindField = ScreenNavigation.FocusMovedIntoNewTextField(before, now);
        }
        if (!foundFindField) return FindOnPageResult.NoFindField;
        // The bar takes focus a moment before it takes keys.
        await Task.Delay(150).ConfigureAwait(false);
        // Ctrl+A: replace what the find bar held before.
        textInserter.SelectAll();
        if (!textInserter.TypeAsKeystrokes(text)) return FindOnPageResult.NotAllowed;
        await Task.Delay(100).ConfigureAwait(false);
        // Enter: jump to the match.
        Send(VirtualKeyInput(VirtualKeys.Return, keyUp: false), VirtualKeyInput(VirtualKeys.Return, keyUp: true));
        return FindOnPageResult.Typed;
    }

    private const int VirtualKeyF = 0x46;

    /// <summary>Ctrl + a key, with Shift, Alt and Win let go first (FrontAppTextInserter's rule for its own shortcuts).</summary>
    private static bool SendControlShortcut(int virtualKey)
    {
        var inputs = new List<INPUT>();
        FrontAppTextInserter.AddModifierRelease(inputs, FrontAppTextInserter.ModifiersReleasedBeforePaste);
        var controlWasDown = FrontAppTextInserter.IsPhysicallyDown(VirtualKeys.LeftControl) || FrontAppTextInserter.IsPhysicallyDown(VirtualKeys.RightControl);
        if (!controlWasDown) inputs.Add(VirtualKeyInput(VirtualKeys.LeftControl, keyUp: false));
        inputs.Add(VirtualKeyInput(virtualKey, keyUp: false));
        inputs.Add(VirtualKeyInput(virtualKey, keyUp: true));
        if (!controlWasDown) inputs.Add(VirtualKeyInput(VirtualKeys.LeftControl, keyUp: true));
        return Send([.. inputs]) == inputs.Count;
    }

    // MARK: - Click

    /// <summary>
    /// One left click at a physical virtual-desktop pixel: an absolute move, then the button down and
    /// up, in one SendInput so nothing can come between them. WindowFromPoint is the hit test the click
    /// itself gets (the click-through overlay is not hit, the island and the panel are), so a spot on
    /// OpenClicky's own window is refused, and so is one on an app that runs as administrator or one
    /// under the taskbar. A web link in the browser in front is followed in its own tab instead
    /// (FollowLinkAsync, DesktopActionExecutor.Browser.cs); <paramref name="inNewTab"/> asks for a new
    /// selected tab: Ctrl+Shift+click on a link, Ctrl+click on anything else (the Mac's ⌘-click).
    /// </summary>
    public async Task<ScreenClickResult> ClickAsync(PixelPoint point, bool inNewTab = false)
    {
        var hit = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
        var root = hit == 0 ? 0 : GetAncestor(hit, GA_ROOT);
        var processId = root == 0 ? 0 : ProcessIdOf(root);
        if (processId == Environment.ProcessId) return ScreenClickResult.OwnWindow;
        if (processId != 0 && IsProcessElevated(processId) == true && IsProcessElevated(Environment.ProcessId) != true) return ScreenClickResult.NotAllowed;
        if (IsUnderTaskbar(point, root)) return ScreenClickResult.UnderTaskbar;
        var isLink = false;
        if (root != 0 && root == GetForegroundWindow() && BrowserProcesses.IsBrowser(ProcessDescriber.ImageName(processId))
            && await WebLinkAtAsync(point).ConfigureAwait(false) is { } link)
        {
            isLink = true;
            // The pointer goes there, the way a person's click would leave it (the buddy has just flown there too).
            Send(AbsoluteMoveInput(point.X, point.Y));
            if (!inNewTab && await LoadInSelectedTabAsync(link).ConfigureAwait(false)) return ScreenClickResult.FollowedLink;
        }
        // Ctrl+Shift+click opens a link in a new tab and selects it in Chrome, Edge, Brave and Firefox;
        // Ctrl alone is the Mac's ⌘-click for the rest.
        int[] modifiers = !inNewTab ? [] : isLink ? [VirtualKeys.LeftControl, VirtualKeys.LeftShift] : [VirtualKeys.LeftControl];
        var inputs = new List<INPUT> { AbsoluteMoveInput(point.X, point.Y) };
        inputs.AddRange(modifiers.Select(key => VirtualKeyInput(key, keyUp: false)));
        inputs.Add(MouseInput(MOUSEEVENTF_LEFTDOWN));
        inputs.Add(MouseInput(MOUSEEVENTF_LEFTUP));
        inputs.AddRange(modifiers.Reverse().Select(key => VirtualKeyInput(key, keyUp: true)));
        if (Send([.. inputs]) != inputs.Count) return ScreenClickResult.NotAllowed;
        return inNewTab && isLink ? ScreenClickResult.OpenedLinkInNewTab : ScreenClickResult.Clicked;
    }

    /// <summary>
    /// The point is outside its monitor's work area, where the taskbar sits (ScreenNavigation.IsUnderTaskbar),
    /// unless <paramref name="rootWindow"/> fills the monitor. The taskbar's own window is not the test: a
    /// page whose bottom row hides behind it is what the model aims at, and only the work area says so.
    /// </summary>
    private static bool IsUnderTaskbar(PixelPoint point, nint rootWindow)
    {
        var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return false;
        PixelRect? window = null;
        if (rootWindow != 0)
        {
            var frame = VisibleFrame(rootWindow);
            window = PixelRect.FromEdges(frame.Left, frame.Top, frame.Right, frame.Bottom);
        }
        return ScreenNavigation.IsUnderTaskbar(point,
            PixelRect.FromEdges(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
            PixelRect.FromEdges(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
            window);
    }
}
