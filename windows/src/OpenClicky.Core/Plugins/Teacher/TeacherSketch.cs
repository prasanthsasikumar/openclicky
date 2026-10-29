//
//  TeacherSketch.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherSketch.swift, and the geometry half of TeacherBoardView.swift)
//
//  The Teacher plugin's drawing language: what a model writes when it draws a lesson on the
//  screen ("shape 80 240 320 240 80 60", "text 200 268 \"b\" blue big"), and the parser that turns
//  it into strokes. A small line language instead of nested JSON because every lane can carry a
//  string (a Realtime tool argument, a Claude tool argument, a plan's [ACTION:] line) and models
//  write it more reliably and in fewer tokens than arrays of objects.
//
//  Also the hand-drawn geometry each stroke becomes (the Mac drew it in TeacherBoardView with
//  SwiftUI's trimmed paths): here every stroke is a polyline in view units, trimmed by length as it
//  is drawn, so the WPF board only strokes lines. Pure: unit-tested.
//

using System.Globalization;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Plugins.Teacher;

/// <summary>The colours a sketch may name. How each looks depends on the board's style (dark ink on a
/// white page, chalk on a dark one), so the sketch names roles, not hex values.</summary>
public enum TeacherInk
{
    Ink,
    Red,
    Blue,
    Green,
    Orange,
    Purple,
    Grey,
}

public static class TeacherInks
{
    public static TeacherInk? Named(string word) => word switch
    {
        "ink" or "black" or "pen" or "white" or "chalk" => TeacherInk.Ink,
        "red" => TeacherInk.Red,
        "blue" => TeacherInk.Blue,
        "green" => TeacherInk.Green,
        "orange" or "yellow" => TeacherInk.Orange,
        "purple" or "violet" or "pink" => TeacherInk.Purple,
        "grey" or "gray" => TeacherInk.Grey,
        _ => null,
    };
}

/// <summary>What a stroke draws.</summary>
public enum TeacherStrokeKind
{
    /// <summary>Straight segments through the points (two for a line).</summary>
    Line,
    /// <summary>A line with an arrowhead at its last point.</summary>
    Arrow,
    /// <summary>A smooth freehand curve through the points.</summary>
    Curve,
    /// <summary>A closed outline through the points (a triangle, a prism, a block of glass).</summary>
    Shape,
    /// <summary>Points[0] is the centre; Radius.</summary>
    Circle,
    /// <summary>Part of a circle around Points[0], angles in degrees, 0 = right, 90 = down.</summary>
    Arc,
    /// <summary>Points[0] is the top-left corner, Points[1] the size.</summary>
    Box,
    /// <summary>The little square in a right angle: the points are the vertex and a point on each arm.</summary>
    RightAngle,
    /// <summary>The arc marking an angle: the points are the vertex and a point on each arm.</summary>
    Angle,
    /// <summary>A filled dot at Points[0].</summary>
    Dot,
    /// <summary>Handwriting centred on Points[0].</summary>
    Text,
    /// <summary>Nothing drawn: the pen rests for Seconds (time to talk about what is there).</summary>
    Pause,
}

/// <summary>One thing drawn on the board, in board units (origin top-left, y down).</summary>
public sealed record TeacherStroke
{
    public required TeacherStrokeKind Kind { get; init; }
    public IReadOnlyList<ScreenPoint> Points { get; init; } = [];
    /// <summary>Circle and arc.</summary>
    public double Radius { get; init; }
    /// <summary>Arc only.</summary>
    public double StartDegrees { get; init; }
    public double EndDegrees { get; init; }
    /// <summary>Text only: the words.</summary>
    public string Words { get; init; } = "";
    /// <summary>Pause only.</summary>
    public double Seconds { get; init; }
    public TeacherInk Ink { get; init; } = TeacherInk.Ink;
    public bool IsDashed { get; init; }
    public bool IsFilled { get; init; }
    /// <summary>Text only: the words start at the point instead of being centred on it (worked solutions).</summary>
    public bool IsLeftAligned { get; init; }
    /// <summary>1 normal, 0.65 thin, 1.6 thick; for text, the size.</summary>
    public double Weight { get; init; } = 1;
}

/// <summary>A whole drawing: the board the model drew on and what is on it, in order.</summary>
public sealed record TeacherSketch(double BoardWidth, double BoardHeight, IReadOnlyList<TeacherStroke> Strokes)
{
    public const double DefaultBoardWidth = 400;
    public const double DefaultBoardHeight = 300;
    public const int MaximumStrokes = 80;
}

public static class TeacherSketchParser
{
    /// <summary>What the parser made of the text: the sketch, or null when nothing drawable came out, plus the
    /// lines it could not read (logged, never shown).</summary>
    public sealed record Result(TeacherSketch? Sketch, IReadOnlyList<string> SkippedLines);

    private static readonly HashSet<string> OptionWords =
        ["left", "dashed", "dotted", "fill", "filled", "shaded", "thick", "big", "large", "bold", "thin", "small", "tiny"];

    public static Result Parse(string source)
    {
        var boardWidth = TeacherSketch.DefaultBoardWidth;
        var boardHeight = TeacherSketch.DefaultBoardHeight;
        var strokes = new List<TeacherStroke>();
        var skippedLines = new List<string>();

        foreach (var rawLine in CommandLines(source))
        {
            var line = rawLine.Trim(' ', '\t');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var (quotedText, words) = SplitQuotedText(line);
            if (words.Count == 0) continue;
            var commandWord = words[0].ToLowerInvariant();
            var rest = words.Skip(1).ToList();
            var numbers = rest.Select(Number).Where(value => value is not null).Select(value => value!.Value).ToList();
            var options = rest.Where(word => Number(word) is null).Select(word => word.ToLowerInvariant()).ToList();

            if (commandWord is "board" or "size")
            {
                if (numbers.Count >= 2 && numbers[0] >= 50 && numbers[1] >= 50)
                {
                    boardWidth = Math.Min(numbers[0], 2000);
                    boardHeight = Math.Min(numbers[1], 2000);
                }
                continue;
            }
            if (strokes.Count >= TeacherSketch.MaximumStrokes)
            {
                skippedLines.Add(line);
                continue;
            }
            if (Stroke(commandWord, numbers, options, quotedText) is not { } stroke)
            {
                skippedLines.Add(line);
                continue;
            }
            foreach (var option in options)
            {
                if (TeacherInks.Named(option) is { } ink) stroke = stroke with { Ink = ink };
                stroke = option switch
                {
                    "dashed" or "dotted" => stroke with { IsDashed = true },
                    "fill" or "filled" or "shaded" => stroke with { IsFilled = true },
                    "left" => stroke with { IsLeftAligned = true },
                    "thick" or "big" or "large" or "bold" => stroke with { Weight = 1.6 },
                    "thin" or "small" or "tiny" => stroke with { Weight = 0.65 },
                    _ => stroke,
                };
            }
            strokes.Add(stroke);
        }

        // Points far outside the board are a typo, not a drawing: pull them back onto it.
        var marginX = boardWidth * 0.1;
        var marginY = boardHeight * 0.1;
        ScreenPoint Clamp(ScreenPoint point) =>
            new(Math.Min(Math.Max(point.X, -marginX), boardWidth + marginX), Math.Min(Math.Max(point.Y, -marginY), boardHeight + marginY));
        strokes = strokes.Select(stroke =>
        {
            // The second point of a box is its size, not a place.
            if (stroke.Kind == TeacherStrokeKind.Box)
            {
                return stroke.Points.Count == 0 ? stroke : stroke with { Points = [Clamp(stroke.Points[0]), .. stroke.Points.Skip(1)] };
            }
            return stroke with { Points = stroke.Points.Select(Clamp).ToList() };
        }).ToList();

        var drawsSomething = strokes.Any(stroke => stroke.Kind != TeacherStrokeKind.Pause);
        return new Result(drawsSomething ? new TeacherSketch(boardWidth, boardHeight, strokes) : null, skippedLines);
    }

    private static TeacherStroke? Stroke(string command, IReadOnlyList<double> numbers, IReadOnlyList<string> options, string? quotedText)
    {
        List<ScreenPoint> Pairs()
        {
            var points = new List<ScreenPoint>();
            for (var index = 0; index + 1 < numbers.Count; index += 2) points.Add(new ScreenPoint(numbers[index], numbers[index + 1]));
            return points;
        }
        switch (command)
        {
            case "line" or "segment":
            {
                var points = Pairs();
                return points.Count >= 2 ? new TeacherStroke { Kind = TeacherStrokeKind.Line, Points = points } : null;
            }
            case "arrow" or "pointer":
            {
                var points = Pairs();
                return points.Count >= 2 ? new TeacherStroke { Kind = TeacherStrokeKind.Arrow, Points = points } : null;
            }
            case "curve" or "path" or "freehand" or "wave":
            {
                var points = Pairs();
                return points.Count >= 2 ? new TeacherStroke { Kind = TeacherStrokeKind.Curve, Points = points } : null;
            }
            case "shape" or "polygon" or "triangle" or "poly":
            {
                var points = Pairs();
                return points.Count >= 3 ? new TeacherStroke { Kind = TeacherStrokeKind.Shape, Points = points } : null;
            }
            case "circle":
                if (numbers.Count < 3 || numbers[2] <= 0) return null;
                return new TeacherStroke { Kind = TeacherStrokeKind.Circle, Radius = numbers[2], Points = [new ScreenPoint(numbers[0], numbers[1])] };
            case "arc":
                if (numbers.Count < 5 || numbers[2] <= 0) return null;
                return new TeacherStroke
                {
                    Kind = TeacherStrokeKind.Arc, Radius = numbers[2], StartDegrees = numbers[3], EndDegrees = numbers[4],
                    Points = [new ScreenPoint(numbers[0], numbers[1])],
                };
            case "box" or "rect" or "rectangle":
                if (numbers.Count < 4 || numbers[2] <= 0 || numbers[3] <= 0) return null;
                return new TeacherStroke { Kind = TeacherStrokeKind.Box, Points = [new ScreenPoint(numbers[0], numbers[1]), new ScreenPoint(numbers[2], numbers[3])] };
            case "right_angle" or "rightangle" or "square_corner":
            {
                var points = Pairs();
                return points.Count >= 3 ? new TeacherStroke { Kind = TeacherStrokeKind.RightAngle, Points = points.Take(3).ToList() } : null;
            }
            case "angle":
            {
                var points = Pairs();
                return points.Count >= 3 ? new TeacherStroke { Kind = TeacherStrokeKind.Angle, Points = points.Take(3).ToList() } : null;
            }
            case "dot" or "point":
                if (numbers.Count < 2) return null;
                return new TeacherStroke { Kind = TeacherStrokeKind.Dot, Points = [new ScreenPoint(numbers[0], numbers[1])] };
            case "text" or "label" or "write" or "note":
            {
                if (numbers.Count < 2) return null;
                // Unquoted words are accepted too, minus the option words (colours, sizes).
                var unquoted = string.Join(" ", options.Where(option => TeacherInks.Named(option) is null && !OptionWords.Contains(option)));
                var words = (quotedText ?? unquoted).Trim(' ', '\t');
                if (words.Length == 0) return null;
                return new TeacherStroke
                {
                    Kind = TeacherStrokeKind.Text, Words = words.Length > 80 ? words[..80] : words,
                    Points = [new ScreenPoint(numbers[0], numbers[1])],
                };
            }
            case "pause" or "wait":
            {
                var seconds = numbers.Count > 0 ? numbers[0] : 1;
                return new TeacherStroke { Kind = TeacherStrokeKind.Pause, Seconds = Math.Min(Math.Max(seconds, 0.1), 4) };
            }
            default:
                return null;
        }
    }

    /// <summary>Commands are one per line; a model that wrote everything on one line may separate them with ";"
    /// instead. A ";" inside quotes is part of the words.</summary>
    public static IReadOnlyList<string> CommandLines(string source)
    {
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        var insideQuotes = false;
        foreach (var character in source)
        {
            if (character is '"' or '“' or '”') insideQuotes = !insideQuotes;
            if (character is '\n' or '\r' or '\u2028' or '\u2029' || (character == ';' && !insideQuotes))
            {
                lines.Add(current.ToString());
                current.Clear();
                insideQuotes = false;
            }
            else
            {
                current.Append(character);
            }
        }
        lines.Add(current.ToString());
        return lines;
    }

    /// <summary>The words in quotes (straight or curly) taken out, and the rest split on spaces and commas.</summary>
    private static (string? QuotedText, List<string> Words) SplitQuotedText(string line)
    {
        var normalized = line.Replace('“', '"').Replace('”', '"');
        string? quotedText = null;
        var remainder = normalized;
        var open = normalized.IndexOf('"');
        if (open >= 0)
        {
            var close = normalized.IndexOf('"', open + 1);
            if (close >= 0)
            {
                quotedText = normalized[(open + 1)..close];
                remainder = normalized[..open] + " " + normalized[(close + 1)..];
            }
        }
        var words = remainder.Split([' ', ',', '\t', '(', ')'], StringSplitOptions.RemoveEmptyEntries).ToList();
        return (quotedText, words);
    }

    private static double? Number(string word) =>
        double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
}

/// <summary>When each stroke starts and how long it takes to draw: lines at a steady hand's pace, words a letter
/// at a time, so the drawing builds up while the voice explains it.</summary>
public static class TeacherSketchTimeline
{
    public readonly record struct Entry(double Start, double Duration);

    public const double GapBetweenStrokes = 0.18;

    public static IReadOnlyList<Entry> Entries(TeacherSketch sketch)
    {
        var boardDiagonal = Math.Max(Math.Sqrt(sketch.BoardWidth * sketch.BoardWidth + sketch.BoardHeight * sketch.BoardHeight), 1);
        var clock = 0.0;
        var entries = new List<Entry>();
        foreach (var stroke in sketch.Strokes)
        {
            var duration = DrawingSeconds(stroke, boardDiagonal);
            entries.Add(new Entry(clock, duration));
            clock += duration + GapBetweenStrokes;
        }
        return entries;
    }

    public static double TotalSeconds(TeacherSketch sketch)
    {
        var entries = Entries(sketch);
        return entries.Count == 0 ? 0 : entries[^1].Start + entries[^1].Duration;
    }

    public static double DrawingSeconds(TeacherStroke stroke, double boardDiagonal) => stroke.Kind switch
    {
        TeacherStrokeKind.Text => Math.Min(Math.Max(stroke.Words.Length * 0.07, 0.3), 2.2),
        TeacherStrokeKind.Pause => stroke.Seconds,
        TeacherStrokeKind.Dot => 0.15,
        TeacherStrokeKind.RightAngle or TeacherStrokeKind.Angle => 0.3,
        _ => Math.Min(Math.Max(TeacherSketchGeometry.ApproximateLength(stroke) / boardDiagonal * 1.6, 0.35), 1.6),
    };
}

public static class TeacherSketchGeometry
{
    public static double Distance(ScreenPoint first, ScreenPoint second) => first.DistanceTo(second);

    public static double PolylineLength(IReadOnlyList<ScreenPoint> points, bool closed)
    {
        if (points.Count < 2) return 0;
        var total = 0.0;
        for (var index = 1; index < points.Count; index++) total += Distance(points[index - 1], points[index]);
        if (closed) total += Distance(points[0], points[^1]);
        return total;
    }

    public static double ApproximateLength(TeacherStroke stroke) => stroke.Kind switch
    {
        TeacherStrokeKind.Line or TeacherStrokeKind.Arrow or TeacherStrokeKind.Curve => PolylineLength(stroke.Points, closed: false),
        TeacherStrokeKind.Shape => PolylineLength(stroke.Points, closed: true),
        TeacherStrokeKind.Circle => 2 * Math.PI * stroke.Radius,
        TeacherStrokeKind.Arc => Math.Abs(stroke.EndDegrees - stroke.StartDegrees) / 180 * Math.PI * stroke.Radius,
        TeacherStrokeKind.Box => stroke.Points.Count > 1 ? 2 * (stroke.Points[1].X + stroke.Points[1].Y) : 0,
        _ => 0,
    };
}

/// <summary>Where the drawing actually is. Models draw on part of their board and put words past its edge, so
/// the board is laid out around what was drawn (words estimated at their written size), not around the size the
/// model declared.</summary>
public sealed record TeacherSketchLayout
{
    /// <summary>In board units.</summary>
    public ScreenRect DrawingBounds { get; }
    public double Scale { get; }
    /// <summary>Where board point (0, 0) lands in the view (origin top-left).</summary>
    public ScreenPoint Origin { get; }

    /// <summary>A word's estimated size in board units at weight 1 (the handwriting at the board's base size).</summary>
    public const double LetterWidth = 11;
    public const double LineHeight = 30;
    /// <summary>Kept around the drawing, in board units.</summary>
    public const double Margin = 10;

    public TeacherSketchLayout(TeacherSketch sketch, double viewWidth, double viewHeight)
    {
        var bounds = DrawingBoundsOf(sketch);
        DrawingBounds = bounds;
        Scale = Math.Min(viewWidth / Math.Max(bounds.Width, 1), viewHeight / Math.Max(bounds.Height, 1));
        Origin = new ScreenPoint((viewWidth - bounds.Width * Scale) / 2 - bounds.X * Scale,
                                 (viewHeight - bounds.Height * Scale) / 2 - bounds.Y * Scale);
    }

    public ScreenPoint ViewPoint(ScreenPoint boardPoint) => new(Origin.X + boardPoint.X * Scale, Origin.Y + boardPoint.Y * Scale);

    /// <summary>The text's written size in view units for a stroke weight.</summary>
    public double FontSize(double weight) => Math.Max(12, Math.Min(LineHeight * 0.75 * weight * Scale, 48));

    public static ScreenRect DrawingBoundsOf(TeacherSketch sketch)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Include(double x, double y, double width, double height)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + width);
            maxY = Math.Max(maxY, y + height);
        }
        foreach (var stroke in sketch.Strokes)
        {
            switch (stroke.Kind)
            {
                case TeacherStrokeKind.Pause:
                    continue;
                case TeacherStrokeKind.Text:
                {
                    if (stroke.Points.Count == 0) continue;
                    var anchor = stroke.Points[0];
                    var width = stroke.Words.Length * LetterWidth * stroke.Weight;
                    var height = LineHeight * stroke.Weight;
                    Include(stroke.IsLeftAligned ? anchor.X : anchor.X - width / 2, anchor.Y - height / 2, width, height);
                    break;
                }
                case TeacherStrokeKind.Circle or TeacherStrokeKind.Arc:
                {
                    if (stroke.Points.Count == 0) continue;
                    var centre = stroke.Points[0];
                    Include(centre.X - stroke.Radius, centre.Y - stroke.Radius, stroke.Radius * 2, stroke.Radius * 2);
                    break;
                }
                case TeacherStrokeKind.Box:
                    if (stroke.Points.Count < 2) continue;
                    Include(stroke.Points[0].X, stroke.Points[0].Y, stroke.Points[1].X, stroke.Points[1].Y);
                    break;
                default:
                    foreach (var point in stroke.Points) Include(point.X, point.Y, 0, 0);
                    break;
            }
        }
        if (double.IsInfinity(minX)) return new ScreenRect(0, 0, sketch.BoardWidth, sketch.BoardHeight);
        var bounds = new ScreenRect(minX, minY, maxX - minX, maxY - minY);
        // A drawing that is one short line still gets a board of some height.
        var minimumSide = Math.Max(sketch.BoardWidth, sketch.BoardHeight) * 0.25;
        if (bounds.Width < minimumSide) bounds = bounds.Inset(-(minimumSide - bounds.Width) / 2, 0);
        if (bounds.Height < minimumSide) bounds = bounds.Inset(0, -(minimumSide - bounds.Height) / 2);
        return bounds.Inset(-Margin, -Margin);
    }
}

/// <summary>A stroke as the hand draws it, in view units: one polyline (closed shapes repeat their first point),
/// the arrowhead apart. Seeded, so a stroke wobbles the same way every frame.</summary>
public static class TeacherStrokePaths
{
    /// <summary>The line through the stroke; empty for dots, words and pauses (drawn otherwise).</summary>
    public static IReadOnlyList<ScreenPoint> Polyline(TeacherStroke stroke, TeacherSketchLayout layout, int seed)
    {
        var random = new WobbleRandom(seed);
        var viewPoints = stroke.Points.Select(layout.ViewPoint).ToList();
        switch (stroke.Kind)
        {
            case TeacherStrokeKind.Line or TeacherStrokeKind.Arrow:
                return WobblyPolyline(viewPoints, closed: false, ref random);
            case TeacherStrokeKind.Shape:
                return WobblyPolyline(viewPoints, closed: true, ref random);
            case TeacherStrokeKind.Curve:
                return SmoothCurve(viewPoints);
            case TeacherStrokeKind.Box:
            {
                if (viewPoints.Count < 1 || stroke.Points.Count < 2) return [];
                var origin = viewPoints[0];
                var width = stroke.Points[1].X * layout.Scale;
                var height = stroke.Points[1].Y * layout.Scale;
                ScreenPoint[] corners = [origin, new(origin.X + width, origin.Y), new(origin.X + width, origin.Y + height), new(origin.X, origin.Y + height)];
                return WobblyPolyline(corners, closed: true, ref random);
            }
            case TeacherStrokeKind.Circle:
                if (viewPoints.Count == 0) return [];
                // A hand closes a circle a little past where it began.
                return WobblyArc(viewPoints[0], stroke.Radius * layout.Scale, -100, 270, ref random);
            case TeacherStrokeKind.Arc:
                if (viewPoints.Count == 0) return [];
                return WobblyArc(viewPoints[0], stroke.Radius * layout.Scale, stroke.StartDegrees, stroke.EndDegrees, ref random);
            case TeacherStrokeKind.RightAngle:
            {
                if (viewPoints.Count != 3) return [];
                var vertex = viewPoints[0];
                var towardsFirst = Unit(vertex, viewPoints[1]);
                var towardsSecond = Unit(vertex, viewPoints[2]);
                var side = Math.Min(16, 0.3 * Math.Min(vertex.DistanceTo(viewPoints[1]), vertex.DistanceTo(viewPoints[2])));
                return
                [
                    new(vertex.X + towardsFirst.X * side, vertex.Y + towardsFirst.Y * side),
                    new(vertex.X + (towardsFirst.X + towardsSecond.X) * side, vertex.Y + (towardsFirst.Y + towardsSecond.Y) * side),
                    new(vertex.X + towardsSecond.X * side, vertex.Y + towardsSecond.Y * side),
                ];
            }
            case TeacherStrokeKind.Angle:
            {
                if (viewPoints.Count != 3) return [];
                var vertex = viewPoints[0];
                var firstDegrees = Math.Atan2(viewPoints[1].Y - vertex.Y, viewPoints[1].X - vertex.X) * 180 / Math.PI;
                var secondDegrees = Math.Atan2(viewPoints[2].Y - vertex.Y, viewPoints[2].X - vertex.X) * 180 / Math.PI;
                // The inside of the angle: the short way round.
                if (secondDegrees - firstDegrees > 180) secondDegrees -= 360;
                if (secondDegrees - firstDegrees < -180) secondDegrees += 360;
                var radius = Math.Min(26, 0.35 * Math.Min(vertex.DistanceTo(viewPoints[1]), vertex.DistanceTo(viewPoints[2])));
                return WobblyArc(vertex, radius, firstDegrees, secondDegrees, ref random, wobble: 0.3);
            }
            default:
                return [];
        }
    }

    /// <summary>The first <paramref name="progress"/> (0…1) of the polyline by length: the stroke as far as the hand has got.</summary>
    public static IReadOnlyList<ScreenPoint> Trimmed(IReadOnlyList<ScreenPoint> points, double progress)
    {
        if (points.Count < 2 || progress >= 1) return points;
        if (progress <= 0) return [];
        var target = TeacherSketchGeometry.PolylineLength(points, closed: false) * progress;
        var trimmed = new List<ScreenPoint> { points[0] };
        var travelled = 0.0;
        for (var index = 1; index < points.Count; index++)
        {
            var length = points[index - 1].DistanceTo(points[index]);
            if (travelled + length >= target)
            {
                var fraction = length <= 0 ? 0 : (target - travelled) / length;
                trimmed.Add(new ScreenPoint(points[index - 1].X + (points[index].X - points[index - 1].X) * fraction,
                                            points[index - 1].Y + (points[index].Y - points[index - 1].Y) * fraction));
                return trimmed;
            }
            travelled += length;
            trimmed.Add(points[index]);
        }
        return trimmed;
    }

    /// <summary>The two strokes of an arrowhead at <paramref name="tip"/>, pointing away from <paramref name="tail"/>.</summary>
    public static IReadOnlyList<ScreenPoint> ArrowHead(ScreenPoint tip, ScreenPoint tail, double size)
    {
        var direction = Unit(tail, tip);
        var back = new ScreenPoint(tip.X - direction.X * size, tip.Y - direction.Y * size);
        var side = new ScreenPoint(-direction.Y * size * 0.55, direction.X * size * 0.55);
        return [new(back.X + side.X, back.Y + side.Y), tip, new(back.X - side.X, back.Y - side.Y)];
    }

    // MARK: - hand-drawn geometry

    /// <summary>Each segment cut into short pieces nudged sideways by a pixel or so.</summary>
    public static IReadOnlyList<ScreenPoint> WobblyPolyline(IReadOnlyList<ScreenPoint> points, bool closed, ref WobbleRandom random)
    {
        if (points.Count == 0) return [];
        var path = new List<ScreenPoint> { points[0] };
        List<ScreenPoint> route = closed ? [.. points, points[0]] : [.. points];
        for (var index = 1; index < route.Count; index++)
        {
            var start = route[index - 1];
            var end = route[index];
            var length = start.DistanceTo(end);
            var pieces = Math.Max((int)(length / 14), 1);
            var normal = new ScreenPoint(-(end.Y - start.Y) / Math.Max(length, 0.001), (end.X - start.X) / Math.Max(length, 0.001));
            for (var piece = 1; piece <= pieces; piece++)
            {
                var fraction = (double)piece / pieces;
                // No nudge at the corners, so shapes still meet.
                var nudge = piece == pieces ? 0 : random.Next(1.1);
                path.Add(new ScreenPoint(start.X + (end.X - start.X) * fraction + normal.X * nudge,
                                         start.Y + (end.Y - start.Y) * fraction + normal.Y * nudge));
            }
        }
        return path;
    }

    public static IReadOnlyList<ScreenPoint> WobblyArc(ScreenPoint centre, double radius, double fromDegrees, double toDegrees, ref WobbleRandom random, double wobble = 1.0)
    {
        var path = new List<ScreenPoint>();
        var sweep = toDegrees - fromDegrees;
        var steps = Math.Max((int)(Math.Abs(sweep) / 6), 2);
        for (var step = 0; step <= steps; step++)
        {
            var degrees = fromDegrees + sweep * step / steps;
            var wobbledRadius = radius + random.Next(wobble);
            path.Add(new ScreenPoint(centre.X + Math.Cos(degrees * Math.PI / 180) * wobbledRadius,
                                     centre.Y + Math.Sin(degrees * Math.PI / 180) * wobbledRadius));
        }
        return path;
    }

    /// <summary>Catmull-Rom through the points, as cubic Béziers sampled into short segments.</summary>
    public static IReadOnlyList<ScreenPoint> SmoothCurve(IReadOnlyList<ScreenPoint> points)
    {
        if (points.Count < 2) return [];
        const int samplesPerSegment = 12;
        var path = new List<ScreenPoint> { points[0] };
        for (var index = 0; index < points.Count - 1; index++)
        {
            var previous = points[Math.Max(index - 1, 0)];
            var start = points[index];
            var end = points[index + 1];
            var next = points[Math.Min(index + 2, points.Count - 1)];
            var control1 = new ScreenPoint(start.X + (end.X - previous.X) / 6, start.Y + (end.Y - previous.Y) / 6);
            var control2 = new ScreenPoint(end.X - (next.X - start.X) / 6, end.Y - (next.Y - start.Y) / 6);
            for (var sample = 1; sample <= samplesPerSegment; sample++)
            {
                var t = (double)sample / samplesPerSegment;
                var u = 1 - t;
                path.Add(new ScreenPoint(
                    u * u * u * start.X + 3 * u * u * t * control1.X + 3 * u * t * t * control2.X + t * t * t * end.X,
                    u * u * u * start.Y + 3 * u * u * t * control1.Y + 3 * u * t * t * control2.Y + t * t * t * end.Y));
            }
        }
        return path;
    }

    public static ScreenPoint Unit(ScreenPoint start, ScreenPoint end)
    {
        var length = Math.Max(start.DistanceTo(end), 0.001);
        return new ScreenPoint((end.X - start.X) / length, (end.Y - start.Y) / length);
    }
}

/// <summary>The same wobble every frame for a stroke (a fresh random each frame would make lines shiver).</summary>
public struct WobbleRandom
{
    private ulong state;

    public WobbleRandom(int seed) => state = unchecked((ulong)((long)seed * 2_654_435_761L + 0x9E37_79B9L)) | 1;

    public double Next(double amplitude)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return ((double)(state % 10_000) / 10_000 * 2 - 1) * amplitude;
    }
}
