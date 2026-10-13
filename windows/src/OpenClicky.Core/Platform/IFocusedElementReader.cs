namespace OpenClicky.Core.Platform;

/// <summary>
/// The focused element of the app in front, as UI Automation reports it (FocusedFieldSnapshot on
/// the Mac). <see cref="AppProcessName"/> plays the part of the Mac's bundle id ("chrome.exe").
/// </summary>
public sealed record FocusedFieldSnapshot
{
    public static readonly FocusedFieldSnapshot Unknown = new();

    /// <summary>The image name of the process that owns the element, e.g. "chrome.exe".</summary>
    public string? AppProcessName { get; init; }
    /// <summary>The app's display name (FileDescription), e.g. "Google Chrome".</summary>
    public string? AppName { get; init; }
    public int? ProcessId { get; init; }
    /// <summary>The UIA control type's programmatic name without the prefix: "Edit", "Document", "ComboBox", "Group"…</summary>
    public string? Role { get; init; }
    /// <summary>A text field, text area, editable document or editable combo box has focus (never a password field).</summary>
    public bool IsEditable { get; init; }
    /// <summary>UIA IsPassword: nothing is ever pasted, typed or read there.</summary>
    public bool IsSecure { get; init; }
    /// <summary>The field's text (≤ 20 000 chars), only for editable non-password fields; null when it cannot be read.</summary>
    public string? Value { get; init; }
    public string? SelectedText { get; init; }
    /// <summary>Selection start in UTF-16 units from the start of <see cref="Value"/>, when TextPattern could tell.</summary>
    public int? SelectionStart { get; init; }
    public int? SelectionLength { get; init; }
    /// <summary>The element's UIA Name (its caption or label).</summary>
    public string? Name { get; init; }
    /// <summary>The element's UIA runtime id ("42.1234.4.17"): equal ids are the same element (what Automation.Compare compares).</summary>
    public string? RuntimeId { get; init; }
}

/// <summary>
/// The focused element system-wide, kept when live typing starts and compared before every typed
/// phrase so keystrokes only go to the field the take began in.
/// </summary>
public sealed record FocusedTextTarget
{
    public int ProcessId { get; init; }
    public string? RuntimeId { get; init; }
    public string? Role { get; init; }
    public bool IsSecure { get; init; }
    /// <summary>Image name of the owning process (the Mac's bundleID).</summary>
    public string? AppProcessName { get; init; }
    /// <summary>The platform's element object (System.Windows.Automation.AutomationElement on Windows); opaque to Core.</summary>
    public object? NativeElement { get; init; }

    /// <summary>
    /// Control types live typing types into: plain text boxes and editable combo boxes. Not
    /// "Document" (a focused web page or rich document, where letters can be single-key shortcuts,
    /// as the Mac excludes AXWebArea): those keep the paste path.
    /// </summary>
    public static readonly IReadOnlySet<string> LiveTypingTextRoles = new HashSet<string>(StringComparer.Ordinal) { "Edit", "ComboBox" };

    public static bool IsLiveTypingTextRole(string? role) => role is not null && LiveTypingTextRoles.Contains(role);

    public bool AcceptsLiveTyping => IsLiveTypingTextRole(Role) && !IsSecure;

    /// <summary>The same element: same pid and same UIA runtime id (an app that hands out a new element for the same field stops live typing early, which only sends the rest to the clipboard).</summary>
    public bool IsSameElement(FocusedTextTarget other) =>
        ProcessId == other.ProcessId && RuntimeId is not null && RuntimeId == other.RuntimeId;
}

/// <summary>One captioned control from the foreground window's UI Automation tree (AccessibleElementCandidate's raw data).</summary>
/// <param name="Role">Control type name: "Button", "Hyperlink", "MenuItem", "TabItem", "Edit", "Text"…</param>
/// <param name="Title">Name, else HelpText, else the ValuePattern value.</param>
/// <param name="ContainerTitles">Captions of the groups the control sits in, nearest first, at most three.</param>
/// <param name="Bounds">Bounding rectangle in physical screen pixels.</param>
public sealed record AccessibleControl(string Role, string Title, IReadOnlyList<string> ContainerTitles, PixelRect Bounds);

/// <summary>
/// Read-only, bounded UI Automation queries about the app in front. Every call runs off the caller's
/// thread with a timeout and answers "unknown" (never a guess) when UIA does not answer in time.
/// </summary>
public interface IFocusedElementReader
{
    /// <summary>The focused element of the app in front, now.</summary>
    Task<FocusedFieldSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>The focused element system-wide with its pid, role and password flag (cheap: runs before every live-typed phrase).</summary>
    Task<FocusedTextTarget?> FocusedTextTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the element in <paramref name="target"/> still has keyboard focus.</summary>
    Task<bool> StillHasFocusAsync(FocusedTextTarget target, CancellationToken cancellationToken = default);

    /// <summary>Captions near the focus: names of the foreground window's controls (depth &lt; 10, ≤ 400 nodes), 2–60 chars with a letter, de-duplicated.</summary>
    Task<IReadOnlyList<string>> NearbyTermsAsync(int limit = 40, CancellationToken cancellationToken = default);

    /// <summary>The highlighted text, whitespace collapsed and capped at 600 chars (+ "…"); null in a password field or when nothing is selected.</summary>
    Task<string?> SelectedTextAsync(CancellationToken cancellationToken = default);

    /// <summary>All the text in the focused box for write-in-place; null when not editable, a password field, empty, or longer than <paramref name="maximumCharacters"/>.</summary>
    Task<string?> FocusedBoxTextAsync(int maximumCharacters = 6_000, CancellationToken cancellationToken = default);

    /// <summary>Every captioned control in the foreground window: breadth-first, ≤ 600 nodes, depth ≤ 12, 0.25 s budget.</summary>
    Task<IReadOnlyList<AccessibleControl>> CaptionedControlsInForegroundWindowAsync(CancellationToken cancellationToken = default);
}

/// <summary>The highlighted-text limits (SelectedTextReader on the Mac).</summary>
public static class SelectedTextLimits
{
    public const int MaximumCharacters = 600;

    /// <summary>Whitespace collapsed and capped; null when nothing is left.</summary>
    public static string? Tidied(string? selectedText)
    {
        if (selectedText is null) return null;
        var collapsed = string.Join(' ', selectedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return null;
        if (collapsed.Length <= MaximumCharacters) return collapsed;
        var cut = MaximumCharacters;
        if (char.IsHighSurrogate(collapsed[cut - 1])) cut--;
        return collapsed[..cut] + "…";
    }
}

/// <summary>Where a paste landed, decided by reading the field back.</summary>
public enum PasteLandingResult
{
    /// <summary>The field's text grew by the pasted text (or contains it).</summary>
    Verified,
    /// <summary>Sent into an editable element but not seen in it (unreadable web/Electron view, or another app has focus now).</summary>
    Posted,
    /// <summary>After the paste nothing editable has focus: the words are left on the clipboard.</summary>
    NoTarget,
    /// <summary>Input could not be sent: the text is on the clipboard only.</summary>
    LeftOnClipboard,
}

public static class PasteLanding
{
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(0.35);

    /// <summary>Reads the field back shortly after the paste and compares it with what was there before.</summary>
    public static async Task<PasteLandingResult> VerifyAsync(
        string text,
        FocusedFieldSnapshot before,
        IFocusedElementReader reader,
        ITextInserter inserter,
        TimeSpan? settle = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(settle ?? DefaultSettle, cancellationToken).ConfigureAwait(false);
        var after = await reader.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (KeepsPastedWordsOnClipboard(before, after)) inserter.CopyToClipboard(text);
        return Landing(text, before, after);
    }

    /// <summary>An editable element whose value cannot be read back: the paste probably landed, but the old clipboard is not put back so the words stay one Ctrl+V away.</summary>
    public static bool KeepsPastedWordsOnClipboard(FocusedFieldSnapshot before, FocusedFieldSnapshot after) =>
        after.AppProcessName == before.AppProcessName && after.Role is not null && after.IsEditable && after.Value is null;

    public static PasteLandingResult Landing(string text, FocusedFieldSnapshot before, FocusedFieldSnapshot after)
    {
        if (after.AppProcessName != before.AppProcessName) return PasteLandingResult.Posted;
        if (after.Role is null || !after.IsEditable) return PasteLandingResult.NoTarget;
        if (after.Value is not { } value) return PasteLandingResult.Posted;
        var needle = text.Trim();
        var probe = needle.Length > 200 ? needle[..200] : needle;
        if (probe.Length > 0 && value.Contains(probe, StringComparison.Ordinal)) return PasteLandingResult.Verified;
        if (before.Value is { } previous && value.Length >= previous.Length + Math.Max(1, needle.Length / 2)) return PasteLandingResult.Verified;
        return PasteLandingResult.Posted;
    }
}
