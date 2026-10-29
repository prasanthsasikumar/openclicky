// Port of macos/OpenClicky/OpenClickyTests/FocusedFieldEditabilityTests.swift
//
// Which focused element counts as a text box for a take and for "rephrase this". A browser page with
// nothing clicked reports the page itself (UIA Document, the Mac's AXWebArea) as focused; counted as
// a box, a take pasted into the page and put the old clipboard back, so the words were lost (the Mac,
// 2026-10-23).

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Tests.Platform;

public class FocusedFieldEditabilityTests
{
    private static bool IsEditable(string? role, bool hasValuePattern = false, bool valueIsWritable = false,
                                   DocumentTextAccess textAccess = DocumentTextAccess.NotReported, bool hasEditableAncestor = false) =>
        FocusedFieldEditability.IsEditable(role, hasValuePattern, () => valueIsWritable, () => textAccess, () => hasEditableAncestor);

    [Fact]
    public void APageWithNothingClickedIsNotATextBox()
    {
        // Chrome's page: a read-only value (the address), text read-only, or mixed when a box is somewhere on it.
        Assert.False(IsEditable("Document", hasValuePattern: true, textAccess: DocumentTextAccess.ReadOnly));
        Assert.False(IsEditable("Document", hasValuePattern: true, textAccess: DocumentTextAccess.Mixed));
    }

    [Fact]
    public void AnEditableDocumentStillIs()
    {
        // Word, Notepad, Outlook's message body: text that takes typing, or a writable value.
        Assert.True(IsEditable("Document", textAccess: DocumentTextAccess.Writable));
        Assert.True(IsEditable("Document", hasValuePattern: true, valueIsWritable: true, textAccess: DocumentTextAccess.ReadOnly));
        // A page inside an editable region.
        Assert.True(IsEditable("Document", textAccess: DocumentTextAccess.Mixed, hasEditableAncestor: true));
        // An app that does not say counts as before.
        Assert.True(IsEditable("Document", textAccess: DocumentTextAccess.NotReported));
    }

    [Fact]
    public void TextRolesAreBoxesAndOthersNeedEvidence()
    {
        Assert.True(IsEditable("Edit"));
        Assert.True(IsEditable("Edit", hasValuePattern: true, valueIsWritable: true));
        Assert.False(IsEditable("Edit", hasValuePattern: true, valueIsWritable: false));
        Assert.True(IsEditable("ComboBox", hasValuePattern: true, valueIsWritable: true));
        Assert.False(IsEditable("ComboBox", hasValuePattern: true));
        foreach (var role in new[] { "Group", "Text", "Custom", "Pane", "Unknown" })
        {
            Assert.False(IsEditable(role, textAccess: DocumentTextAccess.Writable));
            Assert.True(IsEditable(role, hasEditableAncestor: true));
            Assert.True(IsEditable(role, hasValuePattern: true, valueIsWritable: true));
        }
        Assert.False(IsEditable("Button", hasValuePattern: true, valueIsWritable: true, hasEditableAncestor: true));
        Assert.False(IsEditable(null, hasValuePattern: true, valueIsWritable: true, hasEditableAncestor: true));
    }

    [Fact]
    public void ADocumentAncestorCountsOnlyWhenItTakesText()
    {
        // A focused element inside a read-only page is not in a text box.
        Assert.False(FocusedFieldEditability.DocumentTakesText(valueIsWritable: false, DocumentTextAccess.ReadOnly));
        Assert.False(FocusedFieldEditability.DocumentTakesText(valueIsWritable: false, DocumentTextAccess.Mixed));
        Assert.True(FocusedFieldEditability.DocumentTakesText(valueIsWritable: false, DocumentTextAccess.Writable));
        Assert.True(FocusedFieldEditability.DocumentTakesText(valueIsWritable: true, DocumentTextAccess.ReadOnly));
    }
}
