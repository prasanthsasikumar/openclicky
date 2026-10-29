// Port of FocusedFieldReader.isEditable (macos/OpenClicky/OpenClicky/Dictation/FocusedFieldReader.swift)
//
// Which focused element counts as a text box, for a dictation take's paste and for "rephrase this".
// A browser page with nothing clicked reports the page itself as focused (UI Automation's Document,
// the Mac's AXWebArea). Counted as a box, a take pasted into the page and put the old clipboard
// back, so the words were lost instead of waiting for a click into a box, and a rewrite took the
// whole page's text as "the text box" (the Mac, 2026-10-23). A Document is now a text box only with
// evidence: a writable value, text that UI Automation does not report read-only, or an editable
// ancestor. The UIA reads themselves are OpenClicky.Windows/Platform/FocusedElementReader.cs.

namespace OpenClicky.Core.Platform;

/// <summary>What a Document's text says about typing into it (UIA's IsReadOnly text attribute over the whole document).</summary>
public enum DocumentTextAccess
{
    /// <summary>No TextPattern, or the attribute is not supported: the app does not say.</summary>
    NotReported,
    /// <summary>All of it takes typing: an editor's document (Word, Notepad, Outlook's message body).</summary>
    Writable,
    /// <summary>None of it does: a web page.</summary>
    ReadOnly,
    /// <summary>Some does and some does not: a web page with a text box somewhere on it, which is still a page.</summary>
    Mixed,
}

public static class FocusedFieldEditability
{
    /// <summary>
    /// Roles that are a text box only with evidence (a writable value, writable text, or an editable
    /// ancestor). Electron and web apps focus a generic element inside the box; Document is here, not
    /// with Edit, for the page with nothing clicked.
    /// </summary>
    public static readonly IReadOnlySet<string> EditableWithEvidenceRoles =
        new HashSet<string>(StringComparer.Ordinal) { "Document", "Group", "Text", "Custom", "Pane", "Unknown" };

    /// <summary>
    /// Whether a focused element of <paramref name="role"/> (a UIA control type name) takes typed text.
    /// The checks cost UI Automation round trips, so they run only for the roles that need them.
    /// </summary>
    public static bool IsEditable(string? role, bool hasValuePattern, Func<bool> valueIsWritable,
                                  Func<DocumentTextAccess> documentTextAccess, Func<bool> hasEditableAncestor)
    {
        switch (role)
        {
            case "Edit":
                return !hasValuePattern || valueIsWritable();
            case "ComboBox":
                return valueIsWritable();
            case "Document":
                return DocumentTakesText(valueIsWritable(), documentTextAccess()) || hasEditableAncestor();
            case { } other when EditableWithEvidenceRoles.Contains(other):
                return valueIsWritable() || hasEditableAncestor();
            default:
                return false;
        }
    }

    /// <summary>
    /// A Document (focused, or the ancestor of a focused element) that takes typing: a writable value,
    /// or text not reported read-only. An app that does not report it counts as before, so an editor
    /// that never says (some rich documents) keeps taking dictation; a page, which says read-only or
    /// mixed, does not.
    /// </summary>
    public static bool DocumentTakesText(bool valueIsWritable, DocumentTextAccess textAccess) =>
        valueIsWritable || textAccess is DocumentTextAccess.Writable or DocumentTextAccess.NotReported;
}
