// Captions of the spoken answer (Speech/AnswerCaptions): when they show, how they are saved, and how a long fast-voice
// transcript is shortened to the words being said.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class AnswerCaptionsTests
{
    [Fact]
    public void EachChoiceHasAStoredNameAndATitleAndTheDefaultIsNotStored()
    {
        Assert.Equal(["auto", "always", "off"], AnswerCaptions.AllCases.Select(choice => choice.RawValue()));
        foreach (var choice in AnswerCaptions.AllCases)
        {
            Assert.Equal(choice, AnswerCaptions.FromRawValue(" " + choice.RawValue().ToUpperInvariant() + " "));
            Assert.NotEmpty(choice.Title());
        }
        Assert.Null(AnswerCaptions.FromRawValue("sometimes"));
        Assert.Null(AnswerCaptions.Stored(AnswerCaptionsChoice.WhenItCannotBeHeard));
        Assert.Equal("off", AnswerCaptions.Stored(AnswerCaptionsChoice.Off));
    }

    [Theory]
    [InlineData(AnswerCaptionsChoice.WhenItCannotBeHeard, false, 0.8f, false, false)]
    [InlineData(AnswerCaptionsChoice.WhenItCannotBeHeard, true, 0.8f, false, true)]
    [InlineData(AnswerCaptionsChoice.WhenItCannotBeHeard, false, 0.8f, true, true)]
    [InlineData(AnswerCaptionsChoice.WhenItCannotBeHeard, false, 0.2f, false, true)]
    [InlineData(AnswerCaptionsChoice.Always, false, 0.8f, false, true)]
    [InlineData(AnswerCaptionsChoice.Off, true, 0.0f, true, false)]
    public void CaptionsShowWhenTheAnswerMightNotBeHeard(AnswerCaptionsChoice choice, bool typed, float volume, bool muted, bool shown)
    {
        Assert.Equal(shown, AnswerCaptions.AreShown(choice, typed, new TeacherListening.Output(volume, muted)));
    }

    [Fact]
    public void AnUnknownVolumeIsTakenAsHeard()
    {
        Assert.False(AnswerCaptions.AreShown(AnswerCaptionsChoice.WhenItCannotBeHeard, false, TeacherListening.Output.Unknown));
    }

    [Fact]
    public void AShortFastVoiceTranscriptShowsWhole()
    {
        Assert.Equal("yes. the sun is a star.", AnswerCaptions.LiveText("  yes. the sun is a star.  "));
    }

    [Fact]
    public void ALongTranscriptShowsItsEndFromASentenceStart()
    {
        var earlier = string.Concat(Enumerable.Repeat("this sentence came earlier in the answer. ", 10));
        var live = AnswerCaptions.LiveText(earlier + "and this is what is being said now.");
        Assert.StartsWith("… this sentence", live);
        Assert.EndsWith("and this is what is being said now.", live);
        Assert.True(live.Length <= AnswerCaptions.LongestLiveCaption + 2);

        // One endless sentence: cut at a word, never inside one.
        var endless = string.Join(" ", Enumerable.Repeat("word", 120));
        var cut = AnswerCaptions.LiveText(endless);
        Assert.StartsWith("… word ", cut);
        Assert.EndsWith(" word", cut);
    }

    [Fact]
    public void TheChoiceIsCopiedWithTheSettings()
    {
        var settings = new OpenClickyShellSettings { AnswerCaptions = "always" };
        Assert.Equal("always", settings.Clone().AnswerCaptions);
    }
}
