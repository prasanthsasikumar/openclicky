//
//  ScrollFollowSmoke.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  The drawing stage's smoke with --scroll <folder>: do marks pointing at the screen follow it when it
//  scrolls? A test page opens on the left of the cursor's screen (a header that stays put over 120
//  numbered lines that scroll); a drawing rings line 20 and points an arrow at it from a card,
//  highlights line 4 and labels the header. Then the page scrolls by itself, in small steps the way a
//  wheel scrolls smoothly (no input is sent: the follower is told the person just scrolled). Saved in
//  the folder: before.png and after.png, the screen with the drawing over it, and log.txt, the app's
//  log for the run. Line 20 and its ring should have gone up together, the highlight on line 4 gone
//  with its line, and the header's label stayed.
//
//    OpenClicky --openclicky-drawstage-smoke --scroll <folder>
//

using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Plugins.DrawStage;

internal static class ScrollFollowSmoke
{
    private const int CanvasWidth = 1280;

    public static async Task<int> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var logPath = Path.Combine(folder, "log.txt");
        File.Delete(logPath);
        AppLog.UseFile(logPath);
        StageScrollWatch.SinceInputOverride = () => TimeSpan.Zero;
        using var screens = new Screens();
        if (screens.CursorScreen() is not { } screen)
        {
            Console.WriteLine("smoke: no screen");
            return 1;
        }
        var frame = screen.Bounds;
        var canvasHeight = (int)Math.Round(CanvasWidth * frame.Height / (double)Math.Max(1, frame.Width));
        double ToCanvas(double pixels) => pixels * CanvasWidth / frame.Width;

        // The test page: in device-independent pixels, on the left of the screen, under the stage.
        var page = TestPage(out var scroller, out var lines, out var header);
        page.Left = screen.BoundsInDips.X + 60;
        page.Top = screen.BoundsInDips.Y + 60;
        page.Width = Math.Min(620, screen.BoundsInDips.Width * 0.45);
        page.Height = screen.BoundsInDips.Height - 160;
        page.Show();
        await Task.Delay(800);

        PixelRect OnScreen(FrameworkElement element)
        {
            var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));
            return PixelRect.FromEdges((int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y);
        }
        (double X, double Y, double W, double H) Canvas(PixelRect box) =>
            (ToCanvas(box.Left - frame.Left), ToCanvas(box.Top - frame.Top), ToCanvas(box.Width), ToCanvas(box.Height));
        var target = Canvas(OnScreen(lines[19]));
        var early = Canvas(OnScreen(lines[3]));
        var top = Canvas(OnScreen(header));
        var program = string.Join('\n',
            "step(1); say('Look at line twenty.')",
            "card(W * 0.62, H * 0.2, W * 0.3, H * 0.28, {title: 'Scroll test'})",
            "text(W * 0.64, H * 0.33, 'that line is the one', {align: 'start', size: 24})",
            $"ellipse({target.X + 110:0}, {target.Y + target.H / 2:0}, 110, {target.H / 2 + 8:0}, {{color: 'red'}})",
            $"arrow([[W * 0.62, H * 0.36], [{target.X + 235:0}, {target.Y + target.H / 2:0}]], {{color: 'blue'}})",
            $"highlight({early.X:0}, {early.Y:0}, 200, {early.H:0}, {{color: 'yellow'}})",
            $"text({top.X + 260:0}, {top.Y + top.H / 2:0}, 'stays', {{color: 'green', align: 'start', size: 22}})");
        Console.WriteLine($"smoke: line 20 at canvas ({target.X:0}, {target.Y:0}), canvas {CanvasWidth}×{canvasHeight}");

        using var window = new DrawStageWindow();
        var stage = DrawStageController.Shared;
        stage.Surface = window;
        stage.MainThread = new DispatcherMainThread(Application.Current.Dispatcher);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = 0;
        stage.Logged += line =>
        {
            if (line.StartsWith("error: ", StringComparison.Ordinal)) errors++;
            Console.WriteLine($"smoke: page: {line}");
        };
        stage.Finished += () => finished.TrySetResult();
        stage.Begin(frame, screen.Scale, CanvasWidth, canvasHeight, DrawStagePace.Estimate);
        stage.Feed(program, done: true);
        if (await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(30))) != finished.Task)
        {
            Console.WriteLine("smoke: the drawing didn't finish");
            page.Close();
            return 1;
        }
        await Task.Delay(1000);
        await SaveAsync(window, frame, Path.Combine(folder, "before.png"));

        // A smooth wheel scroll: 200 DIPs in ten steps over a third of a second, then a pause, then 100 more.
        foreach (var (distance, steps) in new[] { (200.0, 10), (100.0, 5) })
        {
            for (var i = 0; i < steps; i++)
            {
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset + distance / steps);
                await Task.Delay(33);
            }
            await Task.Delay(700);
        }
        await Task.Delay(800);
        await SaveAsync(window, frame, Path.Combine(folder, "after.png"));
        var moved = OnScreen(lines[19]);
        Console.WriteLine($"smoke: line 20 is now at canvas y {ToCanvas(moved.Top - frame.Top):0} (was {target.Y:0})");

        stage.Clear("smoke done");
        stage.Surface = null;
        page.Close();
        StageScrollWatch.SinceInputOverride = null;
        // The log is still open for writing.
        using var log = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var followed = (await log.ReadToEndAsync()).Split('\n').Count(line => line.Contains("mark(s) follow it", StringComparison.Ordinal));
        Console.WriteLine($"smoke: the marks followed {followed} scroll(s); log in {logPath}");
        return errors == 0 && followed > 0 ? 0 : 1;
    }

    private static Window TestPage(out ScrollViewer scroller, out List<TextBlock> lines, out TextBlock header)
    {
        var stack = new StackPanel { Background = System.Windows.Media.Brushes.White };
        lines = [];
        string[] words = ["the quick brown fox", "jumps over", "a lazy dog", "while the sky", "turns blue at noon", "and red at dusk"];
        for (var i = 1; i <= 120; i++)
        {
            var line = new TextBlock
            {
                Text = $"{i,3}   {words[i % words.Length]} {words[(i * 7) % words.Length]}",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 16,
                Padding = new Thickness(10, 3, 10, 3),
                Background = i % 2 == 0 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF5, 0xFA)) : System.Windows.Media.Brushes.White,
            };
            lines.Add(line);
            stack.Children.Add(line);
        }
        scroller = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
        header = new TextBlock
        {
            Text = "Scroll test page",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0x51, 0xC7)),
            Foreground = System.Windows.Media.Brushes.White,
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(scroller, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        return new Window
        {
            Title = "OpenClicky scroll test",
            Content = grid,
            WindowStyle = WindowStyle.ToolWindow,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
        };
    }

    /// <summary>The screen (the stage is left out of screen grabs) with the stage's own picture of the drawing over it.</summary>
    private static async Task SaveAsync(DrawStageWindow window, PixelRect frame, string path)
    {
        using var screen = ScreenCapture.CaptureRegion(frame);
        var drawing = await window.CaptureAsync(png: true);
        if (screen is null || drawing is null) { Console.WriteLine($"smoke: couldn't save {path}"); return; }
        using (var graphics = Graphics.FromImage(screen))
        using (var stream = new MemoryStream(drawing))
        using (var over = System.Drawing.Image.FromStream(stream))
        {
            graphics.DrawImage(over, 0, 0, screen.Width, screen.Height);
        }
        screen.Save(path, ImageFormat.Png);
        Console.WriteLine($"smoke: saved {path}");
    }
}
