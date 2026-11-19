//
//  DrawStageSmoke.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Draws a program on the cursor's screen without a voice turn, then exits: the stage's own sample
//  (Assets/DrawStage/samples/rocket.js) or a program file, streamed a few characters at a time the
//  way a model's answer arrives. --speech plays the voice's part (each line asked for, started and
//  ended on a clock) instead of the page's estimate. The stage is left capturable so `--shot` can
//  save the screen with the drawing on it. Exits 1 if the page reported an error.
//
//  reply:<file> and lesson:<question> run a whole drawing answer (DrawingAnswer) on a screenshot of
//  the cursor's screen, as a question does: a written model reply streamed in (the sample is
//  samples/heart-reply.md), or the answer model's own reply to the question (needs a key or an
//  account in shell.json). Its lines are said by this PC's voice with --voice, else on a silent
//  clock. --then reply:<file> answers a follow-up about the drawing (keep() adds to it), and
//  --save saves the drawing at the end, as a question's would be (Videos\OpenClicky). --pieces offers
//  the model 3D pieces (f.piece) whatever Settings say; they are made by the maker Settings pick.
//  --program <file.js> writes the program the model wrote (a follow-up's after it), to read or replay.
//
//    OpenClicky --openclicky-drawstage-smoke [program.js] [--speech] [--shot <file.png>]
//    OpenClicky --openclicky-drawstage-smoke reply:<file> | lesson:<question> [--then reply:<file>] [--voice] [--save] [--language <code>] [--shot <file.png>]
//                                            [--program <file.js>]
//
//  --language asks the lesson in that language (ReplyLanguage: ur for Urdu), whatever Settings say.
//

using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Core.Speech;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Plugins.DrawStage;

internal static class DrawStageSmoke
{
    public const string Flag = "--openclicky-drawstage-smoke";

    /// <summary>The canvas the sample is drawn on is this wide (a screenshot's width); its height follows the screen.</summary>
    private const int CanvasWidth = 1280;
    private const int ChunkCharacters = 40;
    private static readonly TimeSpan ChunkInterval = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>The lesson's language: --language, else Settings' own.</summary>
    private static string languageCode = "en";

    public static int Run(string[] args)
    {
        ConsoleAttachment.Attach();
        var flagIndex = Array.IndexOf(args, Flag);
        var programPath = flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith("--", StringComparison.Ordinal) ? args[flagIndex + 1] : null;
        var shotIndex = Array.IndexOf(args, "--shot");
        var shotPath = shotIndex >= 0 && shotIndex + 1 < args.Length ? args[shotIndex + 1] : null;
        var pace = args.Contains("--speech") ? DrawStagePace.Speech : DrawStagePace.Estimate;
        var languageIndex = Array.IndexOf(args, "--language");
        languageCode = languageIndex >= 0 && languageIndex + 1 < args.Length ? args[languageIndex + 1] : ReplyLanguage.CurrentCode;

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        application.Startup += async (_, _) =>
        {
            try
            {
                var thenIndex = Array.IndexOf(args, "--then");
                var followUp = thenIndex >= 0 && thenIndex + 1 < args.Length ? args[thenIndex + 1] : null;
                var programIndex = Array.IndexOf(args, "--program");
                var programOut = programIndex >= 0 && programIndex + 1 < args.Length ? args[programIndex + 1] : null;
                exitCode = programPath is not null && (programPath.StartsWith("reply:", StringComparison.Ordinal) || programPath.StartsWith("lesson:", StringComparison.Ordinal))
                    ? await RunAnswerAsync(programPath, followUp, shotPath, args.Contains("--voice"), args.Contains("--save"), args.Contains("--pieces"), programOut)
                    : await RunAsync(programPath, shotPath, pace);
            }
            catch (Exception error)
            {
                Console.WriteLine($"smoke: failed: {error}");
                exitCode = 1;
            }
            application.Shutdown(exitCode);
        };
        application.Run();
        return exitCode;
    }

    /// <summary>The screen with the drawing on it, saved as a PNG: the frame DWM composed (<see cref="MonitorFrameCapture"/>),
    /// else a BitBlt grab. BitBlt alone tears over the stage now and then (a block shows the desktop behind it).</summary>
    private static async Task SaveShotAsync(string shotPath, PixelRect screenBounds, ScreenCapture fallback)
    {
        var frame = await MonitorFrameCapture.CapturePngAsync(screenBounds);
        var how = "composed frame";
        if (frame is null)
        {
            var shots = await fallback.CaptureAllScreensAsync(new ScreenCaptureOptions { MaxDimension = 0, Format = ScreenImageFormat.Png, CursorScreenOnly = true });
            frame = shots.Count > 0 ? shots[0].ImageData : null;
            how = "BitBlt";
        }
        if (frame is null) { Console.WriteLine("smoke: no screen to save"); return; }
        await File.WriteAllBytesAsync(shotPath, frame);
        Console.WriteLine($"smoke: screen saved to {shotPath} ({how})");
    }

    private static async Task<int> RunAsync(string? programPath, string? shotPath, DrawStagePace pace)
    {
        FloatingWindowSupport.AllowsScreenCapture = true;
        using var screens = new Screens();
        if (screens.CursorScreen() is not { } screen)
        {
            Console.WriteLine("smoke: no screen");
            return 1;
        }
        var program = await File.ReadAllTextAsync(programPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "DrawStage", "samples", "rocket.js"));

        using var window = new DrawStageWindow();
        var stage = DrawStageController.Shared;
        stage.Surface = window;
        stage.MainThread = new DispatcherMainThread(Application.Current.Dispatcher);
        stage.FindPhoto = DrawStagePhotos.Finder(() => null);
        var errors = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stage.Logged += line =>
        {
            if (line.StartsWith("error: ", StringComparison.Ordinal)) errors++;
            Console.WriteLine($"smoke: page: {line}");
        };
        stage.Finished += () => finished.TrySetResult();
        if (pace == DrawStagePace.Speech) stage.LineDue += (k, text) => _ = SpeakAsync(stage, k, text);

        var canvasHeight = (int)Math.Round(CanvasWidth * screen.Bounds.Height / (double)Math.Max(1, screen.Bounds.Width));
        Console.WriteLine($"smoke: stage over {screen.DeviceName} {screen.Bounds.Width}×{screen.Bounds.Height} at {screen.Scale:0.##}x, canvas {CanvasWidth}×{canvasHeight}, pace {pace}");
        var started = DateTime.UtcNow;
        stage.Begin(screen.Bounds, screen.Scale, CanvasWidth, canvasHeight, pace);

        for (var length = ChunkCharacters; length < program.Length; length += ChunkCharacters)
        {
            stage.Feed(program[..length], done: false);
            await Task.Delay(ChunkInterval);
        }
        stage.Feed(program, done: true);
        Console.WriteLine($"smoke: streamed {program.Length} characters");

        if (await Task.WhenAny(finished.Task, Task.Delay(Timeout)) != finished.Task)
        {
            Console.WriteLine(window.StartFailure is { } failure ? $"smoke: the stage did not start: {failure}" : "smoke: timed out waiting for the drawing to finish");
            return 1;
        }
        Console.WriteLine($"smoke: finished drawing in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");

        if (shotPath is not null) await SaveShotAsync(shotPath, screen.Bounds, new ScreenCapture(screens));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        stage.Clear("smoke done");
        stage.Surface = null;
        return errors == 0 ? 0 : 1;
    }

    /// <summary>A whole drawing answer, as a question gets one: on the cursor screen's screenshot, spoken line by line; then a
    /// follow-up about it, and saving it, when asked for.</summary>
    private static async Task<int> RunAnswerAsync(string argument, string? followUpArgument, string? shotPath, bool withVoice, bool save, bool pieces,
        string? programOut)
    {
        FloatingWindowSupport.AllowsScreenCapture = true;
        SpeechVoiceServices.Current = SpeechVoiceServices.ForThisPC(new WindowsSystemVoice());
        using var screens = new Screens();
        var screenCapture = new ScreenCapture(screens);
        // Taken before the stage is up, as a question's is.
        if (await screenCapture.CaptureCursorScreenAsync() is not { } capture)
        {
            Console.WriteLine("smoke: no screenshot");
            return 1;
        }
        // A written reply streamed in, or the answer model's own reply to a question (a follow-up's prompt has the drawing).
        Func<Action<string>, CancellationToken, Task<string>> WriterFor(string source, DrawnAnswer? drawingUp)
        {
            if (source.StartsWith("reply:", StringComparison.Ordinal))
            {
                var reply = File.ReadAllText(source["reply:".Length..]);
                return async (onReplySoFar, token) =>
                {
                    for (var length = ChunkCharacters; length < reply.Length; length += ChunkCharacters)
                    {
                        onReplySoFar(reply[..length]);
                        await Task.Delay(ChunkInterval, token);
                    }
                    onReplySoFar(reply);
                    return reply;
                };
            }
            var question = source["lesson:".Length..];
            Console.WriteLine($"smoke: asking the answer model (wants a drawing: {DrawStageLesson.WantsADrawing(question)})");
            var answers = DrawStageLesson.WriterFor(OpenClickyConfiguration.Settings, new ClaudeAPI(() => $"{OpenClickyConfiguration.BackendBaseUrl}/chat"));
            return async (onReplySoFar, token) => (await answers.AnalyzeImageStreamingAsync(
                [Core.Companion.CompanionManager.ImageLabeledWithItsPixelSize(capture)],
                DrawStageLesson.SystemPrompt(capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels,
                    DrawStageLesson.DetailFrom(OpenClickyConfiguration.Settings.DrawingDetail), OpenClickyConfiguration.Settings.DrawingInstructions,
                    pieces || DrawStageLesson.OffersPieces(OpenClickyConfiguration.Settings), languageCode),
                null,
                drawingUp is null ? DrawStageLesson.UserPrompt(question) : DrawStageLesson.FollowUpPrompt(question, drawingUp),
                null,
                onReplySoFar,
                token,
                DrawStageLesson.OutputTokens(DrawStageLesson.DetailFrom(OpenClickyConfiguration.Settings.DrawingDetail)),
                forDrawing: true)).Text;
        }

        // This PC's voice, or a silent one that takes as long as reading the line at 2.6 words a second. The voice's audio
        // is kept with the drawing, as a question's is.
        var player = new AudioPlayer();
        var audioByLine = new ConcurrentDictionary<string, byte[]>();
        SpeechQueue MakeSpeech()
        {
            var speech = withVoice
                ? new SpeechQueue(async (text, token) => audioByLine[text] = await CloudVoiceSpeaker.AudioAsync(text, SpeechVoiceChoice.Mac, cancellationToken: token),
                    (audio, token) => player.PlayToCompletionAsync(audio, token))
                : new SpeechQueue((text, _) => Task.FromResult(BitConverter.GetBytes(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)),
                    (audio, token) => Task.Delay(TimeSpan.FromSeconds(Math.Max(0.8, BitConverter.ToInt32(audio) / 2.6)), token));
            speech.PieceStarted += k => Console.WriteLine($"smoke: voice: line {k} starts");
            return speech;
        }

        using var window = new DrawStageWindow();
        var stage = DrawStageController.Shared;
        stage.Surface = window;
        stage.MainThread = new DispatcherMainThread(Application.Current.Dispatcher);
        stage.FindPhoto = DrawStagePhotos.Finder(() => null);
        var errors = 0;
        stage.Logged += line => { if (line.StartsWith("error: ", StringComparison.Ordinal)) errors++; };
        stage.LineAdded += (k, text) => Console.WriteLine($"smoke: line {k}: {text}");
        // As in the app: the Quiz me button unless Settings turns quizzes off, and the lines a quiz would say.
        stage.OffersQuiz = () => DrawStageLesson.QuizFrom(OpenClickyConfiguration.Settings.DrawingQuiz) != DrawingQuiz.Off;
        stage.SayRequested += text => Console.WriteLine($"smoke: say: {text}");
        stage.Logged += line => { if (line.StartsWith("piece", StringComparison.Ordinal) || line.StartsWith("no piece", StringComparison.Ordinal)) Console.WriteLine($"smoke: {line}"); };
        // Pieces (f.piece) are made as a question's are: by the maker Settings picks, the drawing model when it fails.
        var pieceWorkshop = new PieceWorkshop(() => PieceMakers.For(OpenClickyConfiguration.Settings,
            DrawStageLesson.WriterFor(OpenClickyConfiguration.Settings, new ClaudeAPI(() => $"{OpenClickyConfiguration.BackendBaseUrl}/chat")),
            () => Core.Providers.ProviderRouter.CurrentCredentials().OpenAIKey));
        var piecesAsked = new List<Task<DrawStagePiece?>>();
        stage.MakePiece = request => { var made = pieceWorkshop.MakeAsync(request); lock (piecesAsked) piecesAsked.Add(made); return made; };
        var mainThread = Application.Current.Dispatcher;
        var started = DateTime.UtcNow;
        Console.WriteLine($"smoke: canvas {capture.ScreenshotWidthInPixels}×{capture.ScreenshotHeightInPixels} over {capture.DeviceName}, voice {(withVoice ? "this PC's" : "silent")}");
        using var timeout = new CancellationTokenSource(DrawingAnswer.Limit * 2 + PieceWorkshop.Limit);
        DrawnAnswer? drawing = null;
        var drew = true;
        foreach (var (source, question) in new[] { (argument, "smoke question"), (followUpArgument, "smoke follow-up") })
        {
            if (source is null || !drew) continue;
            var drawingUp = drawing;
            var result = await DrawingAnswer.RunAsync(stage, capture, WriterFor(source, drawingUp), MakeSpeech(),
                action => mainThread.BeginInvoke(action),
                onFirstAudio: () => Console.WriteLine($"smoke: voice started after {(DateTime.UtcNow - started).TotalSeconds:0.0} s"),
                line => Console.WriteLine($"smoke: {line}"),
                timeout.Token,
                followUp: drawingUp);
            Console.WriteLine($"smoke: drew {result.Drew}, {result.SpokenLines.Count} lines, {result.ProgramCharacters} chars, in {(DateTime.UtcNow - started).TotalSeconds:0.0} s"
                + (drawingUp is null ? "" : result.KeptTheDrawing ? ", kept the drawing" : ", drew afresh")
                + (result.UnspokenAfterVoiceFailure is { } unspoken ? $", voice failed with {unspoken.Count} unsaid" : ""));
            if (programOut is not null && result.Program.Length > 0)
            {
                var written = drawingUp is null ? result.Program : "\n// the follow-up\n" + result.Program;
                if (drawingUp is null) await File.WriteAllTextAsync(programOut, written); else await File.AppendAllTextAsync(programOut, written);
                Console.WriteLine($"smoke: program written to {programOut}");
            }
            drew = result.Drew;
            if (!drew) break;
            var lines = result.DrawnLines(text => withVoice && audioByLine.TryGetValue(text, out var audio) ? audio : null);
            drawing = result.KeptTheDrawing && drawingUp is not null
                ? drawingUp.AddedTo(question, result.Program, lines)
                : new DrawnAnswer([question], result.Program, capture.DisplayFrame, capture.DisplayScale, capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels, lines);
            stage.Remember(drawing);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        // Pieces made apart may still be arriving after the last line: they get until the workshop's limit to land.
        if (pieces && drawing is not null)
        {
            Task[] waiting;
            lock (piecesAsked) waiting = [.. piecesAsked];
            await Task.WhenAny(Task.WhenAll(waiting), Task.Delay(PieceWorkshop.Limit));
            await Task.Delay(TimeSpan.FromSeconds(3));
            Console.WriteLine($"smoke: {waiting.Length} pieces asked for, all in after {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
        }
        if (save && drawing is not null)
        {
            var recorder = new DrawingRecorder(window, stage) { RevealWhenSaved = false };
            var savingStarted = DateTime.UtcNow;
            var lastSaid = "";
            var saved = await recorder.SaveAsync(drawing, DrawingSaveKind.Both, progress =>
            {
                // Every tenth of the way.
                var said = progress.Fraction is { } fraction ? $"{progress.Text} {(int)(fraction * 10) * 10}%" : progress.Text;
                if (said != lastSaid) Console.WriteLine($"smoke: save: {said}");
                lastSaid = said;
                // Shown beside the drawing as a question's save is (it must stay out of the video).
                mainThread.BeginInvoke(() => stage.ShowSaving(progress));
            }, timeout.Token);
            stage.ShowSaved(saved.Failure is null ? "Saved" : "Couldn't save it", failed: saved.Failure is not null);
            Console.WriteLine($"smoke: saved in {(DateTime.UtcNow - savingStarted).TotalSeconds:0.0} s: picture {saved.PicturePath ?? "none"}, video {saved.VideoPath ?? "none"}"
                + (saved.Failure is { } failure ? $", failed: {failure}" : ""));
            if (saved.VideoPath is null) errors++;
        }
        if (shotPath is not null && drew) await SaveShotAsync(shotPath, capture.DisplayFrame, screenCapture);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        stage.Clear("smoke done");
        stage.Surface = null;
        return drew && errors == 0 ? 0 : 1;
    }

    /// <summary>The voice's part for one line: it starts now and lasts as long as reading it at 2.6 words a second.</summary>
    private static async Task SpeakAsync(DrawStageController stage, int k, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        Console.WriteLine($"smoke: voice: line {k} \"{text}\"");
        stage.LineStarted(k);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.8, words / 2.6)));
        stage.LineEnded(k);
    }
}
