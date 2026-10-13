//
//  DictationHotkey.cs
//  OpenClicky (Windows port of Dictation/DictationHotkey.swift)
//
//  The one key that is held (or tapped) to dictate anywhere. A single key, because it has to work
//  on top of whatever app is in front without being a shortcut that app already has. The Mac uses
//  fn by default; fn is invisible to Windows, so here it is Right Ctrl by default, with Right Alt,
//  Right Shift and Caps Lock as alternatives.
//

namespace OpenClicky.Core.Shortcuts;

public enum DictationHotkey
{
    RightControl,
    RightAlt,
    RightShift,
    CapsLock,
}

/// <summary>The physical modifier keys as the low-level keyboard hook tells them apart (left and right
/// sides are different virtual-key codes). <see cref="CapsLock"/> means the key is physically held,
/// not the caps-lock toggle state.</summary>
[Flags]
public enum PhysicalModifiers
{
    None = 0,
    LeftControl = 1 << 0,
    RightControl = 1 << 1,
    LeftWindows = 1 << 2,
    RightWindows = 1 << 3,
    LeftAlt = 1 << 4,
    RightAlt = 1 << 5,
    LeftShift = 1 << 6,
    RightShift = 1 << 7,
    CapsLock = 1 << 8,
}

/// <summary>The modifiers with the sides folded together, as the shortcuts read them (the Mac's
/// <c>NSEvent.ModifierFlags</c> subset: ⌃ → Control, ⌥ → Windows, ⌘ → Alt, fn → CapsLock).</summary>
[Flags]
public enum CompanionModifiers
{
    None = 0,
    Control = 1 << 0,
    Windows = 1 << 1,
    Alt = 1 << 2,
    Shift = 1 << 3,
    CapsLock = 1 << 4,
}

/// <summary>Win32 virtual-key codes the shortcuts care about (as the WH_KEYBOARD_LL hook reports them).</summary>
public static class VirtualKeys
{
    public const int Escape = 0x1B;
    public const int CapsLock = 0x14;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int LeftControl = 0xA2;
    public const int RightControl = 0xA3;
    public const int LeftAlt = 0xA4; // VK_LMENU
    public const int RightAlt = 0xA5; // VK_RMENU
    public const int LeftWindows = 0x5B;
    public const int RightWindows = 0x5C;

    /// <summary>The side-specific modifier a modifier key's virtual-key code stands for, or None.</summary>
    public static PhysicalModifiers ModifierFor(int virtualKey) => virtualKey switch
    {
        LeftControl => PhysicalModifiers.LeftControl,
        RightControl => PhysicalModifiers.RightControl,
        LeftWindows => PhysicalModifiers.LeftWindows,
        RightWindows => PhysicalModifiers.RightWindows,
        LeftAlt => PhysicalModifiers.LeftAlt,
        RightAlt => PhysicalModifiers.RightAlt,
        LeftShift => PhysicalModifiers.LeftShift,
        RightShift => PhysicalModifiers.RightShift,
        CapsLock => PhysicalModifiers.CapsLock,
        _ => PhysicalModifiers.None,
    };
}

public static class DictationHotkeys
{
    public static readonly IReadOnlyList<DictationHotkey> AllCases =
        new[] { DictationHotkey.RightControl, DictationHotkey.RightAlt, DictationHotkey.RightShift, DictationHotkey.CapsLock };

    public const DictationHotkey Default = DictationHotkey.RightControl;

    /// <summary>The value stored in preferences (same idea as the Swift enum's raw value).</summary>
    public static string RawValue(this DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightControl => "rightControl",
        DictationHotkey.RightAlt => "rightAlt",
        DictationHotkey.RightShift => "rightShift",
        DictationHotkey.CapsLock => "capsLock",
        _ => "rightControl",
    };

    /// <summary>Reads a stored raw value. Anything unknown — including the Mac-only values "fn",
    /// "rightOption" and "rightCommand" from a shared or migrated preference — falls back to Right Ctrl,
    /// as the Mac falls back to fn.</summary>
    public static DictationHotkey FromRawValue(string? rawValue)
    {
        foreach (var hotkey in AllCases)
        {
            if (hotkey.RawValue() == rawValue) return hotkey;
        }
        return Default;
    }

    /// <summary>The virtual-key code the hook reports when this physical key moves.</summary>
    public static int VirtualKey(this DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightControl => VirtualKeys.RightControl,
        DictationHotkey.RightAlt => VirtualKeys.RightAlt,
        DictationHotkey.RightShift => VirtualKeys.RightShift,
        DictationHotkey.CapsLock => VirtualKeys.CapsLock,
        _ => VirtualKeys.RightControl,
    };

    /// <summary>The (sides folded) modifier this key sets while it is down.</summary>
    public static CompanionModifiers ModifierFlag(this DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightControl => CompanionModifiers.Control,
        DictationHotkey.RightAlt => CompanionModifiers.Alt,
        DictationHotkey.RightShift => CompanionModifiers.Shift,
        DictationHotkey.CapsLock => CompanionModifiers.CapsLock,
        _ => CompanionModifiers.Control,
    };

    /// <summary>The key's own side bit, and the same modifier on the left (the Mac's device bits).
    /// Caps Lock has no twin, like fn.</summary>
    public static (PhysicalModifiers Own, PhysicalModifiers Twin) SideBits(this DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightControl => (PhysicalModifiers.RightControl, PhysicalModifiers.LeftControl),
        DictationHotkey.RightAlt => (PhysicalModifiers.RightAlt, PhysicalModifiers.LeftAlt),
        DictationHotkey.RightShift => (PhysicalModifiers.RightShift, PhysicalModifiers.LeftShift),
        DictationHotkey.CapsLock => (PhysicalModifiers.CapsLock, PhysicalModifiers.None),
        _ => (PhysicalModifiers.RightControl, PhysicalModifiers.LeftControl),
    };

    /// <summary>What the settings page and the hint pill call it.</summary>
    public static string KeycapLabel(this DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightControl => "right Ctrl",
        DictationHotkey.RightAlt => "right Alt",
        DictationHotkey.RightShift => "right Shift",
        DictationHotkey.CapsLock => "Caps Lock",
        _ => "right Ctrl",
    };

    public static string DisplayName(this DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightControl => "right control",
        DictationHotkey.RightAlt => "right alt",
        DictationHotkey.RightShift => "right shift",
        DictationHotkey.CapsLock => "caps lock",
        _ => "right control",
    };

    /// <summary>Whether a modifier event with this virtual key and this modifier state means the key
    /// went down; null when it was another key.</summary>
    public static bool? IsDown(this DictationHotkey hotkey, int virtualKey, PhysicalModifiers modifiers)
    {
        if (virtualKey != hotkey.VirtualKey()) return null;
        return (modifiers & hotkey.SideBits().Own) != 0;
    }
}
