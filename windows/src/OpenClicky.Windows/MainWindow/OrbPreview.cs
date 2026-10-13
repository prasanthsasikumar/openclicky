// Ports OrbPreview (Dictation/UI/SettingsOrbPage.swift): a still orb for The Orb page's style
// tiles, in the chosen theme. Pill: a capsule with two short bars; classic: a disc with the mark;
// pixel: the same disc with the mark drawn in squares.

using System.Windows;
using System.Windows.Media;
using OpenClicky.Core.Dictation;

namespace OpenClicky.Windows.AppWindow;

internal sealed class OrbPreview : FrameworkElement
{
    private readonly OrbLook look;
    private readonly OrbTheme theme;
    private readonly OrbSize size;

    public OrbPreview(OrbLook look, OrbTheme theme, OrbSize size)
    {
        this.look = look;
        this.theme = theme;
        this.size = size;
        IsHitTestVisible = false;
    }

    public static Color Fill(OrbTheme theme) => theme switch
    {
        OrbTheme.Black => Color.FromRgb(0x29, 0x29, 0x29),
        OrbTheme.Coral => Color.FromRgb(0xE3, 0x54, 0x36),
        _ => Color.FromRgb(0xED, 0xED, 0xED),
    };

    /// <summary>The pill's size (the orb's OrbMetrics.pillSize).</summary>
    public static Size PillSize(OrbSize size) => size == OrbSize.Mini ? new Size(44, 18) : new Size(64, 24);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var fill = new SolidColorBrush(Fill(theme));
        var ink = theme == OrbTheme.Mist ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) : new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF));
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        var pill = PillSize(size);
        // A soft shadow under the orb, like the real one floating over the screen.
        var shadow = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
        if (look == OrbLook.Pill)
        {
            var rect = new Rect(center.X - pill.Width / 2, center.Y - pill.Height / 2, pill.Width, pill.Height);
            drawingContext.DrawRoundedRectangle(shadow, null, new Rect(rect.X, rect.Y + 2, rect.Width, rect.Height), pill.Height / 2, pill.Height / 2);
            drawingContext.DrawRoundedRectangle(fill, null, rect, pill.Height / 2, pill.Height / 2);
            drawingContext.DrawRoundedRectangle(ink, null, new Rect(center.X - 10.5, center.Y - 1.25, 9, 2.5), 1.25, 1.25);
            drawingContext.DrawRoundedRectangle(ink, null, new Rect(center.X + 1.5, center.Y - 1.25, 9, 2.5), 1.25, 1.25);
            return;
        }
        var diameter = pill.Height * 1.4;
        drawingContext.DrawEllipse(shadow, null, new Point(center.X, center.Y + 2), diameter / 2, diameter / 2);
        drawingContext.DrawEllipse(fill, null, center, diameter / 2, diameter / 2);
        var markSide = pill.Height * 0.6;
        var mark = OpenClickyMarkDrawing.Geometry(center.X - markSide / 2, center.Y - markSide / 2, markSide);
        if (look == OrbLook.Classic)
        {
            drawingContext.DrawGeometry(ink, null, mark);
            return;
        }
        // Pixel: the mark sampled on a coarse grid of squares.
        const int cells = 7;
        var cell = markSide / cells;
        for (var row = 0; row < cells; row++)
        {
            for (var column = 0; column < cells; column++)
            {
                var cellCenter = new Point(center.X - markSide / 2 + (column + 0.5) * cell, center.Y - markSide / 2 + (row + 0.5) * cell);
                if (mark.FillContains(cellCenter)) drawingContext.DrawRectangle(ink, null, new Rect(cellCenter.X - cell * 0.42, cellCenter.Y - cell * 0.42, cell * 0.84, cell * 0.84));
            }
        }
    }
}
