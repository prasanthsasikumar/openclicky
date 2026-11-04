// Answering by drawing (Windows only so far): which questions get a drawing, the program read out of a streamed reply,
// the prompt's canvas, and DrawingAnswer end to end against a fake page that runs say() lines the way stage.js does.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Plugins;

public partial class DrawingAnswerTests
{
    // MARK: - which questions

    [Theory]
    [InlineData("how does this work?")]
    [InlineData("How does a jet engine work")]
    [InlineData("can you draw the water cycle")]
    [InlineData("what's inside this?")]
    [InlineData("explain this to me")]
    [InlineData("teach me how a transistor switches")]
    [InlineData("how does the heart pump blood?")]
    [InlineData("why is the sky blue")]
    [InlineData("hey, how do birds fly?")]
    [InlineData("what happens when ice melts")]
    [InlineData("how are rainbows made")]
    public void QuestionsAPictureAnswersGetADrawing(string question) => Assert.True(DrawStageLesson.WantsADrawing(question));

    [Theory]
    [InlineData("how do I export this")]
    [InlineData("where is the save button")]
    [InlineData("what time is it")]
    [InlineData("open spotify")]
    [InlineData("hi")]
    [InlineData("how do you make a new folder")]
    [InlineData("how are you today")]
    [InlineData("why not")]
    public void DoingThingsAndSmallTalkDoNot(string question) => Assert.False(DrawStageLesson.WantsADrawing(question));

    [Fact]
    public void TheStageIsOnWithTheTeacherUnlessSwitchedOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"drawstage-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var preferences = new Preferences(path);
            Assert.True(DrawStageLesson.IsEnabled(preferences, teacherIsOn: true));
            Assert.False(DrawStageLesson.IsEnabled(preferences, teacherIsOn: false));
            preferences.Set(DrawStageLesson.PreferenceKey, false);
            Assert.False(DrawStageLesson.IsEnabled(preferences, teacherIsOn: true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // MARK: - the program in a reply

    [Fact]
    public void TheProgramIsWhatTheFenceHolds()
    {
        const string reply = "Sure, here it is:\n```js\nstep(1); say(\"hi\")\ndot(1, 2)\n```\nHope that helps!";
        Assert.Equal("step(1); say(\"hi\")\ndot(1, 2)\n", DrawStageLesson.ProgramIn(reply));
    }

    [Theory]
    [InlineData("Sure", "")]
    [InlineData("Sure:\n```", "")]
    [InlineData("Sure:\n```js\nstep(1)", "step(1)")]
    [InlineData("```js\nstep(1)\n``", "step(1)\n")]
    [InlineData("```js\r\nstep(1)\r\n", "step(1)\n")]
    [InlineData("step(1); say(\"no fence\")", "step(1); say(\"no fence\")")]
    [InlineData("I can't draw that, but here is what I know.", "")]
    public void APartlyStreamedReplyGivesTheProgramSoFar(string replySoFar, string program) =>
        Assert.Equal(program, DrawStageLesson.ProgramIn(replySoFar));

    [Fact]
    public void ThePromptNamesTheCanvasAndTheCaptionStrip()
    {
        var prompt = DrawStageLesson.SystemPrompt(1280, 720);
        Assert.Contains("W = 1280 by H = 720", prompt);
        Assert.Contains("below y = 619", prompt);
        Assert.Contains("```js", prompt);
        Assert.Contains("figure(x, y, w, h", prompt);
        Assert.Contains(".stroke({axis, amount, period})", prompt);
        Assert.Contains("\"how does this work\"", DrawStageLesson.UserPrompt("  how does this work  "));
    }

    // MARK: - DrawingAnswer end to end

    /// <summary>The page's part: ready on Present, a "line" for each new say() in the program, finished once the program is
    /// done and its last line has been heard (or at once without lines, or on finishNow).</summary>
    private sealed partial class FakePage(bool startFails = false, bool neverReady = false) : IDrawStageSurface
    {
        private readonly object gate = new();
        private readonly List<string> lines = [];
        private bool programDone;
        private bool finished;

        public ConcurrentQueue<string> Received { get; } = new();
        public int Hides;
        public Action<string>? MessageReceived { get; set; }

        public void Present(PixelRect frame, double scale)
        {
            if (!neverReady) MessageReceived?.Invoke(startFails ? DrawStageMessages.Error("the stage did not start: no runtime") : """{"type":"ready"}""");
        }

        public void Hide() => Interlocked.Increment(ref Hides);

        public void Prepare() { }

        public void Post(string json)
        {
            var message = JsonNode.Parse(json)!;
            var type = (string)message["type"]!;
            Received.Enqueue(type);
            var replies = new List<string>();
            lock (gate)
            {
                switch (type)
                {
                    case "continueStream":
                        lines.Clear();
                        programDone = false;
                        finished = false;
                        break;
                    case "feed":
                        foreach (Match say in SayCall().Matches((string)message["code"]!).Skip(lines.Count))
                        {
                            lines.Add(say.Groups[1].Value);
                            replies.Add(new JsonObject { ["type"] = "line", ["k"] = lines.Count - 1, ["text"] = say.Groups[1].Value }.ToJsonString());
                        }
                        programDone = (bool)message["done"]!;
                        if (programDone) replies.Add(new JsonObject { ["type"] = "programRan", ["lines"] = lines.Count }.ToJsonString());
                        if (programDone && lines.Count == 0) replies.Add(Finish());
                        break;
                    case "lineEnded" when programDone && (int)message["k"]! == lines.Count - 1:
                    case "finishNow":
                        replies.Add(Finish());
                        break;
                }
            }
            foreach (var reply in replies.Where(reply => reply.Length > 0)) MessageReceived?.Invoke(reply);
        }

        private string Finish()
        {
            if (finished) return "";
            finished = true;
            return """{"type":"finished"}""";
        }

        [GeneratedRegex("say\\(\"([^\"]*)\"\\)")]
        private static partial Regex SayCall();
    }

    private static readonly CompanionScreenCapture Capture = new([1], "image/jpeg", "screen", true, 1920, 1080,
        new PixelRect(0, 0, 1920, 1080), 1, "DISPLAY1", 1280, 720);

    private static Func<Action<string>, CancellationToken, Task<string>> Streams(string reply) => async (onReplySoFar, token) =>
    {
        onReplySoFar(reply[..(reply.Length / 2)]);
        await Task.Yield();
        onReplySoFar(reply);
        return reply;
    };

    private static async Task<(DrawingAnswerResult Result, FakePage Page, List<string> Played)> DrawAsync(
        Func<Action<string>, CancellationToken, Task<string>> writeReply, FakePage? page = null, string? failingLine = null,
        string? holdLine = null, TimeSpan? holdLineAfter = null, DrawnAnswer? followUp = null, TimeSpan? readyLimit = null)
    {
        page ??= new FakePage();
        var stage = new DrawStageController { Surface = page };
        var played = new List<string>();
        var speech = new SpeechQueue(
            (text, _) => text == failingLine ? throw new IOException("no voice") : Task.FromResult(Encoding.UTF8.GetBytes(text)),
            (audio, _) => { lock (played) played.Add(Encoding.UTF8.GetString(audio)); return Task.CompletedTask; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await DrawingAnswer.RunAsync(stage, Capture, writeReply, speech, action => action(), () => { }, _ => { }, timeout.Token,
            holdLine, holdLineAfter, followUp, readyLimit);
        return (result, page, played);
    }

    private const string TwoBeats = "Here you go:\n```js\nstep(1); say(\"One.\")\ndot(1, 1)\nstep(2); say(\"Two.\")\ndot(2, 2)\n```";

    [Fact]
    public async Task EveryLineIsSpokenInOrderAndTheProgramIsDrawn()
    {
        var (result, page, played) = await DrawAsync(Streams(TwoBeats));
        Assert.True(result.Drew);
        Assert.Equal(["One.", "Two."], result.SpokenLines);
        Assert.Equal(["One.", "Two."], played);
        Assert.Null(result.UnspokenAfterVoiceFailure);
        Assert.Equal("beginStream", page.Received.First());
        Assert.Contains("lineStarted", page.Received);
        Assert.DoesNotContain("clear", page.Received);
    }

    [Fact]
    public async Task AProseReplyIsNotDrawnAndTheStageClears()
    {
        var (result, page, played) = await DrawAsync(Streams("I'd rather explain that in words."));
        Assert.False(result.Drew);
        Assert.Empty(played);
        Assert.Contains("clear", page.Received);
        Assert.Equal(1, page.Hides);
    }

    [Fact]
    public async Task AWriterThatFailsBeforeAnyLineIsNotDrawn()
    {
        var (result, _, _) = await DrawAsync((_, _) => throw new HttpRequestException("refused"));
        Assert.False(result.Drew);
    }

    [Fact]
    public async Task AStageThatNeverStartsIsNotDrawn()
    {
        var (result, _, played) = await DrawAsync(Streams(TwoBeats), new FakePage(startFails: true));
        Assert.False(result.Drew);
        Assert.Empty(played);
    }

    [Fact]
    public async Task AStageThatNeverLoadsIsNotDrawnWithoutWaitingOutTheLimit()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (result, page, played) = await DrawAsync(Streams(TwoBeats), new FakePage(neverReady: true), readyLimit: TimeSpan.FromMilliseconds(100));
        Assert.False(result.Drew);
        Assert.Empty(played);
        Assert.Equal(1, page.Hides);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AProgramThatSaysNothingIsNotDrawn()
    {
        var (result, page, _) = await DrawAsync(Streams("```js\ndot(1, 1)\ncircle(5, 5, 3)\n```"));
        Assert.False(result.Drew);
        Assert.Contains("clear", page.Received);
    }

    [Fact]
    public async Task AVoiceThatFailsHandsBackWhatItDidNotSayAndTheDrawingFinishes()
    {
        var (result, page, played) = await DrawAsync(Streams(TwoBeats), failingLine: "Two.");
        Assert.True(result.Drew);
        Assert.Equal(["One."], played);
        Assert.Equal(["Two."], result.UnspokenAfterVoiceFailure);
        Assert.Contains("finishNow", page.Received);
    }

    [Fact]
    public async Task ASlowFirstLineIsPrecededByTheHoldLineAndTheBeatsStillMatchTheirLines()
    {
        async Task<string> Slow(Action<string> onReplySoFar, CancellationToken token)
        {
            await Task.Delay(400, token);
            onReplySoFar(TwoBeats);
            return TwoBeats;
        }
        var (result, page, played) = await DrawAsync(Slow, holdLine: DrawingAnswer.HoldLine, holdLineAfter: TimeSpan.FromMilliseconds(50));
        Assert.True(result.Drew);
        Assert.Equal([DrawingAnswer.HoldLine, "One.", "Two."], played);
        // The hold line is the voice's, not the drawing's: the beats are still lines 0 and 1, and the drawing finished on line 1.
        Assert.Equal(["One.", "Two."], result.SpokenLines);
        Assert.Equal(2, page.Received.Count(type => type == "lineStarted"));
    }

    [Fact]
    public async Task AQuickFirstLineHasNoHoldLine()
    {
        var (_, _, played) = await DrawAsync(Streams(TwoBeats), holdLine: DrawingAnswer.HoldLine, holdLineAfter: TimeSpan.FromSeconds(5));
        Assert.Equal(["One.", "Two."], played);
    }
}
