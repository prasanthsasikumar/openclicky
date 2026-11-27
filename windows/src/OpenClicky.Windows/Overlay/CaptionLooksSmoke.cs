//
//  CaptionLooksSmoke.cs
//  OpenClicky (Windows)
//
//  Shows every caption look without opening a window or touching the mouse:
//    OpenClicky --openclicky-caption-looks <folder>
//  draws the overlay's own caption bubble (OverlayWindow.MakeBubble + StyleCaption) in each colour, without a glow and
//  with each glow, on a white page and on a dark screen, and saves one PNG sheet per glow in <folder>.
//

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Overlay;

internal static class CaptionLooksSmoke
{
    public const string Flag = "--openclicky-caption-looks";

    private const string Sample = "The sky looks blue because air scatters blue light more than the other colours.";

    public static int Run(string[] args)
    {
        ConsoleAttachment.Attach();
        var flagIndex = Array.IndexOf(args, Flag);
        var folder = flagIndex >= 0 && flagIndex + 1 < args.Length ? args[flagIndex + 1] : Path.Combine(Path.GetTempPath(), "OpenClickyCaptionLooks");
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var glow in CaptionLook.Glows)
            {
                var path = Path.Combine(folder, $"caption-looks-glow-{glow.RawValue()}.png");
                Save(Sheet(glow), path);
                Console.WriteLine($"smoke: {path}");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"smoke: failed: {error}");
            return 1;
        }
    }

    /// <summary>One row per colour: its name, then the caption on white and on a dark screen.</summary>
    private static FrameworkElement Sheet(CaptionGlow glow)
    {
        var rows = new StackPanel { Background = Brushes.White };
        foreach (var (_, hex, title) in CaptionLook.Colors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = title, Width = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontSize = 12 });
            foreach (var background in new[] { Brushes.White, new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x30)) })
            {
                var (bubble, text) = OverlayWindow.MakeBubble(wraps: true, verticalPadding: 5);
                bubble.MaxWidth = 300;
                text.Text = Sample;
                OverlayWindow.StyleCaption(bubble, text, hex, glow);
                row.Children.Add(new Border { Background = background, Padding = new Thickness(24, 16, 24, 16), Child = bubble, Width = 360 });
            }
            rows.Children.Add(row);
        }
        return rows;
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 2), (int)Math.Ceiling(element.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
