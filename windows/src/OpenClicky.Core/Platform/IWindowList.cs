namespace OpenClicky.Core.Platform;

/// <summary>A top-level window as EnumWindows + DWM report it (CGWindowList's entry).</summary>
/// <param name="Bounds">DWMWA_EXTENDED_FRAME_BOUNDS (the visible frame, without the invisible resize border), physical pixels.</param>
/// <param name="IsCloaked">DWM cloaked: on another virtual desktop, a suspended UWP app, or hidden by the shell — not actually visible.</param>
/// <param name="ZOrder">0 = frontmost.</param>
public sealed record OnScreenWindowInfo(
    nint Handle,
    int ProcessId,
    string? ProcessName,
    string Title,
    string ClassName,
    PixelRect Bounds,
    bool IsCloaked,
    bool IsMinimized,
    bool IsTopMost,
    bool IsToolWindow,
    int ZOrder);

public interface IWindowList
{
    /// <summary>Visible top-level windows front to back (minimised and zero-sized ones left out; cloaked ones only when asked).</summary>
    IReadOnlyList<OnScreenWindowInfo> VisibleWindows(bool includeCloaked = false);

    /// <summary>The pid of OpenClicky's own process (to skip its windows when deciding what covers what).</summary>
    int OwnProcessId { get; }

    /// <summary>Where a window's caption buttons are (WindowControlLocator); null when the window is gone or minimised.</summary>
    Pointing.CaptionButtonLayout? CaptionButtonLayout(nint windowHandle, string appName) => null;
}
