//
//  HUDPlacement.cs
//  OpenClicky (Windows port of HUDPlacement.swift)
//
//  Where the island lives: hanging from the top centre of the screen (the default; the Mac's notch),
//  tucked against the left or right edge of the screen as a drawer, or nowhere at all while idle
//  (shortcuts only: the busy strip, the composer and the permission cards still open at the top,
//  and the tray icon is how the panel opens). The raw values are the Mac's, so a stored preference
//  reads the same on both.
//

namespace OpenClicky.Core.Pointing;

public enum HUDPlacement
{
    /// <summary>Top centre (raw value "notch", kept from the Mac).</summary>
    Notch,
    Left,
    Right,
    Hidden,
}

public static class HUDPlacements
{
    public static readonly IReadOnlyList<HUDPlacement> AllCases = new[] { HUDPlacement.Notch, HUDPlacement.Left, HUDPlacement.Right, HUDPlacement.Hidden };

    public const string DefaultsKey = "openclicky.hudPlacement";

    public static string RawValue(this HUDPlacement placement) => placement switch
    {
        HUDPlacement.Notch => "notch",
        HUDPlacement.Left => "left",
        HUDPlacement.Right => "right",
        _ => "hidden",
    };

    /// <summary>The stored placement (read from preferences under <see cref="DefaultsKey"/>); unknown or missing → top centre.</summary>
    public static HUDPlacement FromRawValue(string? rawValue) =>
        AllCases.FirstOrDefault(placement => placement.RawValue() == rawValue, HUDPlacement.Notch);

    public static string Title(this HUDPlacement placement) => placement switch
    {
        HUDPlacement.Notch => "top",
        HUDPlacement.Left => "left edge",
        HUDPlacement.Right => "right edge",
        _ => "hidden",
    };

    public static string Detail(this HUDPlacement placement) => placement switch
    {
        HUDPlacement.Notch => "hangs from the top of the screen; hover it to open.",
        HUDPlacement.Left => "a drawer on the left edge of every screen; hover the handle to open.",
        HUDPlacement.Right => "a drawer on the right edge of every screen; hover the handle to open.",
        _ => "nothing on screen until you use a shortcut. the tray icon opens the panel.",
    };

    /// <summary>True for the two drawer placements, whose island hangs from a screen edge instead of the top.</summary>
    public static bool IsSideDrawer(this HUDPlacement placement) => placement is HUDPlacement.Left or HUDPlacement.Right;
}
