namespace OpenClicky.Core.Platform;

/// <summary>One monitor (NSScreen).</summary>
/// <param name="DeviceName">GDI device name, e.g. "\\.\DISPLAY1" (stable while the monitor stays connected).</param>
/// <param name="Bounds">The whole monitor in physical pixels of the virtual desktop.</param>
/// <param name="WorkArea">The monitor without the taskbar and docked app bars, physical pixels.</param>
/// <param name="BoundsInDips">Bounds divided by this monitor's own scale (WPF units on this monitor). With mixed DPI there is no single DIP space, so only sizes are comparable across monitors.</param>
/// <param name="Scale">Effective DPI / 96 (1.0, 1.25, 1.5…).</param>
/// <param name="Index">Position in EnumDisplayMonitors order.</param>
public sealed record ScreenInfo(
    string DeviceName,
    nint MonitorHandle,
    PixelRect Bounds,
    PixelRect WorkArea,
    DoubleRect BoundsInDips,
    DoubleRect WorkAreaInDips,
    double Scale,
    int Dpi,
    bool IsPrimary,
    bool ContainsCursor,
    int Index);

public interface IScreens
{
    /// <summary>Every monitor now, primary first.</summary>
    IReadOnlyList<ScreenInfo> All();

    ScreenInfo? Primary();

    /// <summary>The monitor the cursor is on (else the primary).</summary>
    ScreenInfo? CursorScreen();

    /// <summary>The monitor containing (or nearest to) a point.</summary>
    ScreenInfo? ScreenAt(PixelPoint point);

    /// <summary>The cursor in physical pixels.</summary>
    PixelPoint CursorPosition();

    /// <summary>Monitors added, removed, moved or rescaled (raised on a background thread).</summary>
    event Action? DisplaysChanged;
}

public enum ScreenImageFormat
{
    Jpeg,
    Png,
}

public sealed record ScreenCaptureOptions
{
    /// <summary>The longer side of the image, in pixels (the Mac sends Realtime 1280). 0 = native size.</summary>
    public int MaxDimension { get; init; } = 1280;
    public ScreenImageFormat Format { get; init; } = ScreenImageFormat.Jpeg;
    /// <summary>JPEG quality 1–100 (the Mac uses compression factor 0.8).</summary>
    public int JpegQuality { get; init; } = 80;
    /// <summary>Only the monitor the cursor is on.</summary>
    public bool CursorScreenOnly { get; init; }
}

/// <summary>
/// One captured monitor (CompanionScreenCapture). On Windows <see cref="DisplayFrame"/> is the
/// monitor's bounds in physical virtual-desktop pixels (top-left origin), and the "points" sizes are
/// DIPs; the screenshot's size is the image's real size.
/// </summary>
public sealed record CompanionScreenCapture(
    byte[] ImageData,
    string MimeType,
    string Label,
    bool IsCursorScreen,
    int DisplayWidthInPoints,
    int DisplayHeightInPoints,
    PixelRect DisplayFrame,
    double DisplayScale,
    string DeviceName,
    int ScreenshotWidthInPixels,
    int ScreenshotHeightInPixels)
{
    /// <summary>A global physical-pixel point → this screenshot's pixels (origin top-left).</summary>
    public (double X, double Y) ScreenshotPoint(PixelPoint globalPoint)
    {
        var scaleX = ScreenshotWidthInPixels / (double)Math.Max(DisplayFrame.Width, 1);
        var scaleY = ScreenshotHeightInPixels / (double)Math.Max(DisplayFrame.Height, 1);
        return ((globalPoint.X - DisplayFrame.Left) * scaleX, (globalPoint.Y - DisplayFrame.Top) * scaleY);
    }

    /// <summary>A point in this screenshot's pixels → global physical pixels.</summary>
    public PixelPoint GlobalPoint(double screenshotX, double screenshotY)
    {
        var scaleX = DisplayFrame.Width / (double)Math.Max(ScreenshotWidthInPixels, 1);
        var scaleY = DisplayFrame.Height / (double)Math.Max(ScreenshotHeightInPixels, 1);
        return new PixelPoint(
            DisplayFrame.Left + (int)Math.Round(screenshotX * scaleX),
            DisplayFrame.Top + (int)Math.Round(screenshotY * scaleY));
    }

    public bool ContainsScreenshotPoint(double x, double y) =>
        x >= 0 && y >= 0 && x <= ScreenshotWidthInPixels && y <= ScreenshotHeightInPixels;

    /// <summary>The label the Mac gives each capture, verbatim.</summary>
    public static string LabelFor(int displayIndex, int displayCount, bool isCursorScreen)
    {
        if (displayCount == 1) return "user's screen (cursor is here)";
        return isCursorScreen
            ? $"screen {displayIndex + 1} of {displayCount}, cursor is on this screen (primary focus)"
            : $"screen {displayIndex + 1} of {displayCount}, secondary screen";
    }

    /// <summary>The size an image gets when its longer side is capped at <paramref name="maxDimension"/> (aspect kept, never upscaled).</summary>
    public static (int Width, int Height) ScaledSize(int width, int height, int maxDimension)
    {
        if (maxDimension <= 0 || (width <= maxDimension && height <= maxDimension)) return (width, height);
        var aspectRatio = width / (double)height;
        return width >= height
            ? (maxDimension, Math.Max(1, (int)(maxDimension / aspectRatio)))
            : (Math.Max(1, (int)(maxDimension * aspectRatio)), maxDimension);
    }
}

/// <summary>Screenshots of the monitors (ScreenCaptureKit on the Mac; GDI BitBlt here). OpenClicky's own overlay windows are left out through WDA_EXCLUDEFROMCAPTURE.</summary>
public interface IScreenCapture
{
    /// <summary>Every monitor (or only the cursor's), cursor screen first, labelled as on the Mac.</summary>
    Task<IReadOnlyList<CompanionScreenCapture>> CaptureAllScreensAsync(ScreenCaptureOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>The monitor the cursor is on.</summary>
    Task<CompanionScreenCapture?> CaptureCursorScreenAsync(ScreenCaptureOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="area"/> (in the image's own pixels) cut out of an encoded screenshot and
    /// encoded as JPEG: the ask lane's close-up around the pointer. Null when it cannot be cut.
    /// </summary>
    byte[]? CropJpeg(byte[] imageData, PixelRect area, int jpegQuality = 85) => null;
}
