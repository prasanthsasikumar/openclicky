//
//  ScreenCaptureGeometry.cs
//  OpenClicky (Windows port of the pointing-coordinate mapping in CompanionManager.swift,
//  BrowserTabLocator.swift's CompanionScreenCapture extension and OverlayWindow.screensChanged)
//
//  A screenshot of one display and where that display sits: the bridge between screenshot pixels
//  (what the models and OCR see, origin top-left of the image) and global screen coordinates
//  (where the overlay draws). On the Mac global space is AppKit's y-up space, so screenLocation
//  flipped y; Windows global space is y-down like the screenshot, so the flip is dropped:
//  clamp → scale → add the display origin.
//

namespace OpenClicky.Core.Pointing;

/// <summary>The geometry half of a screen capture (the Mac's CompanionScreenCapture without the JPEG).</summary>
/// <param name="DisplayFrame">The captured display in global coordinates (y down).</param>
/// <param name="ScreenshotWidthInPixels">The screenshot's real pixel width.</param>
/// <param name="ScreenshotHeightInPixels">The screenshot's real pixel height.</param>
public sealed record ScreenCaptureGeometry(ScreenRect DisplayFrame, int ScreenshotWidthInPixels, int ScreenshotHeightInPixels)
{
    /// <summary>Maps a point in the screenshot's pixel space (origin top-left) onto global screen coordinates for the
    /// display that was captured. Out-of-range coordinates are clamped to the screenshot.</summary>
    public ScreenPoint ScreenLocation(ScreenPoint screenshotPoint)
    {
        double screenshotWidth = Math.Max(ScreenshotWidthInPixels, 1);
        double screenshotHeight = Math.Max(ScreenshotHeightInPixels, 1);
        var displayWidth = DisplayFrame.Width;
        var displayHeight = DisplayFrame.Height;

        // NaN would survive min/max; treat it as the origin. Infinite values clamp to the edges.
        var clampedX = double.IsNaN(screenshotPoint.X) ? 0 : Math.Max(0, Math.Min(screenshotPoint.X, screenshotWidth));
        var clampedY = double.IsNaN(screenshotPoint.Y) ? 0 : Math.Max(0, Math.Min(screenshotPoint.Y, screenshotHeight));

        // Scale from screenshot pixels to display units. No flip: both spaces are y-down on Windows.
        var displayLocalX = clampedX * (displayWidth / screenshotWidth);
        var displayLocalY = clampedY * (displayHeight / screenshotHeight);
        return new ScreenPoint(displayLocalX + DisplayFrame.X, displayLocalY + DisplayFrame.Y);
    }

    /// <summary>A point in global coordinates (as UI Automation reports element frames) → this capture's screenshot
    /// pixels. The Mac converted from CG (top-left) through AppKit (bottom-left) space; here both are y-down.</summary>
    public ScreenPoint ScreenshotPoint(ScreenPoint globalPoint)
    {
        var scaleX = ScreenshotWidthInPixels / Math.Max(DisplayFrame.Width, 1);
        var scaleY = ScreenshotHeightInPixels / Math.Max(DisplayFrame.Height, 1);
        return new ScreenPoint((globalPoint.X - DisplayFrame.Left) * scaleX, (globalPoint.Y - DisplayFrame.Top) * scaleY);
    }

    public bool ContainsScreenshotPoint(ScreenPoint point) =>
        point.X >= 0 && point.Y >= 0 && point.X <= ScreenshotWidthInPixels && point.Y <= ScreenshotHeightInPixels;
}

public static class OverlayScreenLayout
{
    /// <summary>Whether the overlay windows must be rebuilt: a display was plugged in, unplugged or moved.
    /// Order does not matter.</summary>
    public static bool ScreensChanged(IEnumerable<ScreenRect> from, IEnumerable<ScreenRect> to)
    {
        static List<ScreenRect> Sorted(IEnumerable<ScreenRect> frames) =>
            frames.OrderBy(frame => frame.X).ThenBy(frame => frame.Y).ThenBy(frame => frame.Width).ThenBy(frame => frame.Height).ToList();
        return !Sorted(from).SequenceEqual(Sorted(to));
    }
}
