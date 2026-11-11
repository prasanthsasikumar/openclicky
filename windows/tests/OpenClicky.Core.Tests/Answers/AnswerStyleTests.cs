// Remembered answer styles (Answers/AnswerStyle): "use easier words" once, and every lane keeps doing it.

using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Answers;

public class AnswerStyleTests
{
    [Fact]
    public void EachStyleHasAStoredNameATitleAPromptLineAndWhatIsSaid()
    {
        Assert.Equal(["simpler", "shorter", "yesOrNoFirst"], AnswerStyle.AllCases.Select(choice => choice.RawValue()));
        foreach (var choice in AnswerStyle.AllCases)
        {
            Assert.Equal(choice, AnswerStyle.FromRawValue(choice.RawValue()));
            Assert.Equal(choice, AnswerStyle.FromRawValue(" " + choice.RawValue().ToUpperInvariant() + " "));
            Assert.NotEmpty(choice.Title());
            Assert.NotEmpty(choice.PromptLine());
            Assert.NotEqual(choice.SpokenSentence(true), choice.SpokenSentence(false));
        }
        Assert.Null(AnswerStyle.FromRawValue("louder"));
        Assert.Null(AnswerStyle.FromRawValue(null));
    }

    [Fact]
    public void StoredNamesAreReadInOrderAndUnknownOnesAreIgnored()
    {
        Assert.Equal([AnswerStyleChoice.Simpler, AnswerStyleChoice.YesOrNoFirst], AnswerStyle.From(["yesOrNoFirst", "louder", "simpler", "simpler"]));
        Assert.Empty(AnswerStyle.From(null));
    }

    [Fact]
    public void SwitchingAddsOnceRemovesAndDropsTheKeyWhenNoneIsLeft()
    {
        Assert.Equal(["simpler"], AnswerStyle.Switched(null, AnswerStyleChoice.Simpler, true));
        Assert.Equal(["simpler", "shorter"], AnswerStyle.Switched(["shorter"], AnswerStyleChoice.Simpler, true));
        Assert.Equal(["simpler"], AnswerStyle.Switched(["simpler"], AnswerStyleChoice.Simpler, true));
        Assert.Equal(["shorter"], AnswerStyle.Switched(["simpler", "shorter"], AnswerStyleChoice.Simpler, false));
        Assert.Null(AnswerStyle.Switched(["simpler"], AnswerStyleChoice.Simpler, false));
        Assert.Null(AnswerStyle.Switched(null, AnswerStyleChoice.Shorter, false));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData(null, true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("False", false)]
    public void AToolCallNamesAStyleAndWhetherItGoesOn(string? on, bool expected)
    {
        var arguments = new Dictionary<string, string> { ["style"] = "shorter" };
        if (on is not null) arguments["on"] = on;
        Assert.Equal((AnswerStyleChoice.Shorter, expected), AnswerStyle.Parse(arguments));
    }

    [Fact]
    public void AToolCallIsSavedAndSaysWhatChanged()
    {
        var saved = new List<(AnswerStyleChoice, bool)>();
        var logged = new List<string>();
        var sentence = AnswerStyle.Perform(new Dictionary<string, string> { ["style"] = "simpler", ["on"] = "1" }, logged.Add,
                                           (choice, on) => saved.Add((choice, on)));
        Assert.Equal(AnswerStyleChoice.Simpler.SpokenSentence(true), sentence);
        Assert.Equal([(AnswerStyleChoice.Simpler, true)], saved);
        Assert.Equal(["answer style: simpler on"], logged);

        Assert.Null(AnswerStyle.Perform(new Dictionary<string, string> { ["style"] = "louder" }, set: (choice, on) => saved.Add((choice, on))));
        Assert.Single(saved);
    }

    [Fact]
    public void ThePromptAlwaysSaysHowToRememberAStyleAndListsTheOnesThatAreOn()
    {
        var none = AnswerStyle.PromptSection([]);
        Assert.Contains(AnswerStyle.ToolName, none);
        Assert.DoesNotContain(AnswerStyleChoice.Simpler.PromptLine(), none);

        var two = AnswerStyle.PromptSection([AnswerStyleChoice.Simpler, AnswerStyleChoice.YesOrNoFirst]);
        Assert.StartsWith(none + "\n", two);
        Assert.Contains("- " + AnswerStyleChoice.Simpler.PromptLine(), two);
        Assert.Contains("- " + AnswerStyleChoice.YesOrNoFirst.PromptLine(), two);
        Assert.DoesNotContain(AnswerStyleChoice.Shorter.PromptLine(), two);

        // The ask lane's prompt carries it, before the skills.
        var ask = AskLanePrompts.AskSystemPrompt(false, false, "skills", "en", answerStyle: [AnswerStyleChoice.Shorter]);
        Assert.EndsWith(AnswerStyle.PromptSection([AnswerStyleChoice.Shorter]) + "\n\nskills", ask);
    }

    [Fact]
    public void DrawingsGetTheStylesThatAreOnBeforeTheirLanguage()
    {
        Assert.Equal("", AnswerStyle.DrawingSection([]));
        var plain = DrawStageLesson.SystemPrompt(1280, 800, languageCode: "ur");
        var simpler = DrawStageLesson.SystemPrompt(1280, 800, languageCode: "ur", answerStyle: [AnswerStyleChoice.Simpler]);
        var sectionAt = simpler.IndexOf(AnswerStyleChoice.Simpler.PromptLine(), StringComparison.Ordinal);
        var languageAt = simpler.IndexOf(ReplyLanguage.Instruction("ur"), StringComparison.Ordinal);
        Assert.True(sectionAt > 0 && languageAt > sectionAt);
        Assert.Equal(plain.Length + AnswerStyle.DrawingSection([AnswerStyleChoice.Simpler]).Length, simpler.Length);
        // No tool on the drawing lane: nothing is said about calling one.
        Assert.DoesNotContain(AnswerStyle.ToolName, simpler);
    }

    [Fact]
    public void TheToolIsTheSameOnBothLanes()
    {
        var anthropic = AnswerStyle.AnthropicToolDefinition();
        var realtime = AnswerStyle.RealtimeToolDefinition();
        Assert.Equal(AnswerStyle.ToolName, anthropic["name"]!.GetValue<string>());
        Assert.Equal("function", realtime["type"]!.GetValue<string>());
        Assert.Equal(anthropic["input_schema"]!.ToJsonString(), realtime["parameters"]!.ToJsonString());
        Assert.Equal(AnswerStyle.AllCases.Select(choice => choice.RawValue()),
                     anthropic["input_schema"]!["properties"]!["style"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public void TheStoredListIsCopiedWithTheSettings()
    {
        var settings = new OpenClickyShellSettings { AnswerStyle = ["simpler"] };
        var copy = settings.Clone();
        copy.AnswerStyle!.Add("shorter");
        Assert.Equal(["simpler"], settings.AnswerStyle);
    }
}
