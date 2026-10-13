// Port of macos/OpenClicky/OpenClicky/WriteInPlace.swift (the decision logic; the OS side is IWriteInPlaceHost)
//
// "Can you rephrase this?" about an email being drafted: Clicky rewrites the text and puts the new
// version where the old one was. The text is what the person highlighted, or else everything in
// the text box the cursor is in. It is read through UI Automation (the Mac read Accessibility) when
// the question is asked, so the model gets the exact words, and the ask lane offers the
// `write_in_place` tool only then.
//
// The tool's text replaces the old text straight away (Ctrl+Z undoes it), but only if the same box
// still has focus with the same text in it: a reply that arrives after they clicked elsewhere is
// spoken, never pasted somewhere new. The original stays on the clipboard afterwards. Password
// fields and OpenClicky's own windows are never targets.

using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenClicky.Core.Answers;

/// <summary>
/// The focused element as the OS side reads it (Swift `FocusedFieldSnapshot`). Apps are named by
/// process image name ("chrome.exe") where the Mac used bundle ids.
/// Wired (OpenClicky.Windows/Companion/CompanionAdapters.cs WriteInPlaceHost): map from the Platform layer's focused-field snapshot (UIA: ValuePattern /
/// TextPattern selection, IsPassword, ControlType) once it exists.
/// </summary>
public sealed record WritingFieldSnapshot(
    string? AppProcessName,
    string? AppName,
    string? Role,
    bool IsEditable,
    bool IsSecure,
    string? Value,
    string? SelectedText);

public enum WritingScope
{
    /// <summary>What the person highlighted: the paste replaces just that.</summary>
    Selection,
    /// <summary>Everything in the focused text box: it is selected first, then replaced.</summary>
    WholeField,
}

/// <summary>The text a rewrite would replace, captured when the question was asked.</summary>
public sealed record WritingTarget(WritingScope Scope, string Text, string? AppProcessName, string? AppName)
{
    /// <summary>The most text sent along for a rewrite: a long email, not a whole document.</summary>
    public const int MaximumCharacters = 6_000;

    /// <summary>OpenClicky's own process image name (the Mac compared Bundle.main.bundleIdentifier).</summary>
    public const string OwnProcessName = "OpenClicky.exe";

    /// <summary>The target in a snapshot of the focused element, or null when there is nothing to rewrite.</summary>
    public static WritingTarget? Target(WritingFieldSnapshot snapshot, string? ownProcessName = OwnProcessName)
    {
        if (!snapshot.IsEditable || snapshot.IsSecure || SameApp(snapshot.AppProcessName, ownProcessName)) return null;
        if (snapshot.SelectedText is string selectedText && !string.IsNullOrWhiteSpace(selectedText))
        {
            if (CharacterCount(selectedText) > MaximumCharacters) return null;
            return new WritingTarget(WritingScope.Selection, selectedText, snapshot.AppProcessName, snapshot.AppName);
        }
        if (snapshot.Value is not string value || string.IsNullOrWhiteSpace(value) || CharacterCount(value) > MaximumCharacters) return null;
        return new WritingTarget(WritingScope.WholeField, value, snapshot.AppProcessName, snapshot.AppName);
    }

    /// <summary>
    /// Whether `snapshot` (read just before pasting) is still this target: the same app, an editable
    /// box, and the same words selected or in the box. Anything else and the rewrite is not pasted.
    /// </summary>
    public bool StillMatches(WritingFieldSnapshot snapshot)
    {
        if (!SameApp(snapshot.AppProcessName, AppProcessName) || !snapshot.IsEditable || snapshot.IsSecure) return false;
        return Scope switch
        {
            WritingScope.Selection => snapshot.SelectedText == Text,
            _ => snapshot.Value == Text,
        };
    }

    /// <summary>Added to the question, so the model has the exact words.</summary>
    public string ContextLine
    {
        get
        {
            var place = AppName is null ? "" : $" in {AppName}";
            return Scope switch
            {
                WritingScope.Selection =>
                    $"The user has highlighted this text{place}: «{Text}». If they ask to rephrase, rewrite, shorten, fix, polish or translate it, call write_in_place with the complete new text.",
                _ =>
                    $"The text box the user's cursor is in{place} holds this text: «{Text}». If they ask to rephrase, rewrite, shorten, fix, polish or translate it (\"this email\", \"this\", \"what i wrote\"), call write_in_place with the complete new text.",
            };
        }
    }

    /// <summary>
    /// Reads the target from the app in front. Right after the typed composer closes, OpenClicky is
    /// still in front for a moment while the previous app comes back, so this waits for it.
    /// </summary>
    public static async Task<WritingTarget?> CaptureFromFrontAppAsync(IWriteInPlaceHost host, Action<string>? log = null,
                                                                       Func<TimeSpan, Task>? delay = null, string? ownProcessName = OwnProcessName)
    {
        delay ??= time => Task.Delay(time);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!host.IsOwnProcessInFront()) break;
            await delay(TimeSpan.FromMilliseconds(100));
        }
        var snapshot = host.SnapshotFocusedField();
        // A browser builds a page's accessibility tree on the first request, which can come back
        // with nothing focused: asked again a moment later, it answers with the text box.
        for (var attempt = 0; attempt < 2 && snapshot.Role is null && snapshot.AppProcessName is not null; attempt++)
        {
            await delay(TimeSpan.FromMilliseconds(250));
            snapshot = host.SnapshotFocusedField();
        }
        var capturedTarget = Target(snapshot, ownProcessName);
        // What the text box looked like, never its words: enough to see why nothing was offered.
        var targetDescription = capturedTarget is null
            ? "no target"
            : $"{(capturedTarget.Scope == WritingScope.Selection ? "highlight" : "whole box")} of {CharacterCount(capturedTarget.Text)} chars";
        log?.Invoke($"write in place: {targetDescription} in {snapshot.AppName ?? "?"}, role {snapshot.Role ?? "none"}, editable {(snapshot.IsEditable ? "true" : "false")}, value {(snapshot.Value is null ? -1 : CharacterCount(snapshot.Value))} chars");
        return capturedTarget;
    }

    /// <summary>Process names compared without case and with or without ".exe".</summary>
    internal static bool SameApp(string? first, string? second)
    {
        if (first is null || second is null) return first is null && second is null;
        return string.Equals(NormalizedProcessName(first), NormalizedProcessName(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizedProcessName(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";

    /// <summary>Characters as Swift counts them (grapheme clusters).</summary>
    internal static int CharacterCount(string text) => text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;
}

/// <summary>How a paste was judged after the field was read back (Swift `PasteLanding`).</summary>
public enum WriteInPlaceLanding
{
    /// <summary>The new text was read back in the field.</summary>
    Verified,
    /// <summary>Pasted into an editable field that could not be read back.</summary>
    Posted,
    /// <summary>Nothing editable had focus after the paste.</summary>
    NoTarget,
    /// <summary>The paste could not be posted at all; the text is on the clipboard.</summary>
    LeftOnPasteboard,
}

/// <summary>
/// The OS side of writing in place (UI Automation + clipboard + SendInput on Windows).
/// </summary>
public interface IWriteInPlaceHost
{
    /// <summary>
    /// Whether focused fields can be read at all (the Mac's AXIsProcessTrusted). UI Automation needs
    /// no permission on Windows, so this is normally true; false when UIA is unavailable.
    /// </summary>
    bool CanReadFocusedFields { get; }
    /// <summary>OpenClicky's own window is the foreground window.</summary>
    bool IsOwnProcessInFront();
    WritingFieldSnapshot SnapshotFocusedField();
    /// <summary>Ctrl+A into the focused field (selects only that box).</summary>
    void SelectAllInFocusedField();
    /// <summary>Puts `text` on the clipboard and posts Ctrl+V.</summary>
    void Insert(string text);
    /// <summary>Reads the field back and judges whether `text` landed.</summary>
    Task<WriteInPlaceLanding> VerifyLandingAsync(string text, WritingFieldSnapshot before);
    void CopyToClipboard(string text);
}

public enum WriteInPlaceOutcome
{
    Replaced,
    /// <summary>Pasted, but the box could not be read back to confirm it.</summary>
    ProbablyReplaced,
    /// <summary>Focus moved, or the text changed, since the question: nothing was pasted.</summary>
    TargetGone,
    /// <summary>Nothing editable had focus after the paste: the new version is left on the clipboard.</summary>
    LeftOnClipboard,
    /// <summary>The field could not be reached (the Mac's missing Accessibility permission).</summary>
    NoAccessibility,
    EmptyText,
}

public static class WriteInPlaceOutcomeExtensions
{
    public static bool DidReplace(this WriteInPlaceOutcome outcome) =>
        outcome is WriteInPlaceOutcome.Replaced or WriteInPlaceOutcome.ProbablyReplaced;

    /// <summary>The Mac sentences, with control z / control v for command z / command v.</summary>
    public static string SpokenSentence(this WriteInPlaceOutcome outcome) => outcome switch
    {
        WriteInPlaceOutcome.Replaced or WriteInPlaceOutcome.ProbablyReplaced =>
            "it's in place. press control z if you want yours back.",
        WriteInPlaceOutcome.TargetGone =>
            "you moved on before i finished, so i didn't change anything. click back into the text and ask again.",
        WriteInPlaceOutcome.LeftOnClipboard =>
            "i couldn't put it in, so the new version is on your clipboard. press control v where you want it.",
        // Windows has no Accessibility permission to point at; UI Automation simply failed.
        WriteInPlaceOutcome.NoAccessibility =>
            "i couldn't reach the text in that app to write it in. click into the text box and ask again.",
        _ => "i didn't get a new version to put in. please ask again.",
    };
}

public static class WriteInPlace
{
    public const string ToolName = "write_in_place";

    /// <summary>The ask lane's tool, in Anthropic's shape (plans get it through PlanToolProtocol's list).</summary>
    public static JsonObject AnthropicToolDefinition() => new()
    {
        ["name"] = ToolName,
        ["description"] = "Replace the text the user is working on (the highlighted text, or the text box their cursor is in, quoted in the question) with your rewritten version. Use it when they ask you to rephrase, rewrite, shorten, fix, polish or translate that text. It replaces the old text straight away.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The complete new text, exactly as it should appear. Keep their meaning, names and greeting style, and the same language unless they asked for another. Plain text with line breaks, no quotes around it, no em dashes.",
                },
            },
            ["required"] = new JsonArray("text"),
        },
    };

    /// <summary>Added to the ask prompt when the tool is offered.</summary>
    public const string PromptInstruction =
        "writing in place: when the user asks you to rephrase, rewrite, shorten, fix or translate the text quoted as highlighted or in their text box, call write_in_place with the complete new text, and say only a few words about what you changed (for example \"made it warmer and shorter.\"). openclicky puts it in their email and tells them how to undo it. never read the new text out.";

    /// <summary>
    /// Replaces the target with `newText`: selects the box's text when the whole box is the target,
    /// pastes, checks it landed, and leaves the original on the clipboard.
    /// </summary>
    public static async Task<WriteInPlaceOutcome> ReplaceAsync(WritingTarget target, string newText, IWriteInPlaceHost host,
                                                               Action<string>? log = null, Func<TimeSpan, Task>? delay = null)
    {
        delay ??= time => Task.Delay(time);
        if (string.IsNullOrWhiteSpace(newText)) return WriteInPlaceOutcome.EmptyText;
        var trimmedNewText = KeepingSurroundingSpace(target.Text, newText);
        if (!host.CanReadFocusedFields) return WriteInPlaceOutcome.NoAccessibility;
        var before = host.SnapshotFocusedField();
        if (!target.StillMatches(before))
        {
            log?.Invoke("write in place: the target moved, nothing pasted");
            return WriteInPlaceOutcome.TargetGone;
        }
        if (target.Scope == WritingScope.WholeField)
        {
            // Ctrl+A in a focused text box selects only that box (in Gmail, the message body).
            host.SelectAllInFocusedField();
            await delay(TimeSpan.FromMilliseconds(120));
        }
        host.Insert(trimmedNewText);
        var landing = await host.VerifyLandingAsync(trimmedNewText, before);
        if (landing == WriteInPlaceLanding.NoTarget)
        {
            host.CopyToClipboard(trimmedNewText);
            log?.Invoke("write in place: nothing editable after the paste, the new text is on the clipboard");
            return WriteInPlaceOutcome.LeftOnClipboard;
        }
        // The person's own words stay one Ctrl+V away, after the paste has read the clipboard.
        await delay(TimeSpan.FromMilliseconds(400));
        host.CopyToClipboard(target.Text);
        log?.Invoke($"write in place: {(target.Scope == WritingScope.Selection ? "selection" : "whole box")} in {target.AppName ?? "an app"}, {LandingLogName(landing)}");
        return landing switch
        {
            WriteInPlaceLanding.Verified => WriteInPlaceOutcome.Replaced,
            WriteInPlaceLanding.LeftOnPasteboard => WriteInPlaceOutcome.NoAccessibility,
            _ => WriteInPlaceOutcome.ProbablyReplaced,
        };
    }

    private static string LandingLogName(WriteInPlaceLanding landing) => landing switch
    {
        WriteInPlaceLanding.Verified => "verified",
        WriteInPlaceLanding.Posted => "posted",
        WriteInPlaceLanding.NoTarget => "noTarget",
        _ => "leftOnPasteboard",
    };

    /// <summary>
    /// The new text with the spaces and line breaks the old text had at its ends: a triple-clicked
    /// paragraph comes with its line break, and a rewrite without it ran into the next line.
    /// </summary>
    public static string KeepingSurroundingSpace(string originalText, string newText)
    {
        var leadingLength = 0;
        while (leadingLength < originalText.Length && char.IsWhiteSpace(originalText[leadingLength])) leadingLength++;
        var trailingStart = originalText.Length;
        while (trailingStart > leadingLength && char.IsWhiteSpace(originalText[trailingStart - 1])) trailingStart--;
        // An all-whitespace original: Swift takes the whole of it as both leading and trailing.
        var trailing = trailingStart == leadingLength && leadingLength == originalText.Length ? originalText : originalText[trailingStart..];
        return originalText[..leadingLength] + newText.Trim() + trailing;
    }
}
