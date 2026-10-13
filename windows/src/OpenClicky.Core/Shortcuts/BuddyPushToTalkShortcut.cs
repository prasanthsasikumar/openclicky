//
//  BuddyPushToTalkShortcut.cs
//  OpenClicky (Windows port of the pure part of BuddyDictationManager.swift)
//

namespace OpenClicky.Core.Shortcuts;

public static class BuddyPushToTalkShortcut
{
    /// <summary>Talk (Ctrl + Win) going down or coming up, as the global push-to-talk shortcut monitor publishes it.</summary>
    public enum ShortcutTransition
    {
        None,
        Pressed,
        Released,
    }
}
