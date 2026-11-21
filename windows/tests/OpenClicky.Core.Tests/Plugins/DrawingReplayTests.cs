// "Play it again" (Plugins/DrawStage/DrawingReplay): which words ask for it, and how a finished drawing is drawn again on
// the stage with its own voice, line by line, then remembered again.

using System.Text.Json.Nodes;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Plugins;

public class DrawingReplayTests
{
    private sealed class FakeSurface : IDrawStageSurface
    {
        public List<string> Posted { get; } = [];
        public Action<string>? MessageReceived { get; set; }
        public void Present(PixelRect frame, double scale) { }
        public void Hide() { }
        public void Prepare() { }
        public void Post(string json) => Posted.Add(json);
        public void PageSays(string json) => MessageReceived?.Invoke(json);

        /// <summary>Each message's type, with its line for a line's start or end ("lineStarted 0").</summary>
        public List<string> Steps() => Posted.Select(json =>
        {
            var message = JsonNode.Parse(json)!;
            var type = (string)message["type"]!;
            return message["k"] is { } k ? $"{type} {(int)k}" : type;
        }).ToList();
    }

    private static readonly byte[] FirstAudio = [1, 2, 3];

    private static DrawnAnswer Drawing() => new(
        ["how does rain form?"],
        "step(1); say('water rises as vapour.'); step(2); say('it cools into clouds.');",
        new PixelRect(0, 0, 1920, 1080), 1.0, 1280, 720,
        [new DrawnLine("water rises as vapour.", 2.0, 3.5, FirstAudio), new DrawnLine("it cools into clouds.", 1.5, 0.4, null)]);

    private static (DrawStageController Stage, FakeSurface Surface) ReadyStage()
    {
        var surface = new FakeSurface();
        var stage = new DrawStageController { Surface = surface };
        surface.PageSays("""{"type":"ready"}""");
        return (stage, surface);
    }

    [Fact]
    public async Task ADrawingIsDrawnAgainWithItsOwnVoiceThenRememberedAgain()
    {
        var (stage, surface) = ReadyStage();
        var played = new List<byte[]>();
        var waits = new List<double>();
        var drawing = Drawing();
        await DrawingReplay.PlayAsync(stage, drawing,
            (audio, _) => { played.Add(audio); return Task.CompletedTask; }, CancellationToken.None,
            (delay, _) => { waits.Add(delay.TotalSeconds); return Task.CompletedTask; });

        Assert.Equal(["beginStream", "feed", "lineStarted 0", "lineEnded 0", "lineStarted 1", "lineEnded 1", "finishNow", "remembered"], surface.Steps());
        var begin = JsonNode.Parse(surface.Posted[0])!;
        Assert.Equal("speech", (string)begin["pace"]!);
        Assert.True((bool)begin["keepPlace"]!);
        Assert.Equal(drawing.Program, (string)JsonNode.Parse(surface.Posted[1])!["code"]!);
        // The kept audio plays; a line with none waits as long as it was said for.
        Assert.Equal([FirstAudio], played);
        // The lead, the first line's pause (at most a second), the second line's pause, the second line's length.
        Assert.Equal([0.6, 1.0, 0.4, 1.5], waits);
        Assert.Same(drawing, stage.Current);
    }

    [Fact]
    public async Task StoppingItLeavesTheDrawingUnremembered()
    {
        var (stage, surface) = ReadyStage();
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DrawingReplay.PlayAsync(stage, Drawing(),
            (_, token) => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, stop.Token,
            (_, _) => Task.CompletedTask));
        Assert.DoesNotContain("remembered", surface.Steps());
        Assert.Null(stage.Current);
    }

    [Fact]
    public void ThePageButtonAsksForIt()
    {
        var (stage, surface) = ReadyStage();
        var asked = 0;
        stage.ReplayRequested += () => asked++;
        surface.PageSays("""{"type":"replay"}""");
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData("play it again")]
    [InlineData("Play that again, please")]
    [InlineData("can you show me that again?")]
    [InlineData("show me the drawing again")]
    [InlineData("replay")]
    [InlineData("replay it")]
    [InlineData("again")]
    [InlineData("one more time")]
    [InlineData("do it again")]
    [InlineData("explain that again")]
    [InlineData("repeat that please")]
    [InlineData("watch it again from the start")]
    [InlineData("hey clicky, play it again")]
    [InlineData("دوبارہ دکھاؤ")]
    [InlineData("ایک بار پھر سمجھاؤ")]
    public void AskingToWatchItAgainIsAReplay(string request) => Assert.True(DrawingReplay.IsReplayRequest(request));

    [Theory]
    [InlineData("draw it again bigger")]
    [InlineData("draw it again")]
    [InlineData("explain it again slower")]
    [InlineData("show it again in 3d")]
    [InlineData("what was that again?")]
    [InlineData("play some music")]
    [InlineData("show me how a volcano works")]
    [InlineData("save it")]
    [InlineData("can you play that video again on youtube for me")]
    [InlineData("دوبارہ بناؤ")]
    [InlineData("")]
    public void ChangingItOrAskingSomethingElseIsNot(string request) => Assert.False(DrawingReplay.IsReplayRequest(request));
}
