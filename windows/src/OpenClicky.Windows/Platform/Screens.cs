using Microsoft.Win32;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// The monitors (NSScreen.screens) via EnumDisplayMonitors + GetMonitorInfo + GetDpiForMonitor.
/// Coordinates are physical pixels: the app is per-monitor-v2 DPI aware (app.manifest).
/// Display changes come from SystemEvents.DisplaySettingsChanged (its hidden top-level window gets
/// WM_DISPLAYCHANGE, which a message-only window would not).
/// </summary>
public sealed class Screens : IScreens, IDisposable
{
    private Action? displaysChanged;

    public event Action? DisplaysChanged
    {
        add
        {
            if (displaysChanged is null) SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            displaysChanged += value;
        }
        remove
        {
            displaysChanged -= value;
            if (displaysChanged is null) SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs eventArgs) => displaysChanged?.Invoke();

    public IReadOnlyList<ScreenInfo> All()
    {
        var cursor = CursorPosition();
        var monitors = new List<nint>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint deviceContext, ref RECT monitorRect, nint data) =>
        {
            monitors.Add(monitor);
            return true;
        }, 0);

        var screens = new List<ScreenInfo>();
        foreach (var monitor in monitors)
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
            if (!GetMonitorInfo(monitor, ref info)) continue;
            var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? (int)dpiX : 96;
            var scale = dpi / 96.0;
            var bounds = PixelRect.FromEdges(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
            var workArea = PixelRect.FromEdges(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
            screens.Add(new ScreenInfo(
                DeviceName: info.szDevice,
                MonitorHandle: monitor,
                Bounds: bounds,
                WorkArea: workArea,
                BoundsInDips: ToDips(bounds, scale),
                WorkAreaInDips: ToDips(workArea, scale),
                Scale: scale,
                Dpi: dpi,
                IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                ContainsCursor: bounds.Contains(cursor),
                Index: screens.Count));
        }
        return screens.OrderByDescending(screen => screen.IsPrimary).ToList();
    }

    public ScreenInfo? Primary() => All().FirstOrDefault(screen => screen.IsPrimary);

    public ScreenInfo? CursorScreen()
    {
        var screens = All();
        return screens.FirstOrDefault(screen => screen.ContainsCursor) ?? screens.FirstOrDefault(screen => screen.IsPrimary);
    }

    public ScreenInfo? ScreenAt(PixelPoint point)
    {
        var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        var screens = All();
        return screens.FirstOrDefault(screen => screen.MonitorHandle == monitor) ?? screens.FirstOrDefault();
    }

    public PixelPoint CursorPosition() => GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : default;

    private static DoubleRect ToDips(PixelRect rect, double scale) =>
        new(rect.Left / scale, rect.Top / scale, rect.Width / scale, rect.Height / scale);

    public void Dispose() => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
}
