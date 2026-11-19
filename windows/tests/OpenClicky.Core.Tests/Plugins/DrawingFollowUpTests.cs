// Follow-up questions about the drawing on the stage, and saving it: which questions may be about it, how the model's
// program says it keeps the drawing, the follow-up prompt, a follow-up end to end against the fake page (keep() continues
// the drawing, anything else starts a new one, NONE hands the question back), the drawing the stage remembers, and the
// messages for measuring and recording it.

using System.Text.Json.Nodes;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Plugins;

public partial class DrawingAnswerTests
{
    private static readonly DrawnAnswer JetEngine = new(
        ["how does a jet engine work?"],
        "step(1); say(\"A fan sucks air in.\")\nconst c = {x: 300, y: 40, w: 900, h: 600}\ncard(c.x, c.y, c.w, c.h, {title: 'Inside a jet engine'})\nstep(4); say(\"It pushes the plane.\")\n",
        new PixelRect(0, 0, 1920, 1080), 1, 1280, 720,
        [new DrawnLine("A fan sucks air in.", 2.1, 0.6, [1, 2]), new DrawnLine("It pushes the plane.", 1.8, 0.3, null)]);

    // MARK: - which questions

    [Theory]
    [InlineData("now show it in 3D")]
    [InlineData("zoom in on the nozzle")]
    [InlineData("what does the turbine do?")]
    [InlineData("and the compressor?")]
    [InlineData("make it simpler")]
    [InlineData("what's that orange part")]
    [InlineData("how does the fan work")]
    [InlineData("make the nucleus bigger")]
    [InlineData("colour the arteries red")]
    [InlineData("slow it down")]
    [InlineData("hide the labels")]
    [InlineData("remove the lungs")]
    public void QuestionsAboutTheDrawingMayBeFollowUps(string question) => Assert.True(DrawStageLesson.MayBeAFollowUp(question));

    [Theory]
    [InlineData("what time is it")]
    [InlineData("open spotify")]
    [InlineData("save it")]
    [InlineData("how do I export this")]
    [InlineData("thanks")]
    [InlineData("how are you doing")]
    public void OtherQuestionsAreNot(string question) => Assert.False(DrawStageLesson.MayBeAFollowUp(question));

    [Theory]
    [InlineData("save it")]
    [InlineData("Save this drawing")]
    [InlineData("please save that")]
    [InlineData("can you save the video")]
    [InlineData("download this")]
    [InlineData("export the drawing")]
    [InlineData("save")]
    public void AskingToSaveIsASaveRequest(string request) => Assert.True(DrawStageLesson.IsSaveRequest(request));

    [Theory]
    [InlineData("how do I save a file")]
    [InlineData("the save button is broken")]
    [InlineData("what does save mean")]
    [InlineData("how does a jet engine work")]
    public void OtherQuestionsAboutSavingAreNot(string request) => Assert.False(DrawStageLesson.IsSaveRequest(request));

    [Theory]
    [InlineData("save it", DrawingSaveKind.Both)]
    [InlineData("save the picture", DrawingSaveKind.Picture)]
    [InlineData("save a screenshot of this", DrawingSaveKind.Picture)]
    [InlineData("save image", DrawingSaveKind.Picture)]
    [InlineData("can you save the video", DrawingSaveKind.Video)]
    [InlineData("download that clip", DrawingSaveKind.Video)]
    [InlineData("save the picture and the video", DrawingSaveKind.Both)]
    public void ASaveRequestSaysWhatToSave(string request, DrawingSaveKind what) => Assert.Equal(what, DrawStageLesson.SaveRequestKind(request));

    // MARK: - keep()

    [Theory]
    [InlineData("keep()\nstep(5); say(\"Five.\")", false, true)]
    [InlineData("// adds to the drawing\nkeep()\n", false, true)]
    [InlineData("  keep();", false, true)]
    [InlineData("step(1); say(\"One.\")", false, false)]
    [InlineData("const c = {x: 1}", false, false)]
    [InlineData("", false, null)]
    [InlineData("kee", false, null)]
    [InlineData("// still writing this comm", false, null)]
    [InlineData("kee", true, false)]
    [InlineData("", true, false)]
    public void AProgramKeepsTheDrawingWhenItStartsWithKeep(string program, bool done, bool? keeps) =>
        Assert.Equal(keeps, DrawStageLesson.KeepsTheDrawing(program, done));

    [Fact]
    public void AReplyThatStartsWithKeepWithoutAFenceIsAProgram() =>
        Assert.Equal("keep()\nstep(5)", DrawStageLesson.ProgramIn("keep()\nstep(5)"));

    [Fact]
    public void TheFollowUpPromptHasTheDrawingAndTheThreeWaysToAnswer()
    {
        var prompt = DrawStageLesson.FollowUpPrompt("what does the turbine do?", JetEngine.AddedTo("and the fan?", "keep()\nstep(5); say(\"Fan.\")", []));
        Assert.Contains("\"how does a jet engine work?\", then \"and the fan?\"", prompt);
        Assert.Contains("card(c.x, c.y, c.w, c.h, {title: 'Inside a jet engine'})", prompt);
        Assert.Contains("Now they ask: \"what does the turbine do?\"", prompt);
        Assert.Contains("keep()", prompt);
        Assert.Contains("numbered on from step(6)", prompt);
        Assert.Contains($"only the word {DrawStageLesson.NotAFollowUp}", prompt);
    }

    [Fact]
    public void AChangeToTheDrawingIsMadeInPlace()
    {
        var prompt = DrawStageLesson.FollowUpPrompt("make the fan bigger", JetEngine);
        Assert.Contains("keep it and change it in place, without drawing it again", prompt);
        Assert.Contains("then in step(5) one line saying what changes", prompt);
        Assert.Contains("cl.part('nucleus').change({size: 1.5})", prompt);
        Assert.DoesNotContain("(in 3D, as a cutaway, bigger, simpler)", prompt);
        var system = DrawStageLesson.SystemPrompt(1280, 720);
        Assert.Contains("f.tempo(0.5) runs every motion in the figure at half speed", system);
        Assert.Contains("erase(arrowA) takes it away", system);
    }

    // MARK: - how detailed, and their own instructions (Settings → Clicky → Drawings)

    [Fact]
    public void HowDetailedAndTheirInstructionsShapeThePrompt()
    {
        Assert.Contains("usually 15-40 parts", DrawStageLesson.SystemPrompt(1280, 720));
        var simple = DrawStageLesson.SystemPrompt(1280, 720, DrawingDetail.Simple);
        Assert.Contains("usually 8-15 parts", simple);
        Assert.Contains("- 3 to 4 beats.", simple);
        var veryDetailed = DrawStageLesson.SystemPrompt(1280, 720, DrawingDetail.VeryDetailed, "  Use bright colours.  ");
        Assert.Contains("usually 30-70 parts", veryDetailed);
        Assert.Contains("up to 90 parts", veryDetailed);
        Assert.EndsWith("\nUse bright colours.", veryDetailed);
        Assert.DoesNotContain("THEIR OWN INSTRUCTIONS", DrawStageLesson.SystemPrompt(1280, 720, theirInstructions: "  "));
        Assert.Equal(DrawingDetail.VeryDetailed, DrawStageLesson.DetailFrom(DrawStageLesson.RawValue(DrawingDetail.VeryDetailed)));
        Assert.Equal(DrawingDetail.Detailed, DrawStageLesson.DetailFrom(null));
        Assert.True(DrawStageLesson.OutputTokens(DrawingDetail.VeryDetailed) > DrawStageLesson.OutputTokens(DrawingDetail.Detailed));
        Assert.True(DrawStageLesson.OutputTokens(DrawingDetail.Detailed) > DrawStageLesson.OutputTokens(DrawingDetail.Simple));
    }

    // MARK: - a follow-up end to end

    [Fact]
    public async Task AFollowUpThatKeepsTheDrawingContinuesItAndKeepsItsLinesApart()
    {
        const string reply = "```js\nkeep()\nstep(5); say(\"Five.\")\nturbine.label('blades')\nstep(6); say(\"Six.\")\n```";
        var (result, page, played) = await DrawAsync(Streams(reply), followUp: JetEngine);
        Assert.True(result.Drew);
        Assert.True(result.KeptTheDrawing);
        Assert.Equal(["Five.", "Six."], result.SpokenLines);
        Assert.Equal(["Five.", "Six."], played);
        Assert.Equal("continueStream", page.Received.First());
        Assert.DoesNotContain("beginStream", page.Received);
        Assert.DoesNotContain("clear", page.Received);
        Assert.StartsWith("keep()", result.Program);
    }

    [Fact]
    public async Task AFollowUpWithoutKeepStartsANewDrawing()
    {
        var (result, page, _) = await DrawAsync(Streams(TwoBeats), followUp: JetEngine);
        Assert.True(result.Drew);
        Assert.False(result.KeptTheDrawing);
        Assert.Equal("beginStream", page.Received.First());
        Assert.DoesNotContain("continueStream", page.Received);
    }

    [Fact]
    public async Task AFollowUpAnsweredWithNoneIsNotDrawnAndTheDrawingGoes()
    {
        var page = new FakePage();
        var stage = new DrawStageController { Surface = page };
        page.Present(Capture.DisplayFrame, 1);
        stage.Begin(Capture.DisplayFrame, 1, 1280, 720);
        stage.Remember(JetEngine);
        var speech = new SpeechQueue((_, _) => Task.FromResult(Array.Empty<byte>()), (_, _) => Task.CompletedTask);
        var result = await DrawingAnswer.RunAsync(stage, Capture, Streams(DrawStageLesson.NotAFollowUp), speech, action => action(), () => { }, _ => { },
            CancellationToken.None, followUp: JetEngine);
        Assert.False(result.Drew);
        Assert.Contains("clear", page.Received);
        Assert.Null(stage.Current);
        Assert.False(stage.IsShowing);
    }

    [Fact]
    public void TheLinesAreKeptWithHowLongTheyTookAndThePauseBeforeEach()
    {
        var result = new DrawingAnswerResult(true, ["One two three.", "Four.", "Never heard."], null, 10, "p", false,
            [(1.0, 3.5), (4.0, 5.0)]);
        var lines = result.DrawnLines(text => text == "Four." ? [9] : null);
        Assert.Equal(2.5, lines[0].Seconds, 3);
        Assert.Equal(0.6, lines[0].PauseBefore, 3);
        Assert.Equal(1.0, lines[1].Seconds, 3);
        Assert.Equal(0.5, lines[1].PauseBefore, 3);
        Assert.Equal([9], lines[1].Audio);
        Assert.Null(lines[0].Audio);
        // A line the voice never got to takes as long as reading it.
        Assert.Equal(0.8, lines[2].Seconds, 3);
    }

    // MARK: - the drawing the stage keeps

    [Fact]
    public void ADrawingAddedToKeepsItsQuestionsProgramAndLines()
    {
        var added = JetEngine.AddedTo("and the turbine?", "keep()\nstep(5); say(\"Five.\")", [new DrawnLine("Five.", 1, 30, null)]);
        Assert.Equal(["how does a jet engine work?", "and the turbine?"], added.Questions);
        Assert.Contains("say(\"It pushes the plane.\")\n\nkeep()\nstep(5)", added.Program);
        Assert.Equal(3, added.Lines.Count);
        // A long wait between the two questions isn't kept when it is replayed.
        Assert.Equal(DrawnAnswer.LongestPauseReplayed, added.Lines[2].PauseBefore);
        Assert.Equal("Inside a jet engine", added.Title);
        Assert.Equal("how does a heart work", (JetEngine with { Program = "step(1)", Questions = ["how does a heart work?"] }).Title);
    }

    [Fact]
    public void ANewQuestionHoldsAFinishedDrawingAndClearsAnythingElse()
    {
        var page = new FakePage();
        var stage = new DrawStageController { Surface = page };
        page.Present(Capture.DisplayFrame, 1);
        stage.Begin(Capture.DisplayFrame, 1, 1280, 720);
        // Still being drawn: a new question clears it.
        stage.HoldForFollowUp("a new question");
        Assert.False(stage.IsShowing);

        stage.Begin(Capture.DisplayFrame, 1, 1280, 720);
        stage.Remember(JetEngine);
        stage.HoldForFollowUp("a new question");
        Assert.True(stage.IsShowing);
        Assert.Same(JetEngine, stage.Current);
        Assert.Contains("remembered", page.Received);

        // A new drawing, or a clear, lets it go.
        stage.Begin(Capture.DisplayFrame, 1, 1280, 720);
        Assert.Null(stage.Current);
        stage.Remember(JetEngine);
        stage.Clear("esc");
        Assert.Null(stage.Current);
        // One cleared before it was remembered isn't.
        stage.Remember(JetEngine);
        Assert.Null(stage.Current);
    }

    [Fact]
    public async Task MeasuringAsksThePageForTheDrawingsBox()
    {
        var page = new FakePage();
        var stage = new DrawStageController { Surface = page };
        page.Present(Capture.DisplayFrame, 1);
        var measuring = stage.MeasureAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("measure", page.Received);
        page.MessageReceived!("""{"type":"bounds","x":10,"y":20,"w":300,"h":200,"pageWidth":1280,"pageHeight":720}""");
        var bounds = await measuring;
        Assert.Equal(new DrawStageBounds(10, 20, 300, 200, 1280, 720), bounds);
        Assert.False(bounds!.IsEmpty);
        Assert.Null(await stage.MeasureAsync(TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void ThePicturesAndVideosButtonsAskTheAppToSaveThat()
    {
        var page = new FakePage();
        var stage = new DrawStageController { Surface = page };
        var asked = new List<DrawingSaveKind>();
        stage.SaveRequested += asked.Add;
        page.MessageReceived!("""{"type":"save","text":"picture"}""");
        page.MessageReceived!("""{"type":"save","text":"video"}""");
        page.MessageReceived!("""{"type":"save"}""");
        Assert.Equal([DrawingSaveKind.Picture, DrawingSaveKind.Video, DrawingSaveKind.Both], asked);
    }

    [Fact]
    public void TheCloseButtonAsksTheAppToCloseTheDrawing()
    {
        var page = new FakePage();
        var stage = new DrawStageController { Surface = page };
        var closed = 0;
        stage.CloseRequested += () => closed++;
        page.MessageReceived!("""{"type":"close"}""");
        Assert.Equal(1, closed);
    }

    [Fact]
    public void SavingShowsOnThePageAndAReplayKeepsWhereTheDrawingWasMoved()
    {
        var working = JsonNode.Parse(DrawStageMessages.Saving("Recording the video…", 0.42, "working"))!;
        Assert.Equal("saving", (string)working["type"]!);
        Assert.Equal(0.42, (double)working["fraction"]!);
        var done = JsonNode.Parse(DrawStageMessages.Saving("Saved", null, "done"))!;
        Assert.Equal("done", (string)done["state"]!);
        Assert.Null(done["fraction"]);
        Assert.True((bool)JsonNode.Parse(DrawStageMessages.BeginStream(1280, 720, DrawStagePace.Speech, keepPlace: true))!["keepPlace"]!);
        Assert.Null(JsonNode.Parse(DrawStageMessages.BeginStream(1280, 720, DrawStagePace.Speech))!["keepPlace"]);
    }

    [Fact]
    public void ContinuingAndRecordingAreMessagesThePageReads()
    {
        Assert.Equal("speech", (string)JsonNode.Parse(DrawStageMessages.ContinueStream(DrawStagePace.Speech))!["pace"]!);
        Assert.True((bool)JsonNode.Parse(DrawStageMessages.Recording(true))!["on"]!);
        Assert.True(new DrawStageBounds(0, 0, 0, 0, 1280, 720).IsEmpty);
    }
}
