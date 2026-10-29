// Port of macos/OpenClicky/OpenClickyTests/AskLaneControlTests.swift
//
// Esc stops the ask lane at any time, except that a dictation take in progress keeps esc to itself.
// A question pauses before its screen is sent, unless the pause is off or a guide asks for itself.

using OpenClicky.Core.Answers;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;

namespace OpenClicky.Core.Tests.Answers;

public class AskLaneControlTests
{
    [Fact]
    public void ADictationTakeKeepsEscToItself()
    {
        Assert.Equal(EscapeTarget.DictationTake, AskLaneEscape.Target(dictationTakeInProgress: true, askLaneCanBeStopped: true));
        Assert.Equal(EscapeTarget.DictationTake, AskLaneEscape.Target(dictationTakeInProgress: true, askLaneCanBeStopped: false));
    }

    [Fact]
    public void EscStopsTheAskLaneWhenItHasSomethingUnderWay()
    {
        Assert.Equal(EscapeTarget.AskLane, AskLaneEscape.Target(dictationTakeInProgress: false, askLaneCanBeStopped: true));
        Assert.Equal(EscapeTarget.Nothing, AskLaneEscape.Target(dictationTakeInProgress: false, askLaneCanBeStopped: false));
    }

    /// <summary>Hands-free (Realtime) answering, speaking or still working on an action: esc stops that too.</summary>
    [Fact]
    public void EscStopsTheRealtimeLaneWhenItIsBusy()
    {
        Assert.Equal(EscapeTarget.Realtime, AskLaneEscape.Target(false, false, realtimeIsBusy: true));
        Assert.Equal(EscapeTarget.AskLane, AskLaneEscape.Target(false, true, realtimeIsBusy: true));
        Assert.Equal(EscapeTarget.DictationTake, AskLaneEscape.Target(true, false, realtimeIsBusy: true));
        Assert.Equal(EscapeTarget.Nothing, AskLaneEscape.Target(false, false, realtimeIsBusy: false));
    }

    [Fact]
    public void APressStillListeningOrTranscribingCanBeStopped() =>
        Assert.True(AskLaneEscape.CanStop(pressIsSettled: false, isAskInFlight: false, guideStep: null));

    /// <summary>The first step of a guide: no step number yet, but the question is in flight.</summary>
    [Fact]
    public void AQuestionOnItsWayCanBeStoppedBeforeAnyGuideStepIsKnown() =>
        Assert.True(AskLaneEscape.CanStop(pressIsSettled: true, isAskInFlight: true, guideStep: null));

    [Fact]
    public void AGuideWaitingForAClickCanBeStopped() =>
        Assert.True(AskLaneEscape.CanStop(pressIsSettled: true, isAskInFlight: false, guideStep: 2));

    [Fact]
    public void NothingUnderWayMeansEscIsLeftAlone() =>
        Assert.False(AskLaneEscape.CanStop(pressIsSettled: true, isAskInFlight: false, guideStep: null));

    // MARK: The pause before sending the screen

    [Fact]
    public void AQuestionPausesBeforeItsScreenIsSent()
    {
        Assert.Equal(AskSendPause.DefaultSeconds, AskSendPause.Seconds(isEnabled: true, isGuidedStepContinuation: false));
        Assert.Equal(1.2, AskSendPause.DefaultSeconds);
    }

    [Fact]
    public void NoPauseWhenTheSettingIsOff() =>
        Assert.Null(AskSendPause.Seconds(isEnabled: false, isGuidedStepContinuation: false));

    /// <summary>The person already let the task go ahead: a pause after every click would only slow it down.</summary>
    [Fact]
    public void AGuidesOwnNextStepNeverPauses() =>
        Assert.Null(AskSendPause.Seconds(isEnabled: true, isGuidedStepContinuation: true));

    [Fact]
    public void ThePauseSaysEscCancels()
    {
        Assert.Contains("esc to cancel", AskSendPause.Caption);
        Assert.DoesNotContain("—", AskSendPause.Caption);
    }

    [Fact]
    public async Task APauseThatRunsOutLetsTheQuestionGo() =>
        Assert.True(await AskSendPause.WaitOutAsync(0.01, CancellationToken.None));

    [Fact]
    public async Task ACancelledPauseSendsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var pause = AskSendPause.WaitOutAsync(5, cancellation.Token);
        cancellation.Cancel();
        Assert.False(await pause);
    }

    /// <summary>On by default, and kept once turned off, like its neighbours on the privacy page.</summary>
    [Fact]
    public void PauseBeforeSendingIsOnByDefaultAndRemembered()
    {
        var preferences = Preferences.InMemory();
        Assert.True(new DictationSettings(preferences).PauseBeforeSendingScreen);
        _ = new DictationSettings(preferences) { PauseBeforeSendingScreen = false };
        Assert.False(new DictationSettings(preferences).PauseBeforeSendingScreen);
    }

    /// <summary>The pause's word on the island wins over the ask lane's state while it lasts.</summary>
    [Fact]
    public void TheIslandSaysTheScreenIsAboutToBeSent()
    {
        var island = new IslandCompanionModel { VoiceState = CompanionVoiceState.Processing };
        Assert.Equal("looking at your screen", island.CompactStatusText);
        island.AskIslandStatus = AskSendPause.Caption;
        Assert.Equal(AskSendPause.Caption, island.CompactStatusText);
    }

    /// <summary>The island's strip stops a question on its way, but an agent's work keeps the click that opens the panel.</summary>
    [Fact]
    public void TheIslandStopsOnlyTheAskLanesOwnQuestion()
    {
        var island = new IslandCompanionModel { IsAskInFlight = true, VoiceState = CompanionVoiceState.Processing };
        Assert.True(island.IsQuestionOnItsWay);
        island.AgentActivityText = "deciding…";
        Assert.False(island.IsQuestionOnItsWay);
        island.AgentActivityText = null;
        // Speaking has its own stop (it keeps agent work going).
        island.VoiceState = CompanionVoiceState.Responding;
        Assert.False(island.IsQuestionOnItsWay);
    }
}
