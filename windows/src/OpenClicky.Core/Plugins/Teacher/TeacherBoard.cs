//
//  TeacherBoard.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherBoard.swift's TeacherBoard)
//
//  The board's state, one per app: what is on it, where it went, and the token that lets a
//  drawing or picture written in the background know whether the board moved on meanwhile. The
//  windows are the WPF side's (ITeacherBoardSurface: OpenClicky.Windows/Plugins/Teacher); this
//  class decides what they show and where. As each label is written the pointer flies beside it.
//
//  Threading: called on the UI thread; background work comes back through Post.
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Plugins.Teacher;

/// <summary>One lesson on the board: a sketch or a picture, its title, its look and when it started (the drawing's clock).</summary>
public sealed record TeacherLesson(string Title, TeacherSketch? Sketch, TeacherPicture? Picture, TeacherBoardStyle Style, DateTimeOffset StartedAt);

/// <summary>What only the app's windows can do for the board.</summary>
public interface ITeacherBoardSurface
{
    /// <summary>Shows the lesson in <paramref name="frame"/> (physical pixels, on a monitor of <paramref name="scale"/>), with
    /// the close button at its top-right corner. Called again for the next lesson.</summary>
    void Present(TeacherLesson lesson, PixelRect frame, double scale);

    void Hide();

    /// <summary>The screenshot drawn at <paramref name="width"/> × <paramref name="height"/> in greys, row 0 at the top; null
    /// when it cannot be decoded.</summary>
    byte[]? GreyPixels(byte[] imageData, int width, int height);

    /// <summary>The pixel size of image bytes; null when they are not an image this PC can show.</summary>
    (int Width, int Height)? ImageSize(byte[] imageData);

    /// <summary>The mouse, physical pixels.</summary>
    PixelPoint CursorPosition();

    /// <summary>The monitor the mouse is on, for a lesson without a screenshot: its bounds in pixels and its scale.</summary>
    (PixelRect Frame, double Scale)? CursorDisplay();

    /// <summary>The default output device's volume and mute.</summary>
    TeacherListening.Output CurrentOutput();
}

public sealed class TeacherBoard
{
    public static TeacherBoard Shared { get; } = new();

    /// <summary>The windows (set by the composition root); without one, lessons are only logged.</summary>
    public ITeacherBoardSurface? Surface { get; set; }
    /// <summary>Where background results are shown (the UI thread); without one they run where they finish.</summary>
    public IMainThread? MainThread { get; set; }

    public TeacherLesson? Lesson { get; private set; }

    /// <summary>A drawing or picture was asked for since the board was last cleared (each question clears it), whether
    /// by the app or by the model; a second request for the same question is ignored.</summary>
    public bool QuestionHasLesson { get; set; }

    /// <summary>The lesson came or went (the island's row follows it). Raised on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>The pointer flies to each label as it is written: (physical pixel location, the monitor's frame in pixels).</summary>
    public Action<ScreenPoint, ScreenRect>? OnLabelWritten { get; set; }

    /// <summary>Bumped by every show and clear, so a picture that arrives after the board moved on is dropped.</summary>
    private int requestToken;
    private CancellationTokenSource? labelPointing;

    /// <summary>The padding between the board's frame and the drawing, in DIPs: a sheet of paper has a margin.</summary>
    public static double Padding(TeacherBoardStyle style) => style == TeacherBoardStyle.Paper ? 22 : 6;

    /// <summary>The padding around a picture on its card, in DIPs.</summary>
    public const double PicturePadding = 14;

    /// <summary>False when the board moved on (another lesson, a clear) while this one was being written.</summary>
    public bool Show(TeacherSketch sketch, string title, CompanionScreenCapture? capture, int? expectedToken = null)
    {
        if (expectedToken is { } expected && expected != requestToken) return false;
        requestToken++;
        var drawingBounds = TeacherSketchLayout.DrawingBoundsOf(sketch);
        var (placement, displayFrame, scale) = Place(drawingBounds.Width / Math.Max(drawingBounds.Height, 1), capture);
        var frame = placement.FrameInPixels(displayFrame, scale);
        var lesson = new TeacherLesson(title.Length == 0 ? "drawing" : title, sketch, null, placement.Style, DateTimeOffset.UtcNow);
        AppLog.Write($"teacher: drawing ({sketch.Strokes.Count} strokes, {placement.Style}) at {frame.Width}×{frame.Height} ({frame.Left}, {frame.Top})");
        Present(lesson, frame, scale);
        PointAtLabels(sketch, frame, scale, placement.Style, lesson, displayFrame);
        return true;
    }

    /// <summary>The token a background job (a picture search, a drawing being written) shows its result with.</summary>
    public int BeginBackgroundJob() => requestToken;

    /// <summary>False when something else was shown or the board cleared while the picture downloaded.</summary>
    public bool Show(TeacherPicture picture, CompanionScreenCapture? capture, int expectedToken)
    {
        if (expectedToken != requestToken) return false;
        requestToken++;
        // Room for the caption and credit under the picture.
        var aspectRatio = Math.Max(picture.PixelWidth, 1) / (Math.Max(picture.PixelHeight, 1) * 1.18);
        var (placement, displayFrame, scale) = Place(aspectRatio, capture);
        var frame = placement.FrameInPixels(displayFrame, scale);
        var lesson = new TeacherLesson(picture.Caption.Length == 0 ? "picture" : picture.Caption, null, picture, TeacherBoardStyle.Paper, DateTimeOffset.UtcNow);
        AppLog.Write($"teacher: showing a picture from {picture.Credit}");
        StopPointingAtLabels();
        Present(lesson, frame, scale);
        return true;
    }

    public bool Clear(string reason)
    {
        requestToken++;
        QuestionHasLesson = false;
        if (Lesson is null) return false;
        AppLog.Write($"teacher: board cleared ({reason})");
        StopPointingAtLabels();
        Lesson = null;
        Surface?.Hide();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    public void Post(Action action)
    {
        if (MainThread is { } mainThread) mainThread.Post(action);
        else action();
    }

    public (int Width, int Height)? ImageSize(byte[] imageData) => Surface?.ImageSize(imageData);

    public TeacherListening.Output CurrentOutput() => Surface?.CurrentOutput() ?? TeacherListening.Output.Unknown;

    private void Present(TeacherLesson lesson, PixelRect frame, double scale)
    {
        Lesson = lesson;
        Surface?.Present(lesson, frame, scale);
        Changed?.Invoke();
    }

    /// <summary>The placement on the captured monitor (or the mouse's, without a screenshot), with that monitor's frame and scale.</summary>
    private (TeacherBoardPlacement.Placement Placement, PixelRect DisplayFrame, double Scale) Place(double aspectRatio, CompanionScreenCapture? capture)
    {
        var (displayFrame, scale) = capture is not null
            ? (capture.DisplayFrame, capture.DisplayScale > 0 ? capture.DisplayScale : 1.0)
            : Surface?.CursorDisplay() ?? (new PixelRect(0, 0, 1920, 1080), 1.0);
        var displayWidth = displayFrame.Width / scale;
        var displayHeight = displayFrame.Height / scale;
        TeacherScreenGrid? grid = null;
        if (capture is not null && Surface is { } surface)
        {
            var (columns, rows) = TeacherBoardPlacement.GridSize(displayWidth, displayHeight);
            var width = columns * TeacherBoardPlacement.PixelsPerCell;
            var height = rows * TeacherBoardPlacement.PixelsPerCell;
            if (columns > 0 && rows > 0 && surface.GreyPixels(capture.ImageData, width, height) is { } pixels && pixels.Length >= width * height)
            {
                grid = TeacherBoardPlacement.GridFromGreyPixels(pixels, width, height, TeacherBoardPlacement.PixelsPerCell);
            }
        }
        var mouse = Surface?.CursorPosition() ?? displayFrame.Center;
        var placement = TeacherBoardPlacement.Place(aspectRatio, grid, displayWidth, displayHeight, (mouse.X - displayFrame.Left) / scale);
        return (placement, displayFrame, scale);
    }

    // MARK: - The pointer points at each label

    /// <summary>
    /// Where and when the pointer goes as a sketch's labels are written: a little left of each word's centre and below
    /// it, so the triangle sits beside the writing instead of over it. At most six labels, at least 1.2 s apart, so the
    /// pointer never darts about. Locations in physical pixels; the layout is the one the board draws with.
    /// </summary>
    public static IReadOnlyList<(double Seconds, ScreenPoint Location)> LabelStops(TeacherSketch sketch, PixelRect frame, double scale, TeacherBoardStyle style)
    {
        var padding = Padding(style) * scale;
        var innerLeft = frame.Left + padding;
        var innerTop = frame.Top + padding;
        var layout = new TeacherSketchLayout(sketch, (frame.Width - 2 * padding) / scale, (frame.Height - 2 * padding) / scale);
        var timeline = TeacherSketchTimeline.Entries(sketch);
        var stops = new List<(double Seconds, ScreenPoint Location)>();
        for (var index = 0; index < sketch.Strokes.Count; index++)
        {
            var stroke = sketch.Strokes[index];
            if (stroke.Kind != TeacherStrokeKind.Text || stroke.Points.Count == 0) continue;
            var seconds = timeline[index].Start;
            if (stops.Count > 0 && seconds - stops[^1].Seconds < 1.2) continue;
            var inView = layout.ViewPoint(stroke.Points[0]);
            stops.Add((seconds, new ScreenPoint(innerLeft + (inView.X - 14) * scale, innerTop + (inView.Y + 12) * scale)));
            if (stops.Count == 6) break;
        }
        return stops;
    }

    private void PointAtLabels(TeacherSketch sketch, PixelRect frame, double scale, TeacherBoardStyle style, TeacherLesson lesson, PixelRect displayFrame)
    {
        StopPointingAtLabels();
        if (OnLabelWritten is null) return;
        var stops = LabelStops(sketch, frame, scale, style);
        if (stops.Count == 0) return;
        var cancellation = new CancellationTokenSource();
        labelPointing = cancellation;
        var display = new ScreenRect(displayFrame.Left, displayFrame.Top, displayFrame.Width, displayFrame.Height);
        _ = PointAtLabelsAsync(stops, lesson, display, cancellation.Token);
    }

    private async Task PointAtLabelsAsync(IReadOnlyList<(double Seconds, ScreenPoint Location)> stops, TeacherLesson lesson, ScreenRect display,
                                          CancellationToken cancellationToken)
    {
        foreach (var stop in stops)
        {
            var wait = stop.Seconds - (DateTimeOffset.UtcNow - lesson.StartedAt).TotalSeconds;
            try
            {
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Post(() =>
            {
                if (cancellationToken.IsCancellationRequested || !ReferenceEquals(Lesson, lesson)) return;
                OnLabelWritten?.Invoke(stop.Location, display);
            });
        }
    }

    private void StopPointingAtLabels()
    {
        labelPointing?.Cancel();
        labelPointing = null;
    }
}
