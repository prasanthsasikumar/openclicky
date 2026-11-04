//
//  ScreenMarkerSmoke.cs
//  OpenClicky (Windows)
//
//  Checks the screen marker without a person's hand:
//    OpenClicky --openclicky-marker-smoke
//  opens the marker over the monitor under the pointer, draws a loop round its middle a second
//  later, prints the mark's bounds (physical pixels), keeps the mark up for three seconds (left in
//  screenshots, so one can be taken), and takes it away.
//

using System.Windows;
using System.Windows.Threading;
using OpenClicky.Core.Platform;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Overlay;

internal static class ScreenMarkerSmoke
{
    public const string Flag = "--openclicky-marker-smoke";

    public static int Run()
    {
        ConsoleAttachment.Attach();
        FloatingWindowSupport.AllowsScreenCapture = true;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        application.Startup += async (_, _) =>
        {
            using var screens = new Screens();
            using var marker = new ScreenMarkerWindow(screens);
            try
            {
                var screen = screens.CursorScreen()!;
                PixelRect? marked = null;
                var answered = false;
                marker.BeginMarking(bounds => { marked = bounds; answered = true; });
                Console.WriteLine($"smoke: marking over {screen.DeviceName} {screen.Bounds.Width}×{screen.Bounds.Height} at {screen.Scale:0.##}x");
                await Task.Delay(1200);
                var middle = new Point(screen.BoundsInDips.Width / 2, screen.BoundsInDips.Height / 2);
                marker.DrawForSmoke(Enumerable.Range(0, 48).Select(k =>
                    new Point(middle.X + 220 * Math.Cos(k * Math.PI / 22), middle.Y + 110 * Math.Sin(k * Math.PI / 22))));
                Console.WriteLine(answered ? $"smoke: marked {marked}" : "smoke: no answer from the marker");
                await Task.Delay(3000);
                marker.HideMark();
                exitCode = marked is { Width: > 400, Height: > 200 } ? 0 : 1;
            }
            catch (Exception error)
            {
                Console.WriteLine($"smoke: failed: {error}");
            }
            application.Shutdown(exitCode);
        };
        application.Run();
        return exitCode;
    }
}
