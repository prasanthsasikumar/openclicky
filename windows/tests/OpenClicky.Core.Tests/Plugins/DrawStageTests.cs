// The drawing stage's controller and its messages (Windows only so far): what the page is told before and after it
// has loaded, and what the app hears back, with a fake surface in place of the window and WebView2.

using System.Text.Json.Nodes;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Plugins;

public class DrawStageTests
{
    private sealed class FakeSurface : IDrawStageSurface
    {
        public List<string> Posted { get; } = [];
        public List<PixelRect> Presented { get; } = [];
        public int Hides { get; private set; }
        public Action<string>? MessageReceived { get; set; }

        public void Present(PixelRect frame, double scale) => Presented.Add(frame);

        public void Hide() => Hides++;

        public void Post(string json) => Posted.Add(json);

        public void PageSays(string json) => MessageReceived?.Invoke(json);

        public List<string> Types() => Posted.Select(json => (string)JsonNode.Parse(json)!["type"]!).ToList();
    }

    private static (DrawStageController Stage, FakeSurface Surface) MakeStage()
    {
        var surface = new FakeSurface();
        return (new DrawStageController { Surface = surface }, surface);
    }

    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void MessagesWaitForThePageThenGoInOrder()
    {
        var (stage, surface) = MakeStage();
        stage.Begin(Screen, 1.5, 1280, 720);
        stage.Feed("step(1); say('hi')", done: false);
        Assert.Empty(surface.Posted);
        Assert.Single(surface.Presented);

        surface.PageSays("""{"type":"ready"}""");
        Assert.True(stage.IsReady);
        Assert.Equal(["beginStream", "feed"], surface.Types());

        stage.Feed("step(1); say('hi')\ndot(1, 2)", done: true);
        Assert.Equal(["beginStream", "feed", "feed"], surface.Types());
    }

    [Fact]
    public void BeginCarriesTheCanvasAndThePace()
    {
        var (stage, surface) = MakeStage();
        surface.PageSays("""{"type":"ready"}""");
        stage.Begin(Screen, 1, 1280, 720, DrawStagePace.Speech);
        var begin = JsonNode.Parse(surface.Posted.Single())!;
        Assert.Equal(1280, (int)begin["w"]!);
        Assert.Equal(720, (int)begin["h"]!);
        Assert.Equal("speech", (string)begin["pace"]!);
        Assert.True(stage.IsShowing);
    }

    [Fact]
    public void FeedSendsTheWholeProgramSoFar()
    {
        var feed = JsonNode.Parse(DrawStageMessages.Feed("say(\"a \\\"quoted\\\" line\")", done: true))!;
        Assert.Equal("feed", (string)feed["type"]!);
        Assert.Equal("say(\"a \\\"quoted\\\" line\")", (string)feed["code"]!);
        Assert.True((bool)feed["done"]!);
    }

    [Fact]
    public void ThePageAsksForLinesAndSaysWhenItIsDone()
    {
        var (stage, surface) = MakeStage();
        var due = new List<(int, string)>();
        var finished = 0;
        var logged = new List<string>();
        stage.LineDue += (k, text) => due.Add((k, text));
        stage.Finished += () => finished++;
        stage.Logged += logged.Add;

        surface.PageSays("""{"type":"lineDue","k":2,"text":"That is thrust."}""");
        surface.PageSays("""{"type":"error","text":"nope is not defined"}""");
        surface.PageSays("""{"type":"log","text":"warning: figure() is not available yet"}""");
        surface.PageSays("""{"type":"finished"}""");

        Assert.Equal([(2, "That is thrust.")], due);
        Assert.Equal(1, finished);
        Assert.Equal(["error: nope is not defined", "warning: figure() is not available yet"], logged);
    }

    [Fact]
    public void LinesFromTheVoiceAreNumbered()
    {
        var (stage, surface) = MakeStage();
        surface.PageSays("""{"type":"ready"}""");
        stage.LineStarted(3);
        stage.LineEnded(3);
        stage.FinishNow();
        Assert.Equal(["lineStarted", "lineEnded", "finishNow"], surface.Types());
        Assert.Equal(3, (int)JsonNode.Parse(surface.Posted[0])!["k"]!);
    }

    [Fact]
    public void ClearWipesTheDrawingHidesTheStageAndDropsWhatWasWaiting()
    {
        var (stage, surface) = MakeStage();
        stage.Begin(Screen, 1, 1280, 720);
        stage.Clear("test");
        Assert.False(stage.IsShowing);
        Assert.Equal(1, surface.Hides);

        // Nothing queued before the clear reaches the page once it loads.
        surface.PageSays("""{"type":"ready"}""");
        Assert.Empty(surface.Posted);

        stage.Begin(Screen, 1, 1280, 720);
        stage.Clear("again");
        Assert.Equal(["beginStream", "clear"], surface.Types());
    }

    [Fact]
    public void ANewSurfaceStartsUnloaded()
    {
        var (stage, surface) = MakeStage();
        surface.PageSays("""{"type":"ready"}""");
        var next = new FakeSurface();
        stage.Surface = next;
        Assert.False(stage.IsReady);
        Assert.Null(surface.MessageReceived);
        stage.Feed("dot(1, 1)", done: true);
        Assert.Empty(next.Posted);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"k": 1}""")]
    [InlineData("""{"type": 5}""")]
    public void UnreadablePageMessagesAreIgnored(string json)
    {
        Assert.Null(DrawStageMessages.ParseEvent(json));
    }

    [Fact]
    public void AnErrorBeforeThePageLoadsReadsLikeOneFromThePage()
    {
        var parsed = DrawStageMessages.ParseEvent(DrawStageMessages.Error("the stage did not start: no WebView2"));
        Assert.Equal(new DrawStageEvent("error", null, "the stage did not start: no WebView2"), parsed);
    }
}
