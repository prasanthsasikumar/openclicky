//
//  WindowControlLocator.cs
//  OpenClicky (Windows port of WindowControlLocator.swift)
//
//  `point_at` for a window's close, minimize and maximize buttons. They carry no caption a screenshot
//  model can read reliably, so a guess lands anywhere (on the Mac, asked for the Terminal's yellow
//  button, Claude put it in the menu bar). The OS knows where they are. On the Mac the front app's
//  focused window exposes each traffic light's frame through Accessibility, top-left. On Windows the
//  caption buttons sit top-right, minimize | maximize | close: DWM reports the area they take
//  (DWMWA_CAPTION_BUTTON_BOUNDS) for a standard frame, and a window that draws its own title bar
//  (Chrome, VS Code, Terminal) still puts them there at the usual size, so the window's rect
//  places them. Runs first in the pointing chain, before any guessing.
//

namespace OpenClicky.Core.Pointing;

public enum WindowControl
{
    Close,
    Minimize,
    /// <summary>Maximize (or restore): the Mac's zoom button.</summary>
    Maximize,
}

/// <summary>
/// What the platform reads about the front window's caption buttons, in global physical pixels.
/// </summary>
/// <param name="WindowBounds">The window's visible frame (DWMWA_EXTENDED_FRAME_BOUNDS).</param>
/// <param name="CaptionButtonBounds">The area the caption buttons take, as DWM reports it; null when it has none.</param>
/// <param name="HasMinimizeOrMaximize">WS_MINIMIZEBOX or WS_MAXIMIZEBOX: without either, a window shows only close.</param>
/// <param name="Scale">The window's monitor scale (DPI / 96).</param>
/// <param name="AppName">The app's name, for the log.</param>
public sealed record CaptionButtonLayout(ScreenRect WindowBounds, ScreenRect? CaptionButtonBounds, bool HasMinimizeOrMaximize, double Scale, string AppName);

public static class WindowControlLocator
{
    /// <summary>A caption button's size in DIPs on Windows 10 and 11 (what apps that draw their own title bar copy).</summary>
    public const double ButtonWidthInDips = 46;
    public const double ButtonHeightInDips = 32;

    public static string Name(WindowControl control) => control switch
    {
        WindowControl.Close => "close",
        WindowControl.Minimize => "minimize",
        _ => "maximize",
    };

    public sealed record LocatedControl(ScreenPoint Point, WindowControl Control, string AppName);

    // MARK: - Entry point

    /// <summary>
    /// The screenshot-pixel centre of the window button the request is about, when the request is about
    /// one and the front window shows it on the captured display.
    /// </summary>
    public static LocatedControl? Locate(string label, string text, string? userRequest, ScreenCaptureGeometry capture,
                                         Func<CaptionButtonLayout?> frontWindowLayout, Action<string>? log = null)
    {
        if (RequestedControl(label, text, userRequest) is not { } control) return null;
        if (frontWindowLayout() is not { } layout || ButtonFrame(control, layout) is not { } frame)
        {
            log?.Invoke($"window control: {Name(control)} button of the front app could not be read");
            return null;
        }
        var point = capture.ScreenshotPoint(frame.Center);
        if (!capture.ContainsScreenshotPoint(point))
        {
            log?.Invoke($"window control: {Name(control)} button of {layout.AppName} is off the capture; not pointing");
            return null;
        }
        return new LocatedControl(point, control, layout.AppName);
    }

    // MARK: - Pure parts (unit-tested)

    /// <summary>
    /// The button's frame: the caption-button area DWM reports, split evenly right to left (close,
    /// then maximize, then minimize), when it is a believable one at the window's top right; else the
    /// usual 46 × 32 DIP buttons in the window's top-right corner. A window with neither a minimize
    /// nor a maximize box has only close.
    /// </summary>
    public static ScreenRect? ButtonFrame(WindowControl control, CaptionButtonLayout layout)
    {
        var window = layout.WindowBounds;
        if (window.IsEmpty) return null;
        var buttonCount = layout.HasMinimizeOrMaximize ? 3 : 1;
        // Counted from the right edge: close 0, maximize 1, minimize 2.
        var slot = control switch
        {
            WindowControl.Close => 0,
            WindowControl.Maximize => 1,
            _ => 2,
        };
        if (slot >= buttonCount) return null;

        if (layout.CaptionButtonBounds is { } area && IsBelievableCaptionArea(area, window, buttonCount))
        {
            var width = area.Width / buttonCount;
            return new ScreenRect(area.Right - (slot + 1) * width, area.Top, width, area.Height);
        }
        var scale = Math.Max(layout.Scale, 0.5);
        var buttonWidth = ButtonWidthInDips * scale;
        var buttonHeight = Math.Min(ButtonHeightInDips * scale, window.Height);
        if (window.Width < buttonWidth * buttonCount) return null;
        return new ScreenRect(window.Right - (slot + 1) * buttonWidth, window.Top, buttonWidth, buttonHeight);
    }

    /// <summary>At the window's top right, inside it, and about as wide as that many buttons are tall or wider.</summary>
    private static bool IsBelievableCaptionArea(ScreenRect area, ScreenRect window, int buttonCount)
    {
        if (area.IsEmpty) return false;
        var slack = 16;
        var atTopRight = Math.Abs(area.Right - window.Right) <= slack && Math.Abs(area.Top - window.Top) <= slack;
        var inside = area.Left >= window.Left - slack && area.Bottom <= window.Bottom + slack;
        return atTopRight && inside && area.Width >= area.Height * buttonCount * 0.8;
    }

    /// <summary>
    /// Which window button the request asks for, or null when it is about something else. The model's
    /// label is trusted first ("minimize button"); the user's own words count when they name the
    /// button or the window ("the x button", "minimize this window"), never a tab ("close this tab" is
    /// the tab's own ✕, which the tab locator or OCR finds).
    /// </summary>
    public static WindowControl? RequestedControl(string label, string text, string? userRequest)
    {
        if (Control(label) is { } fromLabel && Words(label).Contains("button")) return fromLabel;
        if (Control(text) is { } fromText && Words(text).Contains("button")) return fromText;
        if (userRequest is null) return null;
        var requestWords = Words(userRequest);
        if (requestWords.Contains("tab") || requestWords.Contains("tabs")) return null;
        if (Control(userRequest) is not { } control) return null;
        var aboutTheWindow = requestWords.Any(word => word is "window" or "button" or "app" or "terminal" or "it" or "this");
        return aboutTheWindow ? control : null;
    }

    /// <summary>
    /// The Mac's words, the Windows way: maximize (and restore) for zoom; no traffic-light colours (the
    /// buttons are not coloured here), but "x" names the close button.
    /// </summary>
    private static WindowControl? Control(string text)
    {
        var words = Words(text).ToHashSet();
        var mentionsButton = words.Overlaps(["button", "icon"]);
        if (words.Overlaps(["minimize", "minimise", "minimizing", "minimising"])) return WindowControl.Minimize;
        if (words.Overlaps(["maximize", "maximise", "maximizing", "maximising", "restore", "zoom", "fullscreen", "enlarge"])
            || (words.Contains("full") && words.Contains("screen")))
        {
            return WindowControl.Maximize;
        }
        if (words.Contains("close") || words.Contains("closing") || (mentionsButton && words.Contains("x"))) return WindowControl.Close;
        return null;
    }

    private static List<string> Words(string text) => AccessibleElementLocator.Words(text);
}
