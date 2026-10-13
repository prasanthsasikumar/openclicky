namespace OpenClicky.Core.Platform;

/// <summary>A point in physical screen pixels of the virtual desktop (origin top-left of the primary monitor).</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>
/// A rectangle in physical screen pixels of the virtual desktop (origin top-left of the primary
/// monitor, y grows downward). This is what Win32, UI Automation and DWM report to a
/// per-monitor-v2 DPI aware process, so every rectangle the platform layer hands out is in it.
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelPoint Center => new(Left + Width / 2, Top + Height / 2);

    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public bool Contains(PixelPoint point) => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>Grown by <paramref name="margin"/> on every side (negative shrinks).</summary>
    public PixelRect Inflated(int margin) => new(Left - margin, Top - margin, Width + 2 * margin, Height + 2 * margin);

    public bool IntersectsWith(PixelRect other) =>
        other.Left < Right && Left < other.Right && other.Top < Bottom && Top < other.Bottom;
}

/// <summary>A rectangle with fractional coordinates (OCR boxes in screenshot pixels, DIP bounds).</summary>
public readonly record struct DoubleRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public static DoubleRect Union(DoubleRect first, DoubleRect second)
    {
        double left = Math.Min(first.X, second.X), top = Math.Min(first.Y, second.Y);
        double right = Math.Max(first.Right, second.Right), bottom = Math.Max(first.Bottom, second.Bottom);
        return new DoubleRect(left, top, right - left, bottom - top);
    }
}
