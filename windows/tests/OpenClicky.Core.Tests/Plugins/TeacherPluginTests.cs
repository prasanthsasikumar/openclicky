// Ports TeacherPluginTests.swift: the drawing language, its timing, where the board lands on a screen, the picture
// search replies, the tools, the lesson writer's routes and bodies, teaching questions and written answers. Added on
// Windows: the hand-drawn geometry (Core here, the view on the Mac), the board in physical pixels, the pointer's label
// stops, and the board's tokens with a fake surface.

using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Plugins;

public class TeacherPluginTests
{
    private static string Example => TeacherPlugin.SketchGrammar.Split("Example, Pythagoras:\n")[^1];

    // MARK: - the drawing language

    [Fact]
    public void TheToolsOwnExampleParsesWithoutASkippedLine()
    {
        var result = TeacherSketchParser.Parse(Example);
        var sketch = Assert.IsType<TeacherSketch>(result.Sketch);
        Assert.Empty(result.SkippedLines);
        Assert.Equal(8, sketch.Strokes.Count);
        Assert.Equal((400.0, 300.0), (sketch.BoardWidth, sketch.BoardHeight));
        Assert.Equal(TeacherStrokeKind.Shape, sketch.Strokes[0].Kind);
        Assert.Equal(3, sketch.Strokes[0].Points.Count);
        Assert.Equal(TeacherStrokeKind.RightAngle, sketch.Strokes[1].Kind);
        Assert.Equal("b", sketch.Strokes[2].Words);
        Assert.Equal(TeacherInk.Blue, sketch.Strokes[2].Ink);
        Assert.True(sketch.Strokes[2].Weight > 1);
        Assert.Equal("a² + b² = c²", sketch.Strokes[7].Words);
    }

    [Fact]
    public void CommandsOnOneLineSplitOnSemicolonsButNotInsideQuotes()
    {
        var sketch = TeacherSketchParser.Parse("board 200 100; line 0 0 100 50 red dashed; text 50 50 \"a; b\" small").Sketch!;
        Assert.Equal((200.0, 100.0), (sketch.BoardWidth, sketch.BoardHeight));
        Assert.Equal(2, sketch.Strokes.Count);
        Assert.Equal(TeacherInk.Red, sketch.Strokes[0].Ink);
        Assert.True(sketch.Strokes[0].IsDashed);
        Assert.Equal(TeacherStrokeKind.Text, sketch.Strokes[1].Kind);
        Assert.Equal("a; b", sketch.Strokes[1].Words);
        Assert.True(sketch.Strokes[1].Weight < 1);
    }

    [Fact]
    public void EveryCommandReadsItsNumbers()
    {
        const string source = """
            arrow 10 10 50 50
            curve 0 0 10 10 20 0
            circle 100 100 40 fill
            arc 100 100 30 0 90
            box 10 20 30 40
            angle 50 50 90 50 50 10
            dot 5 5
            pause 1.5
            label 20 20 hypotenuse orange
            """;
        var sketch = TeacherSketchParser.Parse(source).Sketch!;
        Assert.Equal(
            [TeacherStrokeKind.Arrow, TeacherStrokeKind.Curve, TeacherStrokeKind.Circle, TeacherStrokeKind.Arc, TeacherStrokeKind.Box,
             TeacherStrokeKind.Angle, TeacherStrokeKind.Dot, TeacherStrokeKind.Pause, TeacherStrokeKind.Text],
            sketch.Strokes.Select(stroke => stroke.Kind));
        Assert.Equal(40, sketch.Strokes[2].Radius);
        Assert.True(sketch.Strokes[2].IsFilled);
        Assert.Equal((30.0, 0.0, 90.0), (sketch.Strokes[3].Radius, sketch.Strokes[3].StartDegrees, sketch.Strokes[3].EndDegrees));
        Assert.Equal([new ScreenPoint(10, 20), new ScreenPoint(30, 40)], sketch.Strokes[4].Points);
        Assert.Equal(1.5, sketch.Strokes[7].Seconds);
        Assert.Equal("hypotenuse", sketch.Strokes[8].Words);
        Assert.Equal(TeacherInk.Orange, sketch.Strokes[8].Ink);
    }

    [Fact]
    public void UnreadableLinesAreSkippedAndNothingDrawableIsNull()
    {
        var result = TeacherSketchParser.Parse("triangle 1 2\nscribble 1 2 3\nline 0 0 10 10");
        Assert.Single(result.Sketch!.Strokes);
        Assert.Equal(["triangle 1 2", "scribble 1 2 3"], result.SkippedLines);
        Assert.Null(TeacherSketchParser.Parse("").Sketch);
        Assert.Null(TeacherSketchParser.Parse("pause 2").Sketch);
        Assert.Null(TeacherSketchParser.Parse("circle 10 10 -5").Sketch);
    }

    [Fact]
    public void PointsFarOffTheBoardArePulledBackOntoIt()
    {
        var sketch = TeacherSketchParser.Parse("line -900 50 5000 50").Sketch!;
        Assert.Equal([new ScreenPoint(-40, 50), new ScreenPoint(440, 50)], sketch.Strokes[0].Points);
    }

    [Fact]
    public void ASketchIsCappedAtItsMaximumStrokes()
    {
        var source = string.Join("\n", Enumerable.Range(0, 120).Select(index => $"dot {index} 10"));
        var result = TeacherSketchParser.Parse(source);
        Assert.Equal(TeacherSketch.MaximumStrokes, result.Sketch!.Strokes.Count);
        Assert.Equal(120 - TeacherSketch.MaximumStrokes, result.SkippedLines.Count);
    }

    [Fact]
    public void LeftIsAnOptionNotAWord()
    {
        var stroke = TeacherSketchParser.Parse("text 10 20 \"x = 6\" left red").Sketch!.Strokes[0];
        Assert.True(stroke.IsLeftAligned);
        Assert.Equal(TeacherInk.Red, stroke.Ink);
        Assert.Equal("x = 6", stroke.Words);
    }

    [Fact]
    public void CurlyQuotesAndCommasAreReadLikeStraightOnes()
    {
        var sketch = TeacherSketchParser.Parse("text 10,20 “hi there” blue\r\nline (0, 0) (10, 10)").Sketch!;
        Assert.Equal("hi there", sketch.Strokes[0].Words);
        Assert.Equal(new ScreenPoint(10, 20), sketch.Strokes[0].Points[0]);
        Assert.Equal(TeacherStrokeKind.Line, sketch.Strokes[1].Kind);
    }

    // MARK: - timing

    [Fact]
    public void StrokesFollowEachOtherAtAHandsPace()
    {
        var sketch = TeacherSketchParser.Parse("line 0 0 400 0\ntext 10 10 \"hypotenuse\"\npause 2\ndot 1 1").Sketch!;
        var entries = TeacherSketchTimeline.Entries(sketch);
        Assert.Equal(4, entries.Count);
        Assert.Equal(0, entries[0].Start);
        Assert.InRange(entries[0].Duration, 0.35, 1.6);
        Assert.Equal(0.7, entries[1].Duration, 3);
        Assert.Equal(2, entries[2].Duration);
        Assert.Equal(entries[0].Duration + TeacherSketchTimeline.GapBetweenStrokes, entries[1].Start, 4);
        Assert.Equal(entries[3].Start + entries[3].Duration, TeacherSketchTimeline.TotalSeconds(sketch));
    }

    // MARK: - fitting the drawing

    [Fact]
    public void TheLayoutFitsWhatWasDrawnNotTheDeclaredBoard()
    {
        // Drawn in the board's middle, with a formula written past its right edge.
        var sketch = TeacherSketchParser.Parse("line 140 200 220 200\nline 140 140 140 200\ntext 380 220 \"a² + b² = c²\" big").Sketch!;
        var bounds = TeacherSketchLayout.DrawingBoundsOf(sketch);
        Assert.InRange(bounds.Left, 100.0001, 139.9999);
        Assert.True(bounds.Right > 400);
        var layout = new TeacherSketchLayout(sketch, 400, 300);
        // The drawing is centred and fills the view's width or height.
        var topLeft = layout.ViewPoint(new ScreenPoint(bounds.Left, bounds.Top));
        var bottomRight = layout.ViewPoint(new ScreenPoint(bounds.Right, bounds.Bottom));
        Assert.True(Math.Abs(topLeft.X) < 0.5 || Math.Abs(topLeft.Y) < 0.5);
        Assert.True(Math.Abs(bottomRight.X - 400) < 0.5 || Math.Abs(bottomRight.Y - 300) < 0.5);
        Assert.Equal(200, (topLeft.X + bottomRight.X) / 2, 1);
        Assert.Equal(150, (topLeft.Y + bottomRight.Y) / 2, 1);
    }

    [Fact]
    public void ATinyDrawingStillGetsABoardOfSomeSize()
    {
        var bounds = TeacherSketchLayout.DrawingBoundsOf(TeacherSketchParser.Parse("dot 200 150").Sketch!);
        Assert.True(bounds.Width >= 100 && bounds.Height >= 100);
    }

    // MARK: - the hand-drawn geometry (Windows: Core draws the lines the view strokes)

    [Fact]
    public void AStrokeWobblesTheSameWayEveryFrameAndItsCornersMeet()
    {
        var sketch = TeacherSketchParser.Parse(Example).Sketch!;
        var layout = new TeacherSketchLayout(sketch, 400, 300);
        var triangle = TeacherStrokePaths.Polyline(sketch.Strokes[0], layout, seed: 0);
        Assert.Equal(triangle, TeacherStrokePaths.Polyline(sketch.Strokes[0], layout, seed: 0));
        Assert.NotEqual(triangle, TeacherStrokePaths.Polyline(sketch.Strokes[0], layout, seed: 1));
        // Closed: it ends where it began, on the first corner (no nudge at a corner).
        Assert.True(triangle[0].DistanceTo(triangle[^1]) < 1e-9);
        Assert.Equal(layout.ViewPoint(sketch.Strokes[0].Points[0]), triangle[0]);
        Assert.Empty(TeacherStrokePaths.Polyline(sketch.Strokes[2], layout, seed: 2));
    }

    [Fact]
    public void AStrokeIsTrimmedByLengthAsTheHandDrawsIt()
    {
        ScreenPoint[] line = [new(0, 0), new(10, 0), new(10, 10)];
        Assert.Empty(TeacherStrokePaths.Trimmed(line, 0));
        Assert.Equal([new ScreenPoint(0, 0), new ScreenPoint(5, 0)], TeacherStrokePaths.Trimmed(line, 0.25));
        Assert.Equal([new ScreenPoint(0, 0), new ScreenPoint(10, 0), new ScreenPoint(10, 5)], TeacherStrokePaths.Trimmed(line, 0.75));
        Assert.Same(line, TeacherStrokePaths.Trimmed(line, 1));
    }

    [Fact]
    public void AnArrowheadPointsAlongTheLine()
    {
        var head = TeacherStrokePaths.ArrowHead(new ScreenPoint(100, 0), new ScreenPoint(0, 0), 10);
        Assert.Equal(new ScreenPoint(100, 0), head[1]);
        Assert.True(head[0].X < 100 && head[2].X < 100);
        Assert.Equal(-head[0].Y, head[2].Y, 6);
    }

    // MARK: - where the board lands

    /// <summary>A grid that is empty except where <paramref name="busy"/> says, all of one brightness.</summary>
    private static TeacherScreenGrid Grid(int columns, int rows, double brightness, Func<int, int, bool> busy)
    {
        var isEmpty = new List<bool>();
        for (var row = 0; row < rows; row++) for (var column = 0; column < columns; column++) isEmpty.Add(!busy(column, row));
        return new TeacherScreenGrid(columns, rows, isEmpty, Enumerable.Repeat(brightness, columns * rows).ToList());
    }

    [Fact]
    public void AGoogleLikePageGetsInkInItsWideRightMargin()
    {
        // 1440 × 900 DIPs: 90 × 56 cells. Results text fills columns 10–50 from row 8 down.
        var page = Grid(90, 56, 0.98, (column, row) => column >= 10 && column <= 50 && row >= 8);
        var placement = TeacherBoardPlacement.Place(4.0 / 3.0, page, 1440, 900, mouseX: 400);
        Assert.Equal(TeacherBoardStyle.InkOnLightPage, placement.Style);
        Assert.True(placement.FrameInDips.Left > 51 * 16);
        Assert.True(placement.FrameInDips.Right < 1440);
        Assert.True(placement.FrameInDips.Width >= TeacherBoardPlacement.MinimumBoardWidth);
        Assert.Equal(4.0 / 3.0, placement.FrameInDips.Width / placement.FrameInDips.Height, 1);
        // Never in the island's band at the top.
        Assert.True(placement.FrameInDips.Top >= TeacherBoardPlacement.TopBand);
    }

    [Fact]
    public void ADarkEmptyAreaGetsChalk()
    {
        var editor = Grid(90, 56, 0.1, (column, _) => column < 40);
        Assert.Equal(TeacherBoardStyle.ChalkOnDarkPage, TeacherBoardPlacement.Place(1.5, editor, 1440, 900, mouseX: 0).Style);
    }

    [Fact]
    public void ABusyScreenGetsPaperAwayFromTheMouse()
    {
        var busy = Grid(90, 56, 0.5, (_, _) => true);
        var mouseOnTheRight = TeacherBoardPlacement.Place(4.0 / 3.0, busy, 1440, 900, mouseX: 1300);
        Assert.Equal(TeacherBoardStyle.Paper, mouseOnTheRight.Style);
        Assert.True(mouseOnTheRight.FrameInDips.Right < 720);
        Assert.True(TeacherBoardPlacement.Place(4.0 / 3.0, busy, 1440, 900, mouseX: 100).FrameInDips.Left > 720);
        // No screenshot at all: the same sheet.
        Assert.Equal(mouseOnTheRight, TeacherBoardPlacement.Place(4.0 / 3.0, null, 1440, 900, mouseX: 1300));
    }

    [Fact]
    public void GreyPixelsBecomeEmptyAndBusyCells()
    {
        // 2 × 1 cells of 8 px: the left one flat white, the right one with a black stroke.
        var pixels = Enumerable.Repeat((byte)250, 16 * 8).ToArray();
        for (var row = 0; row < 8; row++) pixels[row * 16 + 12] = 0;
        var cells = TeacherBoardPlacement.GridFromGreyPixels(pixels, 16, 8, 8);
        Assert.Equal((2, 1), (cells.Columns, cells.Rows));
        Assert.Equal([true, false], cells.IsEmpty);
        Assert.True(cells.Brightness[0] > 0.97);
    }

    [Fact]
    public void TheLargestRectangleIsFoundAroundAnObstacle()
    {
        // 20 × 10 with a busy column at 6: the empty block on the right (13 wide) wins.
        var cells = Grid(20, 10, 1, (column, _) => column == 6);
        Assert.Equal(new TeacherCellRect(7, 0, 13, 10), TeacherBoardPlacement.LargestEmptyRectangle(cells, 1));
    }

    [Fact]
    public void TheBoardLandsOnItsMonitorInPhysicalPixels()
    {
        // A 150 % monitor to the left of the primary one.
        var placement = new TeacherBoardPlacement.Placement(new ScreenRect(100, 80, 400, 300), TeacherBoardStyle.Paper);
        Assert.Equal(new PixelRect(-2880 + 150, 120, 600, 450), placement.FrameInPixels(new PixelRect(-2880, 0, 2880, 1620), 1.5));
    }

    // MARK: - the pointer follows the labels (Windows: physical pixels)

    [Fact]
    public void ThePointerVisitsAtMostSixLabelsAtLeastASecondApart()
    {
        var many = string.Join("\n", Enumerable.Range(0, 20).Select(index => $"text {20 * index} 50 \"label {index}\""));
        var sketch = TeacherSketchParser.Parse(many).Sketch!;
        var stops = TeacherBoard.LabelStops(sketch, new PixelRect(0, 0, 800, 600), 1, TeacherBoardStyle.Paper);
        Assert.Equal(6, stops.Count);
        Assert.All(stops.Zip(stops.Skip(1)), pair => Assert.True(pair.Second.Seconds - pair.First.Seconds >= 1.2));
    }

    [Fact]
    public void ALabelStopSitsJustLeftOfAndBelowItsWordOnTheScaledBoard()
    {
        var sketch = TeacherSketchParser.Parse(Example).Sketch!;
        var frame = new PixelRect(1000, 200, 800, 600);
        var stops = TeacherBoard.LabelStops(sketch, frame, 2, TeacherBoardStyle.InkOnLightPage);
        var padding = TeacherBoard.Padding(TeacherBoardStyle.InkOnLightPage) * 2;
        var layout = new TeacherSketchLayout(sketch, (800 - 2 * padding) / 2, (600 - 2 * padding) / 2);
        var word = layout.ViewPoint(sketch.Strokes[2].Points[0]);
        Assert.Equal(new ScreenPoint(1000 + padding + (word.X - 14) * 2, 200 + padding + (word.Y + 12) * 2), stops[0].Location);
        Assert.All(stops, stop => Assert.True(frame.Contains(stop.Location.X, stop.Location.Y)));
    }

    // MARK: - the board's state

    private sealed class FakeSurface : ITeacherBoardSurface
    {
        public List<(TeacherLesson Lesson, PixelRect Frame)> Presented { get; } = [];
        public int HideCount { get; private set; }
        public void Present(TeacherLesson lesson, PixelRect frame, double scale) => Presented.Add((lesson, frame));
        public void Hide() => HideCount++;
        public byte[]? GreyPixels(byte[] imageData, int width, int height) => Enumerable.Repeat((byte)250, width * height).ToArray();
        public (int Width, int Height)? ImageSize(byte[] imageData) => (900, 600);
        public PixelPoint CursorPosition() => new(10, 10);
        public (PixelRect Frame, double Scale)? CursorDisplay() => (new PixelRect(0, 0, 1440, 900), 1);
        public TeacherListening.Output CurrentOutput() => new(0.2f, false);
    }

    private static CompanionScreenCapture Capture() =>
        new([1], "image/jpeg", "screen", true, 1440, 900, new PixelRect(0, 0, 1440, 900), 1, "\\\\.\\DISPLAY1", 1280, 800);

    [Fact]
    public void AnEmptyWhiteScreenGetsInkAndALateResultIsDropped()
    {
        var surface = new FakeSurface();
        var board = new TeacherBoard { Surface = surface };
        var token = board.BeginBackgroundJob();
        Assert.True(board.Show(TeacherSketchParser.Parse(Example).Sketch!, "pythagoras", Capture()));
        Assert.Equal(TeacherBoardStyle.InkOnLightPage, surface.Presented[^1].Lesson.Style);
        Assert.Equal("pythagoras", board.Lesson?.Title);
        // A picture searched for before that drawing went up arrives after it: not shown.
        Assert.False(board.Show(new TeacherPicture([1], 900, 600, "neuron", "Wikipedia"), Capture(), token));
        Assert.True(board.Clear("esc"));
        Assert.Null(board.Lesson);
        Assert.Equal(1, surface.HideCount);
        Assert.False(board.Clear("esc"));
    }

    [Fact]
    public void EscClearsTheBoardUnlessADictationTakeHasIt()
    {
        Assert.True(TeacherPlugin.EscClearsTheBoard(EscapeTarget.AskLane));
        Assert.True(TeacherPlugin.EscClearsTheBoard(EscapeTarget.Realtime));
        Assert.True(TeacherPlugin.EscClearsTheBoard(EscapeTarget.Nothing));
        Assert.False(TeacherPlugin.EscClearsTheBoard(EscapeTarget.DictationTake));
    }

    [Fact]
    public void ClearingTheBoardLetsTheNextQuestionHaveItsOwnLesson()
    {
        var board = new TeacherBoard();
        board.QuestionHasLesson = true;
        board.Clear("a new question");
        Assert.False(board.QuestionHasLesson);
    }

    // MARK: - pictures

    [Fact]
    public void CommonsRepliesGiveStillImageThumbnailsInSearchOrder()
    {
        const string reply = """
            {"query":{"pages":{
              "11":{"index":2,"title":"File:B.png","imageinfo":[{"mime":"image/png","thumburl":"https://upload.wikimedia.org/b.png"}]},
              "22":{"index":1,"title":"File:A.svg","imageinfo":[{"mime":"image/svg+xml","thumburl":"https://upload.wikimedia.org/a.svg.png"}]},
              "33":{"index":3,"title":"File:C.ogv","imageinfo":[{"mime":"video/ogg","thumburl":"https://upload.wikimedia.org/c.jpg"}]}
            }}}
            """;
        Assert.Equal(["https://upload.wikimedia.org/a.svg.png", "https://upload.wikimedia.org/b.png"],
            TeacherPictureFinder.CommonsThumbnailUrls(Encoding.UTF8.GetBytes(reply)).Select(url => url.AbsoluteUri));
        Assert.Empty(TeacherPictureFinder.CommonsThumbnailUrls(Encoding.UTF8.GetBytes("{}")));
        Assert.Empty(TeacherPictureFinder.CommonsThumbnailUrls(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void WikipediaRepliesGiveLeadImages()
    {
        const string reply = """{"query":{"pages":{"5":{"index":1,"thumbnail":{"source":"https://upload.wikimedia.org/neuron.jpg"}},"6":{"index":2}}}}""";
        Assert.Equal(["https://upload.wikimedia.org/neuron.jpg"], TeacherPictureFinder.WikipediaThumbnailUrls(Encoding.UTF8.GetBytes(reply)).Select(url => url.AbsoluteUri));
    }

    [Fact]
    public void TheSearchSendsOnlyTheWords()
    {
        var url = TeacherPictureFinder.CommonsSearchUrl("neuron diagram");
        Assert.Equal("commons.wikimedia.org", url.Host);
        var query = url.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        Assert.Equal("neuron diagram filetype:bitmap|drawing", query["gsrsearch"]);
        Assert.Contains("OpenClicky", TeacherPictureFinder.UserAgent);
    }

    // MARK: - tools

    [Fact]
    public void BothLanesGetTheSameTwoTools()
    {
        Assert.Equal(TeacherPlugin.ToolNames.Order(), TeacherPlugin.RealtimeToolDefinitions().Select(tool => tool["name"]!.GetValue<string>()).Order());
        Assert.Equal(TeacherPlugin.ToolNames.Order(), TeacherPlugin.AnthropicToolDefinitions().Select(tool => tool["name"]!.GetValue<string>()).Order());
        Assert.All(TeacherPlugin.AnthropicToolDefinitions(), tool => Assert.NotNull(tool["input_schema"]));
        // Gemini Live takes them too.
        Assert.All(TeacherPlugin.RealtimeToolDefinitions(), tool => Assert.NotNull(GeminiLiveDialect.FunctionDeclaration(tool)?["parameters"]));
    }

    [Fact]
    public void TheVoiceLaneDescribesTheDrawingAndTheAskLaneWritesIt()
    {
        static IEnumerable<string> Required(JsonObject? schema) => (schema?["required"] as JsonArray ?? []).Select(name => name!.GetValue<string>());
        var realtimeDraw = TeacherPlugin.RealtimeToolDefinitions().First(tool => tool["name"]!.GetValue<string>() == TeacherPlugin.DrawToolName);
        Assert.Equal(["title", "what_to_draw"], Required(realtimeDraw["parameters"] as JsonObject));
        var anthropicDraw = TeacherPlugin.AnthropicToolDefinitions().First(tool => tool["name"]!.GetValue<string>() == TeacherPlugin.DrawToolName);
        Assert.Equal(["title", "sketch"], Required(anthropicDraw["input_schema"] as JsonObject));
    }

    [Fact]
    public void AnotherToolIsNotTheTeachersBusiness() =>
        Assert.Null(TeacherPlugin.Perform("open_app", new Dictionary<string, string>(), null));

    [Fact]
    public void BothLanesPromptsCarryTheTeachingLineOnlyWhileTheTeacherIsOn()
    {
        Assert.Equal("", TeacherPlugin.PromptLines(false));
        var on = AskLanePrompts.AskSystemPrompt(false, false, "", "en", pluginPromptLines: TeacherPlugin.PromptLines(true));
        Assert.Contains(TeacherPlugin.PromptInstruction, on);
        // Straight after the companion prompt, before the reply language.
        Assert.True(on.IndexOf(TeacherPlugin.PromptInstruction, StringComparison.Ordinal) < on.IndexOf(ReplyLanguage.Instruction("en"), StringComparison.Ordinal));
        Assert.DoesNotContain(TeacherPlugin.PromptInstruction, AskLanePrompts.AskSystemPrompt(false, false, "", "en"));
    }

    [Fact]
    public void ARealtimeSessionOffersThePluginsToolsBesideItsOwn()
    {
        var update = RealtimeVoiceClient.BuildSessionUpdate(RealtimeTurnMode.PushToTalk, "x", null, "en", TeacherPlugin.RealtimeToolDefinitions());
        var names = ((update["session"] as JsonObject)!["tools"] as JsonArray)!.Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        Assert.Contains(TeacherPlugin.DrawToolName, names);
        Assert.Contains("point_at", names);
    }

    [Fact]
    public void ARealtimeCallsArgumentsAreReadAsStrings()
    {
        var arguments = TeacherPlugin.StringArguments(new JsonObject { ["title"] = "Refraction", ["count"] = 3, ["missing"] = null });
        Assert.Equal("Refraction", arguments["title"]);
        Assert.Equal("3", arguments["count"]);
        Assert.Equal("", arguments["missing"]);
    }

    [Fact]
    public void TheSketchWritersReplyIsReadAndUnfenced()
    {
        var text = TeacherSketchWriter.ChatCompletionText(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"role":"assistant","content":"```\nline 0 0 10 10\n```"}}]}"""));
        Assert.Equal("line 0 0 10 10", TeacherSketchWriter.WithoutCodeFences(text!));
        Assert.Null(TeacherSketchWriter.ChatCompletionText(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"  "}}]}""")));
        Assert.Contains("Draw: a ray", TeacherSketchWriter.UserPrompt("Refraction", "a ray"));
    }

    // MARK: - teaching questions

    [Fact]
    public void TeachingQuestionsAreToldApartFromScreenAndHowToQuestions()
    {
        string[] lessons =
        [
            "how does a rotatry engine work compared to a normal engine",
            "Teach me how the Pythagoras theorem works.",
            "teach me refraction",
            "how do vaccines work?",
            "explain how photosynthesis happens",
            "what's the difference between a series and a parallel circuit",
            "AC vs DC",
        ];
        string[] notLessons =
        [
            "how do I export this video",
            "how does this button work",
            "where is the save menu",
            "open safari",
            "what time is it in Tokyo",
            "how to make a folder on the desktop",
            "hi",
        ];
        Assert.All(lessons, question => Assert.True(TeacherQuestion.IsTeachingQuestion(question), question));
        Assert.All(notLessons, question => Assert.False(TeacherQuestion.IsTeachingQuestion(question), question));
    }

    [Fact]
    public void SolvingWhatIsOnTheScreenIsALessonThatNeedsTheScreenshot()
    {
        foreach (var question in new[] { "please solve this equaltion in the screen", "can you help me solve this", "work this out step by step" })
        {
            Assert.True(TeacherQuestion.IsTeachingQuestion(question), question);
            Assert.True(TeacherQuestion.NeedsTheScreen(question), question);
        }
        Assert.False(TeacherQuestion.NeedsTheScreen("how does a rotary engine work"));
        Assert.False(TeacherQuestion.IsProblemToSolve("how does a rotary engine work"));
    }

    // MARK: - when they cannot hear

    [Fact]
    public void AnswersAreWrittenWhenTheyCannotHear()
    {
        var loud = new TeacherListening.Output(0.8f, false);
        Assert.False(TeacherListening.AnswersShouldBeWritten(false, loud));
        Assert.True(TeacherListening.AnswersShouldBeWritten(true, loud));
        Assert.True(TeacherListening.AnswersShouldBeWritten(false, new TeacherListening.Output(0.8f, true)));
        Assert.True(TeacherListening.AnswersShouldBeWritten(false, new TeacherListening.Output(0.31f, false)));
        Assert.False(TeacherListening.AnswersShouldBeWritten(false, new TeacherListening.Output(null, false)));
    }

    [Fact]
    public void AWrittenAnswerWrapsIntoLeftAlignedLines()
    {
        const string answer = "Add the two equations: (x + y) + (2x - y) = 24 + (-6). That gives 3x = 18, so x = 6. Then 6 + y = 24, so y = 18.";
        var lines = TeacherNote.WrappedLines(answer);
        Assert.True(lines.Count >= 3);
        Assert.All(lines, line => Assert.True(line.Length <= TeacherNote.CharactersPerLine || !line.Contains(' ')));
        Assert.Equal(answer, string.Join(" ", lines));
        Assert.All(TeacherNote.Sketch(answer)!.Strokes, stroke => Assert.True(stroke.IsLeftAligned));
        var long_ = TeacherNote.WrappedLines(string.Concat(Enumerable.Repeat("word ", 400)));
        Assert.Equal(TeacherNote.MaximumLines, long_.Count);
        Assert.EndsWith("…", long_[^1]);
        Assert.Null(TeacherNote.Sketch("   "));
    }

    // MARK: - the lesson writer

    [Fact]
    public void TheScreenshotRidesAlongAsAnImage()
    {
        Assert.Equal("hi", TeacherSketchWriter.UserContent("hi", null).GetValue<string>());
        var parts = Assert.IsType<JsonArray>(TeacherSketchWriter.UserContent("hi", [1, 2]));
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("data:image/jpeg;base64,", parts[1]!["image_url"]!["url"]!.GetValue<string>());
        var body = TeacherSketchWriter.OpenAIBody("draw", null);
        Assert.Equal("gpt-5.5", body["model"]!.GetValue<string>());
        Assert.Equal("none", body["reasoning_effort"]!.GetValue<string>());
    }

    private static ProviderCredentials Credentials(string? openAI = null, string? anthropic = null, string? account = null, string? gemini = null, bool claudeCodePlan = false) =>
        new(openAI, anthropic, account, geminiKey: gemini, claudeCodePlanOn: claudeCodePlan);

    [Fact]
    public void LessonsAreWrittenWithoutOpenAITooButNeverOnAPlanAlone()
    {
        Assert.Equal(new TeacherSketchWriter.Route.OpenAI("sk-o"), TeacherSketchWriter.RouteFor(Credentials(openAI: "sk-o", anthropic: "sk-a", account: "t")));
        Assert.Equal(new TeacherSketchWriter.Route.Anthropic("sk-a"), TeacherSketchWriter.RouteFor(Credentials(anthropic: "sk-a", account: "t")));
        Assert.Equal(new TeacherSketchWriter.Route.Account(), TeacherSketchWriter.RouteFor(Credentials(account: "t", claudeCodePlan: true)));
        Assert.Equal(new TeacherSketchWriter.Route.AnswerSourceRoute(AnswerSource.Gemini), TeacherSketchWriter.RouteFor(Credentials(gemini: "g")));
        Assert.Null(TeacherSketchWriter.RouteFor(Credentials(claudeCodePlan: true)));
        Assert.Null(TeacherSketchWriter.RouteFor(Credentials()));
    }

    [Fact]
    public void ClaudeWritesWithThinkingOffAndRoomForADrawing()
    {
        var body = TeacherSketchWriter.ClaudeBody("draw", [1]);
        Assert.Equal("claude-sonnet-5-5", body["model"]!.GetValue<string>());
        Assert.Equal(1024, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("between_tools", body["thinking"]!["type"]!.GetValue<string>());
        var content = (body["messages"]![0]!["content"] as JsonArray)!;
        Assert.Equal(["image", "text"], content.Select(part => part!["type"]!.GetValue<string>()));
        Assert.Single((TeacherSketchWriter.ClaudeBody("draw", null)["messages"]![0]!["content"] as JsonArray)!);
    }

    [Fact]
    public void TheWritersReplyIsADrawingOrAPicture()
    {
        Assert.Equal(new TeacherSketchWriter.Reply.Sketch("line 0 0 1 1"), TeacherSketchWriter.Reply.Parse("```\nline 0 0 1 1\n```"));
        Assert.Equal(new TeacherSketchWriter.Reply.Picture("human heart diagram"), TeacherSketchWriter.Reply.Parse("PICTURE: human heart diagram"));
        Assert.Equal(new TeacherSketchWriter.Reply.Picture("neuron"), TeacherSketchWriter.Reply.Parse("picture: neuron"));
    }

    [Fact]
    public void TheTeacherIsAPluginWithASettingsRow()
    {
        Assert.True(TeacherPlugin.Component.IsPlugin);
        Assert.Equal(IslandPluginRegistry.TeacherId, TeacherPlugin.Component.Id);
        Assert.Contains("only the search words are sent", TeacherPlugin.Component.SettingsDetail);
        var settings = new IslandComponentSettings(Preferences.InMemory());
        Assert.False(settings.IsEnabled(TeacherPlugin.Id, IslandComponentRegistry.All));
        settings.SetPluginEnabled(TeacherPlugin.Id, true);
        Assert.True(settings.IsEnabled(TeacherPlugin.Id, IslandComponentRegistry.All));
    }
}
