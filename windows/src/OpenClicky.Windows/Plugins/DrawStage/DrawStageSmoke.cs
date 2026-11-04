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
//  clock.
//
//    OpenClicky --openclicky-drawstage-smoke [program.js] [--speech] [--shot <file.png>]
//    OpenClicky --openclicky-drawstage-smoke reply:<file> | lesson:<question> [--voice] [--shot <file.png>]
//

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

    public static int Run(string[] args)
    {
        ConsoleAttachment.Attach();
        var flagIndex = Array.IndexOf(args, Flag);
        var programPath = flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith("--", StringComparison.Ordinal) ? args[flagIndex + 1] : null;
        var shotIndex = Array.IndexOf(args, "--shot");
        var shotPath = shotIndex >= 0 && shotIndex + 1 < args.Length ? args[shotIndex + 1] : null;
        var pace = args.Contains("--speech") ? DrawStagePace.Speech : DrawStagePace.Estimate;

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        application.Startup += async (_, _) =>
        {
            try
            {
                exitCode = programPath is not null && (programPath.StartsWith("reply:", StringComparison.Ordinal) || programPath.StartsWith("lesson:", StringComparison.Ordinal))
                    ? await RunAnswerAsync(programPath, shotPath, args.Contains("--voice"))
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

        if (shotPath is not null)
        {
            var screenCapture = new ScreenCapture(screens);
            var shots = await screenCapture.CaptureAllScreensAsync(new ScreenCaptureOptions { MaxDimension = 0, Format = ScreenImageFormat.Png, CursorScreenOnly = true });
            if (shots.Count > 0)
            {
                await File.WriteAllBytesAsync(shotPath, shots[0].ImageData);
                Console.WriteLine($"smoke: screen saved to {shotPath}");
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        stage.Clear("smoke done");
        stage.Surface = null;
        return errors == 0 ? 0 : 1;
    }

    /// <summary>A whole drawing answer, as a question gets one: on the cursor screen's screenshot, spoken line by line.</summary>
    private static async Task<int> RunAnswerAsync(string argument, string? shotPath, bool withVoice)
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
        Func<Action<string>, CancellationToken, Task<string>> writeReply;
        if (argument.StartsWith("reply:", StringComparison.Ordinal))
        {
            var reply = await File.ReadAllTextAsync(argument["reply:".Length..]);
            writeReply = async (onReplySoFar, token) =>
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
        else
        {
            var question = argument["lesson:".Length..];
            Console.WriteLine($"smoke: asking the answer model (wants a drawing: {DrawStageLesson.WantsADrawing(question)})");
            var answers = DrawStageLesson.WriterFor(OpenClickyConfiguration.Settings, new ClaudeAPI(() => $"{OpenClickyConfiguration.BackendBaseUrl}/chat"));
            writeReply = async (onReplySoFar, token) => (await answers.AnalyzeImageStreamingAsync(
                [Core.Companion.CompanionManager.ImageLabeledWithItsPixelSize(capture)],
                DrawStageLesson.SystemPrompt(capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels),
                null,
                DrawStageLesson.UserPrompt(question),
                null,
                onReplySoFar,
                token,
                DrawStageLesson.MaximumOutputTokens)).Text;
        }

        // This PC's voice, or a silent one that takes as long as reading the line at 2.6 words a second.
        var player = new AudioPlayer();
        var speech = withVoice
            ? new SpeechQueue((text, token) => CloudVoiceSpeaker.AudioAsync(text, SpeechVoiceChoice.Mac, cancellationToken: token),
                (audio, token) => player.PlayToCompletionAsync(audio, token))
            : new SpeechQueue((text, _) => Task.FromResult(BitConverter.GetBytes(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)),
                (audio, token) => Task.Delay(TimeSpan.FromSeconds(Math.Max(0.8, BitConverter.ToInt32(audio) / 2.6)), token));
        speech.PieceStarted += k => Console.WriteLine($"smoke: voice: line {k} starts");

        using var window = new DrawStageWindow();
        var stage = DrawStageController.Shared;
        stage.Surface = window;
        var errors = 0;
        stage.Logged += line => { if (line.StartsWith("error: ", StringComparison.Ordinal)) errors++; };
        stage.LineAdded += (k, text) => Console.WriteLine($"smoke: line {k}: {text}");
        var mainThread = Application.Current.Dispatcher;
        var started = DateTime.UtcNow;
        Console.WriteLine($"smoke: canvas {capture.ScreenshotWidthInPixels}×{capture.ScreenshotHeightInPixels} over {capture.DeviceName}, voice {(withVoice ? "this PC's" : "silent")}");
        using var timeout = new CancellationTokenSource(DrawingAnswer.Limit);
        var result = await DrawingAnswer.RunAsync(stage, capture, writeReply, speech,
            action => mainThread.BeginInvoke(action),
            onFirstAudio: () => Console.WriteLine($"smoke: voice started after {(DateTime.UtcNow - started).TotalSeconds:0.0} s"),
            line => Console.WriteLine($"smoke: {line}"),
            timeout.Token);
        Console.WriteLine($"smoke: drew {result.Drew}, {result.SpokenLines.Count} lines, {result.ProgramCharacters} chars, in {(DateTime.UtcNow - started).TotalSeconds:0.0} s"
            + (result.UnspokenAfterVoiceFailure is { } unspoken ? $", voice failed with {unspoken.Count} unsaid" : ""));
        if (shotPath is not null && result.Drew)
        {
            var shots = await screenCapture.CaptureAllScreensAsync(new ScreenCaptureOptions { MaxDimension = 0, Format = ScreenImageFormat.Png, CursorScreenOnly = true });
            if (shots.Count > 0)
            {
                await File.WriteAllBytesAsync(shotPath, shots[0].ImageData);
                Console.WriteLine($"smoke: screen saved to {shotPath}");
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        stage.Clear("smoke done");
        stage.Surface = null;
        return result.Drew && errors == 0 ? 0 : 1;
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
