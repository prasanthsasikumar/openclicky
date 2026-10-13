// Port of the pure parts of macos/OpenClicky/OpenClickyTests/LiveTypingTests.swift.
// LivePhraseFormatter's tests run over a stand-in for TakeFormatter's local pass (copied from
// TakeFormatter.swift) until Dictation/TakeFormatter is ported.
// Not ported here: PasteLandingTests, KeyboardTextChunkingTests and onlyRealTextRolesAreTypedInto
// (they test FocusedFieldReader / PasteLanding / FrontAppTextInserter, OS-side code in other folders).

using System.Text.RegularExpressions;
using OpenClicky.Core.Dictation;

namespace OpenClicky.Core.Tests.Dictation;

internal static class LiveTypingTestTiming
{
    /// <summary>The values the algorithm tests were written against (two held-back words, 0.6 s).</summary>
    public static readonly StableWordCommitter.Timing AlgorithmTests = new(VolatileTailWordCount: 2, RequiredStableSeconds: 0.6, PauseSeconds: 60);
    public static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_000);
    public static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);
    public static string[] Phrase(params string[] words) => words;
}

public class StableWordCommitterTests
{
    private static DateTimeOffset At(double seconds) => LiveTypingTestTiming.At(seconds);
    private static StableWordCommitter Committer(IEnumerable<IReadOnlyList<string>>? protectedPhrases = null, IEnumerable<IReadOnlyList<string>>? takeStartTriggers = null) =>
        new(protectedPhrases, takeStartTriggers, LiveTypingTestTiming.AlgorithmTests);

    [Fact]
    public void NothingCommitsBeforeTheWordsHaveBeenStableLongEnough()
    {
        var committer = Committer();
        Assert.Empty(committer.Observe("hello there my friend", At(0)));
        Assert.Empty(committer.Observe("hello there my friend", At(0.3)));
        Assert.Empty(committer.CommittedWords);
    }

    [Fact]
    public void OnlyWordsOlderThanTheVolatileTailCommit()
    {
        var committer = Committer();
        committer.Observe("hello there my friend", At(0));
        var committed = committer.Observe("hello there my friend", At(0.7));
        Assert.Equal(new[] { "hello", "there" }, committed);
        Assert.Equal(new[] { "hello", "there" }, committer.CommittedWords);
    }

    [Fact]
    public void ARevisedWordRestartsItsClock()
    {
        var committer = Committer();
        committer.Observe("the whether is nice today", At(0));
        committer.Observe("the weather is nice today", At(0.5));
        Assert.Equal(new[] { "the" }, committer.Observe("the weather is nice today", At(0.7)));
        Assert.Equal(new[] { "weather", "is" }, committer.Observe("the weather is nice today", At(1.2)));
    }

    [Fact]
    public void CommittedWordsAreNeverRetracted()
    {
        var committer = Committer();
        committer.Observe("send it to bob now please", At(0));
        committer.Observe("send it to bob now please", At(0.7));
        Assert.Equal(new[] { "send", "it", "to", "bob" }, committer.CommittedWords);
        Assert.Empty(committer.Observe("send it to rob now please and thanks", At(0.8)));
        Assert.Equal(new[] { "send", "it", "to", "bob" }, committer.CommittedWords);
        var more = committer.Observe("send it to bob now please and thanks", At(1.5));
        Assert.Equal(new[] { "now", "please" }, more);
        Assert.Equal(new[] { "send", "it", "to", "bob", "now", "please" }, committer.CommittedWords);
    }

    [Fact]
    public void AShorterPartialDoesNotLoseCommittedWords()
    {
        var committer = Committer();
        committer.Observe("one two three four", At(0));
        committer.Observe("one two three four", At(0.7));
        Assert.Empty(committer.Observe("one", At(0.9)));
        Assert.Equal(new[] { "one", "two" }, committer.CommittedWords);
    }

    [Fact]
    public void AMultiWordDictionaryAliasIsNotSplitAcrossPhrases()
    {
        var alias = new[] { LiveTypingTestTiming.Phrase("aditya", "shatriya") };
        var committer = Committer(protectedPhrases: alias);
        committer.Observe("ask aditya shatriya about it", At(0));
        Assert.Equal(new[] { "ask", "aditya", "shatriya" }, committer.Observe("ask aditya shatriya about it", At(0.7)));

        var holding = Committer(protectedPhrases: alias);
        holding.Observe("ask aditya shatriya now", At(0));
        Assert.Equal(new[] { "ask" }, holding.Observe("ask aditya shatriya now", At(0.7)));
    }

    [Fact]
    public void ATakeThatMightStillBeASpokenShortcutWaits()
    {
        var committer = Committer(takeStartTriggers: new[] { LiveTypingTestTiming.Phrase("my", "sign", "off", "please") });
        committer.Observe("my sign off", At(0));
        Assert.Empty(committer.Observe("my sign off", At(0.7)));
        Assert.Empty(committer.Observe("my sign off please", At(0.75)));
        Assert.Equal(new[] { "my", "sign", "off" }, committer.Observe("my sign off please send", At(0.8)));
        committer.Observe("my sign off please send it now", At(0.9));
        committer.Observe("my sign off please send it now", At(1.6));
        Assert.Equal(new[] { "my", "sign", "off", "please", "send" }, committer.CommittedWords);
    }

    [Fact]
    public void TheLiveTimingHoldsBackOneWordForAThirdOfASecond()
    {
        var committer = new StableWordCommitter();
        committer.Observe("hello there friend", At(0));
        Assert.Empty(committer.Observe("hello there friend", At(0.2)));
        Assert.Equal(new[] { "hello", "there" }, committer.Observe("hello there friend", At(0.35)));
    }

    [Fact]
    public void AfterAPauseTheLastWordIsReleasedToo()
    {
        var committer = new StableWordCommitter();
        committer.Observe("this is a test message", At(0));
        Assert.Equal(new[] { "this", "is", "a", "test" }, committer.Observe("this is a test message", At(0.4)));
        Assert.Equal(new[] { "message" }, committer.Observe("this is a test message", At(1.0), personPaused: true));
    }

    [Fact]
    public void APauseStillWaitsForAWordThatJustChanged()
    {
        var committer = new StableWordCommitter();
        committer.Observe("this is a test mess", At(0));
        committer.Observe("this is a test message", At(0.9));
        Assert.Empty(committer.Observe("this is a test message", At(1.0), personPaused: true));
    }
}

/// <summary>TakeFormatter's local pass, copied from TakeFormatter.swift for these tests.</summary>
internal sealed class TakeFormatterStandIn : ILivePhraseFormatting
{
    private static readonly string[] Fillers = { "um", "umm", "uh", "uhh", "erm", "hmm" };
    private readonly IReadOnlyList<(string Written, string[] HeardAs)> dictionary;

    public TakeFormatterStandIn(bool sentenceCase = true, bool removeFillers = true, params (string Written, string[] HeardAs)[] dictionary)
    {
        UsesSentenceCase = sentenceCase;
        RemovesFillers = removeFillers;
        this.dictionary = dictionary;
    }

    public bool RemovesFillers { get; }
    public bool UsesSentenceCase { get; }

    public string CollapseWhitespace(string text) =>
        string.Join(" ", text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Replace(" \n", "\n").Replace("\n ", "\n").Trim();

    public string RemoveFillers(string text)
    {
        var pattern = @"(?<![\p{L}\p{N}])(" + string.Join("|", Fillers.OrderBy(filler => filler, StringComparer.Ordinal).Select(Regex.Escape)) + @")(?![\p{L}\p{N}])[,]?\s*";
        var stripped = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase);
        return CollapseWhitespace(stripped).Replace(" ,", ",").Replace(" .", ".");
    }

    public string ApplyDictionary(string text)
    {
        var replacements = dictionary
            .SelectMany(term => new[] { term.Written }.Concat(term.HeardAs).Select(alias => (Alias: alias, term.Written)))
            .Where(replacement => replacement.Alias.Trim().Length > 0)
            .OrderByDescending(replacement => replacement.Alias.Length);
        foreach (var (alias, written) in replacements)
        {
            text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(alias) + @"(?![\p{L}\p{N}])", written.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
        return text;
    }

    public string SentenceCased(string text, bool startingAtSentenceStart)
    {
        var characters = text.ToCharArray();
        var atSentenceStart = startingAtSentenceStart;
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (atSentenceStart && char.IsLetter(character))
            {
                characters[index] = char.ToUpperInvariant(character);
                atSentenceStart = false;
            }
            else if (char.IsLetterOrDigit(character))
            {
                atSentenceStart = false;
            }
            if (".!?\n".Contains(character)) atSentenceStart = true;
        }
        return new string(characters);
    }
}

public class LivePhraseFormatterTests
{
    /// <summary>The "other" style: fillers removed, sentence case.</summary>
    private static readonly TakeFormatterStandIn OtherStyle = new();

    [Fact]
    public void TheFirstPhraseOfATakeIsCapitalisedLikeAFinishedTake()
    {
        Assert.Equal("Git status", LivePhraseFormatter.TextToType(new[] { "git", "status" }, OtherStyle, ""));
    }

    [Fact]
    public void LaterPhrasesGetASpaceAndKeepTheirCase()
    {
        Assert.Equal(" and then", LivePhraseFormatter.TextToType(new[] { "and", "then" }, OtherStyle, "Hello there"));
    }

    [Fact]
    public void APhraseAfterASentenceEndIsCapitalised()
    {
        Assert.Equal(" How are you", LivePhraseFormatter.TextToType(new[] { "how", "are", "you" }, OtherStyle, "Hello there."));
    }

    [Fact]
    public void NoSpaceIsAddedAfterExistingWhitespaceOrBeforePunctuation()
    {
        Assert.Equal("Next", LivePhraseFormatter.TextToType(new[] { "next" }, OtherStyle, "Hello\n"));
        Assert.Equal(", then", LivePhraseFormatter.TextToType(new[] { ",", "then" }, OtherStyle, "Hello"));
    }

    [Fact]
    public void DictionaryTermsAndFillersAreApplied()
    {
        var formatting = new TakeFormatterStandIn(true, true, ("Aaditya Kshatriya", new[] { "aditya shatriya" }));
        Assert.Equal("Ask Aaditya Kshatriya", LivePhraseFormatter.TextToType(new[] { "um", "ask", "aditya", "shatriya" }, formatting, ""));
    }

    [Fact]
    public void APhraseOfOnlyFillersTypesNothing()
    {
        Assert.Equal("", LivePhraseFormatter.TextToType(new[] { "um" }, OtherStyle, "Hello"));
    }

    [Fact]
    public void ASpokenShortcutIsNeverExpandedInATypedPhrase()
    {
        // Shortcuts are not part of ILivePhraseFormatting at all: inside a typed take a trigger is words.
        Assert.Equal(" my sign off", LivePhraseFormatter.TextToType(new[] { "my", "sign", "off" }, OtherStyle, "Add"));
    }

    [Fact]
    public void ControlCharactersAreDetected()
    {
        Assert.True(LivePhraseFormatter.ContainsControlCharacters("line one\nline two"));
        Assert.True(LivePhraseFormatter.ContainsControlCharacters("tab\there"));
        Assert.True(LivePhraseFormatter.ContainsControlCharacters("escape \u001B[A"));
        Assert.True(LivePhraseFormatter.ContainsControlCharacters("next\u0085line"));
        Assert.True(LivePhraseFormatter.ContainsControlCharacters("line\u2028separator"));
        Assert.True(LivePhraseFormatter.ContainsControlCharacters("paragraph\u2029separator"));
        Assert.False(LivePhraseFormatter.ContainsControlCharacters(" Hello, there! 25 kg — ok"));
    }

    /// <summary>Format characters move no line: they are parts of ordinary words and emoji (F-12).</summary>
    [Fact]
    public void JoinersSoftHyphensAndEmojiSequencesType()
    {
        var wordsThatMustType = new[]
        {
            "ज़िन्दगी\u200Cमें",
            "കാര്\u200D",
            "👨\u200D👩\u200D👧",
            "🏳\uFE0F\u200D🌈",
            "co\u00ADoperate",
            "zero\u200Bwidth",
        };
        foreach (var word in wordsThatMustType)
        {
            Assert.False(LivePhraseFormatter.ContainsControlCharacters(word), word);
        }
    }

    [Fact]
    public void TheCasualStyleStaysLowercase()
    {
        var casual = new TakeFormatterStandIn(sentenceCase: false);
        Assert.Equal("lemme know", LivePhraseFormatter.TextToType(new[] { "lemme", "know" }, casual, ""));
    }
}

public class TranscriptAlignmentTests
{
    [Fact]
    public void TheRemainderIsWhatFollowsTheCommittedWords()
    {
        var finalWords = new[] { "Hello,", "there", "my", "friend." };
        var alignment = TranscriptAlignment.Align(finalWords, new[] { "hello", "there" });
        Assert.Equal(2, alignment.RemainderStartIndex);
        Assert.True(alignment.AlignedByWords);
        Assert.Equal(new[] { "my", "friend." }, alignment.RemainingWords(finalWords));
    }

    [Fact]
    public void NothingCommittedMeansEverythingRemains()
    {
        Assert.Equal(0, TranscriptAlignment.Align(new[] { "a", "b" }, Array.Empty<string>()).RemainderStartIndex);
    }

    [Fact]
    public void AWordInsertedBeforeTheCommittedEndIsFoundByTheTrailingRun()
    {
        var finalWords = new[] { "oh", "move", "the", "meeting", "to", "friday" };
        var alignment = TranscriptAlignment.Align(finalWords, new[] { "move", "the", "meeting" });
        Assert.Equal(4, alignment.RemainderStartIndex);
        Assert.Equal(new[] { "to", "friday" }, alignment.RemainingWords(finalWords));
    }

    [Fact]
    public void RevisedCommittedWordsLeaveEverythingAfterTheLastMatchForThePasteboard()
    {
        var finalWords = new[] { "send", "it", "to", "rob", "now", "please" };
        var alignment = TranscriptAlignment.Align(finalWords, new[] { "send", "it", "to", "bob" });
        Assert.False(alignment.AlignedByWords);
        Assert.Equal(new[] { "rob", "now", "please" }, alignment.RemainingWords(finalWords));
    }

    [Fact]
    public void NumbersRewrittenAtTheTailLoseNoWords()
    {
        var finalWords = new[] { "I", "have", "25", "apples", "and", "three", "oranges." };
        var alignment = TranscriptAlignment.Align(finalWords, new[] { "I", "have", "twenty", "five", "apples", "and" });
        Assert.True(alignment.AlignedByWords);
        Assert.Equal(new[] { "three", "oranges." }, alignment.RemainingWords(finalWords));
    }

    [Fact]
    public void GonnaWrittenOutAsGoingToLosesNoWords()
    {
        var finalWords = new[] { "I'm", "going", "to", "go", "home", "now" };
        var alignment = TranscriptAlignment.Align(finalWords, new[] { "I'm", "gonna", "go" });
        Assert.True(alignment.AlignedByWords);
        Assert.Equal(new[] { "home", "now" }, alignment.RemainingWords(finalWords));
    }

    [Fact]
    public void AMatchFarFromWhereTheWordsShouldBeIsNotTrusted()
    {
        var finalWords = new[] { "we", "will", "see", "a", "b", "c", "d", "e", "f", "g", "h", "go" };
        var alignment = TranscriptAlignment.Align(finalWords, new[] { "I'm", "gonna", "go" });
        Assert.False(alignment.AlignedByWords);
        Assert.Equal(finalWords, alignment.RemainingWords(finalWords));
    }

    /// <summary>A lone common word several words on is another occurrence, not the typed one (F-25).</summary>
    [Fact]
    public void AOneWordRunFarFromTheExpectedEndDoesNotAlign()
    {
        var finalWords = new[] { "the", "cat", "sat", "on", "a", "mat", "near", "the", "door" };
        var alignment = TranscriptAlignment.Align(finalWords, new[] { "the", "cat", "sat", "on", "the" });
        Assert.False(alignment.AlignedByWords);
        Assert.Equal(new[] { "a", "mat", "near", "the", "door" }, alignment.RemainingWords(finalWords));

        var otherFinal = new[] { "please", "send", "it", "two", "times", "to", "bob" };
        var otherAlignment = TranscriptAlignment.Align(otherFinal, new[] { "please", "send", "it", "to" });
        Assert.False(otherAlignment.AlignedByWords);
        Assert.Equal(new[] { "two", "times", "to", "bob" }, otherAlignment.RemainingWords(otherFinal));
    }

    [Fact]
    public void AOneWordRunOneWordAwayStillAligns()
    {
        var finalWords = new[] { "we", "go", "home", "now" };
        var alignment = TranscriptAlignment.Align(finalWords, new[] { "we", "uh", "go" });
        Assert.True(alignment.AlignedByWords);
        Assert.Equal(new[] { "home", "now" }, alignment.RemainingWords(finalWords));
    }

    [Fact]
    public void AFinalShorterThanTheCommittedWordsLeavesNothing()
    {
        var finalWords = new[] { "send", "it" };
        Assert.Empty(TranscriptAlignment.Align(finalWords, new[] { "send", "it", "to", "bob" }).RemainingWords(finalWords));
    }
}

public class LiveTypingDecisionTests
{
    /// <summary>Letters are commands in a terminal's full-screen programs (F-07).</summary>
    [Fact]
    public void TerminalsAreNeverTypedIntoLive()
    {
        var terminalNames = new[] { "WindowsTerminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "wt.exe", "mintty.exe", "pwsh", "WINDOWSTERMINAL.EXE" };
        foreach (var terminalName in terminalNames)
        {
            var terminalView = new LiveTypingTargetIdentity(777, terminalName);
            Assert.Equal("a terminal", LiveTypingExclusions.ReasonNotToType(terminalView, null, ownProcessId: 1));
            // The foreground app's name counts too, when the pid could not be named.
            var unnamed = new LiveTypingTargetIdentity(777, null);
            Assert.Equal("a terminal", LiveTypingExclusions.ReasonNotToType(unnamed, terminalName, ownProcessId: 1));
        }
        var notepad = new LiveTypingTargetIdentity(777, "notepad.exe");
        Assert.Null(LiveTypingExclusions.ReasonNotToType(notepad, "notepad.exe", ownProcessId: 1));
    }

    /// <summary>The record page's "try it here" box would get the words twice (F-23).</summary>
    [Fact]
    public void OpenClickysOwnWindowsAreNeverTypedIntoLive()
    {
        Assert.NotNull(LiveTypingExclusions.ReasonNotToType(new LiveTypingTargetIdentity(1, null), null, ownProcessId: 1));
        Assert.NotNull(LiveTypingExclusions.ReasonNotToType(new LiveTypingTargetIdentity(2, "OpenClicky.exe"), null, ownProcessId: 1));
        Assert.NotNull(LiveTypingExclusions.ReasonNotToType(new LiveTypingTargetIdentity(2, "openclicky"), null, ownProcessId: 1));
    }

    [Fact]
    public void OnlyTheDictationKeysOwnModifierMayBeHeldWhileTyping()
    {
        // Right Ctrl → Control, Right Alt → Alt, Right Shift → Shift, Caps Lock → none.
        Assert.True(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.None, LiveTypingModifiers.None));
        Assert.True(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.Control, LiveTypingModifiers.Control));
        Assert.True(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.Alt, LiveTypingModifiers.Alt));
        Assert.True(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.Shift, LiveTypingModifiers.Shift));
        // Caps lock changes nothing about text posted as unicode.
        Assert.True(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.CapsLock, LiveTypingModifiers.None));
        Assert.True(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.Control | LiveTypingModifiers.CapsLock, LiveTypingModifiers.Control));

        Assert.False(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.Control, LiveTypingModifiers.None));
        Assert.False(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.Windows, LiveTypingModifiers.Control));
        Assert.False(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.Shift, LiveTypingModifiers.Control));
        Assert.False(LiveTypingModifierGuard.AllowsTyping(LiveTypingModifiers.Control | LiveTypingModifiers.Alt, LiveTypingModifiers.Alt));
    }

    [Fact]
    public void ASessionHasTypedOnceAPhraseWentOut()
    {
        var session = new LiveTypingSession<string, string>(new StableWordCommitter(), "target", "context");
        Assert.False(session.HasTyped);
        session.TypedPhraseCount += 1;
        Assert.True(session.HasTyped);
    }
}
