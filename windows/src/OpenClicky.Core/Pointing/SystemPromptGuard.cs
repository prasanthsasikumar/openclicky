//
//  SystemPromptGuard.cs
//  OpenClicky (Windows port of SystemPromptGuard.swift)
//
//  Keeps the pointer off Windows's own permission and password prompts. Asked how to save a file
//  while a macOS "would like to access files in your Documents folder" prompt was on screen, the
//  model pointed at Allow six times out of six, even when the prompt told it not to. Whether to
//  grant a permission is the person's choice, never a step Clicky walks them through, so a point
//  that lands on one of these prompts is not shown.
//
//  On Windows the UAC prompt runs on the secure desktop, where nothing can be pointed at anyway;
//  when UAC is set not to dim the desktop it is drawn by consent.exe on the normal one. The prompts
//  are found by the process that draws them (and the credential dialog by its window class too),
//  from EnumWindows + DWM bounds on the OS side.
//

namespace OpenClicky.Core.Pointing;

/// <summary>One on-screen window: its owner's process image name ("consent.exe"), bounds (global coordinates, y down,
/// the same space as the point being checked) and, when known, its window class.</summary>
public sealed record PromptWindow(string OwnerProcessName, ScreenRect Bounds, string? ClassName = null);

public static class SystemPromptGuard
{
    /// <summary>Processes that draw Windows's permission, password and security prompts.</summary>
    public static readonly IReadOnlySet<string> PromptOwnerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "consent.exe",            // UAC "do you want to allow this app to make changes" (when not on the secure desktop)
        "CredentialUIBroker.exe", // Windows Security credential dialog (passwords, PIN, Windows Hello)
        "LockApp.exe",            // the lock screen
    };

    /// <summary>Window classes of the Windows Security credential dialog, whichever process hosts it.</summary>
    public static readonly IReadOnlySet<string> PromptWindowClassNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Credential Dialog Xaml Host",
    };

    /// <summary>How far outside a prompt a point still counts as aimed at it: the models' pixel guesses are 30 to 100 px off.</summary>
    public const double MarginInPoints = 40;

    public static bool IsPromptWindow(PromptWindow window) =>
        PromptOwnerNames.Contains(window.OwnerProcessName)
        || (window.ClassName is { } className && PromptWindowClassNames.Contains(className));

    /// <summary>Whether <paramref name="point"/> is on or beside a prompt.</summary>
    public static bool IsOnSystemPrompt(ScreenPoint point, IEnumerable<PromptWindow> windows) =>
        windows.Any(window => IsPromptWindow(window) && window.Bounds.Inset(-MarginInPoints, -MarginInPoints).Contains(point));

    /// <summary>The same, against the windows on screen now (the Mac's overload that read CGWindowList). No y-flip:
    /// Windows global space is y-down like the windows' bounds.</summary>
    public static bool IsOnSystemPrompt(ScreenPoint point, Func<IEnumerable<PromptWindow>> onScreenWindows) =>
        IsOnSystemPrompt(point, onScreenWindows());
}
