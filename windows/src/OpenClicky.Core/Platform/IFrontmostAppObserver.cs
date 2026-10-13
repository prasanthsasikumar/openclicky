namespace OpenClicky.Core.Platform;

/// <summary>The app in front (NSWorkspace.frontmostApplication + its window + browser tab URL).</summary>
public sealed record FrontAppInfo
{
    public nint WindowHandle { get; init; }
    public int ProcessId { get; init; }
    /// <summary>Image name, e.g. "chrome.exe" (the Windows stand-in for a bundle id; app skills match it).</summary>
    public string? ProcessName { get; init; }
    public string? ExecutablePath { get; init; }
    /// <summary>FileVersionInfo.FileDescription ("Google Chrome"), else ProductName, else the image name without ".exe".</summary>
    public string? DisplayName { get; init; }
    public string? WindowTitle { get; init; }
    /// <summary>The front tab's URL (browsers only, read from the address bar through UIA), else null.</summary>
    public string? Url { get; init; }
    public bool IsBrowser { get; init; }
}

/// <summary>The browsers whose tab strip and address bar the platform layer reads.</summary>
public static class BrowserProcesses
{
    public static readonly IReadOnlySet<string> ImageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe", "arc.exe", "chromium.exe",
    };

    public static bool IsBrowser(string? imageName) => imageName is not null && ImageNames.Contains(imageName);
}

public interface IFrontmostAppObserver : IDisposable
{
    /// <summary>
    /// The app in front now, never OpenClicky itself (when our own window is in front the next window
    /// down the z-order is described, as the Mac looks for the next active app). The URL lookup is
    /// bounded to 0.25 s. Null when nothing is in front.
    /// </summary>
    Task<FrontAppInfo?> CurrentAsync(bool includeUrl = true, CancellationToken cancellationToken = default);

    /// <summary>The same without the URL and without UI Automation: cheap and synchronous.</summary>
    FrontAppInfo? CurrentWithoutUrl();

    /// <summary>The last foreground app that was not OpenClicky (kept by the foreground hook).</summary>
    FrontAppInfo? LastExternalForeground { get; }

    /// <summary>Raised (on the observer's own thread) when another top-level window comes to the foreground; no URL.</summary>
    event Action<FrontAppInfo>? ForegroundChanged;

    /// <summary>Starts the EVENT_SYSTEM_FOREGROUND hook.</summary>
    void Start();

    void Stop();

    /// <summary>The executable's icon as PNG (16, 32, 48 or 256 px), for AppIconView. Null when there is none.</summary>
    byte[]? AppIconPng(string executablePath, int size = 32);
}
