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
    private static string PageFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "DrawStage");

    /// <summary>The program worker's policy: no network, no scripts or workers of its own (so no import() of an address
    /// either), eval for the program's statements.</summary>
    private const string WorkerPolicy = "default-src 'none'; script-src 'unsafe-eval'; worker-src 'none'; connect-src 'none'";
    private bool workerPolicyLogged;

    private readonly NativeMethods.WndProc windowProcedure;
    private nint windowHandle;
    private object? dispatcherQueueController;
    private Compositor? compositor;
    private DesktopWindowTarget? windowTarget;
    private ContainerVisual? rootVisual;
    private CoreWebView2CompositionController? webView;
    private Task? starting;
    private (PixelRect Frame, double Scale)? placement;
    /// <summary>The 3D figures' boxes in physical screen pixels: over them the window takes the mouse.</summary>
    private List<PixelRect> figureBoxes = [];
    private System.Windows.Threading.DispatcherTimer? pointerWatch;
    private bool takesMouse;
    private bool dragging;

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
        // Hidden, the page gets no frames (its loops and the 3D figures stop) until it is shown again.
        if (webView is not null) webView.IsVisible = false;
        if (windowHandle != 0) ShowWindow(windowHandle, SW_HIDE);
    }

    public void Post(string json) => webView?.CoreWebView2?.PostWebMessageAsJson(json);

    /// <summary>The page as it looks now, the whole monitor's worth, as a PNG or JPEG; null before the page has loaded.
    /// WebView2 renders it itself, so it works although the window is left out of screen captures.</summary>
    public async Task<byte[]?> CaptureAsync(bool png)
    {
        if (webView?.CoreWebView2 is not { } page) return null;
        using var stream = new MemoryStream();
        await page.CapturePreviewAsync(png ? CoreWebView2CapturePreviewImageFormat.Png : CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
        return stream.ToArray();
    }

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
        // Nothing awaits this task: any failure is said here, so a question waiting on the stage hears it and answers in words.
        catch (Exception exception) when (exception is not OutOfMemoryException)
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
        // Visual hosting leaves the pointer's look to the window: the page's (grab over a figure, move over a card).
        controller.CursorChanged += (_, _) => { if (takesMouse) SetCursor(controller.Cursor); };
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
        // The page's files are served from the app folder by this handler (never the network): the html, scripts and
        // styles under Assets/DrawStage, nothing else. A virtual host folder mapping would be simpler, but its responses
        // can't carry headers and don't pass through here, and the program worker needs a CSP of its own: a worker is
        // governed by its own response's policy, not the page's <meta>.
        page.AddWebResourceRequestedFilter($"https://{PageHost}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        page.WebResourceRequested += (_, args) =>
        {
            var (status, reason, body, headers) = PageFile(args.Request.Uri);
            args.Response = environment.CreateWebResourceResponse(body, status, reason, headers);
            if (status == 200 && headers.Contains(WorkerPolicy, StringComparison.Ordinal) && !workerPolicyLogged)
            {
                workerPolicyLogged = true;
                AppLog.Write("draw: the program worker is served with its own policy (no network, no scripts, no workers)");
            }
        };
        // A page that fails to load says so, as a start failure does, instead of never saying ready.
        page.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) MessageReceived?.Invoke(DrawStageMessages.Error($"the stage did not start: its page did not load ({args.WebErrorStatus})"));
        };
        page.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith("https://" + PageHost + "/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        page.NewWindowRequested += (_, args) => args.Handled = true;
        page.WebMessageReceived += (_, args) =>
        {
            var json = args.WebMessageAsJson;
            if (json.Contains("\"figures\"", StringComparison.Ordinal)) NoteFigureBoxes(json);
            MessageReceived?.Invoke(json);
        };
        webView = controller;
        page.Navigate(PageUrl);
    }

    /// <summary>The response for a request to the page's host: a file under Assets/DrawStage with its type (the worker's with its
    /// own policy), or 404 for anything else (another type, a path that leaves the folder, a file that isn't there).</summary>
    private static (int Status, string Reason, Stream? Body, string Headers) PageFile(string uri)
    {
        const string NotFound = "Content-Type: text/plain; charset=utf-8";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !string.Equals(parsed.Host, PageHost, StringComparison.OrdinalIgnoreCase))
        {
            return (404, "Not Found", null, NotFound);
        }
        var relative = Uri.UnescapeDataString(parsed.AbsolutePath).TrimStart('/');
        var type = Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            _ => null,
        };
        var folder = Path.GetFullPath(PageFolder) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(folder, relative));
        if (type is null || !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return (404, "Not Found", null, NotFound);
        var headers = $"Content-Type: {type}\r\nCache-Control: no-store";
        if (string.Equals(relative, "stage-worker.js", StringComparison.OrdinalIgnoreCase)) headers += $"\r\nContent-Security-Policy: {WorkerPolicy}";
        return (200, "OK", new MemoryStream(File.ReadAllBytes(path)), headers);
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

    private nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_NCHITTEST:
                return takesMouse ? HTCLIENT : HTTRANSPARENT;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_SETCURSOR when takesMouse && webView is not null && (lParam.ToInt64() & 0xFFFF) == HTCLIENT:
                SetCursor(webView.Cursor);
                return 1;
            case WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP or WM_MOUSEWHEEL when webView is not null:
            {
                // A 3D figure is under the pointer: the mouse goes into the page (visual hosting has no window of its own).
                var x = (short)(lParam.ToInt64() & 0xFFFF);
                var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                var point = new System.Drawing.Point(x, y);
                if (message == WM_MOUSEWHEEL)
                {
                    var client = new NativePoint { X = x, Y = y };
                    ScreenToClient(handle, ref client);
                    point = new System.Drawing.Point(client.X, client.Y);
                }
                if (message == WM_LBUTTONDOWN) { dragging = true; SetCapture(handle); }
                if (message == WM_LBUTTONUP) { dragging = false; ReleaseCapture(); }
                var keys = (CoreWebView2MouseEventVirtualKeys)(int)(wParam.ToInt64() & 0xFFFF);
                var data = message == WM_MOUSEWHEEL ? (uint)(short)((wParam.ToInt64() >> 16) & 0xFFFF) : 0;
                webView.SendMouseInput((CoreWebView2MouseEventKind)(int)message, keys, data, point);
                return 0;
            }
            default:
                return NativeMethods.DefWindowProc(handle, message, wParam, lParam);
        }
    }

    // MARK: - Taking the mouse over 3D figures

    /// <summary>The page's "figures" message: each figure's box in page pixels, turned into screen pixels.</summary>
    private void NoteFigureBoxes(string json)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject message
            || message["type"]?.GetValue<string>() != "figures" || placement is not { } where) return;
        var boxes = new List<PixelRect>();
        foreach (var node in message["rects"]?.AsArray() ?? [])
        {
            double Value(string key) => node?[key]?.GetValue<double>() ?? 0;
            boxes.Add(new PixelRect(where.Frame.Left + (int)(Value("x") * where.Scale), where.Frame.Top + (int)(Value("y") * where.Scale),
                (int)(Value("w") * where.Scale), (int)(Value("h") * where.Scale)));
        }
        figureBoxes = boxes;
        if (boxes.Count > 0 && pointerWatch is null)
        {
            pointerWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            pointerWatch.Tick += (_, _) => WatchPointer();
            pointerWatch.Start();
        }
        else if (boxes.Count == 0)
        {
            pointerWatch?.Stop();
            pointerWatch = null;
            SetTakesMouse(false);
        }
    }

    /// <summary>Takes the mouse while the pointer is over a figure (or a drag that started on one goes on), else lets it through.</summary>
    private void WatchPointer()
    {
        var cursor = FloatingWindowSupport.CursorPixel();
        var over = figureBoxes.Any(box => cursor.X >= box.Left && cursor.X < box.Right && cursor.Y >= box.Top && cursor.Y < box.Bottom);
        SetTakesMouse(over || dragging);
    }

    private void SetTakesMouse(bool value)
    {
        if (takesMouse == value || windowHandle == 0) return;
        takesMouse = value;
        var style = (long)GetWindowLongPtr(windowHandle, GWL_EXSTYLE);
        style = value ? style & ~(long)WS_EX_TRANSPARENT : style | WS_EX_TRANSPARENT;
        SetWindowLongPtr(windowHandle, GWL_EXSTYLE, (nint)style);
        if (!value) webView?.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, new System.Drawing.Point());
    }

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
    private const uint WM_SETCURSOR = 0x0020;
    private const nint HTTRANSPARENT = -1;
    private const nint HTCLIENT = 1;
    private const int GWL_EXSTYLE = -20;
    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MOUSEWHEEL = 0x020A;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
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
