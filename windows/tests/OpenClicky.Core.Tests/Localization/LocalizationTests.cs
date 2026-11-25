// The screens in Urdu (Localization/L): every string the code sends through L.T / L.F, and every fixed notice, has an
// Urdu translation with the same placeholders; nothing in ur.json is left unused; the lookup falls back to English; the
// screen language follows Clicky's spoken one by default; Urdu text reads right to left. The tests never switch the app's
// language (other tests check English words at the same time): they ask for a language by name.

using System.Text.RegularExpressions;
using OpenClicky.Core.Localization;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Localization;

public partial class LocalizationTests
{
    /// <summary>Strings translated where they are shown rather than written through L.T: captions (L.Sentence), the
    /// tray's status words, and the units Settings counts in (WordsPageSentences.Plural).</summary>
    private static readonly string[] TranslatedWhereShown =
    [
        "stopped", "listening…", "transcribing…", "Marked. Now ask about it: hold the talk key, or type your question.",
        "There's no finished drawing to save.", "Listening", "Thinking", "Speaking", "Agent working", "Ready",
        "take", "word", "model", "term", "snippet",
    ];

    private static string SourceRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "src", "OpenClicky.Core"))) folder = folder.Parent;
        return Path.Combine(folder?.FullName ?? throw new DirectoryNotFoundException("windows/src not found above the test folder"), "src");
    }

    /// <summary>Every L.T("…") / L.F("…") string in the sources (comments left out), joined when split with +.</summary>
    private static HashSet<string> StringsInTheCode()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var code = string.Join("\n", File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (Match match in LookupCall().Matches(code))
            {
                found.Add((match.Groups[1].Value + match.Groups[2].Value).Replace("\\\"", "\""));
            }
        }
        return found;
    }

    [Fact]
    public void EveryStringTheCodeShowsHasAnUrduTranslation()
    {
        var urdu = L.Table("ur");
        var missing = StringsInTheCode().Where(english => !urdu.TryGetValue(english, out var translated) || translated.Trim().Length == 0).ToList();
        Assert.True(missing.Count == 0, "No Urdu for: " + string.Join(" | ", missing));
    }

    [Fact]
    public void EveryFixedNoticeAndShownWordHasAnUrduTranslation()
    {
        var urdu = L.Table("ur");
        var missing = RecordedVoice.Notices.Keys.Concat(TranslatedWhereShown).Where(english => !urdu.ContainsKey(english)).ToList();
        Assert.True(missing.Count == 0, "No Urdu for: " + string.Join(" | ", missing));
    }

    [Fact]
    public void NothingInTheUrduFileIsUnused()
    {
        var used = StringsInTheCode().Concat(RecordedVoice.Notices.Keys).Concat(TranslatedWhereShown).ToHashSet(StringComparer.Ordinal);
        var unused = L.Table("ur").Keys.Where(english => !used.Contains(english)).ToList();
        Assert.True(unused.Count == 0, "Not used any more (the English changed?): " + string.Join(" | ", unused));
    }

    [Fact]
    public void EveryTranslationKeepsItsPlaceholders()
    {
        foreach (var (english, urdu) in L.Table("ur"))
        {
            var expected = Placeholder().Matches(english).Select(match => match.Value).Order().ToList();
            var actual = Placeholder().Matches(urdu).Select(match => match.Value).Order().ToList();
            Assert.True(expected.SequenceEqual(actual), $"\"{english}\" → \"{urdu}\"");
        }
    }

    [Fact]
    public void TheLookupFallsBackToEnglish()
    {
        Assert.Equal("سن رہا ہوں", L.In("ur", "listening"));
        Assert.Equal("a sentence nobody translated", L.In("ur", "a sentence nobody translated"));
        Assert.Equal("listening", L.In("en", "listening"));
        Assert.Equal("listening", L.In("xx", "listening"));
        // A notice said with a capital still finds its lower-case translation.
        Assert.Equal("تیز آواز ابھی دستیاب نہیں ہے۔", L.SentenceIn("ur", "Realtime voice is unavailable right now."));
        Assert.Equal("Realtime voice is unavailable right now.", L.SentenceIn("en", "Realtime voice is unavailable right now."));
    }

    [Theory]
    [InlineData(null, "ur", "ur")]
    [InlineData(null, "en", "en")]
    [InlineData("same", "hi", "en")]
    [InlineData("ur", "en", "ur")]
    [InlineData("en", "ur", "en")]
    [InlineData("fr", "ur", "en")]
    public void TheScreensFollowClickysLanguageUnlessOneIsPicked(string? stored, string spoken, string screens) =>
        Assert.Equal(screens, AppLanguage.Resolve(stored, spoken));

    [Theory]
    [InlineData("سن رہا ہوں", true)]
    [InlineData("“یہ کیا ہے؟”", true)]
    [InlineData("12 سیکنڈ", true)]
    [InlineData("Ctrl دبا کر رکھیں", false)]
    [InlineData("listening", false)]
    [InlineData("", false)]
    public void UrduTextReadsRightToLeft(string text, bool rightToLeft) => Assert.Equal(rightToLeft, TextDirection.IsRightToLeft(text));

    [Fact]
    public void UrduIsRightToLeftAndListedFirstInItsOwnScript()
    {
        Assert.True(L.IsRightToLeftLanguage("ur"));
        Assert.False(L.IsRightToLeftLanguage("en"));
        Assert.Contains(AppLanguage.Choices, choice => choice is ("ur", "اردو"));
    }

    [GeneratedRegex("""L\.[TF]\(\s*"((?:[^"\\]|\\.)*)"(?:\s*\+\s*"((?:[^"\\]|\\.)*)")?""")]
    private static partial Regex LookupCall();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
