using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>EnumWindows (which walks the z-order top to bottom) + DWM frame bounds + the cloaked flag.</summary>
public sealed class WindowList : IWindowList
{
    public int OwnProcessId => Environment.ProcessId;

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
