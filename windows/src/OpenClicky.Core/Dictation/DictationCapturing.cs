// Ports Dictation/DictationCapturing.swift: the seams the take controller is tested through. The
// microphone-and-engine side of a take (`IDictationCapturing`: start, stop, cancel, and three
// streams — level, errors, whether a session is open) and what the controller needs from the PC
// around a take (`DictationHost`: the focused field, the front app, the paste, live typing's
// keystrokes / clipboard copy / clock, the polisher). The real ones are the Speech port's dictation
// manager and the Platform layer; tests drive fakes, since a real session needs a microphone.
//
// The focused-field types below are minimal placeholders for the Platform layer's UI Automation
// snapshot (Swift FocusedFieldSnapshot / FocusedTextTarget / PasteLanding /
// FrontAppTextInserter.Outcome).
// TODO(integration): replace them with (or map them from) Core/Platform's types once it is merged.

using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Dictation;

/// <summary>
/// The microphone-and-engine side of a take, as the take controller sees it. `TProvider` is the
/// Speech port's transcription provider interface.
/// </summary>
public interface IDictationCapturing<in TProvider>
{
    bool IsDictationInProgress { get; }
    string TranscriptionProviderDisplayName { get; }
    string? PreferredMicrophoneUID { get; set; }
    /// <summary>Receives the take's audio as PCM16 mono 16 kHz (for TakeAudioStore), on the capture thread.</summary>
    Action<ReadOnlyMemory<byte>>? AudioRetentionSink { get; set; }

    /// <summary>The microphone level, 0…1.</summary>
    event Action<double>? AudioPowerLevelChanged;
    event Action<string?>? ErrorMessageChanged;
    /// <summary>True from the moment a keyboard session opens until its transcript was delivered or it ended.</summary>
    event Action<bool>? SessionActivityChanged;

    Task StartPushToTalkFromKeyboardShortcutAsync(string currentDraftText, Action<string> updateDraftText, Action<string> submitDraftText);
    void StopPushToTalkFromKeyboardShortcut();
    void CancelCurrentDictation(bool preserveDraftText);
    void ReplaceTranscriptionProvider(TProvider provider);
    /// <summary>Words the engine should expect (the dictionary); sent with the next session.</summary>
    void UpdateContextualKeyterms(IReadOnlyList<string> contextualKeyterms);
}

/// <summary>
/// The focused element through UI Automation, as one read (Swift FocusedFieldSnapshot). Placeholder
/// until Core/Platform defines it.
/// </summary>
public sealed record FocusedFieldInfo(
    string? AppProcessName,
    string? AppName,
    // The UI Automation control type ("Edit", "Document", "ComboBox"…).
    string? Role,
    // A text field, text area or editable document has focus.
    bool IsEditable,
    // The field is a password field: nothing is ever pasted there.
    bool IsSecure,
    string? Value,
    string? SelectedText)
{
    public static FocusedFieldInfo Unknown { get; } = new(null, null, null, false, false, null, null);
}

/// <summary>
/// The focused element system-wide, for live typing: kept at press and compared before every typed
/// phrase (Swift FocusedTextTarget). `Element` is the Platform layer's opaque UI Automation handle;
/// two targets are the same element when `DictationHost.IsSameTextTarget` says so.
/// </summary>
public sealed record FocusedTextTarget(int ProcessId, string? Role, bool IsSecure, string? ProcessName, object? Element)
{
    /// <summary>
    /// Control types live typing types into: Edit and ComboBox. Not Document (a focused web page,
    /// where letters are single-key shortcuts): those keep the paste path.
    /// </summary>
    public static IReadOnlySet<string> LiveTypingTextRoles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Edit", "ComboBox" };

    public static bool IsLiveTypingTextRole(string? role) => role is not null && LiveTypingTextRoles.Contains(role);

    public LiveTypingTargetIdentity Identity => new(ProcessId, ProcessName);
}

/// <summary>What a paste was seen to do (Swift PasteLanding).</summary>
public enum PasteLanding
{
    /// <summary>The field's text grew by the pasted text (or contains it): it landed.</summary>
    Verified,
    /// <summary>The keystroke was posted into an editable element but the text was not seen in it.</summary>
    Posted,
    /// <summary>After the paste nothing editable has focus: the words are left on the clipboard.</summary>
    NoTarget,
    /// <summary>The keystroke could not be posted: the text is on the clipboard only.</summary>
    LeftOnPasteboard,
}

/// <summary>What posting a paste did (Swift FrontAppTextInserter.Outcome).</summary>
public enum TextInsertionOutcome
{
    /// <summary>The keystroke was posted; the app in front received the text.</summary>
    Typed,
    /// <summary>The text is on the clipboard instead.</summary>
    LeftOnPasteboard,
}

/// <summary>
/// What the controller needs from the PC around a take; injectable so tests paste and type nowhere.
/// Every member must be set by the composition root (the Platform layer) or the test.
/// </summary>
public sealed class DictationHost
{
    public Func<FocusedFieldInfo> ReadFocusedField { get; init; } = () => FocusedFieldInfo.Unknown;
    public Func<IReadOnlyList<string>> ReadNearbyTerms { get; init; } = () => [];
    /// <summary>The foreground app's process name ("Code.exe").</summary>
    public Func<string?> FrontAppProcessName { get; init; } = () => null;
    /// <summary>Windows has no global secure input; the Platform layer reports the focused element's IsPassword.</summary>
    public Func<bool> IsSecureInputOn { get; init; } = () => false;
    public Func<string, TextInsertionOutcome> Paste { get; init; } = _ => TextInsertionOutcome.LeftOnPasteboard;
    /// <summary>Live typing: the text as posted keystrokes (no clipboard); false when they could not be posted.</summary>
    public Func<string, bool> TypeText { get; init; } = _ => false;
    /// <summary>Live typing that had to stop leaves the rest of the take here, for a manual Ctrl+V.</summary>
    public Action<string> CopyToPasteboard { get; init; } = _ => { };
    /// <summary>The clock live typing measures how long a word has been stable with.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    /// <summary>The focused element, read at press and before every typed phrase.</summary>
    public Func<FocusedTextTarget?> ReadFocusedTextTarget { get; init; } = () => null;
    /// <summary>Whether two reads are the same element (the Mac's CFEqual + pid).</summary>
    public Func<FocusedTextTarget, FocusedTextTarget, bool> IsSameTextTarget { get; init; } =
        (first, second) => first.ProcessId == second.ProcessId && Equals(first.Element, second.Element);
    /// <summary>The modifier keys physically held right now (live typing waits while a foreign one is).</summary>
    public Func<LiveTypingModifiers> HeldModifiers { get; init; } = () => LiveTypingModifiers.None;
    public Func<string, FocusedFieldInfo, Task<PasteLanding>> VerifyPaste { get; init; } = (_, _) => Task.FromResult(PasteLanding.Posted);
    public Func<ITakePolisher?> MakePolisher { get; init; } = DictationEngineResolver.MakePolisher;
    public Func<DictationEngineChoice, string?> EngineUnavailableReason { get; init; } = DictationEngineResolver.UnavailableReason;
    /// <summary>The dictation key now (live typing lets its own modifier through).</summary>
    public Func<DictationHotkey> DictationKey { get; init; } = () => DictationSettings.Shared.DictationKey;
}
