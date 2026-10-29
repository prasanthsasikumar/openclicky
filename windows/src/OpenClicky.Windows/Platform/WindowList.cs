using System.Runtime.InteropServices;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>EnumWindows (which walks the z-order top to bottom) + DWM frame bounds + the cloaked flag.</summary>
public sealed class WindowList : IWindowList
{
    public int OwnProcessId => Environment.ProcessId;

    private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
    private const long WS_MINIMIZEBOX = 0x00020000;
    private const long WS_MAXIMIZEBOX = 0x00010000;

    /// <summary>
    /// The visible frame, DWM's caption-button area (reported relative to GetWindowRect's corner, so
    /// moved into global pixels here), the minimize / maximize boxes and the window's scale.
    /// </summary>
    public CaptionButtonLayout? CaptionButtonLayout(nint windowHandle, string appName)
    {
        if (windowHandle == 0 || !IsWindowVisible(windowHandle) || IsIconic(windowHandle)) return null;
        var frame = VisibleFrame(windowHandle);
        var windowBounds = new ScreenRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        ScreenRect? buttonArea = null;
        if (GetWindowRect(windowHandle, out var outer)
            && DwmGetWindowAttribute(windowHandle, DWMWA_CAPTION_BUTTON_BOUNDS, out RECT relative, Marshal.SizeOf<RECT>()) == 0
            && relative.Right > relative.Left && relative.Bottom > relative.Top)
        {
            buttonArea = new ScreenRect(outer.Left + relative.Left, outer.Top + relative.Top, relative.Right - relative.Left, relative.Bottom - relative.Top);
        }
        var style = (long)GetWindowLongPtr(windowHandle, GWL_STYLE);
        var dpi = GetDpiForWindow(windowHandle);
        return new CaptionButtonLayout(windowBounds, buttonArea, (style & (WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) != 0,
                                       dpi == 0 ? 1 : dpi / 96.0, appName);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public IReadOnlyList<OnScreenWindowInfo> VisibleWindows(bool includeCloaked = false)
    {
        var windows = new List<OnScreenWindowInfo>();
        var imageNameByProcessId = new Dictionary<int, string?>();
        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle)) return true;
            var isMinimized = IsIconic(windowHandle);
            if (isMinimized) return true;
            var isCloaked = IsCloaked(windowHandle);
            if (isCloaked && !includeCloaked) return true;
            var frame = VisibleFrame(windowHandle);
            var bounds = PixelRect.FromEdges(frame.Left, frame.Top, frame.Right, frame.Bottom);
            if (bounds.IsEmpty) return true;

            var processId = ProcessIdOf(windowHandle);
            if (!imageNameByProcessId.TryGetValue(processId, out var imageName))
            {
                imageName = ProcessDescriber.ImageName(processId);
                imageNameByProcessId[processId] = imageName;
            }
            var exStyle = (long)GetWindowLongPtr(windowHandle, GWL_EXSTYLE);
            windows.Add(new OnScreenWindowInfo(
                Handle: windowHandle,
                ProcessId: processId,
                ProcessName: imageName,
                Title: WindowTitle(windowHandle),
                ClassName: WindowClassName(windowHandle),
                Bounds: bounds,
                IsCloaked: isCloaked,
                IsMinimized: false,
                IsTopMost: (exStyle & WS_EX_TOPMOST) != 0,
                IsToolWindow: (exStyle & WS_EX_TOOLWINDOW) != 0,
                ZOrder: windows.Count));
            return true;
        }, 0);
        return windows;
    }
}
