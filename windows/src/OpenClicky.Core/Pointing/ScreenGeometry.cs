//
//  ScreenGeometry.cs
//  OpenClicky (Windows)
//
//  Plain point / rect records for the pointing logic, standing in for CGPoint / CGRect.
//
//  Coordinate convention on Windows: origin at the top-left of the primary monitor, y grows DOWN
//  (Win32 / WPF virtual-screen space). Global rects are in DIPs unless a type says otherwise
//  (screenshots and OCR boxes are in image pixels, origin top-left of the image). The Mac's AppKit
//  space is y-up with the origin at the bottom-left of the main display; every Swift formula that
//  used minY/maxY in AppKit space is converted where it is ported and says so.
//

namespace OpenClicky.Core.Pointing;

public readonly record struct ScreenPoint(double X, double Y)
{
    public static readonly ScreenPoint Zero = new(0, 0);

    public double DistanceTo(ScreenPoint other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
}

public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public static readonly ScreenRect Empty = new(0, 0, 0, 0);

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public ScreenPoint Center => new(MidX, MidY);

    /// <summary>True when the rect has no area (CGRect.isEmpty).</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Half-open containment, like CGRect.contains: left/top edges are inside, right/bottom are not.</summary>
    public bool Contains(ScreenPoint point) =>
        !IsEmpty && point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    /// <summary>Grows (negative inset) or shrinks the rect on every side, like CGRect.insetBy.</summary>
    public ScreenRect Inset(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);
}
