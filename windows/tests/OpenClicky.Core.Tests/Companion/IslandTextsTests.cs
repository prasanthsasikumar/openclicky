using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Tests.Companion;

public class IslandTextsTests
{
    [Theory]
    [InlineData(CompanionVoiceState.Idle, false, false, null, null, "openclicky")]
    [InlineData(CompanionVoiceState.Idle, false, true, null, null, "hands-free · just talk")]
    [InlineData(CompanionVoiceState.Idle, false, false, "ran: ls", null, "an agent is working")]
    [InlineData(CompanionVoiceState.Listening, true, false, null, null, "dictating")]
    [InlineData(CompanionVoiceState.Listening, false, true, null, null, "listening · hands-free")]
    [InlineData(CompanionVoiceState.Listening, false, false, null, null, "listening")]
    [InlineData(CompanionVoiceState.Processing, true, false, null, null, "typing it in")]
    [InlineData(CompanionVoiceState.Processing, false, false, null, null, "looking at your screen")]
    [InlineData(CompanionVoiceState.Processing, false, false, "ran: ls", null, "working")]
    [InlineData(CompanionVoiceState.Responding, false, false, null, "Claude Code", "speaking · Claude Code")]
    [InlineData(CompanionVoiceState.Responding, false, false, null, null, "speaking")]
    public void TheBusyStripSaysWhatIsHappening(CompanionVoiceState voiceState, bool dictating, bool handsFree, string? agentActivity, string? source, string expected)
    {
        Assert.Equal(expected, IslandTexts.CompactStatus(voiceState, dictating, handsFree, agentActivity, source));
    }

    [Fact]
    public void TheCaptionLineIsTheAgentsProgressOrTheQuestionBeingAnswered()
    {
        Assert.Null(IslandTexts.DetailLine(CompanionVoiceState.Idle, "ran: ls", "hi"));
        Assert.Equal("ran: ls", IslandTexts.DetailLine(CompanionVoiceState.Processing, "ran: ls", "hi"));
        Assert.Equal("“hi”", IslandTexts.DetailLine(CompanionVoiceState.Responding, null, "hi"));
        Assert.Null(IslandTexts.DetailLine(CompanionVoiceState.Listening, null, "hi"));
        Assert.Null(IslandTexts.DetailLine(CompanionVoiceState.Responding, "", ""));
    }

    [Fact]
    public void ThePointerStatusFollowsDockAndPresence()
    {
        Assert.Equal("pointer is docked", IslandTexts.PointerStatus(true, HUDPlacement.Notch, true, PointerPresence.Always, true));
        Assert.Equal("pointer is docked in the drawer", IslandTexts.PointerStatus(true, HUDPlacement.Left, true, PointerPresence.Always, true));
        Assert.Equal("pointer shows while you talk", IslandTexts.PointerStatus(false, HUDPlacement.Notch, false, PointerPresence.Always, true));
        Assert.Equal("pointer follows while moving", IslandTexts.PointerStatus(false, HUDPlacement.Notch, true, PointerPresence.WhileMoving, false));
        Assert.Equal("shake the mouse for the pointer", IslandTexts.PointerStatus(false, HUDPlacement.Notch, true, PointerPresence.OnShake, false));
        Assert.Equal("pointer follows your mouse", IslandTexts.PointerStatus(false, HUDPlacement.Notch, true, PointerPresence.OnShake, true));
    }

    [Fact]
    public void TheShortcutGridShowsTheWindowsKeys()
    {
        var grid = IslandTexts.ShortcutGrid(DictationHotkey.RightControl, isAlwaysListening: true);
        Assert.Equal(new[] { "talk", "type", "dictate", "hands-free" }, grid.Select(shortcut => shortcut.Title));
        Assert.Equal(new[] { "R Ctrl", "Win" }, grid[0].Keys);
        Assert.Equal("tap ×2", grid[1].Gesture);
        Assert.Equal(new[] { "R Ctrl" }, grid[2].Keys);
        Assert.Equal(new[] { "R Ctrl", "Ctrl" }, grid[3].Keys);
        Assert.True(grid[3].IsLive);
        Assert.Equal(new[] { "Caps", "Ctrl" }, IslandTexts.ShortcutGrid(DictationHotkey.CapsLock, false)[3].Keys);
        // With another key dictating, either Ctrl talks (many laptops have no right Ctrl).
        Assert.Equal(new[] { "Ctrl", "Win" }, IslandTexts.ShortcutGrid(DictationHotkey.RightAlt, false)[0].Keys);
        Assert.Equal(new[] { "L Ctrl", "Win" }, IslandTexts.ShortcutGrid(DictationHotkey.LeftControl, false)[0].Keys);
    }

    [Fact]
    public void TheEngineWarningSaysTheRealReason()
    {
        var download = IslandEngineWarning.Make("Parakeet", "needs its 465 MB download", "open dictation settings")!;
        Assert.StartsWith("parakeet needs its 465 MB download", download.Message);
        Assert.EndsWith("dictation stays off until then.", download.Message);
        Assert.Equal("PARAKEET CAN'T HEAR YOU YET", download.Heading);
        var sentence = IslandEngineWarning.Make("Parakeet", "parakeet doesn't hear hindi", "open language settings")!;
        Assert.StartsWith("parakeet doesn't hear hindi.", sentence.Message);
        Assert.Null(IslandEngineWarning.Make("OpenClicky", null, "x"));
    }

    [Fact]
    public void TheDockButtonAndExplanationSayTheWindowsThings()
    {
        Assert.Equal("dock in the island", IslandTexts.DockButtonTitle(false, HUDPlacement.Notch));
        Assert.Equal("dock in the drawer", IslandTexts.DockButtonTitle(false, HUDPlacement.Right));
        Assert.Equal("release pointer", IslandTexts.DockButtonTitle(true, HUDPlacement.Notch));
        var explanation = IslandTexts.ExplainMessage(DictationHotkey.RightAlt);
        Assert.Contains("hold ctrl + windows", explanation);
        Assert.DoesNotContain("right ctrl", explanation);
        Assert.Contains("hold right alt to dictate", explanation);
        Assert.Contains("hold right ctrl + windows", IslandTexts.ExplainMessage(DictationHotkey.RightControl));
        Assert.DoesNotContain("option", explanation);
    }
}

public class PermissionPromptTests
{
    [Fact]
    public void OnlineSpeechIsAskedForOnlyOnceWindowsSpeechRefused()
    {
        Assert.Equal(PermissionStep.Microphone, PermissionPromptController.NextStep(new PermissionStatus()));
        Assert.Null(PermissionPromptController.NextStep(new PermissionStatus(Microphone: true)));
        Assert.Equal(PermissionStep.OnlineSpeech, PermissionPromptController.NextStep(new PermissionStatus(Microphone: true, OnlineSpeech: false)));
        Assert.Equal(new[] { PermissionStep.Microphone }, PermissionSteps.Relevant(new PermissionStatus()));
        Assert.Equal("step 1 of 1", new PermissionPrompt(PermissionStep.Microphone, new PermissionStatus()).ProgressLabel);
        Assert.Equal("step 2 of 2", new PermissionPrompt(PermissionStep.OnlineSpeech, new PermissionStatus(true, false)).ProgressLabel);
    }

    [Fact]
    public void TurningOnOnlineSpeechDropsTheCardAtOnce()
    {
        var controller = new PermissionPromptController();
        controller.PerformRequest = _ => controller.Update(new PermissionStatus(Microphone: true));
        controller.Update(new PermissionStatus(Microphone: true, OnlineSpeech: false));
        Assert.Equal("turn on", controller.CurrentPrompt!.ButtonTitle);
        Assert.Contains("microsoft", controller.CurrentPrompt.Detail);
        controller.Grant();
        Assert.Null(controller.CurrentPrompt);
    }

    [Fact]
    public void WhenOnlineSpeechCannotBeSwitchedOnTheCardPointsAtSettings()
    {
        var controller = new PermissionPromptController();
        controller.Update(new PermissionStatus(Microphone: true, OnlineSpeech: false));
        controller.Grant();
        Assert.Equal(PermissionPromptStage.Waiting, controller.CurrentPrompt!.Stage);
        Assert.Contains("in settings", controller.CurrentPrompt.Detail);
    }

    [Fact]
    public void GrantingAsksWindowsAndWaitsUntilTheMicrophoneLands()
    {
        var requested = new List<PermissionStep>();
        var controller = new PermissionPromptController { PerformRequest = requested.Add };
        controller.Update(new PermissionStatus());
        Assert.Equal("grant", controller.CurrentPrompt!.ButtonTitle);
        Assert.Equal("only while you hold a shortcut or go hands-free.", controller.CurrentPrompt.Detail);
        controller.Grant();
        Assert.Equal(new[] { PermissionStep.Microphone }, requested);
        Assert.Equal(PermissionPromptStage.Waiting, controller.CurrentPrompt!.Stage);
        Assert.Equal("open", controller.CurrentPrompt.ButtonTitle);
        // The poll keeps the stage while the same permission is still missing.
        controller.Update(new PermissionStatus());
        Assert.Equal(PermissionPromptStage.Waiting, controller.CurrentPrompt!.Stage);
        controller.Update(new PermissionStatus(Microphone: true));
        Assert.Null(controller.CurrentPrompt);
        Assert.False(new PermissionPrompt(PermissionStep.Microphone, new PermissionStatus()).OffersAccessibilityTrustReset);
    }

    [Fact]
    public void LaterHidesTheCardsUntilTheNextLaunch()
    {
        var controller = new PermissionPromptController();
        controller.Update(new PermissionStatus());
        controller.Dismiss();
        controller.Update(new PermissionStatus());
        Assert.Null(controller.CurrentPrompt);
        Assert.True(controller.IsDismissedForThisLaunch);
    }
}

public class FirstRunTourCardsTests
{
    private static FirstRunTourCardInputs Inputs => new(DictationHotkey.RightControl, CanAsk: true, UsesAgent: true, MicrophoneName: "Microphone Array");

    [Fact]
    public void TheKeycapsPressHoldAndLetGoEveryTwoPointFourSeconds()
    {
        Assert.Equal(0, FirstRunTourCards.PressAmount(0.2));
        Assert.Equal(0.5, FirstRunTourCards.PressAmount(0.625), 3);
        Assert.Equal(1, FirstRunTourCards.PressAmount(1.2));
        Assert.Equal(0.5, FirstRunTourCards.PressAmount(1.875), 3);
        Assert.Equal(0, FirstRunTourCards.PressAmount(2.2));
        Assert.Equal(1, FirstRunTourCards.PressAmount(2.4 + 1.2));
    }

    [Fact]
    public void TheCardsShowTheWindowsKeys()
    {
        var dictate = FirstRunTourCards.Make(FirstRunTourStep.Dictate, Inputs);
        Assert.Equal("hold right ctrl or right alt and say something", dictate.Title);
        Assert.Equal(new[] { "R Ctrl" }, dictate.Keys);
        Assert.Equal(0, dictate.PracticeIndex);
        Assert.Equal("Microphone Array", dictate.MicrophoneName);
        var talk = FirstRunTourCards.Make(FirstRunTourStep.Talk, Inputs);
        Assert.Equal(new[] { "R Ctrl", "Win" }, talk.Keys);
        Assert.Contains("hold right ctrl + windows", talk.Detail);
        Assert.Equal("next", talk.PrimaryTitle);
        Assert.Contains("no right ctrl? right alt is just right of the space bar.", dictate.Detail);
        var rightAlt = Inputs with { DictationKey = DictationHotkey.RightAlt };
        Assert.Equal("hold right Alt and say something", FirstRunTourCards.Make(FirstRunTourStep.Dictate, rightAlt).Title);
        Assert.DoesNotContain("no right ctrl", FirstRunTourCards.Make(FirstRunTourStep.Dictate, rightAlt).Detail);
        Assert.Equal(new[] { "Ctrl", "Win" }, FirstRunTourCards.Make(FirstRunTourStep.Talk, rightAlt).Keys);
        Assert.Contains("hold ctrl + windows", FirstRunTourCards.Make(FirstRunTourStep.Act, rightAlt).Detail);
        Assert.Contains("add a note", FirstRunTourCards.Make(FirstRunTourStep.Act, Inputs).Detail);
        Assert.Contains("open notepad", FirstRunTourCards.Make(FirstRunTourStep.Act, Inputs with { UsesAgent = false }).Detail);
        Assert.Equal("add email", FirstRunTourCards.Make(FirstRunTourStep.Talk, Inputs with { CanAsk = false }).PrimaryTitle);
    }

    [Fact]
    public void TheAccountCardAsksForAnEmailThenACode()
    {
        var email = FirstRunTourCards.Make(FirstRunTourStep.Account, Inputs with { AccountCardAsksForEmail = true });
        Assert.Equal(FirstRunTourFieldKind.Email, email.Field);
        Assert.Equal("start", email.PrimaryTitle);
        var code = FirstRunTourCards.Make(FirstRunTourStep.Account, Inputs with { AccountCardAsksForEmail = true, NeedsCode = true });
        Assert.Equal(FirstRunTourFieldKind.Code, code.Field);
        Assert.Equal("confirm", code.PrimaryTitle);
        Assert.Null(FirstRunTourCards.Make(FirstRunTourStep.Account, Inputs).Field);
        var openAtLogin = FirstRunTourCards.Make(FirstRunTourStep.OpenAtLogin, Inputs with { StatusIsGood = true, Status = "✓" });
        Assert.Null(openAtLogin.PrimaryTitle);
        Assert.Equal("close", openAtLogin.SecondaryTitle);
    }
}

public class OrbSettingsTests
{
    [Fact]
    public void DefaultsMatchTheMac()
    {
        var preferences = Preferences.InMemory();
        var settings = new OrbSettings(preferences);
        Assert.True(settings.OrbVisible);
        Assert.Equal(OrbLookChoice.Pill, settings.OrbLook);
        Assert.Equal(OrbThemeChoice.Black, settings.OrbTheme);
        Assert.Equal(OrbSizeChoice.Full, settings.OrbSize);
        Assert.True(settings.OrbIsDraggable);
        // "hide when not in use" was switched on once for everyone.
        Assert.True(settings.OrbHidesWhenIdle);
        Assert.True(preferences.GetBool(OrbSettings.Keys.OrbHidesWhenIdleDefaultApplied));
        Assert.Null(settings.OrbPosition);
        Assert.Equal(DictationHotkey.RightControl, settings.DictationKey);
    }

    [Fact]
    public void ThePersonsChoicesAreReadByTheMacKeyNames()
    {
        var preferences = Preferences.InMemory();
        preferences.Set(OrbSettings.Keys.OrbHidesWhenIdleDefaultApplied, true);
        preferences.Set(OrbSettings.Keys.OrbHidesWhenIdle, false);
        preferences.Set("dictation.orb.look", "pixel");
        preferences.Set("dictation.orb.theme", "coral");
        preferences.Set("dictation.orb.size", "mini");
        preferences.Set("dictation.key", "capsLock");
        var settings = new OrbSettings(preferences);
        Assert.False(settings.OrbHidesWhenIdle);
        Assert.Equal(OrbLookChoice.Pixel, settings.OrbLook);
        Assert.Equal(OrbThemeChoice.Coral, settings.OrbTheme);
        Assert.Equal(OrbSizeChoice.Mini, settings.OrbSize);
        Assert.Equal(DictationHotkey.CapsLock, settings.DictationKey);
        Assert.Equal((44.0, 20.0), OrbMetrics.PillSize(settings.OrbSize));
    }

    [Fact]
    public void TheDraggedSpotIsStoredAsXCommaY()
    {
        var preferences = Preferences.InMemory();
        var settings = new OrbSettings(preferences) { OrbPosition = (1512.5, 1880) };
        Assert.Equal("1512.5,1880", preferences.GetString(OrbSettings.Keys.OrbPosition));
        Assert.Equal((1512.5, 1880.0), settings.OrbPosition);
        Assert.Null(OrbSettings.DecodePoint("12"));
        Assert.Null(OrbSettings.DecodePoint("a,b"));
    }

    [Fact]
    public void TheOrbIsBusyOnlyWhileATakeIsUnderWay()
    {
        var model = new OrbModel();
        Assert.False(model.IsBusy);
        model.Phase = OrbPhase.Listening;
        Assert.True(model.IsBusy);
        model.Phase = OrbPhase.Working(OrbPhaseTexts.MovingYourWords);
        Assert.True(model.IsBusy);
        model.Phase = OrbPhase.Done(OrbPhaseTexts.MovedToTextBox);
        Assert.False(model.IsBusy);
        Assert.Equal(240, OrbMetrics.ContentWidth(OrbPhaseKind.Listening, OrbSizeChoice.Full, showsBox: false));
        Assert.Equal(76, OrbMetrics.ContentWidth(OrbPhaseKind.Idle, OrbSizeChoice.Full, showsBox: false));
        Assert.Equal(380, OrbMetrics.ContentWidth(OrbPhaseKind.Idle, OrbSizeChoice.Full, showsBox: true));
    }
}

public class AgentResultCardModelTests
{
    [Fact]
    public void TheStatusBadgeReadsAsOnTheMac()
    {
        var card = new AgentResultCardModel();
        card.BeginLoading("thread-1");
        Assert.Equal("…", card.StatusLabel);
        card.Status = "completed";
        Assert.Equal("Done", card.StatusLabel);
        card.Status = "interrupted";
        Assert.Equal("Interrupted", card.StatusLabel);
        card.Status = "error";
        Assert.Equal(AgentResultStatusKind.Error, card.StatusKind);
        card.IsWorking = true;
        Assert.Equal("Working", card.StatusLabel);
    }

    [Fact]
    public void TheFreshestResultWinsAndFollowUpsCarryTheThread()
    {
        var card = new AgentResultCardModel();
        card.BeginLoading("thread-2");
        card.Summary = "from history";
        Assert.Equal("from history", card.LiveSummary);
        card.LiveResultText = "just now";
        Assert.Equal("just now", card.LiveSummary);
        var followUps = new List<(string, string)>();
        card.FollowUpRequested += (text, threadId) => followUps.Add((text, threadId));
        card.SendFollowUp("  and the rest?  ");
        card.SendFollowUp("   ");
        Assert.Equal(new[] { ("and the rest?", "thread-2") }, followUps);
    }
}
