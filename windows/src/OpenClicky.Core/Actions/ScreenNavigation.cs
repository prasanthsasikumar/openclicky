// Port of macos/OpenClicky/OpenClicky/ScreenNavigation.swift (the pure parts).
//
// Moving around an app by voice: scroll the window in front, click what the person named, find
// words on the page, find a file. Before this the fast lane could open a page but not move
// through it: "scroll down" was answered with how to use a trackpad and "click the first link"
// with a point at it (2026-10-29, Chrome, hands-free).
//
// The pure parts (wheel steps, the SendInput coordinate, which clicks are refused, the Windows
// Search query, the ranking, whether Ctrl+F really opened a find bar) are unit-tested; the input
// itself is OpenClicky.Windows/Platform/DesktopActionExecutor.Navigation.cs.
//
// Windows differences: wheel events come in notches of 120, not pixels, so a distance becomes
// notches at about 100 DIPs a notch (Chrome's and Edge's step at the default three lines); the
// Mac's Accessibility permission has no twin, and what refuses input here is UIPI (an app that
// runs as administrator), so that is the "not allowed" case; Spotlight is the Windows Search index
// (SystemIndex), with a bounded walk of the home folder when the index does not answer;
// AppData stands in for ~/Library among the paths a person is never shown; Ctrl+F is left alone
// in Outlook, where it forwards an email; OpenClicky's own window under a click is found by
// WindowFromPoint (the hit test the click itself gets) rather than a window list; the Mac's
// "under the Dock or menu bar" is outside the monitor's work area, except under a window that
// fills the monitor (full screen hides the taskbar).

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Actions;

public enum ScrollDirection
{
    Up,
    Down,
    Left,
    Right,
}

public enum ScrollAmount
{
    /// <summary>Most of a screenful, the way Page Down moves.</summary>
    Page,
    /// <summary>A quarter of the window.</summary>
    Little,
    /// <summary>All the way: the top for up, the bottom for down.</summary>
    End,
}

public enum ScrollResult
{
    Scrolled,
    /// <summary>No ordinary window in front (or OpenClicky itself is in front).</summary>
    NoWindow,
    /// <summary>The window in front runs as administrator: Windows drops this app's input to it.</summary>
    NotAllowed,
}

public enum FindOnPageResult
{
    Typed,
    /// <summary>
    /// Ctrl+F did not put the focus in a new text field, so nothing was typed: typing into whatever
    /// had the focus could have written into the person's document.
    /// </summary>
    NoFindField,
    NoApp,
    NotAllowed,
}

public enum ScreenClickResult
{
    Clicked,
    /// <summary>The spot is on one of OpenClicky's own windows.</summary>
    OwnWindow,
    /// <summary>Under the taskbar (outside the monitor's work area): a click there lands on it, not on the page.</summary>
    UnderTaskbar,
    /// <summary>The window there runs as administrator, or nothing can click on this PC.</summary>
    NotAllowed,
}

public static class ScreenNavigation
{
    /// <summary>The schema value: "up", "down", …</summary>
    public static string RawValue(this ScrollDirection direction) => direction.ToString().ToLowerInvariant();

    /// <summary>The schema value: "page", "little", "end".</summary>
    public static string RawValue(this ScrollAmount amount) => amount.ToString().ToLowerInvariant();

    public static readonly IReadOnlyList<ScrollDirection> AllDirections = [ScrollDirection.Up, ScrollDirection.Down, ScrollDirection.Left, ScrollDirection.Right];

    public static readonly IReadOnlyList<ScrollAmount> AllAmounts = [ScrollAmount.Page, ScrollAmount.Little, ScrollAmount.End];

    public static ScrollDirection? Direction(string raw) =>
        AllDirections.Cast<ScrollDirection?>().FirstOrDefault(direction => direction!.Value.RawValue() == raw.Trim().ToLowerInvariant());

    public static ScrollAmount? Amount(string raw) =>
        AllAmounts.Cast<ScrollAmount?>().FirstOrDefault(amount => amount!.Value.RawValue() == raw.Trim().ToLowerInvariant());

    /// <summary>One wheel notch (WHEEL_DELTA).</summary>
    public const int WheelDelta = 120;

    /// <summary>
    /// About how far one notch moves a page, in DIPs: Chrome and Edge move 100 at Windows' default of
    /// three lines a notch; a Win32 list moves three lines, a little less.
    /// </summary>
    public const double DipsPerNotch = 100;

    /// <summary>
    /// Wheel steps, each (vertical, horizontal) in wheel units (multiples of 120). Positive vertical
    /// scrolls up (the content moves down), as a wheel turned away from the person; positive
    /// horizontal scrolls right (MOUSEEVENTF_HWHEEL's tilt to the right). <paramref name="viewportWidth"/>
    /// and <paramref name="viewportHeight"/> are physical pixels, <paramref name="scale"/> the
    /// window's DPI / 96: a page is 80 % of the window, a little 25 %, at least one notch.
    /// </summary>
    public static IReadOnlyList<(int Vertical, int Horizontal)> ScrollSteps(ScrollDirection direction, ScrollAmount amount,
                                                                             double viewportWidth, double viewportHeight, double scale = 1)
    {
        var span = (direction is ScrollDirection.Up or ScrollDirection.Down ? viewportHeight : viewportWidth) / Math.Max(scale, 0.1);
        int notches;
        int count;
        switch (amount)
        {
            case ScrollAmount.Little:
                notches = Math.Max(1, (int)Math.Round(span * 0.25 / DipsPerNotch));
                count = 1;
                break;
            // Ten big steps reach the end of any page a person would scroll by voice (the Mac's ten
            // 5,000 px steps).
            case ScrollAmount.End:
                notches = 50;
                count = 10;
                break;
            default:
                notches = Math.Max(1, (int)Math.Round(span * 0.8 / DipsPerNotch));
                count = 1;
                break;
        }
        var step = notches * WheelDelta;
        (int, int) one = direction switch
        {
            ScrollDirection.Up => (step, 0),
            ScrollDirection.Down => (-step, 0),
            ScrollDirection.Left => (0, -step),
            _ => (0, step),
        };
        return Enumerable.Repeat(one, count).ToList();
    }

    /// <summary>
    /// A physical pixel on the virtual desktop → SendInput's absolute coordinate with
    /// MOUSEEVENTF_VIRTUALDESK: 0 at the desktop's first pixel, 65535 at its last, on each axis
    /// (<paramref name="origin"/> is SM_XVIRTUALSCREEN, which is negative with a monitor left of
    /// the primary). The Mac's AppKit-to-CG flip has no twin: both are top-left here.
    /// </summary>
    public static int AbsoluteMouseCoordinate(int pixel, int origin, int size) =>
        size <= 1 ? 0 : (int)Math.Round((pixel - origin) * 65535.0 / (size - 1));

    /// <summary>
    /// Words on a control that commit something a mishearing must never commit. Such a control is
    /// pointed at and left for the person to click. Whole words only: "Posts" and "Ordering" pass.
    /// </summary>
    public static readonly IReadOnlyList<string> RiskyClickWords =
    [
        "send", "buy", "pay", "purchase", "order", "checkout", "check out", "delete", "remove",
        "erase", "trash", "submit", "confirm", "transfer", "donate", "subscribe", "unsubscribe",
        "sign out", "log out", "uninstall", "publish", "post", "place order", "book now",
    ];

    public static bool ClickIsRisky(string label, string text)
    {
        var joined = " " + string.Join(" ", Words($"{label} {text}")) + " ";
        return RiskyClickWords.Any(word => joined.Contains($" {word} ", StringComparison.Ordinal));
    }

    private static readonly HashSet<string> PictureWords = new(StringComparer.Ordinal)
    {
        "video", "videos", "thumbnail", "image", "picture", "photo", "play", "player",
    };

    /// <summary>
    /// Whether the model's label names a picture rather than words: a video, thumbnail, image. Such a
    /// target is aimed at the image itself, not snapped to the caption under it.
    /// </summary>
    public static bool NamesAPicture(string label) => Words(label).Any(PictureWords.Contains);

    /// <summary>
    /// Whether a click at <paramref name="point"/> would land on the taskbar rather than the page: the
    /// point is on <paramref name="monitor"/> but outside its work area (less 4 px at the top and
    /// bottom, the Mac's inset). A window that fills the whole monitor (a full-screen video, F11) has
    /// the taskbar hidden under it, so nothing is under the edge then.
    /// </summary>
    public static bool IsUnderTaskbar(PixelPoint point, PixelRect monitor, PixelRect workArea, PixelRect? windowAtPoint)
    {
        if (!monitor.Contains(point)) return false;
        if (windowAtPoint is { } window && window.Left <= monitor.Left && window.Top <= monitor.Top
            && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom)
        {
            return false;
        }
        var visible = PixelRect.FromEdges(workArea.Left, workArea.Top + 4, workArea.Right, workArea.Bottom - 4);
        return !visible.Contains(point);
    }

    /// <summary>Lower-cased runs of letters and digits; everything else separates.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(character);
                continue;
            }
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// <summary>
    /// The words a file's name must hold, at most six. Only letters and digits survive, so nothing
    /// said can change a query's shape: no quote, no LIKE wildcard (% and _), no path.
    /// </summary>
    public static IReadOnlyList<string> FileSearchTerms(string words) => Words(words).Take(6).ToList();

    /// <summary>
    /// A Windows Search (SystemIndex) query for files under <paramref name="scopeDirectory"/> whose
    /// names contain every word the person said, in any order ("tax pdf" finds "Tax return
    /// 2025.pdf"). The scope's single quotes are doubled (a profile can be called O'Brien); the
    /// words cannot carry any. Null when nothing usable was said.
    /// </summary>
    public static string? WindowsSearchQuery(string words, string scopeDirectory, int limit = 400)
    {
        var terms = FileSearchTerms(words);
        if (terms.Count == 0) return null;
        var scope = "file:" + scopeDirectory.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var conditions = string.Join(" AND ", terms.Select(term => $"System.FileName LIKE '%{term}%'"));
        return $"SELECT TOP {limit} System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='{scope}' AND {conditions}";
    }

    /// <summary>Whether a file's name holds every word said (the walk's test; the index does it in SQL).</summary>
    public static bool NameHasEveryTerm(string fileName, IReadOnlyList<string> terms) =>
        terms.Count > 0 && terms.All(term => fileName.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>Folder names never worth showing a person, nor walking into.</summary>
    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "node_modules", "$Recycle.Bin",
    };

    /// <summary>
    /// A path worth showing a person: nothing hidden (a name starting with a dot), nothing inside
    /// AppData (the Mac's Library), the recycle bin or a dependency folder.
    /// </summary>
    public static bool IsWorthShowing(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return !parts.Any(part => part.StartsWith('.') || SkippedFolderNames.Contains(part));
    }

    public static bool IsSkippedFolderName(string name) => name.StartsWith('.') || SkippedFolderNames.Contains(name);

    /// <summary>
    /// The paths worth showing (<see cref="IsWorthShowing"/>), best first: names that are exactly the
    /// request, then ones that start with it, then ones where every word said starts a word of the
    /// name ("riti" is "Riti", not "Writing"); otherwise the order found is kept.
    /// </summary>
    public static IReadOnlyList<string> RankFoundFiles(IEnumerable<string> paths, string query, int limit = 5)
    {
        var wanted = query.Trim().ToLowerInvariant();
        var wantedWords = Words(wanted);
        int Score(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/')).ToLowerInvariant();
            if (name == wanted) return 0;
            if (name.StartsWith(wanted, StringComparison.Ordinal)) return 1;
            var nameWords = Words(name);
            if (wantedWords.All(wantedWord => nameWords.Any(nameWord => nameWord.StartsWith(wantedWord, StringComparison.Ordinal)))) return 2;
            return 3;
        }
        return paths.Where(IsWorthShowing)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((path, index) => (Path: path, Index: index, Score: Score(path)))
            .OrderBy(entry => entry.Score).ThenBy(entry => entry.Index)
            .Take(limit)
            .Select(entry => entry.Path)
            .ToList();
    }

    /// <summary>
    /// The fallback when the index does not answer: a breadth-first walk of <paramref name="roots"/>
    /// (shallow folders first, where people keep things), skipping hidden and system entries and the
    /// folders <see cref="IsWorthShowing"/> leaves out, stopping after <paramref name="maximumEntries"/>
    /// entries, <paramref name="maximumMatches"/> matches or <paramref name="budget"/>, whichever
    /// comes first. Files and folders whose names hold every word said, in the order found.
    /// </summary>
    public static IReadOnlyList<string> WalkForFiles(IEnumerable<string> roots, string words, TimeSpan budget,
                                                     int maximumEntries = 60_000, int maximumMatches = 400, int maximumDepth = 8)
    {
        var terms = FileSearchTerms(words);
        if (terms.Count == 0) return [];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var matches = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Directory, int Depth)>();
        foreach (var root in roots)
        {
            if (Directory.Exists(root) && seen.Add(Path.GetFullPath(root))) queue.Enqueue((root, 0));
        }
        var visited = 0;
        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory, "*", options); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            try
            {
                foreach (var entry in entries)
                {
                    if (++visited > maximumEntries || stopwatch.Elapsed > budget) return matches;
                    var name = Path.GetFileName(entry);
                    var isDirectory = Directory.Exists(entry);
                    if (isDirectory && IsSkippedFolderName(name)) continue;
                    if (!name.StartsWith('.') && NameHasEveryTerm(name, terms))
                    {
                        matches.Add(entry);
                        if (matches.Count >= maximumMatches) return matches;
                    }
                    if (isDirectory && depth + 1 < maximumDepth && seen.Add(entry)) queue.Enqueue((entry, depth + 1));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return matches;
    }

    /// <summary>
    /// Whether Ctrl+F opened a find bar the words may go into: the focus is now in a text field
    /// (Edit or ComboBox, never a password box) that is not the element focused before. When either
    /// read is missing (UI Automation did not answer in time) or the field has no runtime id, it
    /// cannot be told apart from a box the person was typing in, so the answer is no.
    /// </summary>
    public static bool FocusMovedIntoNewTextField(FocusedTextTarget? before, FocusedTextTarget? after)
    {
        if (before is null || after is null) return false;
        if (!after.AcceptsLiveTyping || after.RuntimeId is null) return false;
        return !after.IsSameElement(before);
    }

    /// <summary>
    /// Apps where Ctrl+F is not find: in Outlook (classic and new) it forwards the selected email, and
    /// the forward's To box would take the words.
    /// </summary>
    public static readonly IReadOnlySet<string> CtrlFIsNotFindProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "outlook.exe", "olk.exe",
    };
}
