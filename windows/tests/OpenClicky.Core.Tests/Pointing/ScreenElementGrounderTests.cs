// Port of ScreenElementGrounderTests.swift.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class ScreenElementGrounderTests
{
    private const int ImageWidth = 1280;
    private const int ImageHeight = 831;

    [Fact]
    public void ParsesThePointTagWithOrWithoutSpaces()
    {
        Assert.Equal(new ScreenPoint(1063, 157), ScreenElementGrounder.ParsePoint("[POINT:1063, 157]", ImageWidth, ImageHeight));
        Assert.Equal(new ScreenPoint(237, 277), ScreenElementGrounder.ParsePoint("Sure — [POINT:237,277]", ImageWidth, ImageHeight));
    }

    [Fact]
    public void TheVisibleTextRidesAlongWithThePoint()
    {
        const string reply = "[POINT:516, 503:arXiv]";
        Assert.Equal(new ScreenPoint(516, 503), ScreenElementGrounder.ParsePoint(reply, ImageWidth, ImageHeight));
        Assert.Equal("arXiv", ScreenElementGrounder.ParseVisibleText(reply));
        Assert.Null(ScreenElementGrounder.ParseVisibleText("[POINT:10,20]"));
        Assert.Null(ScreenElementGrounder.ParseVisibleText("[POINT:10,20: ]"));
        Assert.Null(ScreenElementGrounder.ParsePoint("[POINT:none]", ImageWidth, ImageHeight));
    }

    [Fact]
    public void ThePromptAsksForTheTextTheElementShows()
    {
        var prompt = ScreenElementGrounder.Prompt("paper link", "", "how do I open this paper", ImageWidth, ImageHeight);
        Assert.Contains("[POINT:x,y:visible text]", prompt);
        Assert.Contains("arXiv", prompt);
    }

    [Fact]
    public void GroundingRunsOnEveryAnswerSourceButThePlans()
    {
        Assert.Null(ScreenElementGrounder.ReasonGroundingCannotRun(new GroundingRoute.AnthropicDirect()));
        Assert.Null(ScreenElementGrounder.ReasonGroundingCannotRun(new GroundingRoute.Backend()));
        Assert.Null(ScreenElementGrounder.ReasonGroundingCannotRun(new GroundingRoute.OpenAIDirect()));
        foreach (var source in new[] { "OpenAI", "OpenRouter", "Gemini", "Groq", "Mistral", "xAI" })
        {
            Assert.Null(ScreenElementGrounder.ReasonGroundingCannotRun(new GroundingRoute.AnswerProvider(source, IsPlan: false)));
        }
        Assert.NotNull(ScreenElementGrounder.ReasonGroundingCannotRun(new GroundingRoute.AnswerProvider("Claude Code plan", IsPlan: true)));
        Assert.NotNull(ScreenElementGrounder.ReasonGroundingCannotRun(new GroundingRoute.AnswerProvider("Codex plan", IsPlan: true)));
        Assert.NotNull(ScreenElementGrounder.ReasonGroundingCannotRun(new GroundingRoute.Unavailable("add a key")));
    }

    [Fact]
    public void NoneAndMissingTagsGiveNull()
    {
        Assert.Null(ScreenElementGrounder.ParsePoint("[POINT:none]", ImageWidth, ImageHeight));
        Assert.Null(ScreenElementGrounder.ParsePoint("I can't see that element.", ImageWidth, ImageHeight));
    }

    [Fact]
    public void PointsOutsideTheScreenshotAreRejected()
    {
        Assert.Null(ScreenElementGrounder.ParsePoint("[POINT:1400,100]", ImageWidth, ImageHeight));
        Assert.Null(ScreenElementGrounder.ParsePoint("[POINT:100,900]", ImageWidth, ImageHeight));
    }

    [Fact]
    public void PromptNamesTheElementTheSizeAndTheUsersRequest()
    {
        var prompt = ScreenElementGrounder.Prompt("plus menu", "+", "show me the plus sign", ImageWidth, ImageHeight);
        Assert.Contains("plus menu", prompt);
        Assert.Contains("\"+\"", prompt);
        Assert.Contains("show me the plus sign", prompt);
        Assert.Contains("1280×831", prompt);
        Assert.Contains("[POINT:none]", prompt);
        var bare = ScreenElementGrounder.Prompt("gear icon", "", null, ImageWidth, ImageHeight);
        Assert.Contains("gear icon", bare);
        Assert.DoesNotContain("\"\"", bare);
        Assert.DoesNotContain("asked", bare);
    }

    [Fact]
    public async Task LocateSendsTheRequestThroughTheInjectedCall()
    {
        GroundingRequest? sent = null;
        var located = await ScreenElementGrounder.LocateAsync("paper link", "", "open the paper", new byte[] { 1 }, ImageWidth, ImageHeight,
            new GroundingRoute.Backend(), request => { sent = request; return Task.FromResult("[POINT:516, 503:arXiv]"); });
        Assert.Equal(new ScreenElementGrounder.Located(new ScreenPoint(516, 503), "arXiv"), located);
        Assert.Equal(ScreenElementGrounder.Model, sent?.Model);
        Assert.Equal("Screenshot of the user's screen, 1280×831 px, origin top-left.", sent?.ImageLabel);
    }

    [Fact]
    public async Task LocateLogsAndGivesNullWhenItCannotRunOrFails()
    {
        var logLines = new List<string>();
        var called = false;
        Assert.Null(await ScreenElementGrounder.LocateAsync("x", "", null, Array.Empty<byte>(), ImageWidth, ImageHeight,
            new GroundingRoute.Unavailable("no key"), _ => { called = true; return Task.FromResult(""); }, logLines.Add));
        Assert.False(called);
        Assert.Equal("point_at: no grounding (nothing is set up to answer)", logLines.Single());

        Assert.Null(await ScreenElementGrounder.LocateAsync("x", "", null, Array.Empty<byte>(), ImageWidth, ImageHeight,
            new GroundingRoute.Backend(), _ => throw new HttpRequestException("secret body"), logLines.Add));
        Assert.StartsWith("point_at: grounding failed (HttpRequestException", logLines[1]);
        Assert.DoesNotContain("secret", logLines[1]);
    }
}
