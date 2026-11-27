// Ports PointerShowsChoice (Dictation/UI/SettingsClickyPage.swift): when the pointer shows, as one
// picker on Settings → Clicky. Only while you talk (the old switch off), or, with the pointer on,
// always, while the mouse moves, or after a shake. Two settings underneath: IsClickyCursorEnabled and
// PointerPresence.

using OpenClicky.Core.Localization;

namespace OpenClicky.Core.Pointing;

public enum PointerShowsChoice
{
    WhileTalking,
    Always,
    WhileMoving,
    OnShake,
}

public static class PointerShowsChoices
{
    public static IReadOnlyList<PointerShowsChoice> AllCases { get; } = Enum.GetValues<PointerShowsChoice>();

    /// <summary>The choice the two settings add up to.</summary>
    public static PointerShowsChoice From(bool isPointerShown, PointerPresence presence)
    {
        if (!isPointerShown) return PointerShowsChoice.WhileTalking;
        return presence switch
        {
            PointerPresence.Always => PointerShowsChoice.Always,
            PointerPresence.WhileMoving => PointerShowsChoice.WhileMoving,
            _ => PointerShowsChoice.OnShake,
        };
    }

    public static string Title(this PointerShowsChoice choice) => choice switch
    {
        PointerShowsChoice.WhileTalking => L.T("While I talk"),
        PointerShowsChoice.Always => L.T("Always"),
        PointerShowsChoice.WhileMoving => L.T("While the mouse moves"),
        _ => L.T("After a shake"),
    };

    /// <summary>The pointer's line under the picker.</summary>
    public static string Detail(this PointerShowsChoice choice) => choice switch
    {
        PointerShowsChoice.WhileTalking => L.T("Appears when you ask and leaves once Clicky is done."),
        PointerShowsChoice.Always => L.T("Follows the mouse everywhere."),
        PointerShowsChoice.WhileMoving => L.T("Fades a few seconds after the mouse rests."),
        _ => L.T("Hidden until you shake, or until Clicky needs it."),
    };

    /// <summary>
    /// The presence this choice sets; null for "while I talk", which only turns the pointer off and
    /// leaves the presence as it was for when it comes back on.
    /// </summary>
    public static PointerPresence? Presence(this PointerShowsChoice choice) => choice switch
    {
        PointerShowsChoice.Always => PointerPresence.Always,
        PointerShowsChoice.WhileMoving => PointerPresence.WhileMoving,
        PointerShowsChoice.OnShake => PointerPresence.OnShake,
        _ => null,
    };
}
