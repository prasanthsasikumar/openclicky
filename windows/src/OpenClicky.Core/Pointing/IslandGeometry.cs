//
//  IslandGeometry.cs
//  OpenClicky (Windows port of NotchHUD.swift's NotchGeometry and the pure layout of NotchHUDModel)
//
//  The island: a solid black shape hanging from the top centre of each monitor (or a drawer on a
//  screen edge, see HUDPlacement). Windows has no notch, so every display takes the Mac's
//  "display without a hardware notch" branch: collapsed, the island is a thin handle line flush with
//  the top edge, and the cards and panel hang from a band that is the reserved space above the work
//  area (a taskbar docked at the top), at least 34 DIPs for the panel's band row.
//
//  Coordinates: global DIPs, y DOWN (WPF / Win32). The Mac's NotchGeometry is in AppKit's y-up
//  space; each formula is converted where it is written:
//    Mac screenFrame.maxY (top edge)      → ScreenFrame.Top
//    Mac "y: maxY - height" (hang down)   → y: Top
//    Mac notchRect.minY - 6 (just under)  → NotchRect.Bottom + 6
//    Mac menu band = frame.maxY - visible.maxY → WorkArea.Top - ScreenFrame.Top
//
//  The expansion state machine (hover grace periods, timers) lives with the island window; the
//  rule that picks the target expansion is pure and is here as IslandExpansionRules.
//

namespace OpenClicky.Core.Pointing;

/// <summary>Where the island hangs on one monitor (the Mac's NotchGeometry, always without a hardware notch).</summary>
/// <param name="ScreenFrame">The monitor's bounds.</param>
/// <param name="NotchWidth">The virtual notch's width: 190.</param>
/// <param name="NotchHeight">The band above the work area (0 unless the taskbar is docked at the top).</param>
/// <param name="VisibleFrame">The monitor's work area (minus the taskbar): where a side drawer may hang.</param>
public sealed record IslandGeometry(ScreenRect ScreenFrame, double NotchWidth, double NotchHeight, ScreenRect VisibleFrame)
{
    public const double VirtualNotchWidth = 190;

    /// <summary>Always false on Windows; kept so the Mac's formulas read the same.</summary>
    public bool HasHardwareNotch => false;

    public ScreenRect NotchRect => new(ScreenFrame.MidX - NotchWidth / 2, ScreenFrame.Top, NotchWidth, NotchHeight);

    /// <summary>Where the docked buddy "lives": just under the virtual notch's bottom edge.</summary>
    public ScreenPoint DockPoint => new(NotchRect.MidX, NotchRect.Bottom + 6);

    /// <summary>A display hangs the island from the band above its work area. With no such band (the usual bottom
    /// taskbar, or an auto-hidden one) the island starts at the very top edge.</summary>
    public static IslandGeometry VirtualNotch(ScreenRect screenFrame, ScreenRect visibleFrame)
    {
        var bandHeight = Math.Max(0, visibleFrame.Top - screenFrame.Top);
        return new IslandGeometry(screenFrame, VirtualNotchWidth, bandHeight, visibleFrame);
    }

    /// <summary>The geometry the island is laid out in for a placement. A side drawer has no band to clear, so its cards
    /// start at the top of the island.</summary>
    public static IslandGeometry ForScreen(ScreenRect screenFrame, ScreenRect workArea, HUDPlacement placement) =>
        placement.IsSideDrawer()
            ? new IslandGeometry(screenFrame, VirtualNotchWidth, 0, workArea)
            : VirtualNotch(screenFrame, workArea);
}

public enum IslandExpansion
{
    Collapsed,
    /// <summary>Hovered: a little wider and one line taller, with the pointer's status and "click to open". A click opens the panel.</summary>
    Peek,
    Compact,
    Connect,
    /// <summary>A permission card: nothing else opens on the island until it is answered.</summary>
    Permission,
    /// <summary>The text composer (Ctrl tapped twice): a one-line field to type a request.</summary>
    Composer,
    /// <summary>The first-run tour's card.</summary>
    Tour,
    Full,
}

/// <summary>What the island's size depends on besides the expansion (the NotchHUDModel state the sizes read).</summary>
public sealed record IslandContentState(
    IslandExpansion Expansion = IslandExpansion.Collapsed,
    bool IsShowingCaption = false,
    bool HasCompactDetailLine = false,
    bool ConnectHasExamplePrompts = false,
    bool PermissionOffersTrustReset = false,
    bool TourHasStatus = false,
    bool TourHasMicrophoneName = false,
    bool TourHasField = false,
    double? SlotHeight = null);

/// <summary>The island's sizes and frames for one monitor, placement and state. Sizes from the refinement sheet: every
/// card and the open panel are 512 wide (360 in a side drawer); heights include the band the island hangs from.</summary>
public sealed class IslandLayout
{
    public const double StripWidth = 380;   // DS.HUD.stripWidth
    public const double StripHeight = 37;   // DS.HUD.stripHeight
    /// <summary>Nothing shows below the band while idle.</summary>
    public const double CollapsedLipHeight = 0;
    public const double CompactDetailLineHeight = 26;
    public const double ConnectWidth = 512;
    public const double PermissionWidth = 512;
    public const double ComposerWidth = 460;
    public const double ComposerContentHeight = 52;
    /// <summary>The collapsed handle line, flush with the top edge of the screen.</summary>
    public const double HandleStripHeight = 12;
    /// <summary>A side drawer collapses to a vertical handle this tall against the screen edge.</summary>
    public const double SideHandleLength = 64;
    /// <summary>How far the window reaches past the island on its open sides, so the hover margin and the opening
    /// spring are not clipped.</summary>
    public const double WindowMargin = 16;

    public IslandGeometry Geometry { get; }
    public HUDPlacement Placement { get; }
    public IslandContentState State { get; }

    public IslandLayout(IslandGeometry geometry, HUDPlacement placement, IslandContentState? state = null)
    {
        Geometry = geometry;
        Placement = placement;
        State = state ?? new IslandContentState();
    }

    public double CompactWidth => State.IsShowingCaption ? 400 : StripWidth;
    public double CompactContentHeight =>
        State.IsShowingCaption ? 70 : StripHeight + (State.HasCompactDetailLine ? CompactDetailLineHeight : 0);
    /// <summary>A side drawer is a narrow column with its content stacked, not the top panel moved.</summary>
    public double FullWidth => Placement.IsSideDrawer() ? 360 : 512;

    /// <summary>The open panel's band row: the band plus a little air above the controls.</summary>
    public double BandRowHeight => TopBandHeight + 6;

    /// <summary>The band the band row sits in: at least 34 (the band above the work area may be 0).</summary>
    public double TopBandHeight => Geometry.HasHardwareNotch ? Geometry.NotchHeight : Math.Max(Geometry.NotchHeight, 34);

    public double TourWidth => Placement.IsSideDrawer() ? 360 : 512;
    public double TourHeight
    {
        get
        {
            // A side drawer is narrower, so the copy wraps onto more lines.
            var statusRoom = (State.TourHasStatus ? 38 : 0) + (State.TourHasMicrophoneName ? 20 : 0) + (State.TourHasField ? 38 : 0);
            return CardTopInset + (Placement.IsSideDrawer() ? 168 : 116) + statusRoom;
        }
    }

    public double CollapsedWidth => Placement switch
    {
        HUDPlacement.Notch => Geometry.NotchWidth,
        HUDPlacement.Left or HUDPlacement.Right => HandleStripHeight,
        _ => 0,
    };

    public double CollapsedHeight => Placement switch
    {
        HUDPlacement.Notch => Geometry.HasHardwareNotch ? Geometry.NotchHeight + CollapsedLipHeight : HandleStripHeight,
        HUDPlacement.Left or HUDPlacement.Right => SideHandleLength,
        _ => 0,
    };

    /// <summary>Hovered: the island grows a little on each side and one line under the band (a side drawer's handle is
    /// too small to peek: hovering it opens the drawer).</summary>
    public double PeekWidth => Placement.IsSideDrawer() ? CollapsedWidth : Geometry.NotchWidth + 90;
    public double PeekHeight => Placement.IsSideDrawer() ? CollapsedHeight : Geometry.NotchHeight + PeekLineHeight;
    public const double PeekLineHeight = 30;

    public double CompactHeight => Geometry.NotchHeight + CompactContentHeight;
    /// <summary>Where a card's content starts: the island covers the band and the content uses that height too.</summary>
    public double CardTopInset => Geometry.HasHardwareNotch ? Geometry.NotchHeight : 8;
    /// <summary>12 top + 40 icon row + 12 + 30 buttons + 16 bottom, and the example chips (26 + 12).</summary>
    public double ConnectHeight => CardTopInset + 110 + (State.ConnectHasExamplePrompts ? 38 : 0);
    /// <summary>The stale-row escape hatch adds a line under the card's row.</summary>
    public double PermissionHeight => CardTopInset + (State.PermissionOffersTrustReset ? 118 : 96);
    public double ComposerHeight => Geometry.NotchHeight + ComposerContentHeight;
    /// <summary>The band, then the slot: the shortcuts row (at most 88 at the top, Claude Design's "max 88pt"; a side
    /// drawer stacks the dock button over the shortcut grid), or the active component's own height.</summary>
    public double FullHeight => BandRowHeight + (State.SlotHeight ?? (Placement.IsSideDrawer() ? 92 : 45));

    public double Width => State.Expansion switch
    {
        IslandExpansion.Collapsed => CollapsedWidth,
        IslandExpansion.Peek => PeekWidth,
        IslandExpansion.Compact => CompactWidth,
        IslandExpansion.Connect => ConnectWidth,
        IslandExpansion.Permission => PermissionWidth,
        IslandExpansion.Composer => ComposerWidth,
        IslandExpansion.Tour => TourWidth,
        _ => FullWidth,
    };

    public double Height => State.Expansion switch
    {
        IslandExpansion.Collapsed => CollapsedHeight,
        IslandExpansion.Peek => PeekHeight,
        IslandExpansion.Compact => CompactHeight,
        IslandExpansion.Connect => ConnectHeight,
        IslandExpansion.Permission => PermissionHeight,
        IslandExpansion.Composer => ComposerHeight,
        IslandExpansion.Tour => TourHeight,
        _ => FullHeight,
    };

    /// <summary>The island's rectangle for a size: centred at the top, or against the chosen screen edge with its top a
    /// fifth of the way down (lifted when a tall panel would run past the bottom of the screen).</summary>
    public ScreenRect IslandFrame(double width, double height)
    {
        switch (Placement)
        {
            case HUDPlacement.Left:
            case HUDPlacement.Right:
            {
                var area = Geometry.VisibleFrame.IsEmpty ? Geometry.ScreenFrame : Geometry.VisibleFrame;
                // Mac (y-up): top = min(maxY, max(maxY - h*0.2, minY + height)); y-down mirror:
                var top = Math.Max(area.Top, Math.Min(area.Top + area.Height * 0.2, area.Bottom - height));
                var x = Placement == HUDPlacement.Left ? area.Left : area.Right - width;
                return new ScreenRect(x, top, width, height);
            }
            default:
                return new ScreenRect(Geometry.NotchRect.MidX - width / 2, Geometry.ScreenFrame.Top, width, height);
        }
    }

    public ScreenRect Island => IslandFrame(Width, Height);

    /// <summary>Where the docked buddy "lives": under the band, or just inside a side drawer's handle.</summary>
    public ScreenPoint DockPoint
    {
        get
        {
            if (!Placement.IsSideDrawer()) return Geometry.DockPoint;
            var handle = IslandFrame(CollapsedWidth, CollapsedHeight);
            return new ScreenPoint(Placement == HUDPlacement.Left ? handle.Right + 6 : handle.Left - 6, handle.MidY);
        }
    }

    /// <summary>The window around the island: the island plus <see cref="WindowMargin"/> on every side that does not touch
    /// the screen edge it hangs from.</summary>
    public ScreenRect WindowFrame
    {
        get
        {
            var island = Island;
            const double margin = WindowMargin;
            return Placement switch
            {
                // Mac (y-up) grows downward from a flush top; y-down: same top, taller.
                HUDPlacement.Left => new ScreenRect(island.Left, island.Top - margin, island.Width + margin, island.Height + margin * 2),
                HUDPlacement.Right => new ScreenRect(island.Left - margin, island.Top - margin, island.Width + margin, island.Height + margin * 2),
                _ => new ScreenRect(island.Left - margin, island.Top, island.Width + margin * 2, island.Height + margin),
            };
        }
    }

    /// <summary>Hidden and idle, the island is nothing at all: no hover zone, no click target.</summary>
    public bool IsInvisible => Placement == HUDPlacement.Hidden && State.Expansion == IslandExpansion.Collapsed;
}

/// <summary>The island's inputs for picking an expansion (the NotchHUDModel flags).</summary>
public sealed record IslandExpansionInputs(
    bool IsSayingGoodbye = false,
    bool IsComposerOpen = false,
    bool HasPermissionPrompt = false,
    bool IsPinnedOpen = false,
    bool HasTourCard = false,
    bool IsBusy = false,
    bool HasConnectPrompt = false,
    bool IsHovering = false,
    bool IsSpeaking = false,
    bool HasCaption = false,
    bool IsSideDrawer = false)
{
    /// <summary>The permission cards or the tour are up: hovering or clicking the island must not open the panel over them.</summary>
    public bool IsSetupInProgress => HasTourCard || HasPermissionPrompt;

    /// <summary>The caption strip shows only while nothing busier needs the island (the goodbye excepted).</summary>
    public bool IsShowingCaption => HasCaption && (!IsBusy || IsSayingGoodbye);
}

/// <summary>The pure part of NotchHUDModel.reconcile: which expansion to show, and whether it applies at once or after the
/// island's 0.4 s grace period (so the panel does not flicker when the pointer skims the edge).</summary>
public static class IslandExpansionRules
{
    public const double CollapseGraceSeconds = 0.4;

    /// <summary>The state the island should be in once every hover grace period has run out.</summary>
    public static IslandExpansion RestingExpansion(IslandExpansionInputs inputs)
    {
        if (inputs.HasPermissionPrompt) return IslandExpansion.Permission;
        if (inputs.HasTourCard && !inputs.IsBusy) return IslandExpansion.Tour;
        if (inputs.HasConnectPrompt) return IslandExpansion.Connect;
        if (inputs.IsBusy || inputs.HasCaption) return IslandExpansion.Compact;
        return IslandExpansion.Collapsed;
    }

    /// <summary>The expansion to move to, and whether it applies immediately. When <c>Immediately</c> is false the island
    /// waits <see cref="CollapseGraceSeconds"/>, then applies <see cref="RestingExpansion"/> only if
    /// <see cref="SettlesAfterGrace"/> still holds.</summary>
    public static (IslandExpansion Target, bool Immediately) Target(IslandExpansionInputs inputs, bool immediately = false)
    {
        if (inputs.IsSayingGoodbye) return (IslandExpansion.Compact, true);
        IslandExpansion target;
        if (inputs.IsComposerOpen)
            // The composer has the keyboard: nothing swaps it out until it is closed.
            target = IslandExpansion.Composer;
        else if (inputs.HasPermissionPrompt)
            // Without permissions there is nothing else worth opening the island for.
            target = IslandExpansion.Permission;
        else if (inputs.IsPinnedOpen && !inputs.IsSetupInProgress)
            target = IslandExpansion.Full;
        else if (inputs.HasTourCard && !inputs.IsBusy)
            // Like the connect card: hovering it must not swap it for the panel.
            target = IslandExpansion.Tour;
        else if (inputs.HasConnectPrompt)
            // Hovering the connect card must not swap it for the full panel while the user is reaching for its buttons;
            // a click on the island still pins the full panel open.
            target = IslandExpansion.Connect;
        else if (inputs.IsHovering && !inputs.IsSetupInProgress && !inputs.IsSpeaking && inputs.IsSideDrawer)
            // A drawer's handle is too small to peek: hovering it opens the drawer.
            target = IslandExpansion.Full;
        else if (inputs.IsHovering && !inputs.IsSetupInProgress && !inputs.IsSpeaking && !inputs.IsBusy && !inputs.HasCaption)
            // Hovering only teases; a click opens the panel.
            target = IslandExpansion.Peek;
        else
            target = RestingExpansion(inputs);

        var applyNow = target is IslandExpansion.Full or IslandExpansion.Peek or IslandExpansion.Connect or IslandExpansion.Permission
            or IslandExpansion.Composer or IslandExpansion.Tour || immediately;
        return (target, applyNow);
    }

    /// <summary>After the grace period: hovering or a pinned panel holds the island open, except during setup, where the
    /// pointer resting on the tour card must not keep the busy strip from showing.</summary>
    public static bool SettlesAfterGrace(IslandExpansionInputs inputs) =>
        (inputs.IsSetupInProgress || (inputs.IsSpeaking && !inputs.IsPinnedOpen) || (!inputs.IsHovering && !inputs.IsPinnedOpen))
        && !inputs.IsComposerOpen;
}
