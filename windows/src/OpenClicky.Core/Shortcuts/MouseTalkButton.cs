// Asking with a mouse button (2026-11-01): hold a mouse button to talk to Clicky, release to send, the same as
// holding the talk keys. For someone away from the keyboard with a wireless mouse. The back and forward side
// buttons are the usual choice; most mice with more buttons can map any of them to those in their own software.
// The button is swallowed while chosen, so holding "back" to talk doesn't also go back a page.

namespace OpenClicky.Core.Shortcuts;

public enum MouseTalkButton
{
    Off,
    /// <summary>The back side button (XBUTTON1).</summary>
    Back,
    /// <summary>The forward side button (XBUTTON2).</summary>
    Forward,
    /// <summary>The middle button (the wheel's click); it then no longer opens links in new tabs or auto-scrolls.</summary>
    Middle,
}

/// <summary>One mouse button going down or up, read from a low-level mouse message.</summary>
public readonly record struct MouseButtonChange(MouseTalkButton Button, bool IsDown);

public static class MouseTalkButtons
{
    /// <summary>The preference that holds the choice ("off", "back", "forward", "middle").</summary>
    public const string PreferenceKey = "keyboard.mouseTalkButton";

    public static IReadOnlyList<MouseTalkButton> AllCases { get; } = Enum.GetValues<MouseTalkButton>();

    public static string RawValue(this MouseTalkButton button) => button switch
    {
        MouseTalkButton.Back => "back",
        MouseTalkButton.Forward => "forward",
        MouseTalkButton.Middle => "middle",
        _ => "off",
    };

    public static MouseTalkButton FromRawValue(string? raw) =>
        AllCases.FirstOrDefault(button => string.Equals(button.RawValue(), raw?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Title(this MouseTalkButton button) => button switch
    {
        MouseTalkButton.Back => "Back side button",
        MouseTalkButton.Forward => "Forward side button",
        MouseTalkButton.Middle => "Middle button (wheel click)",
        _ => "Off",
    };

    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    private const int XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;

    /// <summary>
    /// The button change a low-level mouse message is: <paramref name="message"/> is WM_*, and <paramref name="mouseData"/> is
    /// MSLLHOOKSTRUCT.mouseData (its high word says which side button). Null for anything that isn't a middle or side
    /// button going down or up.
    /// </summary>
    public static MouseButtonChange? Read(int message, uint mouseData)
    {
        var sideButton = (int)(mouseData >> 16);
        return message switch
        {
            WM_MBUTTONDOWN => new MouseButtonChange(MouseTalkButton.Middle, true),
            WM_MBUTTONUP => new MouseButtonChange(MouseTalkButton.Middle, false),
            WM_XBUTTONDOWN or WM_XBUTTONUP when sideButton == XBUTTON1 => new MouseButtonChange(MouseTalkButton.Back, message == WM_XBUTTONDOWN),
            WM_XBUTTONDOWN or WM_XBUTTONUP when sideButton == XBUTTON2 => new MouseButtonChange(MouseTalkButton.Forward, message == WM_XBUTTONDOWN),
            _ => null,
        };
    }
}
