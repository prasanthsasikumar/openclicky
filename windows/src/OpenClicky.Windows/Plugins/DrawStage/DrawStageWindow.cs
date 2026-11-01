//
//  DrawStageWindow.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  The drawing stage on screen (IDrawStageSurface): one see-through, click-through, topmost window
//  over a monitor, showing Assets/DrawStage/index.html in WebView2. A WPF window can't do this: a
//  WebView2 control in a transparent (layered) WPF window draws nothing, and in an opaque one it
//  covers the screen. So the window is a plain Win32 popup without a redirection surface, its
//  content is a Windows.UI.Composition visual tree, and WebView2 runs in visual hosting mode
//  (CoreWebView2CompositionController) with a transparent background: the page's alpha reaches
//  the desktop. Left out of screenshots like every floating OpenClicky window, so the next
//  question's screenshot sees the screen, not the drawing.
//
//  The page loads from the app's folder through a virtual host and may not navigate anywhere
//  else; it has no network (its Content-Security-Policy) and no bridge but web messages.
//

using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Platform;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace OpenClicky.Windows.Plugins.DrawStage;

internal sealed class DrawStageWindow : IDrawStageSurface, IDisposable
{
    /// <summary>The page's origin: the app's Assets/DrawStage folder, mapped by WebView2 (never on the network).</summary>
    public const string PageHost = "drawstage.openclicky";
    private const string PageUrl = "https://" + PageHost + "/index.html";

    private readonly NativeMethods.WndProc windowProcedure;
    private nint windowHandle;
    private object? dispatcherQueueController;
    private Compositor? compositor;
    private DesktopWindowTarget? windowTarget;
    private ContainerVisual? rootVisual;
    private CoreWebView2CompositionController? webView;
    private Task? starting;
    private (PixelRect Frame, double Scale)? placement;

    public Action<string>? MessageReceived { get; set; }

    /// <summary>Why the page could not be shown, when it couldn't (no WebView2 runtime, no composition).</summary>
    public string? StartFailure { get; private set; }

    public DrawStageWindow()
    {
        windowProcedure = WindowProcedure;
    }

    public void Present(PixelRect frame, double scale)
    {
        placement = (frame, scale);
        starting ??= StartAsync();
        if (webView is not null) Place();
    }

    public void Prepare() => starting ??= StartAsync();

    public void Hide()
    {
        if (windowHandle != 0) ShowWindow(windowHandle, SW_HIDE);
    }

    public void Post(string json) => webView?.CoreWebView2?.PostWebMessageAsJson(json);

    public void Dispose()
    {
        webView?.Close();
        webView = null;
        windowTarget?.Dispose();
        compositor?.Dispose();
        if (windowHandle != 0) NativeMethods.DestroyWindow(windowHandle);
        windowHandle = 0;
    }

    // MARK: - Starting

    private async Task StartAsync()
    {
        try
        {
            CreateWindow();
            CreateVisualTree();
            await CreateWebViewAsync();
            Place();
        }
        catch (Exception exception) when (exception is COMException or WebView2RuntimeNotFoundException or InvalidOperationException or FileNotFoundException)
        {
            StartFailure = exception.Message;
            MessageReceived?.Invoke(DrawStageMessages.Error("the stage did not start: " + exception.Message));
        }
    }

    private void CreateWindow()
    {
        var className = "OpenClicky.DrawStage." + Guid.NewGuid().ToString("N");
        var windowClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(windowProcedure),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className,
        };
        if (NativeMethods.RegisterClassEx(ref windowClass) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        // No redirection surface: the composition tree is the window's only content, alpha and all. Layered (fully
        // opaque as a layer) + transparent: the mouse goes to whatever is under the drawing.
        const uint exStyle = WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        windowHandle = NativeMethods.CreateWindowEx(exStyle, className, "OpenClicky drawing", WS_POPUP, 0, 0, 1, 1, 0, 0, windowClass.hInstance, 0);
        if (windowHandle == 0) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        SetLayeredWindowAttributes(windowHandle, 0, 255, LWA_ALPHA);
        if (!FloatingWindowSupport.AllowsScreenCapture) WindowCaptureExclusion.Exclude(windowHandle);
    }

    private void CreateVisualTree()
    {
        // Windows.UI.Composition needs a DispatcherQueue on this (the UI) thread; WPF's message loop pumps it.
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = DQTYPE_THREAD_CURRENT, apartmentType = DQTAT_COM_STA };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
            dispatcherQueueController = Marshal.GetObjectForIUnknown(controller);
            Marshal.Release(controller);
        }
        compositor = new Compositor();
        var interop = compositor.As<ICompositorDesktopInterop>();
        interop.CreateDesktopWindowTarget(windowHandle, true, out var rawTarget);
        windowTarget = DesktopWindowTarget.FromAbi(rawTarget);
        Marshal.Release(rawTarget);
        rootVisual = compositor.CreateContainerVisual();
        rootVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;
        windowTarget.Root = rootVisual;
    }

    private async Task CreateWebViewAsync()
    {
        // No white flash while the page loads: the default background is see-through from the first frame.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "0");
        var userData = Path.Combine(OpenClickyPaths.AppDataDirectory, "DrawStage");
        var environment = await CoreWebView2Environment.CreateAsync(null, userData);
        var controller = await environment.CreateCoreWebView2CompositionControllerAsync(windowHandle);
        // WebView2 marshals RootVisualTarget as a classic COM object: hand it the visual's own IUnknown.
        var visualPointer = MarshalInspectable<object>.FromManaged(rootVisual!);
        try { controller.RootVisualTarget = Marshal.GetObjectForIUnknown(visualPointer); }
        finally { Marshal.Release(visualPointer); }

        controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        controller.ShouldDetectMonitorScaleChanges = false;
        var page = controller.CoreWebView2;
        var settings = page.Settings;
        var devTools = Environment.GetEnvironmentVariable("OPENCLICKY_DRAWSTAGE_DEVTOOLS") == "1";
        settings.AreDevToolsEnabled = devTools;
        settings.AreDefaultContextMenusEnabled = devTools;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        page.SetVirtualHostNameToFolderMapping(PageHost, Path.Combine(AppContext.BaseDirectory, "Assets", "DrawStage"), CoreWebView2HostResourceAccessKind.DenyCors);
        page.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith("https://" + PageHost + "/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        page.NewWindowRequested += (_, args) => args.Handled = true;
        page.WebMessageReceived += (_, args) => MessageReceived?.Invoke(args.WebMessageAsJson);
        webView = controller;
        page.Navigate(PageUrl);
    }

    private void Place()
    {
        if (placement is not { } where || webView is null || windowHandle == 0) return;
        var frame = where.Frame;
        webView.RasterizationScale = where.Scale;
        webView.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, frame.Width), Math.Max(1, frame.Height));
        webView.IsVisible = true;
        NativeSetWindowPos(windowHandle, HWND_TOPMOST, frame.Left, frame.Top, Math.Max(1, frame.Width), Math.Max(1, frame.Height), SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam) => message switch
    {
        WM_NCHITTEST => HTTRANSPARENT,
        WM_MOUSEACTIVATE => MA_NOACTIVATE,
        _ => NativeMethods.DefWindowProc(handle, message, wParam, lParam),
    };

    // MARK: - Win32 and composition interop

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_HIDE = 0;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const nint HTTRANSPARENT = -1;
    private const nint MA_NOACTIVATE = 3;
    private const int DQTYPE_THREAD_CURRENT = 2;
    private const int DQTAT_COM_STA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(nint hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out nint result);

        void EnsureOnThread(uint threadId);
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint dispatcherQueueController);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
