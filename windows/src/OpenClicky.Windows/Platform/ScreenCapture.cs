using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// Keeps a window out of every screenshot and screen recording (SetWindowDisplayAffinity
/// WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+): what ScreenCaptureKit's excludingWindows did on the
/// Mac. The overlay, island and orb windows call it once their HWND exists (SourceInitialized).
/// </summary>
public static class WindowCaptureExclusion
{
    /// <summary>False when Windows refused (older build, or the window is a child window).</summary>
    public static bool Exclude(nint windowHandle) => windowHandle != 0 && SetWindowDisplayAffinity(windowHandle, WDA_EXCLUDEFROMCAPTURE);

    public static bool Include(nint windowHandle) => windowHandle != 0 && SetWindowDisplayAffinity(windowHandle, WDA_NONE);

    public static bool IsExcluded(nint windowHandle) =>
        GetWindowDisplayAffinity(windowHandle, out var affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;
}

/// <summary>
/// Screenshots of each monitor with GDI BitBlt (SRCCOPY | CAPTUREBLT so layered windows are in),
/// scaled so the longer side is at most <see cref="ScreenCaptureOptions.MaxDimension"/> and encoded
/// as JPEG/PNG. The real encoded size is what is recorded, as on the Mac.
/// </summary>
public sealed class ScreenCapture(IScreens screens) : IScreenCapture
{
    public Task<IReadOnlyList<CompanionScreenCapture>> CaptureAllScreensAsync(ScreenCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScreenCaptureOptions();
        var captureOptions = options;
        return Task.Run<IReadOnlyList<CompanionScreenCapture>>(() =>
        {
            var allScreens = screens.All();
            if (allScreens.Count == 0) throw new InvalidOperationException("No display available for capture");
            // The cursor screen first, as on the Mac.
            var sorted = allScreens.OrderByDescending(screen => screen.ContainsCursor).ToList();
            if (captureOptions.CursorScreenOnly) sorted = sorted.Take(1).ToList();
            var captures = new List<CompanionScreenCapture>();
            for (var index = 0; index < sorted.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var screen = sorted[index];
                var capture = Capture(screen, captureOptions, CompanionScreenCapture.LabelFor(index, sorted.Count, screen.ContainsCursor));
                if (capture is not null) captures.Add(capture);
            }
            if (captures.Count == 0) throw new InvalidOperationException("Failed to capture any screen");
            return captures;
        }, cancellationToken);
    }

    public async Task<CompanionScreenCapture?> CaptureCursorScreenAsync(ScreenCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        var captures = await CaptureAllScreensAsync((options ?? new ScreenCaptureOptions()) with { CursorScreenOnly = true }, cancellationToken).ConfigureAwait(false);
        return captures.FirstOrDefault();
    }

    /// <summary>The ask lane's close-up: a square cut from the screenshot the model sees, at its full size.</summary>
    public byte[]? CropJpeg(byte[] imageData, PixelRect area, int jpegQuality = 85)
    {
        try
        {
            using var stream = new MemoryStream(imageData);
            using var image = new Bitmap(stream);
            var cropArea = Rectangle.Intersect(new Rectangle(area.Left, area.Top, area.Width, area.Height), new Rectangle(0, 0, image.Width, image.Height));
            if (cropArea.Width <= 0 || cropArea.Height <= 0) return null;
            using var cropped = image.Clone(cropArea, PixelFormat.Format24bppRgb);
            return Encode(cropped, new ScreenCaptureOptions { Format = ScreenImageFormat.Jpeg, JpegQuality = jpegQuality });
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException or ExternalException)
        {
            // Not an image GDI+ can read (or an area it refuses): the question goes without a close-up.
            return null;
        }
    }

    private static CompanionScreenCapture? Capture(ScreenInfo screen, ScreenCaptureOptions options, string label)
    {
        using var native = CaptureRegion(screen.Bounds);
        if (native is null) return null;
        var (width, height) = CompanionScreenCapture.ScaledSize(native.Width, native.Height, options.MaxDimension);
        using var scaled = width == native.Width && height == native.Height ? null : Scale(native, width, height);
        var image = scaled ?? native;
        var bytes = Encode(image, options);
        return new CompanionScreenCapture(
            ImageData: bytes,
            MimeType: options.Format == ScreenImageFormat.Png ? "image/png" : "image/jpeg",
            Label: label,
            IsCursorScreen: screen.ContainsCursor,
            DisplayWidthInPoints: (int)Math.Round(screen.BoundsInDips.Width),
            DisplayHeightInPoints: (int)Math.Round(screen.BoundsInDips.Height),
            DisplayFrame: screen.Bounds,
            DisplayScale: screen.Scale,
            DeviceName: screen.DeviceName,
            ScreenshotWidthInPixels: image.Width,
            ScreenshotHeightInPixels: image.Height);
    }

    /// <summary>BitBlt of a virtual-desktop rectangle into a 32-bit bitmap.</summary>
    internal static Bitmap? CaptureRegion(PixelRect region)
    {
        if (region.IsEmpty) return null;
        var screenDeviceContext = GetDC(0);
        if (screenDeviceContext == 0) return null;
        var memoryDeviceContext = CreateCompatibleDC(screenDeviceContext);
        var bitmapHandle = CreateCompatibleBitmap(screenDeviceContext, region.Width, region.Height);
        var previous = SelectObject(memoryDeviceContext, bitmapHandle);
        try
        {
            if (!BitBlt(memoryDeviceContext, 0, 0, region.Width, region.Height, screenDeviceContext, region.Left, region.Top, SRCCOPY | CAPTUREBLT))
            {
                return null;
            }
            SelectObject(memoryDeviceContext, previous);
            previous = 0;
            // FromHbitmap copies the pixels into a GDI+ bitmap we own; the HBITMAP is freed below.
            return Image.FromHbitmap(bitmapHandle);
        }
        finally
        {
            if (previous != 0) SelectObject(memoryDeviceContext, previous);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDeviceContext);
            ReleaseDC(0, screenDeviceContext);
        }
    }

    internal static Bitmap Scale(Image source, int width, int height)
    {
        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
        graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return scaled;
    }

    private static byte[] Encode(Image image, ScreenCaptureOptions options)
    {
        using var stream = new MemoryStream();
        if (options.Format == ScreenImageFormat.Png)
        {
            image.Save(stream, ImageFormat.Png);
        }
        else
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(options.JpegQuality, 1, 100));
            image.Save(stream, encoder, parameters);
        }
        return stream.ToArray();
    }
}
