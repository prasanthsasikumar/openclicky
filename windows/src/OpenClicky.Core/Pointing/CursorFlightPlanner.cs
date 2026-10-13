//
//  CursorFlightPlanner.cs
//  OpenClicky (Windows port of CursorFlightPlanner.swift)
//
//  Plans the cursor buddy's flights between displays. Each display has its own overlay window,
//  so a flight whose destination is on another display (docking into the island from another
//  monitor, or flying back to a mouse that moved to another screen) is split into two legs: to
//  the edge of the current display nearest the destination, then from the matching edge of the
//  destination display to the destination itself. Pure geometry, unit-tested.
//
//  Coordinates: global DIPs, y down (see ScreenGeometry.cs). Nothing here depends on the y
//  direction (nearest point and containment are symmetric), so the Swift logic is unchanged.
//

namespace OpenClicky.Core.Pointing;

public enum CursorFlightPurpose
{
    /// <summary>Into the island; the final leg ends with the buddy slipping into it.</summary>
    ToDock,
    /// <summary>Back to the mouse pointer; the final leg ends with normal cursor following.</summary>
    ToMouse,
}

/// <summary>One leg of a buddy flight, animated by the overlay whose screen is <see cref="ScreenFrame"/>.</summary>
/// <param name="StartScreenLocation">Where the buddy appears before the leg starts; null = wherever it is on that display now.</param>
/// <param name="EndScreenLocation">Where the leg ends, in global coordinates.</param>
/// <param name="IsFinalLeg">True when the leg ends at the destination (not at a display edge).</param>
/// <param name="Sequence">Distinguishes consecutive legs with identical geometry so observers see a change.</param>
public sealed record CursorFlightLeg(
    CursorFlightPurpose Purpose,
    ScreenRect ScreenFrame,
    ScreenPoint? StartScreenLocation,
    ScreenPoint EndScreenLocation,
    bool IsFinalLeg,
    int Sequence);

public static class CursorFlightPlanner
{
    /// <summary>The point of <paramref name="rect"/> closest to <paramref name="point"/> (the point itself when it is inside).</summary>
    public static ScreenPoint NearestPoint(ScreenPoint point, ScreenRect rect) =>
        new(Math.Min(Math.Max(point.X, rect.Left), rect.Right), Math.Min(Math.Max(point.Y, rect.Top), rect.Bottom));

    /// <summary>The first leg of a flight that starts on <paramref name="fromScreenFrame"/>. When the destination is on
    /// that display this is the whole flight; otherwise it ends at the display edge nearest the destination.</summary>
    public static CursorFlightLeg FirstLeg(CursorFlightPurpose purpose, ScreenRect fromScreenFrame, ScreenPoint destination, ScreenRect destinationScreenFrame, int sequence)
    {
        if (fromScreenFrame == destinationScreenFrame || fromScreenFrame.Contains(destination))
        {
            return new CursorFlightLeg(purpose, fromScreenFrame, null, destination, IsFinalLeg: true, sequence);
        }
        return new CursorFlightLeg(purpose, fromScreenFrame, null, NearestPoint(destination, fromScreenFrame), IsFinalLeg: false, sequence);
    }

    /// <summary>The leg that continues a flight on the destination display: it enters at the edge point
    /// nearest to where the previous leg left off and ends at the destination.</summary>
    public static CursorFlightLeg FinalLeg(CursorFlightPurpose purpose, ScreenPoint afterExitPoint, ScreenPoint destination, ScreenRect destinationScreenFrame, int sequence) =>
        new(purpose, destinationScreenFrame, NearestPoint(afterExitPoint, destinationScreenFrame), destination, IsFinalLeg: true, sequence);
}
