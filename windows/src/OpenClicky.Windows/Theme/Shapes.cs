//
//  Shapes.cs
//  OpenClicky (Windows port of the shapes in OverlayWindow.swift, OpenClickyMark.swift,
//  OrbPanel.swift and NotchHUD.swift)
//
//  The pointer's triangle, the mark (ring + pointer), the orb's own small mark and pixel grid,
//  and the island's outline with its top corners flaring out into the edge it hangs from.
//  Same maths as the SwiftUI shapes; WPF and SwiftUI views are both y-down.
//

using System.Windows;
using System.Windows.Media;

namespace OpenClicky.Windows.Theme;

public static class Shapes
{
    /// <summary>The pointer: an equilateral triangle in a <paramref name="width"/> × <paramref name="height"/> frame, tip up
    /// (OverlayWindow.swift's Triangle). The overlay rotates it -35° to read as a cursor.</summary>
    public static Geometry Triangle(double width, double height)
    {
        var size = Math.Min(width, height);
        var triangleHeight = size * Math.Sqrt(3.0) / 2.0;
        var midX = width / 2;
        var midY = height / 2;
        var figure = new PathFigure { StartPoint = new Point(midX, midY - triangleHeight / 1.5), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(new Point(midX - size / 2, midY + triangleHeight / 3), true));
        figure.Segments.Add(new LineSegment(new Point(midX + size / 2, midY + triangleHeight / 3), true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    // OpenClickyMark proportions in a unit square, y down, measured off the icon artwork.
    public const double MarkInnerRadius = 0.4125;
    public static readonly Point MarkArrowTip = new(0.3125, 0.3125);
    public static readonly Point MarkArrowRight = new(0.7475, 0.475);
    public static readonly Point MarkArrowNotch = new(0.505, 0.52);
    public static readonly Point MarkArrowBottom = new(0.4775, 0.7525);

    /// <summary>The mark: a ring whose top-right corner is square, with the pointer inside aiming up-left. Even-odd.</summary>
    public static Geometry OpenClickyMark(Rect rect)
    {
        var side = Math.Min(rect.Width, rect.Height);
        var originX = rect.X + rect.Width / 2 - side / 2;
        var originY = rect.Y + rect.Height / 2 - side / 2;
        Point Scaled(Point unitPoint) => new(originX + unitPoint.X * side, originY + unitPoint.Y * side);
        var center = new Point(originX + side / 2, originY + side / 2);

        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        // The outer edge: three quarters of a circle (right → bottom → left → top), then the square corner.
        var outer = new PathFigure { StartPoint = new Point(center.X + side / 2, center.Y), IsClosed = true };
        outer.Segments.Add(new ArcSegment(new Point(center.X, center.Y - side / 2), new Size(side / 2, side / 2), 0, true, SweepDirection.Clockwise, true));
        outer.Segments.Add(new LineSegment(new Point(originX + side, originY), true));
        geometry.Figures.Add(outer);
        // The hole.
        var innerRadius = MarkInnerRadius * side;
        var hole = new PathFigure { StartPoint = new Point(center.X + innerRadius, center.Y), IsClosed = true };
        hole.Segments.Add(new ArcSegment(new Point(center.X - innerRadius, center.Y), new Size(innerRadius, innerRadius), 0, false, SweepDirection.Clockwise, true));
        hole.Segments.Add(new ArcSegment(new Point(center.X + innerRadius, center.Y), new Size(innerRadius, innerRadius), 0, false, SweepDirection.Clockwise, true));
        geometry.Figures.Add(hole);
        // The pointer.
        var arrow = new PathFigure { StartPoint = Scaled(MarkArrowTip), IsClosed = true };
        arrow.Segments.Add(new LineSegment(Scaled(MarkArrowRight), true));
        arrow.Segments.Add(new LineSegment(Scaled(MarkArrowNotch), true));
        arrow.Segments.Add(new LineSegment(Scaled(MarkArrowBottom), true));
        geometry.Figures.Add(arrow);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The orb's own small mark: a rounded pointer, the buddy's arrow simplified (OrbMarkShape).</summary>
    public static Geometry OrbMark(Rect rect)
    {
        double width = rect.Width, height = rect.Height;
        Point At(double unitX, double unitY) => new(rect.X + width * unitX, rect.Y + height * unitY);
        var figure = new PathFigure { StartPoint = At(0.2, 0.1), IsClosed = true };
        figure.Segments.Add(new LineSegment(At(0.2, 0.9), true));
        figure.Segments.Add(new LineSegment(At(0.42, 0.68), true));
        figure.Segments.Add(new LineSegment(At(0.6, 0.98), true));
        figure.Segments.Add(new LineSegment(At(0.74, 0.9), true));
        figure.Segments.Add(new LineSegment(At(0.56, 0.62), true));
        figure.Segments.Add(new LineSegment(At(0.86, 0.6), true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The orb's pixel look: the mark seen through a 3 DIP grid of squares (OrbPixelGrid).</summary>
    public static Geometry PixelGrid(Rect rect)
    {
        const double cell = 3;
        var group = new GeometryGroup();
        for (var y = rect.Top; y < rect.Bottom; y += cell)
        {
            for (var x = rect.Left; x < rect.Right; x += cell)
            {
                group.Children.Add(new RectangleGeometry(new Rect(x, y, cell - 0.8, cell - 0.8)));
            }
        }
        group.Freeze();
        return group;
    }

    /// <summary>HeyClicky's island outline (NotchIslandShape): the body is <paramref name="rect"/> minus a
    /// <paramref name="topCornerFlare"/> strip on each side; its top corners curve outward into the edge it hangs from and
    /// its bottom corners are rounded by <paramref name="bottomCornerRadius"/>.</summary>
    public static Geometry Island(Rect rect, double bottomCornerRadius, double topCornerFlare)
    {
        var flare = Math.Min(topCornerFlare, rect.Width / 2);
        var bodyMinX = rect.Left + flare;
        var bodyMaxX = rect.Right - flare;
        var radius = Math.Max(0, Math.Min(Math.Min(bottomCornerRadius, (bodyMaxX - bodyMinX) / 2), rect.Height - flare));
        var figure = new PathFigure { StartPoint = new Point(rect.Left, rect.Top), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Top), true));
        // Top-right flare: a quarter curve from the edge down into the island's straight side.
        figure.Segments.Add(new QuadraticBezierSegment(new Point(bodyMaxX, rect.Top), new Point(bodyMaxX, rect.Top + flare), true));
        figure.Segments.Add(new LineSegment(new Point(bodyMaxX, rect.Bottom - radius), true));
        figure.Segments.Add(new ArcSegment(new Point(bodyMaxX - radius, rect.Bottom), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(bodyMinX + radius, rect.Bottom), true));
        figure.Segments.Add(new ArcSegment(new Point(bodyMinX, rect.Bottom - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(bodyMinX, rect.Top + flare), true));
        figure.Segments.Add(new QuadraticBezierSegment(new Point(bodyMinX, rect.Top), new Point(rect.Left, rect.Top), true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    /// <summary>A side drawer's outline: the screen-side corners square, the others rounded.</summary>
    public static Geometry Drawer(Rect rect, double radius, bool isLeft)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
        var figure = new PathFigure { IsClosed = true };
        if (isLeft)
        {
            figure.StartPoint = new Point(rect.Left, rect.Top);
            figure.Segments.Add(new LineSegment(new Point(rect.Right - radius, rect.Top), true));
            figure.Segments.Add(new ArcSegment(new Point(rect.Right, rect.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Bottom - radius), true));
            figure.Segments.Add(new ArcSegment(new Point(rect.Right - radius, rect.Bottom), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(rect.Left, rect.Bottom), true));
        }
        else
        {
            figure.StartPoint = new Point(rect.Right, rect.Top);
            figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Bottom), true));
            figure.Segments.Add(new LineSegment(new Point(rect.Left + radius, rect.Bottom), true));
            figure.Segments.Add(new ArcSegment(new Point(rect.Left, rect.Bottom - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(rect.Left, rect.Top + radius), true));
            figure.Segments.Add(new ArcSegment(new Point(rect.Left + radius, rect.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
        }
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>Segoe Fluent Icons glyphs standing in for the Mac's SF Symbols.</summary>
public static class Icons
{
    public const string Settings = "";        // gearshape.fill
    public const string Power = "";           // power
    public const string Microphone = "";      // mic.fill
    public const string Stop = "";            // stop.circle.fill (drawn in a circle)
    public const string CheckMark = "";       // checkmark
    public const string Error = "";           // exclamationmark.circle
    public const string Copy = "";            // doc.on.doc
    public const string Close = "";           // xmark
    public const string Keyboard = "";        // keyboard
    public const string Globe = "";           // globe
    public const string TextCursor = "";      // text.cursor
    public const string Wave = "";            // hand.wave.fill
    public const string Waveform = "";        // waveform
    public const string Mail = "";            // envelope.fill
    public const string Sparkle = "";         // sparkles
    public const string Eye = "";             // eye.fill
    public const string Completed = "";       // checkmark.circle.fill

    /// <summary>The glyph for a tour card's SF Symbol name (Companion.TourSymbols).</summary>
    public static string ForTourSymbol(string? symbol) => symbol switch
    {
        "hand.wave.fill" => Wave,
        "waveform" => Microphone,
        "envelope.fill" => Mail,
        "sparkles" => Sparkle,
        "eye.fill" => Eye,
        "checkmark.circle.fill" => Completed,
        "power" => Power,
        _ => Sparkle,
    };
}
