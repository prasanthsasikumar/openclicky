// Port of macos/OpenClicky/OpenClicky/Dictation/LiveTyping.swift (the pure parts)
//
// Live typing: when a take will not be polished by a model, its words are typed into the app in
// front while the person is still speaking, instead of being pasted after the key comes up. The
// partial transcript keeps revising its last words, so only settled words are typed, and typed
// words are never taken back (deleting in a terminal is unsafe). Three pure pieces live here
// (which words have settled, how a settled phrase is formatted, and how the final transcript is
// lined up against what was already typed) plus the per-take bookkeeping the controller keeps.
// Typing itself (SendInput with KEYEVENTF_UNICODE) is OS code in OpenClicky.Windows.

using System.Globalization;
using System.Text;

namespace OpenClicky.Core.Dictation;

/// <summary>
/// Decides which words of a revising partial transcript have settled enough to type.
///
/// A word commits when it is not among the last `VolatileTailWordCount` words of the latest
/// partial (the engines revise those most) and has stood unchanged at its position for at least
/// `RequiredStableSeconds` of partial updates. Committed words are never retracted: when the engine
/// later revises one, the commit stands and new words carry on after it.
/// </summary>
public sealed class StableWordCommitter
{
    /// <summary>The last words of a partial are still being heard; they never commit from a partial.</summary>
    public const int VolatileTailWordCount = 1;
    /// <summary>
    /// How long a word must stay the same at its position before it is typed. Long enough for the
    /// engines' usual revisions ("whether" → "weather") to land, short enough to feel live.
    /// </summary>
    public const double RequiredStableSeconds = 0.3;

    /// <summary>The live values; tests pass their own.</summary>
    public readonly record struct Timing(
        int VolatileTailWordCount = StableWordCommitter.VolatileTailWordCount,
        double RequiredStableSeconds = StableWordCommitter.RequiredStableSeconds,
        // With no new partial for this long the person has paused, and the held-back last word is typed too.
        double PauseSeconds = 0.7)
    {
        public Timing() : this(StableWordCommitter.VolatileTailWordCount, StableWordCommitter.RequiredStableSeconds, 0.7) { }
    }

    /// <summary>
    /// Word sequences (normalised) that must not be split across two typed phrases, because the
    /// formatting that rewrites them (a multi-word dictionary alias) only sees one phrase at a time.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> ProtectedPhrases { get; set; }
    /// <summary>
    /// Spoken shortcut triggers (normalised). A shortcut expands only when it is the whole take, so
    /// while everything said so far could still be one, nothing is typed.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> TakeStartTriggers { get; set; }

    private readonly List<string> committedWords = new();
    /// <summary>The words committed so far, as the engine heard them.</summary>
    public IReadOnlyList<string> CommittedWords => committedWords;

    private readonly record struct TrackedWord(string Word, DateTimeOffset UnchangedSince);
    /// <summary>The words after the committed ones, in partial order, with when each last changed.</summary>
    private List<TrackedWord> trackedWords = new();
    private readonly Timing timing;

    public StableWordCommitter(IEnumerable<IReadOnlyList<string>>? protectedPhrases = null,
                               IEnumerable<IReadOnlyList<string>>? takeStartTriggers = null,
                               Timing? timing = null)
    {
        this.timing = timing ?? new Timing();
        ProtectedPhrases = (protectedPhrases ?? Enumerable.Empty<IReadOnlyList<string>>()).Where(phrase => phrase.Count > 1).ToList();
        TakeStartTriggers = (takeStartTriggers ?? Enumerable.Empty<IReadOnlyList<string>>()).Where(trigger => trigger.Count > 0).ToList();
    }

    /// <summary>
    /// Feeds one partial transcript; returns the words that settled with it (possibly none).
    /// `personPaused`: no new partial arrived for `Timing.PauseSeconds`, so the last words are not
    /// held back as still-being-heard (they still need to have been stable for the usual time).
    /// </summary>
    public IReadOnlyList<string> Observe(string partial, DateTimeOffset now, bool personPaused = false)
    {
        var partialWords = Words(partial);
        var alignment = TranscriptAlignment.Align(partialWords, committedWords);
        // The partial no longer lines up with what was committed (the engine rewrote typed words):
        // nothing more is committed blind; the final transcript decides at release.
        if (!alignment.AlignedByWords) return Array.Empty<string>();
        var uncommittedWords = alignment.RemainingWords(partialWords);

        // Each position keeps its clock while it holds the same word; a revision restarts it.
        var updatedTrackedWords = new List<TrackedWord>();
        for (var position = 0; position < uncommittedWords.Count; position++)
        {
            var word = uncommittedWords[position];
            if (position < trackedWords.Count && trackedWords[position].Word == word) updatedTrackedWords.Add(trackedWords[position]);
            else updatedTrackedWords.Add(new TrackedWord(word, now));
        }
        trackedWords = updatedTrackedWords;

        if (committedWords.Count == 0 && CouldStillBeASpokenShortcut(partialWords)) return Array.Empty<string>();

        var positionsPastTheTail = Math.Max(0, uncommittedWords.Count - (personPaused ? 0 : timing.VolatileTailWordCount));
        var settledCount = 0;
        while (settledCount < positionsPastTheTail
               && (now - trackedWords[settledCount].UnchangedSince).TotalSeconds >= timing.RequiredStableSeconds)
        {
            settledCount += 1;
        }
        settledCount = ShrinkSoNoProtectedPhraseIsSplit(settledCount, uncommittedWords);
        if (settledCount <= 0) return Array.Empty<string>();

        var newlyCommittedWords = uncommittedWords.Take(settledCount).ToList();
        committedWords.AddRange(newlyCommittedWords);
        trackedWords.RemoveRange(0, settledCount);
        return newlyCommittedWords;
    }

    public static IReadOnlyList<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Everything said so far is a beginning (or all) of some trigger.</summary>
    private bool CouldStillBeASpokenShortcut(IReadOnlyList<string> partialWords)
    {
        var spoken = partialWords.Select(TranscriptAlignment.NormalizedWord).ToList();
        return TakeStartTriggers.Any(trigger => spoken.Count <= trigger.Count && trigger.Take(spoken.Count).SequenceEqual(spoken));
    }

    /// <summary>
    /// Steps the commit back while its last words are the beginning, but not the whole, of a
    /// protected phrase: "aditya" waits for a possible "shatriya" so the alias is replaced whole.
    /// </summary>
    private int ShrinkSoNoProtectedPhraseIsSplit(int settledCount, IReadOnlyList<string> uncommittedWords)
    {
        var count = settledCount;
        while (count > 0)
        {
            var wouldBeCommitted = committedWords.Concat(uncommittedWords.Take(count)).Select(TranscriptAlignment.NormalizedWord).ToList();
            var endsInsideAProtectedPhrase = ProtectedPhrases.Any(phrase =>
                Enumerable.Range(1, phrase.Count - 1).Any(prefixLength =>
                    wouldBeCommitted.Count >= prefixLength
                    && wouldBeCommitted.Skip(wouldBeCommitted.Count - prefixLength).SequenceEqual(phrase.Take(prefixLength))));
            if (!endsInsideAProtectedPhrase) break;
            count -= 1;
        }
        return count;
    }
}

/// <summary>
/// The parts of the take's formatting a live phrase uses (Swift passes a `TakeFormattingContext`
/// and calls `TakeFormatter` directly).
/// TODO(integration): implement over the ported TakeFormatter/TakeFormattingContext. The Swift calls are
/// `TakeFormatter.collapseWhitespace(_:)`, `TakeFormatter.removeFillers(from:)` when
/// `context.style.removeFillers`, `TakeFormatter.applyDictionary(to:terms: context.dictionary)`, and
/// `TakeFormatter.sentenceCased(_:startingAtSentenceStart:)` when `context.style.sentenceCase`.
/// </summary>
public interface ILivePhraseFormatting
{
    /// <summary>`context.style.removeFillers`.</summary>
    bool RemovesFillers { get; }
    /// <summary>`context.style.sentenceCase`.</summary>
    bool UsesSentenceCase { get; }
    string CollapseWhitespace(string text);
    string RemoveFillers(string text);
    /// <summary>Applies the take's dictionary terms.</summary>
    string ApplyDictionary(string text);
    string SentenceCased(string text, bool startingAtSentenceStart);
}

/// <summary>
/// Formats one settled phrase with the parts of TakeFormatter's local pass that do not need the
/// whole take (fillers, dictionary terms, sentence case) and spaces it against what was typed.
/// </summary>
public static class LivePhraseFormatter
{
    /// <summary>
    /// The exact text to type for a phrase: its leading space (when one is needed) included.
    /// Spoken shortcuts are not applied: a shortcut expands only when it is the whole take, and a
    /// take that could still be one is held back (`StableWordCommitter.TakeStartTriggers`).
    /// </summary>
    public static string TextToType(IReadOnlyList<string> phraseWords, ILivePhraseFormatting formatting, string typedSoFar)
    {
        var text = formatting.CollapseWhitespace(string.Join(" ", phraseWords));
        if (text.Length == 0) return "";
        if (formatting.RemovesFillers) text = formatting.RemoveFillers(text);
        text = formatting.ApplyDictionary(text);
        if (formatting.UsesSentenceCase) text = formatting.SentenceCased(text, TypedSoFarEndsASentence(typedSoFar));
        if (text.Length == 0) return "";
        return NeedsSeparatingSpace(typedSoFar, text) ? " " + text : text;
    }

    /// <summary>
    /// A line break, tab, escape or any other control (Cc) character, or a line or paragraph
    /// separator (U+2028, U+2029). Typed as a keystroke a new line sends a message or runs a
    /// command, so such text is never typed; it goes to the clipboard instead. Format (Cf)
    /// characters are not among them: the zero-width joiner and non-joiner are ordinary parts of
    /// Hindi, Malayalam and Bengali words and of family and flag emoji.
    /// </summary>
    public static bool ContainsControlCharacters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return true;
        }
        return false;
    }

    /// <summary>The take's start, or after ".", "!", "?" or a new line: where sentence case would capitalise.</summary>
    public static bool TypedSoFarEndsASentence(string typedSoFar)
    {
        var trimmed = typedSoFar.Trim(' ', '\t');
        if (trimmed.Length == 0) return true;
        return ".!?\n".Contains(trimmed[^1]);
    }

    /// <summary>
    /// One space between phrases, none at the start of the take, after whitespace already typed,
    /// or before punctuation that attaches to the previous word.
    /// </summary>
    public static bool NeedsSeparatingSpace(string typedSoFar, string next)
    {
        if (typedSoFar.Length == 0 || next.Length == 0) return false;
        var lastTyped = typedSoFar[^1];
        var firstNext = next[0];
        if (char.IsWhiteSpace(lastTyped) || char.IsWhiteSpace(firstNext)) return false;
        return !",.;:!?)]}".Contains(firstNext);
    }
}

/// <summary>Lines a transcript up against the words already committed, to find what is left to type.</summary>
public readonly record struct TranscriptAlignment(int RemainderStartIndex, bool AlignedByWords)
{
    /// <summary>The longest run of committed words at the end that is searched for when the start no longer matches.</summary>
    public const int LongestTrailingRunLength = 3;
    /// <summary>A run counts only if it ends this close to where the committed words would end.</summary>
    public const int MaximumDistanceFromExpectedEnd = 3;
    /// <summary>One word is weak evidence ("the", "to"), so a run of one must end almost exactly where expected.</summary>
    public const int MaximumDistanceForOneWordRun = 1;

    public IReadOnlyList<string> RemainingWords(IReadOnlyList<string> words) =>
        RemainderStartIndex < words.Count ? words.Skip(RemainderStartIndex).ToList() : Array.Empty<string>();

    /// <summary>
    /// Case and punctuation do not count: "Hello," lines up with "hello".
    /// TODO(integration): call TakeFormatter.NormalizedForMatching once Dictation/TakeFormatter is
    /// ported (Swift: `TakeFormatter.normalizedForMatching(word)`); this is a faithful copy of it:
    /// lowercased, split on anything that is not alphanumeric (letters, marks, numbers), joined with spaces.
    /// </summary>
    public static string NormalizedWord(string word)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in word.ToLowerInvariant().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var isAlphanumeric = Rune.IsLetterOrDigit(rune) || Rune.IsNumber(rune)
                                 || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (isAlphanumeric)
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Where the not-yet-typed words of `finalWords` begin. When the committed words cannot be
    /// found (`AlignedByWords` false), `RemainderStartIndex` is the end of the longest common start:
    /// nothing after it can be typed safely, so the caller hands it to the person instead.
    /// </summary>
    public static TranscriptAlignment Align(IReadOnlyList<string> finalWords, IReadOnlyList<string> committedWords)
    {
        if (committedWords.Count == 0) return new TranscriptAlignment(0, true);
        var finalNormalized = finalWords.Select(NormalizedWord).ToList();
        var committedNormalized = committedWords.Select(NormalizedWord).ToList();
        var expectedEnd = committedNormalized.Count;

        // The usual case: the transcript starts with exactly the committed words.
        if (finalNormalized.Count >= expectedEnd && finalNormalized.Take(expectedEnd).SequenceEqual(committedNormalized))
        {
            return new TranscriptAlignment(expectedEnd, true);
        }

        // The engine rewrote words on the way ("twenty five" → "25", "gonna" → "going to"): find
        // the committed words' last 3, then 2, then 1 near where they should end.
        for (var runLength = Math.Min(LongestTrailingRunLength, expectedEnd); runLength >= 1; runLength--)
        {
            var trailingRun = committedNormalized.Skip(expectedEnd - runLength).ToList();
            if (finalNormalized.Count < runLength) continue;
            var allowedDistance = runLength == 1 ? MaximumDistanceForOneWordRun : MaximumDistanceFromExpectedEnd;
            int? nearestEnd = null;
            for (var end = runLength; end <= finalNormalized.Count; end++)
            {
                if (Math.Abs(end - expectedEnd) > allowedDistance) continue;
                if (!finalNormalized.Skip(end - runLength).Take(runLength).SequenceEqual(trailingRun)) continue;
                if (nearestEnd is null || Math.Abs(end - expectedEnd) < Math.Abs(nearestEnd.Value - expectedEnd)) nearestEnd = end;
            }
            if (nearestEnd is int foundEnd) return new TranscriptAlignment(foundEnd, true);
        }

        // Nothing lines up: never retype, never delete, never skip. Everything after the last word
        // that still matches from the start is left for the person.
        var commonStartCount = 0;
        while (commonStartCount < Math.Min(finalNormalized.Count, expectedEnd)
               && finalNormalized[commonStartCount] == committedNormalized[commonStartCount])
        {
            commonStartCount += 1;
        }
        return new TranscriptAlignment(commonStartCount, false);
    }
}

/// <summary>Modifier keys as the keyboard hook reports them held (either side).</summary>
[Flags]
public enum LiveTypingModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8,
    /// <summary>Caps Lock changes nothing about text posted as unicode (the Mac's maskAlphaShift).</summary>
    CapsLock = 16,
}

/// <summary>
/// Keystrokes posted while a modifier is physically held can merge with it: with right ⌃ as the
/// dictation key "m" became ⌃M (Return) on the Mac; on Windows Ctrl/Alt/Win + a unicode keystroke
/// can likewise trigger shortcuts. Typing waits while any modifier other than the dictation key's
/// own is held. (Right Alt as AltGr on some layouts also reports Ctrl; the hook should report
/// AltGr as Alt only.)
/// </summary>
public static class LiveTypingModifierGuard
{
    public const LiveTypingModifiers ModifiersThatChangeKeystrokes =
        LiveTypingModifiers.Shift | LiveTypingModifiers.Control | LiveTypingModifiers.Alt | LiveTypingModifiers.Windows;

    /// <summary>
    /// The flag the dictation key itself sets, by the Windows dictation keys: Right Ctrl → Control,
    /// Right Alt → Alt, Right Shift → Shift, Caps Lock → none.
    /// TODO(integration): take Shortcuts.DictationHotkey directly once it exists
    /// (Swift: `LiveTypingModifierGuard.ownModifier(of: DictationHotkey)`); callers pass the flag for now.
    /// </summary>
    public static bool AllowsTyping(LiveTypingModifiers heldModifiers, LiveTypingModifiers dictationKeyOwnModifier)
    {
        var heldThatMatter = heldModifiers & ModifiersThatChangeKeystrokes;
        return (heldThatMatter & ~dictationKeyOwnModifier) == LiveTypingModifiers.None;
    }
}

/// <summary>The process behind the focused text target (Swift `FocusedTextTarget`'s processID and bundleID).</summary>
public readonly record struct LiveTypingTargetIdentity(int ProcessId, string? ProcessName);

/// <summary>Apps live typing never types into, whatever their focused element reports.</summary>
public static class LiveTypingExclusions
{
    /// <summary>
    /// Terminals: a full-screen program in them (less, vim, htop, a password prompt) reads each key
    /// as a command, so "quit" typed into `less` quits it on the "q". They keep the paste path.
    /// </summary>
    public static readonly IReadOnlySet<string> TerminalProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "wt.exe", "mintty.exe",
    };

    public const string OwnProcessName = "OpenClicky.exe";

    /// <summary>
    /// Why the focused field must not be typed into live (for the log), or null when it may be.
    /// The target's own process name (from its pid) is the one that counts right after Alt-Tab;
    /// `fieldProcessName` (the foreground app at press) covers a pid that could not be named.
    /// OpenClicky's own windows (the record page's "try it here" box) take the paste path.
    /// Names are compared without case and with or without ".exe".
    /// </summary>
    public static string? ReasonNotToType(LiveTypingTargetIdentity target, string? fieldProcessName,
                                          int ownProcessId, string? ownProcessName = OwnProcessName)
    {
        var processNames = new[] { target.ProcessName, fieldProcessName }.OfType<string>().Select(Normalized).ToList();
        if (target.ProcessId == ownProcessId || (ownProcessName is not null && processNames.Contains(Normalized(ownProcessName), StringComparer.OrdinalIgnoreCase)))
        {
            return "openclicky's own window";
        }
        if (processNames.Any(TerminalProcessNames.Contains)) return "a terminal";
        return null;
    }

    private static string Normalized(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";
}

/// <summary>
/// One take's live typing, as the controller keeps it. `TFocusedTarget` is the OS side's focused
/// element handle (UIA element + pid); `TFormattingContext` the take's formatting context.
/// TODO(integration): close the generics over the Platform focused-target type and TakeFormattingContext.
/// </summary>
public sealed class LiveTypingSession<TFocusedTarget, TFormattingContext>
{
    public LiveTypingSession(StableWordCommitter committer, TFocusedTarget focusedTargetAtPress, TFormattingContext formattingContext)
    {
        Committer = committer;
        FocusedTargetAtPress = focusedTargetAtPress;
        FormattingContext = formattingContext;
    }

    public StableWordCommitter Committer { get; set; }
    /// <summary>The text field that had focus at press; nothing is typed anywhere else.</summary>
    public TFocusedTarget FocusedTargetAtPress { get; }
    /// <summary>The take's style, dictionary and shortcuts, read at press.</summary>
    public TFormattingContext FormattingContext { get; }
    /// <summary>Everything typed so far, exactly as typed.</summary>
    public string TypedText { get; set; } = "";
    /// <summary>The engine's words behind `TypedText` (what the final transcript is lined up against).</summary>
    public List<string> TypedRawWords { get; set; } = new();
    public int TypedPhraseCount { get; set; }
    /// <summary>The latest partial transcript: what was heard, for the clipboard if the take fails.</summary>
    public string LatestPartial { get; set; } = "";
    /// <summary>Why typing stopped before the take ended; null while it may continue.</summary>
    public string? StoppedReason { get; set; }
    /// <summary>
    /// Ctrl joined after words were typed: what is said from then on is an edit instruction, never
    /// typed or copied, and `LatestPartial` stays what was heard up to that moment.
    /// </summary>
    public bool EditKeyJoinedAfterTyping { get; set; }

    /// <summary>Anything reached the app: from here on the take finishes by typing, not by pasting.</summary>
    public bool HasTyped => TypedPhraseCount > 0;
}
