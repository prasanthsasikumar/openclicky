namespace OpenClicky.Core.Platform;

/// <summary>Windows virtual-key codes the app reasons about. The low-level hook reports the side-specific codes for Shift/Ctrl/Alt.</summary>
public static class VirtualKeys
{
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Return = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Alt = 0x12;
    public const int CapsLock = 0x14;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int A = 0x41;
    public const int V = 0x56;
    public const int LeftWindows = 0x5B;
    public const int RightWindows = 0x5C;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int LeftControl = 0xA2;
    public const int RightControl = 0xA3;
    public const int LeftAlt = 0xA4;
    public const int RightAlt = 0xA5;
    public const int VolumeMute = 0xAD;
    public const int MediaNextTrack = 0xB0;
    public const int MediaPreviousTrack = 0xB1;
    public const int MediaPlayPause = 0xB3;
    /// <summary>
    /// An unassigned virtual key (0xE8). Tapped while Alt or Win is held it tells Windows "another key
    /// was pressed", so releasing Alt does not open the app's menu bar and releasing Win does not
    /// open Start. It has no meaning to any app.
    /// </summary>
    public const int Unassigned = 0xE8;

    public static bool IsControl(int virtualKey) => virtualKey is Control or LeftControl or RightControl;
    public static bool IsAlt(int virtualKey) => virtualKey is Alt or LeftAlt or RightAlt;
    public static bool IsShift(int virtualKey) => virtualKey is Shift or LeftShift or RightShift;
    public static bool IsWindows(int virtualKey) => virtualKey is LeftWindows or RightWindows;
    public static bool IsModifier(int virtualKey) => IsControl(virtualKey) || IsAlt(virtualKey) || IsShift(virtualKey) || IsWindows(virtualKey);

    /// <summary>Which hand's key a side-specific modifier code is (None for every other key).</summary>
    public static KeySide SideOf(int virtualKey) => virtualKey switch
    {
        LeftShift or LeftControl or LeftAlt or LeftWindows => KeySide.Left,
        RightShift or RightControl or RightAlt or RightWindows => KeySide.Right,
        _ => KeySide.None,
    };
}

public enum KeySide
{
    None,
    Left,
    Right,
}

/// <summary>
/// The marker stamped into <c>dwExtraInfo</c> of every keystroke OpenClicky injects itself (live
/// typing, the synthetic Ctrl+V, the menu-suppressing tap). The bytes spell "OCLT", as the Mac's
/// <c>liveTypingEventMarker</c>. The keyboard monitor drops events carrying it.
/// </summary>
public static class LiveTypingMarker
{
    public const long Value = 0x4F43_4C54;
}

/// <summary>One key transition as the low-level hook saw it.</summary>
/// <param name="VirtualKey">Side-specific for Shift/Ctrl/Alt (VK_LCONTROL, VK_RCONTROL…).</param>
/// <param name="IsRepeat">A key-down for a key the monitor already saw go down (auto-repeat).</param>
/// <param name="IsInjected">LLKHF_INJECTED: another program's SendInput (never ours; ours are dropped).</param>
/// <param name="TimestampMilliseconds">The event's own time (GetTickCount clock).</param>
/// <param name="UptimeSeconds">Monotonic seconds when the hook saw it, comparable across events (the recogniser's clock).</param>
/// <param name="WasSwallowed">The monitor kept this event from reaching other apps.</param>
public readonly record struct RawKeyEvent(
    int VirtualKey,
    int ScanCode,
    bool IsDown,
    bool IsRepeat,
    bool IsExtended,
    KeySide Side,
    bool IsInjected,
    bool IsAltDown,
    long ExtraInfo,
    uint TimestampMilliseconds,
    double UptimeSeconds,
    bool WasSwallowed);

/// <summary>
/// System-wide keyboard listener (the Mac's listen-only CGEvent tap): a WH_KEYBOARD_LL hook on its
/// own thread with its own message loop. The hook callback only queues; <see cref="KeyEvent"/> is
/// raised on the monitor's own dispatch thread (never the UI thread, never the hook thread), in order.
/// </summary>
public interface IGlobalKeyboardMonitor : IDisposable
{
    /// <summary>Every key transition except OpenClicky's own (marker) keystrokes.</summary>
    event Action<RawKeyEvent>? KeyEvent;

    bool IsRunning { get; }

    /// <summary>Installs the hook. Idempotent: calling it while running keeps the running hook (as the Mac's start()).</summary>
    void Start();

    void Stop();

    /// <summary>
    /// Keys whose events are kept from other apps (still reported through <see cref="KeyEvent"/>).
    /// The Mac tap is listen-only, so this stays empty except for Caps Lock while it is the
    /// dictation key (otherwise every take would toggle Caps Lock). Escape and Right Ctrl are not swallowed.
    /// </summary>
    void SetSwallowedKeys(IReadOnlyCollection<int> virtualKeys);

    /// <summary>Whether the key is physically down now (the monitor's own key table, then GetAsyncKeyState).</summary>
    bool IsKeyDown(int virtualKey);

    /// <summary>
    /// Taps <see cref="VirtualKeys.Unassigned"/> (marked, so the monitor ignores it). Call while Win
    /// or Alt is held as part of a shortcut (Ctrl+Win talk, Right Alt dictation) so their release
    /// opens neither Start nor the app's menu bar.
    /// </summary>
    void SuppressMenuActivation();
}
