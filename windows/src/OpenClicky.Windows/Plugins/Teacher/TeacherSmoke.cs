//
//  TeacherSmoke.cs
//  OpenClicky (Windows port of OpenClickyApp.swift's --openclicky-teacher-smoke)
//
//  Draws a lesson on the cursor's screen without a voice turn, then exits: the draw tool's own
//  Pythagoras example, a sketch file, a picture search, or a lesson the writer composes for a
//  question (that one needs a key or an account in shell.json). The board is left capturable so
//  `--shot` can save the screen with the lesson on it.
//
//    OpenClicky --openclicky-teacher-smoke [sketch.txt | picture:<words> | lesson:<question>] [--shot <file.png>]
//

using System.IO;
using System.Windows;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Plugins.Teacher;

internal static class TeacherSmoke
{
    public const string Flag = "--openclicky-teacher-smoke";

    public static int Run(string[] args)
    {
        ConsoleAttachment.Attach();
        var flagIndex = Array.IndexOf(args, Flag);
        var argument = flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith("--", StringComparison.Ordinal) ? args[flagIndex + 1] : null;
        var shotIndex = Array.IndexOf(args, "--shot");
        var shotPath = shotIndex >= 0 && shotIndex + 1 < args.Length ? args[shotIndex + 1] : null;

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        application.Startup += async (_, _) =>
        {
            try
            {
                exitCode = await RunAsync(application, argument, shotPath);
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

    private static async Task<int> RunAsync(Application application, string? argument, string? shotPath)
    {
        FloatingWindowSupport.AllowsScreenCapture = true;
        using var screens = new Screens();
        var screenCapture = new ScreenCapture(screens);
        var board = TeacherBoard.Shared;
        board.Surface = new TeacherBoardWindows(screens);
        board.MainThread = new DispatcherMainThread(application.Dispatcher);
        board.OnLabelWritten = (location, _) => Console.WriteLine($"smoke: pointer → label at ({location.X:0}, {location.Y:0})");

        var capture = await screenCapture.CaptureCursorScreenAsync();
        Console.WriteLine($"smoke: screenshot {(capture is null ? "unavailable (paper fallback)" : $"taken ({capture.ScreenshotWidthInPixels}×{capture.ScreenshotHeightInPixels})")}");

        var seconds = 8.0;
        if (argument is not null && argument.StartsWith("picture:", StringComparison.Ordinal))
        {
            var query = argument["picture:".Length..];
            if (!await ShowPictureAsync(board, query, capture)) return 1;
        }
        else if (argument is not null && argument.StartsWith("lesson:", StringComparison.Ordinal))
        {
            var question = argument["lesson:".Length..];
            Console.WriteLine($"smoke: teaching question {TeacherQuestion.IsTeachingQuestion(question)}, writer via {TeacherSketchWriter.RouteName(TeacherSketchWriter.RouteFor(Core.Providers.ProviderRouter.CurrentCredentials()))}");
            var screenshot = TeacherQuestion.NeedsTheScreen(question) ? capture?.ImageData : null;
            switch (await TeacherSketchWriter.LessonAsync(question, screenshot))
            {
                case TeacherSketchWriter.Reply.Sketch written:
                    Console.WriteLine($"smoke: the writer drew\n{written.Commands}");
                    if (ShowSketch(board, written.Commands, question, capture) is not { } total) return 1;
                    seconds = total + 4;
                    break;
                case TeacherSketchWriter.Reply.Picture picture:
                    Console.WriteLine($"smoke: the writer asked for a picture of {picture.Query}");
                    if (!await ShowPictureAsync(board, picture.Query, capture)) return 1;
                    break;
                default:
                    Console.WriteLine("smoke: no lesson written");
                    return 1;
            }
        }
        else
        {
            var source = argument is not null ? await File.ReadAllTextAsync(argument) : TeacherPlugin.SketchGrammar.Split("Example, Pythagoras:\n")[^1];
            if (ShowSketch(board, source, "smoke", capture) is not { } total) return 1;
            seconds = total + 4;
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(seconds - 2, 1)));
        if (shotPath is not null)
        {
            var shots = await screenCapture.CaptureAllScreensAsync(new ScreenCaptureOptions { MaxDimension = 0, Format = ScreenImageFormat.Png, CursorScreenOnly = true });
            if (shots.Count > 0)
            {
                await File.WriteAllBytesAsync(shotPath, shots[0].ImageData);
                Console.WriteLine($"smoke: screen saved to {shotPath}");
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
        board.Clear("smoke done");
        return 0;
    }

    /// <summary>The sketch drawn; its drawing time in seconds, or null when nothing was drawable.</summary>
    private static double? ShowSketch(TeacherBoard board, string source, string title, CompanionScreenCapture? capture)
    {
        var result = TeacherSketchParser.Parse(source);
        if (result.Sketch is not { } sketch)
        {
            Console.WriteLine($"smoke: nothing drawable ({result.SkippedLines.Count} lines skipped)");
            return null;
        }
        board.Show(sketch, title, capture);
        Console.WriteLine($"smoke: {sketch.Strokes.Count} strokes, {board.Lesson?.Style}, skipped [{string.Join(" | ", result.SkippedLines)}]");
        return TeacherSketchTimeline.TotalSeconds(sketch);
    }

    private static async Task<bool> ShowPictureAsync(TeacherBoard board, string query, CompanionScreenCapture? capture)
    {
        if (await TeacherPictureFinder.FindAsync(query, query, board.ImageSize) is not { } picture)
        {
            Console.WriteLine("smoke: no picture found");
            return false;
        }
        board.Show(picture, capture, board.BeginBackgroundJob());
        Console.WriteLine($"smoke: picture from {picture.Credit}, {picture.PixelWidth}×{picture.PixelHeight}");
        return true;
    }
}
