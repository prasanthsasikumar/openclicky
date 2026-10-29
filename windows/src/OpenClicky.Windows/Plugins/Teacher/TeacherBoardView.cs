//
//  TeacherBoardView.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherBoardView.swift)
//
//  How a lesson is drawn: each stroke appears in order at a hand's pace (lines trace out, words
//  write a letter at a time), with a slight wobble so it reads as drawn, not plotted. The
//  rendering clock (CompositionTarget.Rendering) runs only while strokes are still appearing; a
//  finished board is static. The geometry (wobble, curves, trimming) is Core's
//  (TeacherStrokePaths); this file only strokes and writes it.
//
//  Windows: the handwriting is Ink Free (in every Windows 10 and 11), then Segoe Print, where the
//  Mac had Noteworthy.
//

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Plugins.Teacher;

/// <summary>The colours for each ink role on each board style, and the handwriting.</summary>
internal static class TeacherInkPalette
{
    public static Color Color(TeacherInk ink, TeacherBoardStyle style)
    {
        var isChalk = style == TeacherBoardStyle.ChalkOnDarkPage;
        return DS.Hex(ink switch
        {
            TeacherInk.Red => isChalk ? "#FF7675" : "#D63031",
            TeacherInk.Blue => isChalk ? "#74B9FF" : "#0984E3",
            TeacherInk.Green => isChalk ? "#55EFC4" : "#00A36C",
            TeacherInk.Orange => isChalk ? "#FDCB6E" : "#E17055",
            TeacherInk.Purple => isChalk ? "#A29BFE" : "#6C5CE7",
            TeacherInk.Grey => isChalk ? "#B2BEC3" : "#8A8F94",
            _ => isChalk ? "#F5F6FA" : "#2D3436",
        });
    }

    /// <summary>Ink Free ships with Windows 10 and 11; Segoe Print and Segoe Script before it.</summary>
    public static readonly FontFamily Handwriting = new("Ink Free, Segoe Print, Segoe Script, Comic Sans MS");
    public static readonly Typeface HandwritingTypeface = new(Handwriting, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static readonly Color PaperColor = DS.Hex("#FFFDF6");
    public static readonly Color PaperEdge = DS.Hex("#E8E2CF");
}

/// <summary>The board's content: the lesson on its paper sheet (or straight on the page), or the picture card.</summary>
internal sealed class TeacherBoardView : Border
{
    public TeacherBoardView(TeacherLesson lesson)
    {
        var isPaper = lesson.Style == TeacherBoardStyle.Paper || lesson.Picture is not null;
        if (isPaper)
        {
            Background = DS.Brush(TeacherInkPalette.PaperColor);
            BorderBrush = DS.Brush(TeacherInkPalette.PaperEdge);
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(12);
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.18, BlurRadius = 28, ShadowDepth = 6, Direction = 270 };
        }
        if (lesson.Picture is { } picture)
        {
            Padding = new Thickness(TeacherBoard.PicturePadding);
            Child = PictureView(picture);
        }
        else if (lesson.Sketch is { } sketch)
        {
            Padding = new Thickness(TeacherBoard.Padding(lesson.Style) - (isPaper ? 1 : 0));
            Child = new TeacherSketchView(sketch, lesson.Style, lesson.StartedAt);
        }
    }

    private static FrameworkElement PictureView(TeacherPicture picture)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var image = new Image { Source = Decode(picture.ImageData), Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var imageClip = new Border { Child = image, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        grid.Children.Add(imageClip);
        if (picture.Caption.Length > 0)
        {
            var caption = new TextBlock
            {
                Text = picture.Caption, FontFamily = TeacherInkPalette.Handwriting, FontWeight = FontWeights.Bold, FontSize = 20,
                Foreground = DS.Brush(DS.Hex("#2D3436")), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 56, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0),
            };
            Grid.SetRow(caption, 1);
            grid.Children.Add(caption);
        }
        var credit = new TextBlock
        {
            Text = $"from {picture.Credit}", FontSize = 10, Foreground = DS.Brush(DS.Hex("#8A8F94")),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(credit, 2);
        grid.Children.Add(credit);
        return grid;
    }

    /// <summary>The picture's bytes as a frozen bitmap (loaded at once, so the stream can go).</summary>
    public static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(data);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The sketch, stroke by stroke from <c>startedAt</c>. Redrawn every frame only until the last stroke is down.</summary>
internal sealed class TeacherSketchView : FrameworkElement
{
    private readonly TeacherSketch sketch;
    private readonly TeacherBoardStyle style;
    private readonly DateTimeOffset startedAt;
    private readonly IReadOnlyList<TeacherSketchTimeline.Entry> timeline;
    private readonly double totalSeconds;
    private bool isAnimating;

    public TeacherSketchView(TeacherSketch sketch, TeacherBoardStyle style, DateTimeOffset startedAt)
    {
        this.sketch = sketch;
        this.style = style;
        this.startedAt = startedAt;
        timeline = TeacherSketchTimeline.Entries(sketch);
        totalSeconds = TeacherSketchTimeline.TotalSeconds(sketch);
        Loaded += (_, _) => StartAnimating();
        Unloaded += (_, _) => StopAnimating();
    }

    /// <summary>Seconds since the lesson started (the drawing's clock, shared with the pointer's label stops).</summary>
    private double Elapsed => (DateTimeOffset.UtcNow - startedAt).TotalSeconds;

    private void StartAnimating()
    {
        if (isAnimating || Elapsed > totalSeconds + 0.1) return;
        isAnimating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimating()
    {
        if (!isAnimating) return;
        isAnimating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs eventArgs)
    {
        InvalidateVisual();
        // The last frame is drawn whole; an idle board costs nothing after it.
        if (Elapsed > totalSeconds + 0.1) StopAnimating();
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var elapsed = isAnimating ? Elapsed : double.PositiveInfinity;
        var layout = new TeacherSketchLayout(sketch, ActualWidth, ActualHeight);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var index = 0; index < sketch.Strokes.Count; index++)
        {
            var entry = timeline[index];
            if (elapsed < entry.Start) break;
            var progress = entry.Duration <= 0 ? 1 : Math.Min(1, (elapsed - entry.Start) / entry.Duration);
            Draw(context, sketch.Strokes[index], progress, index, layout, pixelsPerDip);
        }
    }

    private void Draw(DrawingContext context, TeacherStroke stroke, double progress, int seed, TeacherSketchLayout layout, double pixelsPerDip)
    {
        var color = TeacherInkPalette.Color(stroke.Ink, style);
        var brush = DS.Brush(color);
        var lineWidth = (style == TeacherBoardStyle.ChalkOnDarkPage ? 3.4 : 3.0) * stroke.Weight;
        switch (stroke.Kind)
        {
            case TeacherStrokeKind.Pause:
                return;
            case TeacherStrokeKind.Text:
                DrawWords(context, stroke, progress, brush, layout, pixelsPerDip);
                return;
            case TeacherStrokeKind.Dot:
            {
                if (stroke.Points.Count == 0) return;
                var centre = layout.ViewPoint(stroke.Points[0]);
                var radius = 3.6 * stroke.Weight * progress;
                context.DrawEllipse(brush, null, new Point(centre.X, centre.Y), radius, radius);
                return;
            }
        }

        var outline = TeacherStrokePaths.Polyline(stroke, layout, seed);
        if (outline.Count < 2) return;
        var pen = new Pen(brush, lineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (stroke.IsDashed)
        {
            // WPF dashes are in multiples of the line's width.
            pen.DashStyle = new DashStyle([9 / lineWidth, 7 / lineWidth], 0);
            pen.DashCap = PenLineCap.Flat;
        }
        pen.Freeze();
        if (stroke.IsFilled && progress >= 1)
        {
            context.DrawGeometry(DS.Brush(color, 0.14), null, Polyline(outline, closed: true));
        }
        context.DrawGeometry(null, pen, Polyline(TeacherStrokePaths.Trimmed(outline, progress), closed: false));
        if (stroke.Kind == TeacherStrokeKind.Arrow && progress >= 0.98 && stroke.Points.Count >= 2)
        {
            var tip = layout.ViewPoint(stroke.Points[^1]);
            var tail = layout.ViewPoint(stroke.Points[^2]);
            var headPen = new Pen(brush, lineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            headPen.Freeze();
            context.DrawGeometry(null, headPen, Polyline(TeacherStrokePaths.ArrowHead(tip, tail, 12 + 3 * stroke.Weight), closed: false));
        }
    }

    /// <summary>Written from the left so the letters already down never move; centred as a whole, and never past the
    /// board's edge (the estimate that sized the board can be short).</summary>
    private void DrawWords(DrawingContext context, TeacherStroke stroke, double progress, Brush brush, TeacherSketchLayout layout, double pixelsPerDip)
    {
        if (stroke.Points.Count == 0) return;
        var anchor = layout.ViewPoint(stroke.Points[0]);
        var fontSize = layout.FontSize(stroke.Weight);
        var full = Words(stroke.Words, fontSize, brush, pixelsPerDip);
        var shownCount = Math.Min(stroke.Words.Length, (int)Math.Ceiling(stroke.Words.Length * progress));
        if (shownCount <= 0) return;
        var shown = Words(stroke.Words[..shownCount], fontSize, brush, pixelsPerDip);
        var wantedLeft = stroke.IsLeftAligned ? anchor.X : anchor.X - full.WidthIncludingTrailingWhitespace / 2;
        var left = Math.Min(Math.Max(wantedLeft, 0), Math.Max(ActualWidth - full.WidthIncludingTrailingWhitespace, 0));
        var middle = Math.Min(Math.Max(anchor.Y, full.Height / 2), ActualHeight - full.Height / 2);
        context.DrawText(shown, new Point(left, middle - full.Height / 2));
    }

    private static FormattedText Words(string text, double fontSize, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TeacherInkPalette.HandwritingTypeface, fontSize, brush, pixelsPerDip);

    private static StreamGeometry Polyline(IReadOnlyList<ScreenPoint> points, bool closed)
    {
        var geometry = new StreamGeometry();
        if (points.Count >= 2)
        {
            using var context = geometry.Open();
            context.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: closed, isClosed: closed);
            context.PolyLineTo(points.Skip(1).Select(point => new Point(point.X, point.Y)).ToList(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }
}
