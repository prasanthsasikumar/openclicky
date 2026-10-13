// Ports Dictation/DictationCapturing.swift: the seams the take controller is tested through. The
// microphone-and-engine side of a take (`IDictationCapturing`: start, stop, cancel, and three
// streams — level, errors, whether a session is open) and what the controller needs from the PC
// around a take (`DictationHost`: the focused field, the front app, the paste, live typing's
// keystrokes / clipboard copy / clock, the polisher). The real capture side is the Speech port's
// BuddyDictationManager; the real host is built from the Platform layer by
// OpenClicky.Windows/Dictation/WindowsDictationHost. Tests drive fakes, since a real session needs
// a microphone.

using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Dictation;

/// <summary>
/// The microphone-and-engine side of a take, as the take controller sees it. Every member is called
/// on the main thread, and the events are raised there.
/// </summary>
public interface IDictationCapturing
{
    bool IsDictationInProgress { get; }
    string TranscriptionProviderDisplayName { get; }
    /// <summary>The capture endpoint to record from; null = the default device.</summary>
    string? PreferredMicrophoneId { get; set; }
    /// <summary>Every captured frame (16 kHz mono), on the capture thread, for a caller that keeps the take's audio (TakeAudioStore).</summary>
    Action<AudioFrame>? AudioRetentionSink { get; set; }

    /// <summary>The microphone level, 0…1.</summary>
    event Action<double>? AudioPowerLevelChanged;
    event Action<string?>? ErrorMessageChanged;
    /// <summary>True from the moment a keyboard session opens until its transcript was delivered or it ended.</summary>
    event Action<bool>? SessionActivityChanged;

    Task StartPushToTalkFromKeyboardShortcutAsync(string currentDraftText, Action<string> updateDraftText, Action<string> submitDraftText,
                                                  CancellationToken cancellationToken = default);
    void StopPushToTalkFromKeyboardShortcut();
    void CancelCurrentDictation(bool preserveDraftText = true);
    void ReplaceTranscriptionProvider(IBuddyTranscriptionProvider provider);
    /// <summary>Words the engine should expect (the dictionary); sent with the next session.</summary>
    void UpdateContextualKeyterms(IReadOnlyList<string> contextualKeyterms);
}

/// <summary>
/// What the controller needs from the PC around a take; injectable so tests paste and type nowhere.
/// The defaults do nothing (an unknown field, nothing pasted); the Windows app builds the real one
/// with WindowsDictationHost.Create(PlatformServices).
/// </summary>
public sealed class DictationHost
{
    /// <summary>The focused element of the app in front (UI Automation, bounded).</summary>
    public Func<CancellationToken, Task<FocusedFieldSnapshot>> ReadFocusedFieldAsync { get; init; } = _ => Task.FromResult(FocusedFieldSnapshot.Unknown);
    /// <summary>Captions near the focus, for spelling (Settings → "read nearby text").</summary>
    public Func<CancellationToken, Task<IReadOnlyList<string>>> ReadNearbyTermsAsync { get; init; } = _ => Task.FromResult<IReadOnlyList<string>>([]);
    /// <summary>The foreground app's process name ("Code.exe"); the Mac's frontmost bundle id.</summary>
    public Func<string?> FrontAppProcessName { get; init; } = () => null;
    /// <summary>
    /// Windows has no global secure input (the Mac's IsSecureEventInputEnabled): the focused
    /// element's IsPassword stands in for it.
    /// </summary>
    public Func<CancellationToken, Task<bool>> IsSecureInputOnAsync { get; init; } = _ => Task.FromResult(false);
    /// <summary>The Mac's AXIsProcessTrusted. UI Automation needs no permission on Windows, so the real host says true.</summary>
    public Func<bool> IsAccessibilityTrusted { get; init; } = () => true;
    /// <summary>Clipboard + Ctrl+V into the app in front.</summary>
    public Func<string, TextInsertionOutcome> Paste { get; init; } = _ => TextInsertionOutcome.LeftOnClipboard;
    /// <summary>Live typing: the text as posted keystrokes (no clipboard); false when they could not be posted.</summary>
    public Func<string, bool> TypeText { get; init; } = _ => false;
    /// <summary>Words with nowhere to go (and live typing that had to stop) are left here, for a manual Ctrl+V.</summary>
    public Action<string> CopyToClipboard { get; init; } = _ => { };
    /// <summary>The clock live typing measures how long a word has been stable with.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    /// <summary>The focused element system-wide, read at press and before every typed phrase.</summary>
    public Func<CancellationToken, Task<FocusedTextTarget?>> ReadFocusedTextTargetAsync { get; init; } = _ => Task.FromResult<FocusedTextTarget?>(null);
    /// <summary>The modifier keys physically held right now (live typing waits while a foreign one is).</summary>
    public Func<LiveTypingModifiers> HeldModifiers { get; init; } = () => LiveTypingModifiers.None;
    /// <summary>Reads the field back after a paste (PasteLanding.VerifyAsync over the Platform reader and inserter).</summary>
    public Func<string, FocusedFieldSnapshot, Task<PasteLandingResult>> VerifyPasteAsync { get; init; } = (_, _) => Task.FromResult(PasteLandingResult.Posted);
    public Func<ITakePolisher?> MakePolisher { get; init; } = DictationEngineResolver.MakePolisher;
    public Func<DictationEngineChoice, string?> EngineUnavailableReason { get; init; } = DictationEngineResolver.UnavailableReason;
    /// <summary>This process (OpenClicky's own windows are never typed into live, and the box never pastes into them).</summary>
    public int OwnProcessId { get; init; } = Environment.ProcessId;
    public string OwnProcessName { get; init; } = LiveTypingExclusions.OwnProcessName;
}
