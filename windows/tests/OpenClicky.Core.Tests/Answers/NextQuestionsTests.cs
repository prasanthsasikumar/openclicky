// Next questions (Answers/NextQuestions): reading the model's reply, which exchanges get questions, and that only the
// person's own key or model is asked.

using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Tests.Answers;

public class NextQuestionsTests
{
    [Fact]
    public void TheReplyIsReadAsUpToThreeShortQuestions()
    {
        var reply = "1. Why is the sky red at sunset?\n- What color is the sky on Mars?\n\n“Why are clouds white?”\n• Do other planets have blue skies?";
        Assert.Equal(["Why is the sky red at sunset?", "What color is the sky on Mars?", "Why are clouds white?"], NextQuestions.Parse(reply));
    }

    [Fact]
    public void LongLinesRepeatsAndHeadingsAreLeftOut()
    {
        var reply = "Here are some questions:\r\n" + new string('x', 120) + "?\r\nHow do rainbows form?\r\nhow do rainbows form?\r\n2) Can I make one?";
        Assert.Equal(["How do rainbows form?", "Can I make one?"], NextQuestions.Parse(reply));
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("none.")]
    [InlineData("")]
    public void NoneMeansNoQuestions(string reply) => Assert.Empty(NextQuestions.Parse(reply));

    [Fact]
    public void UrduQuestionsAreKept() =>
        Assert.Equal(["بارش کیوں ہوتی ہے؟"], NextQuestions.Parse("- بارش کیوں ہوتی ہے؟"));

    [Theory]
    [InlineData("why is the sky blue?", "The sky looks blue because air scatters blue light more than the other colours.", true)]
    [InlineData("thanks", "You're welcome! Ask me anything any time you like.", false)]
    [InlineData("thank you so much", "You're welcome! Ask me anything any time you like.", false)]
    [InlineData("why is the sky blue?", "Blue light scatters.", false)]
    [InlineData("", "The sky looks blue because air scatters blue light more than the other colours.", false)]
    public void OnlyExchangesWithSomewhereToGoGetQuestions(string question, string answer, bool offered) =>
        Assert.Equal(offered, NextQuestions.WorthOffering(question, answer));

    [Fact]
    public void OnlyTheirOwnKeyOrModelIsAsked()
    {
        Assert.True(NextQuestions.RouteAllows(ProviderRouteKind.AnswerProvider));
        Assert.True(NextQuestions.RouteAllows(ProviderRouteKind.AnthropicDirect));
        Assert.True(NextQuestions.RouteAllows(ProviderRouteKind.OpenAIDirect));
        Assert.False(NextQuestions.RouteAllows(ProviderRouteKind.Backend));
        Assert.False(NextQuestions.RouteAllows(ProviderRouteKind.Unavailable));
    }

    [Fact]
    public void TheyAreOnUntilTurnedOff()
    {
        var preferences = Preferences.InMemory();
        Assert.True(NextQuestions.IsOn(preferences));
        NextQuestions.SetOn(preferences, false);
        Assert.False(NextQuestions.IsOn(preferences));
    }

    [Fact]
    public void ThePromptCarriesTheExchange()
    {
        var prompt = NextQuestions.UserPrompt(" why is the sky blue? ", " Because air scatters blue light. ");
        Assert.Contains("The question: why is the sky blue?", prompt);
        Assert.Contains("The answer they heard: Because air scatters blue light.", prompt);
        Assert.Contains("NONE", NextQuestions.SystemPrompt);
    }
}
