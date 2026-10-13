//
//  FloatingWindowSupport.cs
//  OpenClicky (Windows)
//
//  What every floating OpenClicky window shares (the overlay, the island, the orb, the agent
//  card): borderless and transparent, topmost, a tool window (never in Alt-Tab or the taskbar),
//  non-activating (WS_EX_NOACTIVATE: a click never takes the keyboard from the app in front),
//  optionally click-through (WS_EX_TRANSPARENT), left out of screenshots
//  (WindowCaptureExclusion), and placed in physical pixels with SetWindowPos so a window lands
//  exactly on a monitor whatever its scale. The NSPanel flags of the Mac
//  (nonactivatingPanel, ignoresMouseEvents, canJoinAllSpaces) map onto these.
//

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Overlay;

internal static class FloatingWindowSupport
{
    /// <summary>The UI demo turns this on so the windows show up in its own screenshots.</summary>
    public static bool AllowsScreenCapture { get; set; }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Makes <paramref name="window"/> a floating OpenClicky window. Call before Show.</summary>
    public static void Configure(Window window, bool clickThrough, bool noActivate = true)
    {
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Topmost = true;
        window.SnapsToDevicePixels = true;
        window.UseLayoutRounding = true;
        TextOptionsForLayeredWindow(window);
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetExtendedStyles(handle, clickThrough, noActivate);
            if (!AllowsScreenCapture) WindowCaptureExclusion.Exclude(handle);
        };
    }

    /// <summary>Layered windows have no ClearType: grayscale, ideal-width text reads closest to the Mac's.</summary>
    private static void TextOptionsForLayeredWindow(Window window)
    {
        System.Windows.Media.TextOptions.SetTextRenderingMode(window, TextRenderingMode.Grayscale);
        System.Windows.Media.TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
    }

    public static void SetExtendedStyles(nint handle, bool clickThrough, bool noActivate)
    {
        if (handle == 0) return;
        var style = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        style &= ~WS_EX_APPWINDOW;
        style = noActivate ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        style = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(handle, GWL_EXSTYLE, (nint)style);
    }

    /// <summary>Moves and sizes the window in physical virtual-desktop pixels, topmost, without activating it.</summary>
    public static void PlaceInPixels(Window window, PixelRect rect)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        SetWindowPos(handle, HWND_TOPMOST, rect.Left, rect.Top, Math.Max(1, rect.Width), Math.Max(1, rect.Height), SWP_NOACTIVATE);
    }

    /// <summary>Moves the window to a pixel position without resizing, activating or restacking it (the overlay's viewport).</summary>
    public static void MoveInPixels(Window window, int left, int top)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        SetWindowPos(handle, 0, left, top, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOZORDER);
    }

    /// <summary>Re-asserts topmost (another topmost window may have come up over ours).</summary>
    public static void BringToTopWithoutActivating(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }

    /// <summary>The mouse in physical pixels.</summary>
    public static PixelPoint CursorPixel() => GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : default;

    public static ScreenPoint CursorScreenPoint()
    {
        var pixel = CursorPixel();
        return new ScreenPoint(pixel.X, pixel.Y);
    }

    /// <summary>Whether a mouse button is down now (polled; the island's click-outside rule).</summary>
    public static bool IsAnyMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    /// <summary>Gives a window the keyboard even though this app is in the background: Windows only lets the foreground
    /// thread hand focus over, so the input queues are joined for the call (the composer, the tour's field, the agent
    /// card's follow-up field). Returns the window that had the keyboard before, to hand it back.</summary>
    public static nint TakeKeyboardFocus(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var previousForeground = GetForegroundWindow();
        if (handle == 0) return previousForeground;
        SetExtendedStyles(handle, clickThrough: false, noActivate: false);
        var foregroundThread = GetWindowThreadProcessId(previousForeground, out _);
        var ownThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(handle);
            window.Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(ownThread, foregroundThread, false);
        }
        return previousForeground == handle ? 0 : previousForeground;
    }

    /// <summary>Hands the keyboard back to <paramref name="previousWindow"/> and makes <paramref name="window"/>
    /// non-activating again.</summary>
    public static void ReturnKeyboardFocus(Window window, nint previousWindow)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (previousWindow != 0 && IsWindow(previousWindow)) SetForegroundWindow(previousWindow);
        if (handle != 0) SetExtendedStyles(handle, clickThrough: false, noActivate: true);
    }

    // MARK: - Screens

    /// <summary>A monitor's frame as the Core models carry it: physical pixels in ScreenRect.</summary>
    public static ScreenRect FrameInPixels(this ScreenInfo screen) =>
        new(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);

    public static ScreenRect ToScreenRect(this PixelRect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    /// <summary>A rectangle in a monitor's own DIP space (ScreenInfo.BoundsInDips) → physical pixels.</summary>
    public static PixelRect DipsToPixels(this ScreenInfo screen, ScreenRect rectInDips) =>
        PixelRect.FromEdges(
            (int)Math.Round(rectInDips.Left * screen.Scale),
            (int)Math.Round(rectInDips.Top * screen.Scale),
            (int)Math.Round(rectInDips.Right * screen.Scale),
            (int)Math.Round(rectInDips.Bottom * screen.Scale));

    public static ScreenPoint DipsToPixels(this ScreenInfo screen, ScreenPoint pointInDips) =>
        new(pointInDips.X * screen.Scale, pointInDips.Y * screen.Scale);

    public static ScreenPoint PixelsToDips(this ScreenInfo screen, ScreenPoint pointInPixels) =>
        new(pointInPixels.X / screen.Scale, pointInPixels.Y / screen.Scale);

    public static ScreenRect BoundsInDipsRect(this ScreenInfo screen) =>
        new(screen.BoundsInDips.X, screen.BoundsInDips.Y, screen.BoundsInDips.Width, screen.BoundsInDips.Height);

    public static ScreenRect WorkAreaInDipsRect(this ScreenInfo screen) =>
        new(screen.WorkAreaInDips.X, screen.WorkAreaInDips.Y, screen.WorkAreaInDips.Width, screen.WorkAreaInDips.Height);
}
