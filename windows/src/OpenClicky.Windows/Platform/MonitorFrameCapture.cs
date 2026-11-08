using System.IO;
using System.Runtime.InteropServices;
using OpenClicky.Core.Platform;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// One frame of a monitor as DWM composed it for the screen, through Windows.Graphics.Capture, as a PNG. GDI BitBlt
/// (<see cref="ScreenCapture"/>) reads the screen while DWM may be part-way through composing it, and over the drawing
/// stage (a DirectComposition window that WebView2 draws into) that tears: in 16 of 200 BitBlt grabs of an animated
/// 3D drawing, a block of the drawing showed the desktop behind it or a figure from another moment, while 200 frames
/// through this API were all whole. Used for the drawing stage smoke's --shot; null when the API isn't there or
/// gives no frame, and the caller falls back to BitBlt. The capture border Windows may draw is only on the screen,
/// not in the frame.
/// </summary>
public static class MonitorFrameCapture
{
    /// <summary>The monitor under the middle of <paramref name="bounds"/>, as a PNG; null when it can't be had.</summary>
    public static async Task<byte[]?> CapturePngAsync(PixelRect bounds, TimeSpan? wait = null)
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) return null;
            var monitor = MonitorFromPoint(new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 }, MONITOR_DEFAULTTONEAREST);
            var item = ItemForMonitor(monitor);
            var device = CreateDevice();
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using var session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch (Exception) { /* older Windows: the cursor may be in */ }
            session.StartCapture();
            var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(2));
            Direct3D11CaptureFrame? frame;
            while ((frame = pool.TryGetNextFrame()) is null)
            {
                if (DateTime.UtcNow > deadline) return null;
                await Task.Delay(15).ConfigureAwait(false);
            }
            using (frame)
            {
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                stream.Seek(0);
                using var memory = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(memory).ConfigureAwait(false);
                return memory.ToArray();
            }
        }
        catch (Exception)
        {
            return null; // no Windows.Graphics.Capture here (or no GPU device): BitBlt it is
        }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    private static GraphicsCaptureItem ItemForMonitor(nint monitor)
    {
        var interop = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem").AsInterface<IGraphicsCaptureItemInterop>();
        var pointer = interop.CreateForMonitor(monitor, in GraphicsCaptureItemIid);
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount,
        uint sdkVersion, out nint device, out int featureLevel, out nint context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    /// <summary>A hardware Direct3D 11 device (BGRA support) as WinRT's IDirect3DDevice.</summary>
    private static IDirect3DDevice CreateDevice()
    {
        const int Hardware = 1;
        const uint BgraSupport = 0x20, SdkVersion = 7;
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, Hardware, 0, BgraSupport, 0, 0, SdkVersion, out var device, out _, out var context));
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in DxgiDeviceIid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            Marshal.Release(context);
            Marshal.Release(device);
        }
    }
}
