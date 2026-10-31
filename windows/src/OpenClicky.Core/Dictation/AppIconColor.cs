// Ports Dictation/AppIconColor.swift: the app icon's colour, picked in Settings → General. The five
// directions from the Claude Design "OpenClicky Icon Directions" (2026-10-30), saved as their
// lower-case names ("signal" … "aqua"), Signal by default. The window, taskbar and tray icons follow
// the pick; the Start menu and Explorer keep the exe's red icon, as Finder does on the Mac.

namespace OpenClicky.Core.Dictation;

public enum AppIconColor
{
    Signal,
    Ultraviolet,
    Sunset,
    Paper,
    Aqua,
}

/// <summary>One colour's gradient (top-left to bottom-right) and the mark drawn on it, as hex.</summary>
public readonly record struct AppIconPalette(string TopLeftHex, string BottomRightHex, string MarkHex);

public static class AppIconColors
{
    public static IReadOnlyList<AppIconColor> AllCases { get; } = Enum.GetValues<AppIconColor>();

    public static string Title(this AppIconColor color) => color switch
    {
        AppIconColor.Signal => "Signal",
        AppIconColor.Ultraviolet => "Ultraviolet",
        AppIconColor.Sunset => "Sunset",
        AppIconColor.Paper => "Paper",
        _ => "Aqua",
    };

    /// <summary>The colours from scripts/make-app-icon.py.</summary>
    public static AppIconPalette Palette(this AppIconColor color) => color switch
    {
        AppIconColor.Signal => new("#FF6A3F", "#D4163A", "#FFFFFF"),
        AppIconColor.Ultraviolet => new("#8E6BFF", "#3417C9", "#FFFFFF"),
        AppIconColor.Sunset => new("#FFBE3D", "#FF1F7A", "#FFFFFF"),
        AppIconColor.Paper => new("#FFFBF2", "#EFE6D2", "#F0452B"),
        _ => new("#3CF0D8", "#0A5BE6", "#FFFFFF"),
    };
}
