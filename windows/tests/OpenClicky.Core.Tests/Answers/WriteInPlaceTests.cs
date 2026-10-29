// Port of macos/OpenClicky/OpenClickyTests/WriteInPlaceTests.swift (+ the Farewell test from
// HUDPlacementTests and a test of the ported replace flow over a fake host).
// theQuestionCarriesTheExactWords (CompanionManager.askUserPrompt) is in AskLaneTests.cs (AskLanePrompts.AskUserPrompt).

using OpenClicky.Core.Answers;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Answers;

public class WriteInPlaceTests
{
    private static WritingFieldSnapshot Field(string? value = null, string? selected = null, bool editable = true, bool secure = false,
                                              string app = "chrome.exe") =>
        new(app, "Google Chrome", "Edit", editable, secure, value, selected);

    [Fact]
    public void AHighlightIsTheTarget()
    {
        var target = WritingTarget.Target(Field(value: "Hi Ravi, see you at 5. Amma", selected: "see you at 5."), ownProcessName: "OpenClicky.exe");
        Assert.Equal(WritingScope.Selection, target?.Scope);
        Assert.Equal("see you at 5.", target?.Text);
    }

    [Fact]
    public void WithoutAHighlightTheWholeBoxIsTheTarget()
    {
        var target = WritingTarget.Target(Field(value: "Hi Ravi,\nsee you at 5.\nAmma"), ownProcessName: "OpenClicky.exe");
        Assert.Equal(WritingScope.WholeField, target?.Scope);
        Assert.Equal("Hi Ravi,\nsee you at 5.\nAmma", target?.Text);
    }

    [Fact]
    public void NothingToRewriteMeansNoTarget()
    {
        Assert.Null(WritingTarget.Target(Field(value: "   "), ownProcessName: "x"));
        Assert.Null(WritingTarget.Target(Field(value: "secret", secure: true), ownProcessName: "x"));
        Assert.Null(WritingTarget.Target(Field(value: "a page", editable: false), ownProcessName: "x"));
        Assert.Null(WritingTarget.Target(Field(value: "my own window", app: "OpenClicky.exe"), ownProcessName: "OpenClicky.exe"));
        Assert.Null(WritingTarget.Target(Field(value: "my own window", app: "openclicky"), ownProcessName: "OpenClicky.exe"));
        Assert.Null(WritingTarget.Target(Field(value: new string('a', WritingTarget.MaximumCharacters + 1)), ownProcessName: "x"));
    }

    [Fact]
    public void ItIsOnlyPastedIntoTheSameTextStill()
    {
        var target = new WritingTarget(WritingScope.WholeField, "Hi Ravi", "chrome.exe", "Google Chrome");
        Assert.True(target.StillMatches(Field(value: "Hi Ravi")));
        Assert.False(target.StillMatches(Field(value: "Hi Ravi, also")));
        Assert.False(target.StillMatches(Field(value: "Hi Ravi", app: "outlook.exe")));
        Assert.False(target.StillMatches(Field(value: "Hi Ravi", editable: false)));
        var highlighted = new WritingTarget(WritingScope.Selection, "at 5", "chrome.exe", null);
        Assert.True(highlighted.StillMatches(Field(value: "see you at 5", selected: "at 5")));
        Assert.False(highlighted.StillMatches(Field(value: "see you at 5", selected: null)));
    }

    [Fact]
    public void TheContextLineCarriesTheExactWordsAndTheApp()
    {
        var target = new WritingTarget(WritingScope.WholeField, "Hi Ravi,\nsee you at 5.", "chrome.exe", "Google Chrome");
        Assert.Contains("Hi Ravi,\nsee you at 5.", target.ContextLine);
        Assert.Contains("in Google Chrome", target.ContextLine);
        Assert.Contains(WriteInPlace.ToolName, target.ContextLine);
    }

    [Fact]
    public void TheToolAsksForTheWholeNewText()
    {
        var definition = WriteInPlace.AnthropicToolDefinition();
        Assert.Equal("write_in_place", (string?)definition["name"]);
        var required = definition["input_schema"]!["required"]!.AsArray().Select(node => (string?)node).ToArray();
        Assert.Equal(new[] { "text" }, required);
    }

    [Fact]
    public void TheRewriteKeepsTheSpacingAroundTheOldText()
    {
        Assert.Equal("Sorry for the delay.\n", WriteInPlace.KeepingSurroundingSpace("sorry it took long.\n", "Sorry for the delay."));
        Assert.Equal(" at five", WriteInPlace.KeepingSurroundingSpace(" at 5", "\nat five\n"));
        Assert.Equal("Hi Ravi,", WriteInPlace.KeepingSurroundingSpace("hi ravi", "Hi Ravi,"));
    }

    [Fact]
    public void EachOutcomeSaysWhatHappened()
    {
        Assert.Contains("control z", WriteInPlaceOutcome.Replaced.SpokenSentence());
        Assert.Contains("didn't change anything", WriteInPlaceOutcome.TargetGone.SpokenSentence());
        Assert.False(WriteInPlaceOutcome.TargetGone.DidReplace());
        foreach (var outcome in Enum.GetValues<WriteInPlaceOutcome>())
        {
            Assert.DoesNotContain("—", outcome.SpokenSentence());
        }
    }

    private sealed class FakeHost : IWriteInPlaceHost
    {
        public WritingFieldSnapshot Snapshot { get; set; } = Field(value: "Hi Ravi");
        public WriteInPlaceLanding Landing { get; set; } = WriteInPlaceLanding.Verified;
        public List<string> Steps { get; } = new();
        public bool CanReadFocusedFields => true;
        public bool IsOwnProcessInFront() => false;
        public WritingFieldSnapshot SnapshotFocusedField() => Snapshot;
        public void SelectAllInFocusedField() => Steps.Add("select all");
        public void Insert(string text) => Steps.Add("insert " + text);
        public Task<WriteInPlaceLanding> VerifyLandingAsync(string text, WritingFieldSnapshot before) => Task.FromResult(Landing);
        public void CopyToClipboard(string text) => Steps.Add("copy " + text);
        public string? CopiedSelection { get; set; }
        public Task<string?> CopySelectionAsync() { Steps.Add("copy selection"); return Task.FromResult(CopiedSelection); }
    }

    // Gmail in Chrome on a tester's Mac gave Accessibility nothing at all (2026-10-18); a browser
    // that answers UI Automation late does the same here. The words they highlighted are then read
    // by copying them, and a highlight read that way is replaced on the same footing.
    private static WritingFieldSnapshot NothingReadable(string app = "chrome.exe") => new(app, "Google Chrome", null, false, false, null, null);

    [Fact]
    public void ACopiedHighlightIsTheTargetWhenUiAutomationReadsNothing()
    {
        var target = WritingTarget.Target(NothingReadable(), copiedSelection: "see you at 5.", ownProcessName: "OpenClicky.exe");
        Assert.Equal(WritingScope.Selection, target?.Scope);
        Assert.Equal(WritingSource.Clipboard, target?.Source);
        Assert.Equal("see you at 5.", target?.Text);
        Assert.Equal("chrome.exe", target?.AppProcessName);
    }

    [Fact]
    public void WhatUiAutomationReadsWinsOverTheCopiedHighlight()
    {
        var target = WritingTarget.Target(Field(value: "Hi Ravi, see you at 5.", selected: "see you at 5."), copiedSelection: "an older copy", ownProcessName: "x");
        Assert.Equal(WritingSource.Accessibility, target?.Source);
        Assert.Equal("see you at 5.", target?.Text);
    }

    [Fact]
    public void ACopiedHighlightIsNoTargetWhereUiAutomationCanSeeThereIsNoTextBox()
    {
        Assert.Null(WritingTarget.Target(Field(value: "a page", editable: false), copiedSelection: "a page", ownProcessName: "x"));
        Assert.Null(WritingTarget.Target(Field(value: "secret", secure: true), copiedSelection: "secret", ownProcessName: "x"));
        Assert.Null(WritingTarget.Target(NothingReadable("OpenClicky.exe"), copiedSelection: "mine", ownProcessName: "OpenClicky.exe"));
        Assert.Null(WritingTarget.Target(NothingReadable(), copiedSelection: " \n", ownProcessName: "x"));
        Assert.Null(WritingTarget.Target(NothingReadable(), copiedSelection: null, ownProcessName: "x"));
        Assert.Null(WritingTarget.Target(NothingReadable(), copiedSelection: new string('a', WritingTarget.MaximumCharacters + 1), ownProcessName: "x"));
        Assert.Equal(WritingSource.Clipboard, WritingTarget.Target(Field(value: ""), copiedSelection: "see you", ownProcessName: "x")?.Source);
    }

    [Fact]
    public async Task TheCaptureCopiesTheHighlightWhenUiAutomationReadsNothing()
    {
        var host = new FakeHost { Snapshot = NothingReadable(), CopiedSelection = "see you at 5." };
        var target = await WritingTarget.CaptureFromFrontAppAsync(host, delay: _ => Task.CompletedTask);
        Assert.Equal(WritingSource.Clipboard, target?.Source);
        Assert.Equal("see you at 5.", target?.Text);
        // A box UI Automation reads is never copied from.
        var readable = new FakeHost { Snapshot = Field(value: "Hi Ravi"), CopiedSelection = "unused" };
        var readableTarget = await WritingTarget.CaptureFromFrontAppAsync(readable, delay: _ => Task.CompletedTask);
        Assert.Equal(WritingSource.Accessibility, readableTarget?.Source);
        Assert.DoesNotContain("copy selection", readable.Steps);
    }

    [Fact]
    public async Task ACopiedHighlightIsReplacedOnlyWhileTheSameWordsAreStillHighlighted()
    {
        var target = new WritingTarget(WritingScope.Selection, "at 5", "chrome.exe", "Google Chrome", WritingSource.Clipboard);
        var host = new FakeHost { Snapshot = NothingReadable(), CopiedSelection = "at 5" };
        Assert.Equal(WriteInPlaceOutcome.ProbablyReplaced, await WriteInPlace.ReplaceAsync(target, "at five", host, delay: _ => Task.CompletedTask));
        Assert.Equal(new[] { "copy selection", "insert at five", "copy at 5" }, host.Steps);
        var moved = new FakeHost { Snapshot = NothingReadable(), CopiedSelection = "something else" };
        Assert.Equal(WriteInPlaceOutcome.TargetGone, await WriteInPlace.ReplaceAsync(target, "at five", moved, delay: _ => Task.CompletedTask));
        Assert.Equal(new[] { "copy selection" }, moved.Steps);
    }

    [Fact]
    public void WithoutATargetTheModelAsksForAHighlightInsteadOfReadingARewriteOut()
    {
        Assert.Contains("highlight", WriteInPlace.NoTargetPromptInstruction);
        Assert.DoesNotContain("—", WriteInPlace.NoTargetPromptInstruction);
        Assert.Contains(WriteInPlace.NoTargetPromptInstruction, AskLanePrompts.AskSystemPrompt(false, false, ""));
        Assert.DoesNotContain(WriteInPlace.NoTargetPromptInstruction, AskLanePrompts.AskSystemPrompt(false, true, ""));
    }

    [Fact]
    public async Task ReplacingSelectsTheWholeBoxPastesAndLeavesTheOriginalOnTheClipboard()
    {
        var host = new FakeHost();
        var target = new WritingTarget(WritingScope.WholeField, "Hi Ravi", "chrome.exe", "Google Chrome");
        var outcome = await WriteInPlace.ReplaceAsync(target, "Hello Ravi", host, delay: _ => Task.CompletedTask);
        Assert.Equal(WriteInPlaceOutcome.Replaced, outcome);
        Assert.Equal(new[] { "select all", "insert Hello Ravi", "copy Hi Ravi" }, host.Steps);
    }

    [Fact]
    public async Task NothingIsPastedOnceTheTargetMoved()
    {
        var host = new FakeHost { Snapshot = Field(value: "something else") };
        var target = new WritingTarget(WritingScope.WholeField, "Hi Ravi", "chrome.exe", "Google Chrome");
        Assert.Equal(WriteInPlaceOutcome.TargetGone, await WriteInPlace.ReplaceAsync(target, "Hello", host, delay: _ => Task.CompletedTask));
        Assert.Empty(host.Steps);
        Assert.Equal(WriteInPlaceOutcome.EmptyText, await WriteInPlace.ReplaceAsync(target, "  ", host, delay: _ => Task.CompletedTask));
    }

    [Fact]
    public async Task NoTargetAfterThePasteLeavesTheNewTextOnTheClipboard()
    {
        var host = new FakeHost { Landing = WriteInPlaceLanding.NoTarget };
        var target = new WritingTarget(WritingScope.Selection, "Hi Ravi", "chrome.exe", null);
        host.Snapshot = Field(value: "x Hi Ravi", selected: "Hi Ravi");
        Assert.Equal(WriteInPlaceOutcome.LeftOnClipboard, await WriteInPlace.ReplaceAsync(target, "Hello", host, delay: _ => Task.CompletedTask));
        Assert.Equal(new[] { "insert Hello", "copy Hello" }, host.Steps);
    }

    /// <summary>A terminal takes dictation, so it reads as editable, but Ctrl+C there interrupts what runs:
    /// fast voice captures a target on every turn, so a terminal is never copied from.</summary>
    [Fact]
    public async Task ATerminalIsNeverCopiedFrom()
    {
        var terminal = new WritingFieldSnapshot("WindowsTerminal.exe", "Terminal", "Document", true, false, null, null);
        Assert.False(WritingTarget.CopyIsWorthTrying(terminal, ownProcessName: "x"));
        Assert.False(WritingTarget.CopyIsWorthTrying(terminal with { AppProcessName = "pwsh" }, ownProcessName: "x"));
        var host = new FakeHost { Snapshot = terminal, CopiedSelection = "^C" };
        Assert.Null(await WritingTarget.CaptureFromFrontAppAsync(host, delay: _ => Task.CompletedTask));
        Assert.DoesNotContain("copy selection", host.Steps);
    }

    // From HUDPlacementTests.swift: everyGoodbyeSaysHowToComeBackOrIsShort (Windows wording).
    [Fact]
    public void EveryGoodbyeSaysHowToComeBackOrIsShort()
    {
        foreach (var farewell in Farewell.All)
        {
            Assert.True(farewell.Text.Contains("openclicky") || farewell.Text.Contains("windows key"), farewell.Text);
            Assert.True(farewell.Text.Length <= 90, farewell.Text);
        }
        Assert.Contains(Farewell.Random(), Farewell.All);
    }
}

/// <summary>The fast voice lane rewrites in place too. It never had the tool: with an own key the question goes to
/// fast voice, and "rephrase this" in a Gmail draft was read out (the Mac, 2026-10-23).</summary>
public class RealtimeWriteInPlaceTests
{
    [Fact]
    public void TheRealtimeSessionGetsTheTool()
    {
        var tool = WriteInPlace.RealtimeToolDefinition();
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal(WriteInPlace.ToolName, tool["name"]!.GetValue<string>());
        Assert.Equal(["text"], tool["parameters"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        var names = RealtimeVoiceClient.BuildSessionUpdate(RealtimeTurnMode.PushToTalk, "x", null, "en")["session"]!["tools"]!
            .AsArray().Select(node => node!["name"]!.GetValue<string>());
        Assert.Contains(WriteInPlace.ToolName, names);
    }

    [Fact]
    public void TheTurnCarriesTheTextToWriteOver()
    {
        var target = new WritingTarget(WritingScope.Selection, "hey can u send it", "chrome.exe", "Google Chrome");
        var caption = RealtimeVoiceClient.ScreenCaption("Screenshot of the screen.", target, "ignored");
        Assert.Contains("«hey can u send it»", caption);
        Assert.Contains("write_in_place", caption);
        Assert.DoesNotContain("ignored", caption);
        Assert.Equal("Screenshot of the screen.", RealtimeVoiceClient.ScreenCaption("Screenshot of the screen.", null, null));
        Assert.Contains("«this»", RealtimeVoiceClient.ScreenCaption("Screenshot.", null, "this"));
    }

    [Fact]
    public void DoingItAndExplainingHowAreToldApart()
    {
        Assert.Contains("write_in_place", RealtimeVoiceClient.DefaultInstructions);
        Assert.Contains("how to rephrase", RealtimeVoiceClient.DefaultInstructions);
    }
}
