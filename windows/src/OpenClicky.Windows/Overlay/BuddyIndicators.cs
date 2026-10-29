//
//  BuddyIndicators.cs
//  OpenClicky (Windows port of OverlayWindow.swift's BlueCursorWaveformView and
//  BlueCursorSpinnerView, and the pointer triangle)
//
//  The three things the pointer can be: the red-orange triangle with its glow, five bars that
//  follow the microphone while listening, and a spinning arc while thinking. Each draws itself
//  from the clock when Tick() is called (the overlay and the island call it every frame while the
//  indicator is visible).
//

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Overlay;

/// <summary>The pointer: a 16 × 16 triangle, rotated (-35° at rest), with a glow of the cursor colour.</summary>
public sealed class PointerTriangleView : Grid
{
    private readonly RotateTransform rotation = new(-35);
    private readonly ScaleTransform scale = new(1, 1);
    private readonly DropShadowEffect glow;
    private readonly double baseGlowRadius;

    public PointerTriangleView(double size = 16, Color? color = null, double glowRadius = 8)
    {
        var fillColor = color ?? DS.Colors.OverlayCursor;
        baseGlowRadius = glowRadius;
        Width = size;
        Height = size;
        IsHitTestVisible = false;
        var path = new Path { Data = Shapes.Triangle(size, size), Fill = DS.Brush(fillColor), Width = size, Height = size };
        glow = new DropShadowEffect { Color = fillColor, ShadowDepth = 0, BlurRadius = glowRadius * 2, Opacity = 1, RenderingBias = RenderingBias.Performance };
        path.Effect = glowRadius > 0 ? glow : null;
        Children.Add(path);
        RenderTransformOrigin = new Point(0.5, 0.5);
        path.RenderTransformOrigin = new Point(0.5, 0.5);
        path.RenderTransform = rotation;
        RenderTransform = scale;
    }

    public double RotationDegrees
    {
        get => rotation.Angle;
        set => rotation.Angle = value;
    }

    /// <summary>The flight's swoop: grows to 1.3 at the apex; the glow widens with it (radius 8 + (scale − 1) · 20).</summary>
    public double FlightScale
    {
        get => scale.ScaleX;
        set
        {
            scale.ScaleX = value;
            scale.ScaleY = value;
            glow.BlurRadius = (baseGlowRadius + (value - 1.0) * 20) * 2;
        }
    }

    /// <summary>Dimmed: half opacity, no glow.</summary>
    public bool IsDimmed
    {
        set
        {
            glow.Opacity = value ? 0 : 1;
            Opacity = value ? 0.5 : 1;
        }
    }
}

/// <summary>Five bars that follow the microphone while listening (BlueCursorWaveformView): 2 wide, 2 apart, a profile of
/// 0.4/0.7/1/0.7/0.4, a slow idle pulse, and a 6-radius glow.</summary>
public sealed class WaveformView : Canvas
{
    private static readonly double[] ListeningBarProfile = { 0.4, 0.7, 1.0, 0.7, 0.4 };
    private readonly Rectangle[] bars = new Rectangle[5];
    private readonly Tween smoothedLevel = new(0);

    public WaveformView(Color? color = null)
    {
        var barColor = color ?? DS.Colors.OverlayCursor;
        Width = 18;
        Height = 18;
        IsHitTestVisible = false;
        var brush = DS.Brush(barColor);
        for (var index = 0; index < bars.Length; index++)
        {
            bars[index] = new Rectangle { Width = 2, Fill = brush, RadiusX = 1, RadiusY = 1 };
            SetLeft(bars[index], index * 4);
            Children.Add(bars[index]);
        }
        Effect = new DropShadowEffect { Color = barColor, ShadowDepth = 0, BlurRadius = 12, Opacity = 0.6, RenderingBias = RenderingBias.Performance };
    }

    /// <summary>0…1 microphone level; the bars follow it over 0.08 s, linearly.</summary>
    public double AudioPowerLevel
    {
        set => smoothedLevel.AnimateTo(value, 0.08, Easing.Linear);
    }

    public void Tick()
    {
        var time = MotionClock.Now;
        var level = smoothedLevel.Value;
        for (var index = 0; index < bars.Length; index++)
        {
            var animationPhase = time * 3.6 + index * 0.35;
            var normalizedAudioPowerLevel = Math.Max(level - 0.008, 0);
            var easedAudioPowerLevel = Math.Pow(Math.Min(normalizedAudioPowerLevel * 2.85, 1), 0.76);
            var reactiveHeight = easedAudioPowerLevel * 10 * ListeningBarProfile[index];
            var idlePulse = (Math.Sin(animationPhase) + 1) / 2 * 1.5;
            var height = 3 + reactiveHeight + idlePulse;
            bars[index].Height = height;
            SetTop(bars[index], (Height - height) / 2);
        }
    }
}

/// <summary>The thinking indicator (BlueCursorSpinnerView): a 14-wide ring trimmed to 0.15…0.85, its colour fading in along
/// the arc (an angular gradient), spinning once every 0.8 s, with a glow.</summary>
public sealed class SpinnerView : FrameworkElement
{
    private const double Diameter = 14;
    private const double LineWidth = 2.5;
    private const int SegmentCount = 40;
    private readonly RotateTransform rotation = new(0, Diameter / 2, Diameter / 2);
    private readonly Pen[] segmentPens = new Pen[SegmentCount];
    private readonly Brush capBrush;

    public SpinnerView(Color? color = null)
    {
        var strokeColor = color ?? DS.Colors.OverlayCursor;
        Width = Diameter;
        Height = Diameter;
        IsHitTestVisible = false;
        RenderTransform = rotation;
        for (var index = 0; index < SegmentCount; index++)
        {
            var location = 0.15 + (0.85 - 0.15) * (index + 0.5) / SegmentCount;
            var pen = new Pen(DS.Brush(strokeColor, location), LineWidth) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            segmentPens[index] = pen;
        }
        capBrush = DS.Brush(strokeColor, 0.85);
        Effect = new DropShadowEffect { Color = strokeColor, ShadowDepth = 0, BlurRadius = 12, Opacity = 0.6, RenderingBias = RenderingBias.Performance };
    }

    public void Tick() => rotation.Angle = MotionClock.Now % 0.8 / 0.8 * 360;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var radius = (Diameter - LineWidth) / 2;
        var center = new Point(Diameter / 2, Diameter / 2);
        Point PointAt(double fraction)
        {
            // Fraction 0 is three o'clock, increasing clockwise (y down), as SwiftUI's Circle path and AngularGradient.
            var angle = fraction * 2 * Math.PI;
            return new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        }
        for (var index = 0; index < SegmentCount; index++)
        {
            var from = 0.15 + (0.85 - 0.15) * index / SegmentCount;
            var to = 0.15 + (0.85 - 0.15) * (index + 1.05) / SegmentCount;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(PointAt(from), false, false);
                context.ArcTo(PointAt(to), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            drawingContext.DrawGeometry(null, segmentPens[index], geometry);
        }
        // The round cap at the bright end.
        drawingContext.DrawEllipse(capBrush, null, PointAt(0.85), LineWidth / 2, LineWidth / 2);
    }
}
