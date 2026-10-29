//
//  TeacherPlugin.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherBoard.swift's TeacherPlugin, and TeacherIslandView.swift's component)
//
//  The Teacher plugin: "teach me how Pythagoras works" draws the lesson on the screen, in the
//  largest empty space, while Clicky explains it; what a drawing cannot show comes up as a real
//  picture. Both voice lanes get two tools while the plugin is on (draw_on_screen, show_picture);
//  TeacherPlugin is the only thing the rest of the app calls. The board itself (TeacherBoard) is
//  a click-through window under the pointer's overlay, with a small close button beside it; it
//  goes away on esc, the close button, the island's "clear", or the next question.
//

using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Plugins.Teacher;

/// <summary>What the rest of the app knows of the Teacher: whether it is on, its tools and prompt, and how to run a
/// tool call or clear the board.</summary>
public static class TeacherPlugin
{
    public const string Id = IslandPluginRegistry.TeacherId;
    public const string DrawToolName = "draw_on_screen";
    public const string PictureToolName = "show_picture";
    public static IReadOnlySet<string> ToolNames { get; } = new HashSet<string> { DrawToolName, PictureToolName };

    /// <summary>The Teacher's row on the island: what is on the board and "clear", or what to ask.</summary>
    public static IslandComponent Component { get; } = new(
        Id, "Teacher", "", 64, IsPlugin: true,
        SettingsDetail: "Ask \"teach me how Pythagoras works\" and Clicky draws it in handwriting in an empty part of your screen while it explains. What a drawing can't show comes up as a picture from Wikimedia Commons (only the search words are sent).");

    /// <summary>The preferences the plugin switch is read from (tests swap in their own).</summary>
    public static Func<Preferences> Preferences { get; set; } = () => Configuration.Preferences.Shared;

    public static bool IsEnabled => new IslandComponentSettings(Preferences()).EnabledPlugins.Contains(Id);

    public const string PromptInstruction =
        "teaching: when they ask you to teach or explain an idea that a picture makes clearer (a theorem, a shape, a graph, how light bends, a cycle, forces, a circuit), draw it on their screen with "
        + DrawToolName + " first, before you explain, then explain it part by part in the order you drew it. it appears in handwriting in an empty part of their screen, so label every part (a, b, c, angles, names) and use arrows with short notes to point things out. keep it simple, like a good teacher on a whiteboard: one clear diagram, no more than about twenty five commands. a lesson may be longer than your usual answers, up to about eighty words, walking through the drawing; don't use point_at or point tags for it, the pointer follows the labels on its own. when a drawing can't show it well (a real animal, an organ, a place, a famous painting, detailed apparatus), call "
        + PictureToolName + " with a short search phrase instead. don't draw for ordinary questions or for how-to steps on screen; those use pointing. never list or read out what you would draw instead of calling the tool; the drawing may already be appearing, so explain the idea while it does.";

    public static readonly string SketchGrammar = """
        One command per line (or separated by ";"). Board units, origin top-left, y grows downward; default board 400 by 300.
        board W H — optional first line to change the board's size.
        line x1 y1 x2 y2 · arrow x1 y1 x2 y2 (head at the second point) · curve x1 y1 x2 y2 x3 y3 … (smooth freehand) · shape x1 y1 x2 y2 x3 y3 … (closed outline) · circle cx cy r · arc cx cy r from to (degrees, 0 right, 90 down) · box x y w h · right_angle vx vy ax ay bx by (square mark at vertex v between the arms to a and b) · angle vx vy ax ay bx by (arc marking that angle; use this, not arc, to mark an angle) · dot x y · text x y "words" (handwriting centred on x y) · pause seconds.
        After the numbers, any of: a colour (ink, red, blue, green, orange, purple, grey), dashed, fill, thick, thin; for text, big or small, and left to start the words at x instead of centring them on it.
        Plan it like a textbook figure, using most of the board: first the setting (the surface or boundary between two materials, the block, the triangle, the axes), then what moves or acts across it (rays, forces, arrows), then labels last. Every line must be physically and mathematically right; work out the coordinates before you write them. Put each label just beside the thing it names, never on top of a line, and keep words short. A title goes at the very top, with nothing else within 50 units below it. Use at most 25 commands: a few clear parts beat many small ones. For a comparison, draw the two side by side, each with its own small heading.
        Example, Pythagoras:
        shape 80 240 320 240 80 60
        right_angle 80 240 320 240 80 60
        text 200 266 "b" blue big
        text 58 150 "a" blue big
        text 222 124 "c" red big
        arrow 330 95 240 122 red
        text 335 80 "hypotenuse" red
        text 200 28 "a² + b² = c²" big
        """.ReplaceLineEndings("\n");

    /// <summary>The prompt lines the plugin adds to both voice lanes while it is on.</summary>
    public static string PromptLines(bool isEnabled) => isEnabled ? "\n\n" + PromptInstruction : "";

    // MARK: - Tools

    public static IReadOnlyList<JsonObject> RealtimeToolDefinitions() =>
    [
        new JsonObject { ["type"] = "function", ["name"] = DrawToolName, ["description"] = RealtimeDrawToolDescription, ["parameters"] = RealtimeDrawParameters() },
        new JsonObject { ["type"] = "function", ["name"] = PictureToolName, ["description"] = PictureToolDescription, ["parameters"] = PictureParameters() },
    ];

    public static IReadOnlyList<JsonObject> AnthropicToolDefinitions() =>
    [
        new JsonObject { ["name"] = DrawToolName, ["description"] = DrawToolDescription, ["input_schema"] = DrawParameters() },
        new JsonObject { ["name"] = PictureToolName, ["description"] = PictureToolDescription, ["input_schema"] = PictureParameters() },
    ];

    private static string DrawToolDescription =>
        "Draw a teaching diagram on the user's screen, in handwriting, in an empty area; it draws itself stroke by stroke while you explain. " + SketchGrammar;

    /// <summary>The voice model describes the drawing and TeacherSketchWriter writes it: asked for coordinates itself,
    /// the Realtime model drew refraction with the ray down the normal.</summary>
    private const string RealtimeDrawToolDescription =
        "Draw a teaching diagram on the user's screen, in handwriting, in an empty area. Describe what to draw in words, never coordinates; a drawing model draws it stroke by stroke, appearing a few seconds after the call.";

    private const string PictureToolDescription =
        "Show a real picture (from Wikimedia Commons or Wikipedia) on the user's screen when a drawing cannot show the idea well.";

    private static JsonObject ObjectSchema(params (string Name, string Description)[] properties)
    {
        var schemaProperties = new JsonObject();
        foreach (var (name, description) in properties) schemaProperties[name] = new JsonObject { ["type"] = "string", ["description"] = description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = schemaProperties,
            ["required"] = new JsonArray(properties.Select(property => (JsonNode)JsonValue.Create(property.Name)!).ToArray()),
        };
    }

    private static JsonObject RealtimeDrawParameters() => ObjectSchema(
        ("title", "What the drawing shows, 1-5 words, e.g. 'Refraction of light'."),
        ("what_to_draw", "One or two sentences: the setting, each part and its label, and what must be right, e.g. 'light going from air into water: the boundary, the dashed normal, the incident ray, the refracted ray bending toward the normal, angles i and r marked'."));

    private static JsonObject DrawParameters() => ObjectSchema(
        ("title", "What the drawing shows, 1-5 words, e.g. 'Pythagoras theorem'."),
        ("sketch", "The drawing commands."));

    private static JsonObject PictureParameters() => ObjectSchema(
        ("query", "A short search phrase in English, e.g. 'neuron diagram'."),
        ("caption", "A few words written under the picture."));

    // MARK: - Running

    /// <summary>
    /// Runs a teacher tool call and returns what the model is told; null when <paramref name="toolName"/> is not one of
    /// the Teacher's tools. <paramref name="capture"/> is the screenshot the question carried (the cursor's screen),
    /// used to find empty space. Returns at once: a drawing to write or a picture to find is done in the background
    /// (the Realtime lane handles tool calls on its receive loop, and waiting there would stall the voice).
    /// </summary>
    public static string? Perform(string toolName, IReadOnlyDictionary<string, string> arguments, CompanionScreenCapture? capture)
    {
        if (!ToolNames.Contains(toolName)) return null;
        if (!IsEnabled) return "the teacher plugin is off; explain in words";
        var board = TeacherBoard.Shared;
        // The app already started this question's lesson (StartLesson): one drawing per question.
        if (board.QuestionHasLesson)
        {
            return "the drawing for this is already appearing on their screen; explain the idea part by part, as a teacher walking through a whiteboard diagram";
        }
        board.QuestionHasLesson = true;
        string Argument(string name) => arguments.TryGetValue(name, out var value) ? value : "";
        if (toolName == DrawToolName)
        {
            var source = Argument("sketch");
            var title = Argument("title").Trim();
            var whatToDraw = Argument("what_to_draw").Trim();
            if (source.Trim().Length == 0 && whatToDraw.Length > 0)
            {
                // Written in the background, for the same reason as a picture search below.
                _ = DrawDescriptionAsync(board, title, whatToDraw, capture, board.BeginBackgroundJob());
                return "the drawing appears on their screen in a few seconds; start explaining now, part by part in the order you described it";
            }
            var result = TeacherSketchParser.Parse(source);
            if (result.Sketch is not { } sketch)
            {
                AppLog.Write($"teacher: nothing drawable in the sketch ({result.SkippedLines.Count} lines skipped)");
                return "could not read the drawing; use the commands described in the tool, one per line";
            }
            if (result.SkippedLines.Count > 0)
            {
                AppLog.Write($"teacher: skipped {result.SkippedLines.Count} sketch lines");
            }
            board.Show(sketch, title, capture);
            return $"drawing it on their screen now ({sketch.Strokes.Count} strokes); explain it part by part in the order you drew it";
        }
        var query = Argument("query");
        var caption = Argument("caption").Trim();
        _ = ShowPictureAsync(board, query, caption, capture, board.BeginBackgroundJob());
        return $"a picture of {(caption.Length == 0 ? query : caption)} appears on their screen in a moment; describe what it shows";
    }

    private static async Task DrawDescriptionAsync(TeacherBoard board, string title, string whatToDraw, CompanionScreenCapture? capture, int requestToken)
    {
        var written = await TeacherSketchWriter.WriteAsync(title, whatToDraw);
        board.Post(() =>
        {
            if (written is null || TeacherSketchParser.Parse(written).Sketch is not { } sketch)
            {
                AppLog.Write("teacher: no drawing for the description");
                return;
            }
            if (!board.Show(sketch, title, capture, requestToken)) AppLog.Write("teacher: drawing arrived after the board moved on; not shown");
        });
    }

    private static async Task ShowPictureAsync(TeacherBoard board, string query, string caption, CompanionScreenCapture? capture, int requestToken)
    {
        var picture = await TeacherPictureFinder.FindAsync(query, caption, board.ImageSize);
        if (picture is null) return;
        board.Post(() =>
        {
            if (!board.Show(picture, capture, requestToken)) AppLog.Write("teacher: picture arrived after the board moved on; not shown");
        });
    }

    /// <summary>
    /// Starts the lesson for a question as soon as it is known, without waiting for the model to call draw_on_screen
    /// (the Realtime model sometimes describes the drawing in words instead). Only for teaching questions
    /// (TeacherQuestion), and only where the drawing is written fast (TeacherSketchWriter.CanStartLessonsEarly); a tool
    /// call that comes later is told the drawing is on its way.
    /// </summary>
    public static void StartLesson(string question, CompanionScreenCapture? capture)
    {
        var board = TeacherBoard.Shared;
        if (!IsEnabled || board.QuestionHasLesson || !TeacherQuestion.IsTeachingQuestion(question) || !TeacherSketchWriter.CanStartLessonsEarly) return;
        board.QuestionHasLesson = true;
        AppLog.Write("teacher: a teaching question; starting the lesson");
        var trimmed = question.Trim();
        var title = trimmed.Length > 60 ? trimmed[..60] : trimmed;
        // "Solve this": the writer reads the problem off the same screenshot the question carried.
        var screenshotJpeg = TeacherQuestion.NeedsTheScreen(question) ? capture?.ImageData : null;
        _ = WriteLessonAsync(board, question, title, screenshotJpeg, capture, board.BeginBackgroundJob());
    }

    private static async Task WriteLessonAsync(TeacherBoard board, string question, string title, byte[]? screenshotJpeg,
                                               CompanionScreenCapture? capture, int requestToken)
    {
        switch (await TeacherSketchWriter.LessonAsync(question, screenshotJpeg))
        {
            case TeacherSketchWriter.Reply.Sketch written:
                board.Post(() =>
                {
                    var result = TeacherSketchParser.Parse(written.Commands);
                    if (result.Sketch is not { } sketch)
                    {
                        AppLog.Write($"teacher: nothing drawable in the written lesson ({result.SkippedLines.Count} lines skipped)");
                        return;
                    }
                    if (!board.Show(sketch, title, capture, requestToken)) AppLog.Write("teacher: lesson arrived after the board moved on; not shown");
                });
                break;
            case TeacherSketchWriter.Reply.Picture picture:
                await ShowPictureAsync(board, picture.Query, picture.Query, capture, requestToken);
                break;
            default:
                AppLog.Write("teacher: no lesson written for the question");
                break;
        }
    }

    /// <summary>When they cannot hear (TeacherListening), a reply that brought no lesson is written on the screen as a
    /// handwritten note, so a muted or noisy room still gets the answer.</summary>
    public static void WriteAnswerIfTheyCannotHear(string answer, bool questionWasTyped, CompanionScreenCapture? capture)
    {
        var board = TeacherBoard.Shared;
        if (!IsEnabled || board.QuestionHasLesson) return;
        var output = board.CurrentOutput();
        if (!TeacherListening.AnswersShouldBeWritten(questionWasTyped, output) || TeacherNote.Sketch(answer.Trim()) is not { } note) return;
        board.QuestionHasLesson = true;
        var volume = output.Volume is { } level ? $"{level * 100:0}%" : "unknown";
        AppLog.Write($"teacher: writing the answer down (typed {questionWasTyped}, volume {volume}, muted {output.IsMuted})");
        board.Show(note, "the answer", capture);
    }

    public static bool ClearBoard(string reason) => TeacherBoard.Shared.Clear(reason);

    /// <summary>Esc takes the lesson off the screen whatever else it stops (the ask lane, the fast voice, or nothing),
    /// as on the Mac; a dictation take in progress keeps esc to itself.</summary>
    public static bool EscClearsTheBoard(EscapeTarget target) => target != EscapeTarget.DictationTake;

    /// <summary>A Realtime tool call's arguments as the strings the Teacher reads.</summary>
    public static IReadOnlyDictionary<string, string> StringArguments(JsonObject arguments) =>
        arguments.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            null => "",
            var other => other.ToJsonString(),
        });
}
