// Ports of NotchGeometryTests.swift and the geometry / expansion parts of HUDPlacementTests.swift, in Windows y-down
// space. Windows has no hardware notch, so the Mac's notch-screen fixtures use the no-notch geometry here.
// Not ported from HUDPlacementTests: theIslandOffersOpenAtLoginOnceAfterFiveUses (FirstRunTourController, Companion
// port) and everyGoodbyeSaysHowToComeBackOrIsShort (Farewell, Answers port). The timing of the 0.4 s grace period
// lives with the island window; the rules it applies are tested here.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class IslandGeometryTests
{
    [Fact]
    public void ADisplayWithABandAboveItsWorkAreaHangsFromTheBand()
    {
        // The Mac's menu bar band; on Windows a taskbar docked at the top.
        var screenFrame = new ScreenRect(0, 0, 1512, 982);
        var workArea = new ScreenRect(0, 33, 1512, 949);
        var geometry = IslandGeometry.VirtualNotch(screenFrame, workArea);
        Assert.False(geometry.HasHardwareNotch);
        Assert.Equal(33, geometry.NotchHeight);
        Assert.Equal(0, geometry.NotchRect.Top);
        Assert.Equal(33, geometry.NotchRect.Bottom);
    }

    [Fact]
    public void ADisplayWithoutABandHangsFromTheTopEdge()
    {
        var screenFrame = new ScreenRect(-173, -1080, 1920, 1080);
        var workArea = new ScreenRect(-173, -1080, 1920, 1032); // the taskbar at the bottom
        var geometry = IslandGeometry.VirtualNotch(screenFrame, workArea);
        Assert.Equal(0, geometry.NotchHeight);
        Assert.Equal(new ScreenRect(-173 + 960 - 95, -1080, 190, 0), geometry.NotchRect);
        Assert.Equal(new ScreenPoint(-173 + 960, -1074), geometry.DockPoint);
    }

    [Fact]
    public void TheCollapsedHandleIgnoresTheBandButThePanelKeepsOne()
    {
        var screenFrame = new ScreenRect(-173, -1080, 1920, 1080);
        var workArea = new ScreenRect(-173, -1050, 1920, 1050);
        var layout = new IslandLayout(IslandGeometry.VirtualNotch(screenFrame, workArea), HUDPlacement.Notch);
        Assert.Equal(30, layout.Geometry.NotchHeight);
        Assert.Equal(IslandLayout.HandleStripHeight, layout.CollapsedHeight);
        Assert.Equal(34, layout.TopBandHeight);
    }
}

public class HUDPlacementTests
{
    private static readonly ScreenRect ScreenFrame = new(0, 0, 1512, 982);
    private static readonly ScreenRect VisibleFrame = new(0, 37, 1512, 865); // Mac (0, 80, 1512, 865) y-up

    private static IslandLayout Layout(HUDPlacement placement, IslandExpansion expansion = IslandExpansion.Collapsed) =>
        new(IslandGeometry.ForScreen(ScreenFrame, VisibleFrame, placement), placement, new IslandContentState(expansion));

    [Fact]
    public void TheOpenPanelAtTheTopIsAtMost88Tall()
    {
        var island = Layout(HUDPlacement.Notch, IslandExpansion.Full);
        Assert.True(island.Height <= 88);
        Assert.Equal(512, island.Width);
    }

    [Fact]
    public void TheIslandHangsCentredFromTheTop()
    {
        var island = Layout(HUDPlacement.Notch).Island;
        Assert.Equal(ScreenFrame.MidX, island.MidX);
        Assert.Equal(ScreenFrame.Top, island.Top);
        Assert.Equal(190, island.Width);
        Assert.Equal(IslandLayout.HandleStripHeight, island.Height);
    }

    [Fact]
    public void TheTopWindowHasItsMarginOnTheThreeOpenSides()
    {
        var layout = Layout(HUDPlacement.Notch, IslandExpansion.Compact);
        Assert.Equal(layout.Island.Top, layout.WindowFrame.Top);
        Assert.Equal(layout.Island.Bottom + IslandLayout.WindowMargin, layout.WindowFrame.Bottom);
        Assert.Equal(layout.Island.Left - IslandLayout.WindowMargin, layout.WindowFrame.Left);
    }

    [Fact]
    public void RightDrawerCollapsesToAHandleOnTheRightEdge()
    {
        var drawer = Layout(HUDPlacement.Right);
        var island = drawer.Island;
        Assert.Equal(VisibleFrame.Right, island.Right);
        Assert.Equal(IslandLayout.HandleStripHeight, island.Width);
        Assert.Equal(IslandLayout.SideHandleLength, island.Height);
        // The window's margin is on the three sides away from the edge.
        Assert.Equal(VisibleFrame.Right, drawer.WindowFrame.Right);
        Assert.Equal(island.Left - IslandLayout.WindowMargin, drawer.WindowFrame.Left);
        Assert.True(drawer.DockPoint.X < island.Left);
    }

    [Fact]
    public void LeftDrawerHangsFromTheLeftEdgeAndStaysOnScreen()
    {
        var island = Layout(HUDPlacement.Left, IslandExpansion.Full).Island;
        Assert.Equal(VisibleFrame.Left, island.Left);
        Assert.True(island.Top >= VisibleFrame.Top);
        Assert.True(island.Bottom <= VisibleFrame.Bottom);
        // A fifth of the way down the work area.
        Assert.Equal(VisibleFrame.Top + VisibleFrame.Height * 0.2, island.Top, 6);
    }

    [Fact]
    public void HiddenIslandIsNothingWhileIdle()
    {
        var hidden = Layout(HUDPlacement.Hidden);
        Assert.True(hidden.IsInvisible);
        Assert.Equal(0, hidden.Width);
        Assert.Equal(0, hidden.Height);
        Assert.False(Layout(HUDPlacement.Hidden, IslandExpansion.Full).IsInvisible);
    }

    [Fact]
    public void GoodbyeTakesTheIslandOverEvenWhileBusy()
    {
        var inputs = new IslandExpansionInputs(IsSayingGoodbye: true, IsBusy: true, HasCaption: true, IsHovering: true);
        Assert.Equal((IslandExpansion.Compact, true), IslandExpansionRules.Target(inputs));
        Assert.True(inputs.IsShowingCaption);
    }

    /// <summary>While the tour (or a permission card) is up, the pointer drifting onto the island, the busy strip under a
    /// question, or a click must never open the panel over the setup.</summary>
    [Fact]
    public void SetupKeepsThePanelClosed()
    {
        var tourHovered = new IslandExpansionInputs(HasTourCard: true, IsHovering: true);
        Assert.Equal(IslandExpansion.Tour, IslandExpansionRules.Target(tourHovered).Target);

        // The card gives way to the busy strip after the grace period.
        var busy = tourHovered with { IsBusy = true };
        var (target, immediately) = IslandExpansionRules.Target(busy);
        Assert.Equal(IslandExpansion.Compact, target);
        Assert.False(immediately);
        Assert.True(IslandExpansionRules.SettlesAfterGrace(busy));

        // A pin (the island ignores the toggle during setup, and even a pinned flag does not open it) plus hover: still the strip.
        var pinnedAndHovered = busy with { IsPinnedOpen = true };
        Assert.Equal(IslandExpansion.Compact, IslandExpansionRules.Target(pinnedAndHovered).Target);
        Assert.True(IslandExpansionRules.SettlesAfterGrace(pinnedAndHovered));

        Assert.Equal(IslandExpansion.Tour, IslandExpansionRules.Target(busy with { IsBusy = false }).Target);
    }

    [Fact]
    public void HoveringPeeksButNotWhileSpeakingAndOpensASideDrawer()
    {
        Assert.Equal((IslandExpansion.Peek, true), IslandExpansionRules.Target(new IslandExpansionInputs(IsHovering: true)));
        Assert.Equal((IslandExpansion.Full, true), IslandExpansionRules.Target(new IslandExpansionInputs(IsHovering: true, IsSideDrawer: true)));
        Assert.Equal((IslandExpansion.Full, true), IslandExpansionRules.Target(new IslandExpansionInputs(IsHovering: true, IsPinnedOpen: true)));
        var speaking = new IslandExpansionInputs(IsHovering: true, IsSpeaking: true, IsBusy: true);
        Assert.Equal((IslandExpansion.Compact, false), IslandExpansionRules.Target(speaking));
    }

    [Fact]
    public void PlacementRawValuesMatchTheMac()
    {
        Assert.Equal(HUDPlacement.Notch, HUDPlacements.FromRawValue(null));
        Assert.Equal(HUDPlacement.Right, HUDPlacements.FromRawValue("right"));
        Assert.Equal("notch", HUDPlacement.Notch.RawValue());
        Assert.Contains("tray", HUDPlacement.Hidden.Detail());
    }
}
