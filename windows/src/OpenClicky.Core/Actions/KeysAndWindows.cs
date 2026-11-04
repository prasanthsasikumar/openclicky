// The keyboard and window actions (2026-11-01): "press tab", "hit enter twice", "copy that" (ctrl+c), "type hello
// in the prompt", "maximize this", "switch to brave". Their pure parts: which keys and shortcuts a spoken request
// may press (a closed set, so a mishearing can't reach the Windows key, or anything that closes, quits, deletes or
// opens system UI), how a chord is
// read ("ctrl+shift+t", "shift tab"), and the window commands. The Windows side sends them (DesktopActionExecutor.Keys).

namespace OpenClicky.Core.Actions;

/// <summary>One key with the modifiers held for it, as virtual-key codes, and how it is said back ("ctrl+c").</summary>
public sealed record KeyChord(IReadOnlyList<int> Modifiers, int Key, string Spoken);

public static class KeyChords
{
    public const int Control = 0x11, Shift = 0x10, Alt = 0x12;
    public const int MaximumChords = 10;
    public const int MaximumTimes = 20;

    private static readonly Dictionary<string, int> ModifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = Control, ["control"] = Control, ["shift"] = Shift, ["alt"] = Alt,
    };

    /// <summary>The keys a request may press, by the names a model writes for them.</summary>
    private static readonly Dictionary<string, int> NamedKeys = BuildNamedKeys();

    private static Dictionary<string, int> BuildNamedKeys()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["tab"] = 0x09, ["enter"] = 0x0D, ["return"] = 0x0D, ["escape"] = 0x1B, ["esc"] = 0x1B, ["space"] = 0x20, ["spacebar"] = 0x20,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["pgup"] = 0x21, ["pgdn"] = 0x22,
        };
        for (var number = 1; number <= 12; number++) keys[$"f{number}"] = 0x6F + number;
        for (var letter = 'a'; letter <= 'z'; letter++) keys[letter.ToString()] = char.ToUpperInvariant(letter);
        for (var digit = '0'; digit <= '9'; digit++) keys[digit.ToString()] = digit;
        return keys;
    }

    /// <summary>
    /// Chords left to the person, also with more modifiers held (Ctrl+W covers Ctrl+Shift+W): what closes or quits (Alt+F4,
    /// Ctrl+F4, Ctrl+W, Ctrl+Q; the window tool closes windows), deletes for good (Shift+Delete, Ctrl+D in Explorer and
    /// Outlook), or opens system UI (Ctrl+Alt+Delete, Ctrl+Esc and Ctrl+Shift+Esc, Alt+Esc, Alt+Space, Alt+Tab).
    /// </summary>
    private static readonly (int[] Modifiers, int Key)[] LeftToThePerson =
    [
        ([Alt], 0x73), ([Control], 0x73), ([Control], 'W'), ([Control], 'Q'),
        ([Shift], 0x2E), ([Control], 'D'),
        ([Control, Alt], 0x2E), ([Control], 0x1B), ([Alt], 0x1B), ([Alt], 0x20), ([Alt], 0x09),
    ];

    /// <summary>
    /// One chord: "tab", "shift+tab", "ctrl+shift+t", "alt+left" (also "ctrl c", "ctrl-c"). Null when it names a key
    /// outside the set, a letter or digit with no Ctrl or Alt (words are typed with type_text, not pressed), or a
    /// combination left to the person (<see cref="LeftToThePerson"/>).
    /// </summary>
    public static KeyChord? Parse(string text)
    {
        var parts = text.Trim().ToLowerInvariant().Replace("arrow", "").Replace("page up", "pageup").Replace("page down", "pagedown")
            .Split(['+', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var modifiers = new List<int>();
        foreach (var part in parts[..^1])
        {
            if (!ModifierKeys.TryGetValue(part, out var modifier)) return null;
            if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
        }
        if (!NamedKeys.TryGetValue(parts[^1], out var key)) return null;
        var isCharacter = parts[^1].Length == 1;
        if (isCharacter && !modifiers.Contains(Control) && !modifiers.Contains(Alt)) return null;
        if (LeftToThePerson.Any(chord => chord.Key == key && chord.Modifiers.All(modifiers.Contains))) return null;
        modifiers.Sort((a, b) => Order(a).CompareTo(Order(b)));
        var spoken = string.Join("+", modifiers.Select(Name).Append(parts[^1]));
        return new KeyChord(modifiers, key, spoken);
    }

    /// <summary>Several chords pressed one after another ("tab tab enter", "ctrl+a, ctrl+c"); null when any is not allowed.</summary>
    public static IReadOnlyList<KeyChord>? ParseSequence(string text)
    {
        var normalized = text.ToLowerInvariant().Replace("page up", "pageup").Replace("page down", "pagedown");
        var pieces = normalized.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // "tab tab enter" (no commas) is three keys; "ctrl c" is one chord: spaces split only when no word is a modifier.
        if (pieces.Count == 1 && !pieces[0].Split(' ').Any(word => ModifierKeys.ContainsKey(word)))
        {
            pieces = pieces[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        if (pieces.Count is 0 or > MaximumChords) return null;
        var chords = new List<KeyChord>();
        foreach (var piece in pieces)
        {
            if (Parse(piece) is not { } chord) return null;
            chords.Add(chord);
        }
        return chords;
    }

    private static int Order(int modifier) => modifier switch { Control => 0, Alt => 1, _ => 2 };

    private static string Name(int modifier) => modifier switch { Control => "ctrl", Alt => "alt", _ => "shift" };
}

/// <summary>What the window tool does to a window.</summary>
public enum WindowCommand
{
    Minimize,
    Maximize,
    Restore,
    Close,
    /// <summary>Brings an app's window to the front ("switch to chrome").</summary>
    Focus,
}

public static class WindowCommands
{
    public static WindowCommand? Named(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "minimize" or "minimise" or "hide" => WindowCommand.Minimize,
        "maximize" or "maximise" or "fullscreen" or "full screen" => WindowCommand.Maximize,
        "restore" or "unmaximize" => WindowCommand.Restore,
        "close" => WindowCommand.Close,
        "focus" or "switch" or "switch to" or "show" or "open" or "bring to front" => WindowCommand.Focus,
        _ => null,
    };

    public static string RawValue(this WindowCommand command) => command switch
    {
        WindowCommand.Minimize => "minimize",
        WindowCommand.Maximize => "maximize",
        WindowCommand.Restore => "restore",
        WindowCommand.Close => "close",
        _ => "focus",
    };

    /// <summary>Said after it worked: "Minimized Chrome.", "Switched to Brave."</summary>
    public static string Sentence(this WindowCommand command, string window) => command switch
    {
        WindowCommand.Minimize => $"Minimized {window}.",
        WindowCommand.Maximize => $"Maximized {window}.",
        WindowCommand.Restore => $"Restored {window}.",
        WindowCommand.Close => $"Closed {window}.",
        _ => $"Switched to {window}.",
    };
}

/// <summary>How pressing keys or typing went on the Windows side.</summary>
public enum KeyInputResult
{
    Sent,
    NoWindow,
    /// <summary>Windows refused input to the window in front (one running as administrator, the lock screen).</summary>
    NotAllowed,
    /// <summary>The keyboard is in a password box: the person types those themselves.</summary>
    PasswordField,
}

/// <param name="Window">The window's app or title, for the sentence; null when nothing was done.</param>
/// <param name="AppNotRunning">The named app has no window open.</param>
public sealed record WindowControlResult(string? Window, bool AppNotRunning = false)
{
    public bool Done => Window is not null;
}
