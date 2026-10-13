// Port of CursorFlightPlannerTests.swift. The Mac layout is given in AppKit (y-up) space; here it is
// converted to Windows y-down space with the built-in display's top-left at the origin:
//   built-in (0, 0, 1512 × 982), external above it at (-173, -1080, 1920 × 1080),
//   the Mac's dock point (756, 944) is 38 below the built-in display's top edge → (756, 38).

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class CursorFlightPlannerTests
{
    private static readonly ScreenRect BuiltInFrame = new(0, 0, 1512, 982);
    private static readonly ScreenRect ExternalFrame = new(-173, -1080, 1920, 1080);
    private static readonly ScreenPoint DockPoint = new(756, 38);

    [Fact]
    public void FlightOnTheDestinationDisplayIsOneLeg()
    {
        var leg = CursorFlightPlanner.FirstLeg(CursorFlightPurpose.ToDock, BuiltInFrame, DockPoint, BuiltInFrame, 1);
        Assert.True(leg.IsFinalLeg);
        Assert.Null(leg.StartScreenLocation);
        Assert.Equal(DockPoint, leg.EndScreenLocation);
        Assert.Equal(BuiltInFrame, leg.ScreenFrame);
    }

    [Fact]
    public void DockingFromTheExternalDisplayExitsAtItsBottomEdge()
    {
        var firstLeg = CursorFlightPlanner.FirstLeg(CursorFlightPurpose.ToDock, ExternalFrame, DockPoint, BuiltInFrame, 1);
        Assert.False(firstLeg.IsFinalLeg);
        Assert.Equal(ExternalFrame, firstLeg.ScreenFrame);
        Assert.Equal(new ScreenPoint(756, 0), firstLeg.EndScreenLocation);

        var finalLeg = CursorFlightPlanner.FinalLeg(CursorFlightPurpose.ToDock, firstLeg.EndScreenLocation, DockPoint, BuiltInFrame, 2);
        Assert.True(finalLeg.IsFinalLeg);
        Assert.Equal(BuiltInFrame, finalLeg.ScreenFrame);
        // Enters at the top edge of the built-in display, right under where it left the external one.
        Assert.Equal(new ScreenPoint(756, 0), finalLeg.StartScreenLocation);
        Assert.Equal(DockPoint, finalLeg.EndScreenLocation);
    }

    [Fact]
    public void ExitPointIsClampedToTheDisplayWhenTheDestinationIsOffToTheSide()
    {
        // Mac: right display (1512, 0, 1920 × 1080) y-up, destination (2500, 1050) y-up.
        var rightDisplay = new ScreenRect(1512, -98, 1920, 1080);
        var leg = CursorFlightPlanner.FirstLeg(CursorFlightPurpose.ToMouse, BuiltInFrame, new ScreenPoint(2500, -68), rightDisplay, 3);
        Assert.False(leg.IsFinalLeg);
        // The Mac's (1512, 982) top-right corner is (1512, 0) here.
        Assert.Equal(new ScreenPoint(1512, 0), leg.EndScreenLocation);
    }
}
