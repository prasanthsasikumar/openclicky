// Port of macos/OpenClicky/OpenClickyTests/SpokenChunkerTests.swift.
// The `PointTagPositionTests` struct in that Swift file tests CompanionManager.parsePointingCoordinates;
// it is ported with Pointing/PointTagParser under tests/Pointing, not here.

using OpenClicky.Core.Answers;

namespace OpenClicky.Core.Tests.Answers;

public class SpokenChunkerTests
{
    [Fact]
    public void TheFirstSentenceIsReadyAsSoonAsItEnds()
    {
        var chunker = new SpokenChunker();
        Assert.Empty(chunker.Feed("that number is your"));
        Assert.Equal(new[] { "that number is your sign-in code." }, chunker.Feed("that number is your sign-in code. it works"));
        Assert.Equal(new[] { "it works" }, chunker.Finish());
    }

    [Fact]
    public void LaterSentencesAreGroupedSoTheVoiceMakesFewerRequests()
    {
        var chunker = new SpokenChunker(laterChunkMinimum: 40);
        chunker.Feed("first one here. ");
        Assert.Empty(chunker.Feed("first one here. short. also short. "));
        Assert.Equal(new[] { "short. also short. now this makes it long enough." },
                     chunker.Feed("first one here. short. also short. now this makes it long enough. tail"));
        Assert.Equal(new[] { "tail" }, chunker.Finish());
    }

    [Fact]
    public void ALeadingPointTagIsHeldBackAndNeverSpoken()
    {
        var chunker = new SpokenChunker();
        Assert.Empty(chunker.Feed("[POINT:120,4"));
        Assert.Null(chunker.PointTag);
        Assert.Equal(new[] { "that's your code." }, chunker.Feed("[POINT:120,40:code] that's your code. more"));
        Assert.Equal("[POINT:120,40:code]", chunker.PointTag);
        Assert.Equal(new[] { "more" }, chunker.Finish());
    }

    [Fact]
    public void ATrailingPointTagIsFoundAtTheEndAndNotSpoken()
    {
        var chunker = new SpokenChunker();
        chunker.Feed("see the save button. click it. [POINT:5,6:save]");
        Assert.Equal(new[] { "click it." }, chunker.Finish());
        Assert.Equal("[POINT:5,6:save]", chunker.PointTag);
    }

    [Fact]
    public void ASecondTagAfterTheLeadingOneIsNeverSpoken()
    {
        var chunker = new SpokenChunker();
        Assert.Empty(chunker.Feed("[POINT:none] [POINT:10,20:x]"));
        Assert.Empty(chunker.Finish());
        Assert.Equal("[POINT:none]", chunker.PointTag);

        var twoPoints = new SpokenChunker();
        twoPoints.Feed("[POINT:10,20:x]\n[POINT:30,40:y]");
        Assert.Empty(twoPoints.Finish());
        Assert.Equal("[POINT:10,20:x]", twoPoints.PointTag);
    }

    [Fact]
    public void DecimalsAndAnswersWithoutTagsStayWhole()
    {
        var chunker = new SpokenChunker();
        Assert.Empty(chunker.Feed("it costs 3.5 dollars"));
        Assert.Equal(new[] { "it costs 3.5 dollars" }, chunker.Finish());
        Assert.Null(chunker.PointTag);
    }

    // F-01: every reply language gets its first sentence out early, with its own sentence end.
    public static TheoryData<string, string[], string> ReplyLanguages => new()
    {
        { "hi", new[] { "[POINT:none] यह", " एक परीक्षण वाक्य है। ", "दूसरा वाक्य है। ", "तीसरा वाक्य।" }, "यह एक परीक्षण वाक्य है।" },
        { "mr", new[] { "हे पहिले मराठी वाक्य आहे। ", "दुसरे वाक्य आहे।" }, "हे पहिले मराठी वाक्य आहे।" },
        { "bn", new[] { "এটি প্রথম বাংলা বাক্য। ", "দ্বিতীয় বাক্য।" }, "এটি প্রথম বাংলা বাক্য।" },
        { "ja", new[] { "これは最初の文です。これは二番目の", "文です。三番目の", "文。" }, "これは最初の文です。これは二番目の文です。" },
        { "zh", new[] { "这是第一句话。这是第二句话。", "第三句。" }, "这是第一句话。这是第二句话。" },
        { "zh-bang", new[] { "这真是太好了！你要再试一次吗？", "好。" }, "这真是太好了！你要再试一次吗？" },
        { "ta", new[] { "இது முதல் வாக்கியம். ", "இரண்டாவது வாக்கியம்." }, "இது முதல் வாக்கியம்." },
        { "ko", new[] { "이것은 첫 번째 문장입니다. ", "두 번째 문장입니다." }, "이것은 첫 번째 문장입니다." },
        { "en", new[] { "that's your sign-in code. ", "it works." }, "that's your sign-in code." },
    };

    [Theory]
    [MemberData(nameof(ReplyLanguages))]
    public void TheFirstSentenceIsReadyInEveryReplyLanguage(string language, string[] deltas, string firstPiece)
    {
        _ = language;
        var chunker = new SpokenChunker();
        var accumulated = "";
        var piecesWhileStreaming = new List<string>();
        foreach (var delta in deltas.Take(deltas.Length - 1))
        {
            accumulated += delta;
            piecesWhileStreaming.AddRange(chunker.Feed(accumulated));
        }
        Assert.Equal(firstPiece, piecesWhileStreaming.FirstOrDefault());
    }

    [Fact]
    public void ANewLineEndsASentence()
    {
        var chunker = new SpokenChunker();
        Assert.Equal(new[] { "here is what to do first" }, chunker.Feed("here is what to do first\nthen the next"));
    }

    // F-02: the voice must not say one word ("Dr.", "1.", "e.g."), stop, then go on.
    [Fact]
    public void AbbreviationsAndListNumbersDoNotEndTheFirstPiece()
    {
        var doctor = new SpokenChunker();
        Assert.Empty(doctor.Feed("[POINT:10,20:x] Dr. "));
        Assert.Equal(new[] { "Dr. Smith is here." }, doctor.Feed("[POINT:10,20:x] Dr. Smith is here. He said hi."));

        var numbered = new SpokenChunker();
        Assert.Equal(new[] { "1. Open the menu." }, numbered.Feed("1. Open the menu.\n"));
        numbered.Feed("1. Open the menu.\n2. Click save.\n");
        Assert.Equal(new[] { "2. Click save." }, numbered.Finish());

        var forExample = new SpokenChunker();
        Assert.Empty(forExample.Feed("e.g. "));
        Assert.Equal(new[] { "e.g. this one." }, forExample.Feed("e.g. this one. And more."));

        var tooShort = new SpokenChunker();
        Assert.Empty(tooShort.Feed("Yes. "));
        Assert.Equal(new[] { "Yes. That is the save button." }, tooShort.Feed("Yes. That is the save button. more"));
    }

    [Fact]
    public void AYearEndingASentenceIsStillASentenceEnd()
    {
        var chunker = new SpokenChunker();
        Assert.Equal(new[] { "that came out in 1990." }, chunker.Feed("that came out in 1990. after"));
    }

    // F-40: the backend speaks at most 400 characters of a piece (JavaScript length, UTF-16).
    public static TheoryData<string, string> LongAnswers => new()
    {
        { "english, no sentence end", string.Concat(Enumerable.Repeat("word ", 400)) },
        { "english, one long sentence", string.Concat(Enumerable.Repeat("and then this, ", 140)) + "end. " },
        { "hindi with dandas", string.Concat(Enumerable.Repeat("यह एक बहुत लंबा हिंदी वाक्य है जो चलता रहता है। ", 40)) },
        { "hindi without dandas", string.Concat(Enumerable.Repeat("यह एक बहुत लंबा हिंदी वाक्य है ", 70)) },
        { "chinese without punctuation", string.Concat(Enumerable.Repeat("这是一个很长的句子没有标点符号", 140)) },
        { "japanese with full stops", string.Concat(Enumerable.Repeat("これはとても長い日本語の文章です。", 120)) },
    };

    [Theory]
    [MemberData(nameof(LongAnswers))]
    public void NoPieceIsLongerThanTheVoiceSpeaks(string name, string answer)
    {
        var chunker = new SpokenChunker();
        var pieces = new List<string>();
        var accumulated = "";
        // Streamed in small deltas (7 text elements, as Swift's prefix(7) of Characters).
        var elements = new List<string>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(answer);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        for (var start = 0; start < elements.Count; start += 7)
        {
            accumulated += string.Concat(elements.Skip(start).Take(7));
            pieces.AddRange(chunker.Feed(accumulated));
        }
        pieces.AddRange(chunker.Finish());
        Assert.True(pieces.All(piece => piece.Length <= 350), $"{name}: {string.Join(",", pieces.Select(piece => piece.Length))}");
        Assert.True(pieces.Count > 1, name);
        // Nothing is lost or reordered: the pieces are the answer, in order.
        Assert.Equal(new string(answer.Where(character => !char.IsWhiteSpace(character)).ToArray()),
                     new string(string.Concat(pieces).Where(character => !char.IsWhiteSpace(character)).ToArray()));
    }
}
