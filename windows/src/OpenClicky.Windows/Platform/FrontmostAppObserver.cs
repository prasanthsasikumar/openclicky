using System.IO;
using System.Diagnostics;
using System.Windows.Automation;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// What the person is looking at: the foreground window → pid → process, and for browsers the
/// front tab's URL from the address bar (UI Automation, bounded to 0.25 s). Foreground changes come
/// from SetWinEventHook(EVENT_SYSTEM_FOREGROUND) on its own message-loop thread.
/// </summary>
public sealed class FrontmostAppObserver : IFrontmostAppObserver
{
    private static readonly TimeSpan UrlBudget = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan UrlTimeout = TimeSpan.FromSeconds(0.4);

    private MessageLoopThread? hookThread;
    private WinEventProc? winEventProcedure;
    private nint winEventHook;
    private volatile FrontAppInfo? lastExternalForeground;

    public event Action<FrontAppInfo>? ForegroundChanged;

    public FrontAppInfo? LastExternalForeground => lastExternalForeground;

    public void Start()
    {
        if (hookThread is not null) return;
        hookThread = new MessageLoopThread("OpenClicky foreground hook");
        winEventProcedure = OnWinEvent;
        winEventHook = hookThread.Invoke(() => SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, winEventProcedure,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        lastExternalForeground = CurrentWithoutUrl();
    }

    public void Stop()
    {
        if (hookThread is null) return;
        var hook = winEventHook;
        if (hook != 0) hookThread.Invoke(() => UnhookWinEvent(hook));
        winEventHook = 0;
        hookThread.Dispose();
        hookThread = null;
    }

    public void Dispose() => Stop();

    private void OnWinEvent(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint threadId, uint time)
    {
        if (windowHandle == 0) return;
        try
        {
            var info = Describe(windowHandle);
            if (info is null || info.ProcessId == Environment.ProcessId) return;
            lastExternalForeground = info;
            ForegroundChanged?.Invoke(info);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"foreground hook handler failed: {exception}");
        }
    }

    public FrontAppInfo? CurrentWithoutUrl()
    {
        var windowHandle = ExternalForegroundWindow();
        return windowHandle == 0 ? null : Describe(windowHandle);
    }

    public async Task<FrontAppInfo?> CurrentAsync(bool includeUrl = true, CancellationToken cancellationToken = default)
    {
        var info = CurrentWithoutUrl();
        if (info is null || !includeUrl || !info.IsBrowser) return info;
        var windowHandle = info.WindowHandle;
        var url = await UiaSupport.RunAsync(() => BrowserAddressBar.ReadUrl(windowHandle, UrlBudget), UrlTimeout, null, cancellationToken).ConfigureAwait(false);
        return info with { Url = url };
    }

    /// <summary>The foreground window, or — when it is OpenClicky's own — the next visible window of another process down the z-order.</summary>
    private static nint ExternalForegroundWindow()
    {
        var windowHandle = GetForegroundWindow();
        if (windowHandle == 0) return 0;
        if (ProcessIdOf(windowHandle) != Environment.ProcessId) return windowHandle;
        for (var candidate = GetWindow(windowHandle, GW_HWNDNEXT); candidate != 0; candidate = GetWindow(candidate, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(candidate) || IsIconic(candidate) || IsCloaked(candidate)) continue;
            if (ProcessIdOf(candidate) == Environment.ProcessId) continue;
            var exStyle = (long)GetWindowLongPtr(candidate, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) continue;
            if (GetWindow(candidate, GW_OWNER) != 0 && (exStyle & WS_EX_NOACTIVATE) != 0) continue;
            if (WindowTitle(candidate).Length == 0) continue;
            return candidate;
        }
        return 0;
    }

    internal static FrontAppInfo? Describe(nint windowHandle)
    {
        var rootWindow = GetAncestor(windowHandle, GA_ROOT);
        if (rootWindow != 0) windowHandle = rootWindow;
        var processId = ProcessIdOf(windowHandle);
        if (processId == 0) return null;
        var description = ProcessDescriber.Describe(processId);

        // A UWP app's frame belongs to ApplicationFrameHost.exe; the app itself owns the CoreWindow inside it.
        if (string.Equals(description.ImageName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var hostedProcessId = HostedUwpProcessId(windowHandle, processId);
            if (hostedProcessId != 0) description = ProcessDescriber.Describe(hostedProcessId);
        }

        return new FrontAppInfo
        {
            WindowHandle = windowHandle,
            ProcessId = description.ProcessId,
            ProcessName = description.ImageName,
            ExecutablePath = description.ExecutablePath,
            DisplayName = description.DisplayName,
            WindowTitle = WindowTitle(windowHandle),
            IsBrowser = BrowserProcesses.IsBrowser(description.ImageName),
        };
    }

    private static int HostedUwpProcessId(nint frameWindow, int frameProcessId)
    {
        var hostedProcessId = 0;
        EnumChildWindows(frameWindow, (child, _) =>
        {
            var childProcessId = ProcessIdOf(child);
            if (childProcessId != 0 && childProcessId != frameProcessId)
            {
                hostedProcessId = childProcessId;
                return false;
            }
            return true;
        }, 0);
        return hostedProcessId;
    }

    public byte[]? AppIconPng(string executablePath, int size = 32) => AppIcons.Png(executablePath, size);
}

/// <summary>The address bar of a browser window, found by a bounded walk that never enters the page itself.</summary>
internal static class BrowserAddressBar
{
    public static string? ReadUrl(nint windowHandle, TimeSpan budget)
    {
        var window = UiaSupport.Safe(() => AutomationElement.FromHandle(windowHandle));
        if (window is null) return null;
        var request = UiaSupport.WalkCacheRequest();
        var root = UiaSupport.Safe(() => window.GetUpdatedCache(request));
        if (root is null) return null;

        var stopwatch = Stopwatch.StartNew();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < 400 && stopwatch.Elapsed < budget)
        {
            var (element, depth) = queue.Dequeue();
            visited++;
            var controlType = UiaSupport.Cached<ControlType>(element, AutomationElement.ControlTypeProperty);
            if (controlType == ControlType.Document) continue; // the page: its links are not the address bar
            if (controlType == ControlType.Edit || controlType == ControlType.ComboBox)
            {
                var automationId = UiaSupport.Cached<string>(element, AutomationElement.AutomationIdProperty) ?? string.Empty;
                var value = UiaSupport.Cached<string>(element, ValuePattern.ValueProperty);
                if (NormalizedUrl(value) is { } url && (automationId.Contains("url", StringComparison.OrdinalIgnoreCase) || LooksLikeAddress(value!)))
                {
                    return url;
                }
            }
            if (depth >= 12) continue;
            foreach (var child in UiaSupport.CachedChildren(element, request)) queue.Enqueue((child, depth + 1));
        }
        return null;
    }

    private static bool LooksLikeAddress(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && !trimmed.Contains(' ') && (trimmed.Contains("://") || trimmed.Contains('.') || trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The address as a URL: Chromium hides "https://", so a bare host gets it back.</summary>
    public static string? NormalizedUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Contains(' ')) return null;
        if (trimmed.Contains("://") || trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return trimmed;
        if (!trimmed.Contains('.') && !trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return null;
        var candidate = "https://" + trimmed;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? candidate : null;
    }
}

/// <summary>Executable icons as PNG through the shell's system image lists.</summary>
internal static class AppIcons
{
    public static byte[]? Png(string executablePath, int size)
    {
        var imageList = size switch
        {
            <= 16 => SHIL_SMALL,
            <= 32 => SHIL_LARGE,
            <= 48 => SHIL_EXTRALARGE,
            _ => SHIL_JUMBO,
        };
        var fileInfo = new SHFILEINFO();
        if (SHGetFileInfo(executablePath, 0, ref fileInfo, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX) == 0)
        {
            return null;
        }
        var interfaceId = IID_IImageList;
        if (SHGetImageList(imageList, ref interfaceId, out var imageListHandle) != 0 || imageListHandle == 0) return null;
        var iconHandle = ImageList_GetIcon(imageListHandle, fileInfo.iIcon, ILD_TRANSPARENT);
        System.Runtime.InteropServices.Marshal.Release(imageListHandle);
        if (iconHandle == 0) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(iconHandle);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }
}
