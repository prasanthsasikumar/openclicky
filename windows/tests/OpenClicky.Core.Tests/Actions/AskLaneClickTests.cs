// AskLaneClick: the ask lane's click tool reads its arguments the way the providers pass them, and
// says what happened in a few words.

using OpenClicky.Core.Actions;

namespace OpenClicky.Core.Tests.Actions;

public class AskLaneClickTests
{
    [Fact]
    public void AToolCallBecomesAClick()
    {
        var request = AskLaneClick.Parse(new Dictionary<string, string> { ["x"] = "196", ["y"] = "651.5", ["label"] = " first result ", ["text"] = "Rotary engine - Wikipedia" });
        Assert.Equal(new AskLaneClickRequest(196, 651.5, "first result", "Rotary engine - Wikipedia", null, false), request);
    }

    /// <summary>A JSON true arrives as "1" from the providers; a plan's [ACTION] line may spell it out.</summary>
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void NewTabIsReadEitherWay(string value, bool inNewTab)
    {
        var request = AskLaneClick.Parse(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2", ["label"] = "link", ["new_tab"] = value });
        Assert.Equal(inNewTab, request!.InNewTab);
    }

    [Fact]
    public void AScreenNumberBelowOneMeansTheCursorScreen()
    {
        Assert.Equal(2, AskLaneClick.Parse(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2", ["screen"] = "2" })!.ScreenNumber);
        Assert.Null(AskLaneClick.Parse(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2", ["screen"] = "0" })!.ScreenNumber);
    }

    [Fact]
    public void NoPointIsNoClick()
    {
        Assert.Null(AskLaneClick.Parse(new Dictionary<string, string> { ["label"] = "first result" }));
        Assert.Null(AskLaneClick.Parse(new Dictionary<string, string> { ["x"] = "left", ["y"] = "2" }));
        Assert.Null(AskLaneClick.Parse(new Dictionary<string, string> { ["x"] = "NaN", ["y"] = "2" }));
    }

    [Fact]
    public void AMissingLabelStillReadsWell()
    {
        Assert.Equal("Clicked that.", AskLaneClick.SpokenSentence(ScreenClickResult.Clicked, AskLaneClick.Parse(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" })!.Label));
    }

    [Fact]
    public void EachResultSaysWhatHappened()
    {
        Assert.Equal("Clicked first result.", AskLaneClick.SpokenSentence(ScreenClickResult.FollowedLink, "first result"));
        Assert.Equal("Opened second result in a new tab.", AskLaneClick.SpokenSentence(ScreenClickResult.OpenedLinkInNewTab, "second result"));
        Assert.StartsWith("Play button is under the taskbar", AskLaneClick.SpokenSentence(ScreenClickResult.UnderTaskbar, "play button"));
        Assert.Contains("administrator", AskLaneClick.SpokenSentence(ScreenClickResult.NotAllowed, "x"));
    }

    [Fact]
    public void TheToolAsksForAPointAndALabel()
    {
        var tool = AskLaneClick.AnthropicToolDefinition();
        Assert.Equal("click", (string?)tool["name"]);
        Assert.Equal(["x", "y", "label"], tool["input_schema"]!["required"]!.AsArray().Select(node => (string?)node));
    }

    /// <summary>"Accept all" on a cookie banner may be clicked; "send" never is (ScreenNavigation's list).</summary>
    [Fact]
    public void TheRiskyWordsAreTheFastLanes()
    {
        Assert.False(ScreenNavigation.ClickIsRisky("accept all button", "Accept all"));
        Assert.True(ScreenNavigation.ClickIsRisky("send button", "Send"));
    }
}
