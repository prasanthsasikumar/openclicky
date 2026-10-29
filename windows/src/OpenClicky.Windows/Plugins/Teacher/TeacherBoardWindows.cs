//
//  TeacherBoardWindows.cs
//  OpenClicky (Windows port of TeacherBoard.swift's panels)
//
//  The Teacher's board on screen (ITeacherBoardSurface): a click-through, topmost, non-activating
//  window that holds the lesson, and a small ✕ window at its top-right corner that clears it. The
//  pointer's overlay re-asserts topmost every time it moves, so it stays over the board (the Mac
//  put the board at .statusBar, under the overlay's level). Both are left out of screenshots like
//  every floating OpenClicky window, so the next question's screenshot sees the page, not the
//  lesson. Also what only WPF and the audio endpoint can tell Core: a screenshot in greys, a
//  picture's size, the mouse, and the output volume.
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Plugins.Teacher;

internal sealed class TeacherBoardWindows(IScreens screens) : ITeacherBoardSurface
{
    private const double CloseButtonSize = 26;

    private Window? boardWindow;
    private Window? closeButtonWindow;

    public void Present(TeacherLesson lesson, PixelRect frame, double scale)
    {
        var board = boardWindow ??= MakeBoardWindow();
        board.Content = new TeacherBoardView(lesson);
        if (!board.IsVisible) board.Show();
        FloatingWindowSupport.PlaceInPixels(board, frame);

        var closeButton = closeButtonWindow ??= MakeCloseButtonWindow();
        if (!closeButton.IsVisible) closeButton.Show();
        var buttonSize = (int)Math.Round(CloseButtonSize * scale);
        var inset = (int)Math.Round(4 * scale);
        FloatingWindowSupport.PlaceInPixels(closeButton,
            new PixelRect(frame.Right - buttonSize / 2 - inset, frame.Top - buttonSize / 2 + inset, buttonSize, buttonSize));
    }

    public void Hide()
    {
        if (boardWindow is { } board)
        {
            board.Hide();
            board.Content = null;
        }
        closeButtonWindow?.Hide();
    }

    private static Window MakeBoardWindow()
    {
        var window = new Window { Title = "OpenClicky lesson", Width = 1, Height = 1 };
        FloatingWindowSupport.Configure(window, clickThrough: true);
        // The lesson's text is drawn by hand in a layered window: grayscale, as the other floating windows.
        AutomationProperties.SetName(window, "Lesson on the board");
        return window;
    }

    private static Window MakeCloseButtonWindow()
    {
        var window = new Window { Title = "OpenClicky clear the board", Width = CloseButtonSize, Height = CloseButtonSize };
        FloatingWindowSupport.Configure(window, clickThrough: false);
        var glyph = IslandUi.Glyph(Icons.Close, 9, Brushes.White);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var normal = DS.Brush(Colors.Black, 0.55);
        var hovered = DS.Brush(Colors.Black, 0.8);
        var button = IslandUi.Clickable(glyph, () => TeacherPlugin.ClearBoard("close button"), "Clear the board (esc)", normal, new CornerRadius(11));
        button.Width = 22;
        button.Height = 22;
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.MouseEnter += (_, _) => button.Background = hovered;
        button.MouseLeave += (_, _) => button.Background = normal;
        AutomationProperties.SetName(button, "Clear the board");
        window.Content = button;
        return window;
    }

    // MARK: - What Core cannot see

    public byte[]? GreyPixels(byte[] imageData, int width, int height)
    {
        if (TeacherBoardView.Decode(imageData) is not { } bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
        try
        {
            var scaled = new TransformedBitmap(bitmap, new ScaleTransform(width / (double)bitmap.PixelWidth, height / (double)bitmap.PixelHeight));
            var grey = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
            // The scaled size can round a pixel short; the rows are read at the size asked for.
            var stride = grey.PixelWidth;
            var pixels = new byte[stride * grey.PixelHeight];
            grey.CopyPixels(pixels, stride, 0);
            if (grey.PixelWidth == width && grey.PixelHeight == height) return pixels;
            var resized = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    resized[y * width + x] = pixels[Math.Min(y, grey.PixelHeight - 1) * stride + Math.Min(x, grey.PixelWidth - 1)];
                }
            }
            return resized;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public (int Width, int Height)? ImageSize(byte[] imageData) =>
        TeacherBoardView.Decode(imageData) is { PixelWidth: > 0, PixelHeight: > 0 } bitmap ? (bitmap.PixelWidth, bitmap.PixelHeight) : null;

    public PixelPoint CursorPosition() => screens.CursorPosition();

    public (PixelRect Frame, double Scale)? CursorDisplay() =>
        (screens.CursorScreen() ?? screens.Primary()) is { } screen ? (screen.Bounds, screen.Scale) : null;

    /// <summary>The default output device's master volume and mute (Core Audio's endpoint volume).</summary>
    public TeacherListening.Output CurrentOutput()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return TeacherListening.Output.Unknown;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var endpoint = device.AudioEndpointVolume;
            return new TeacherListening.Output(endpoint.MasterVolumeLevelScalar, endpoint.Mute);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return TeacherListening.Output.Unknown;
        }
    }
}
