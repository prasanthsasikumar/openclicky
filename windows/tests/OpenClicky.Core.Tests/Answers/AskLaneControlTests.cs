// Port of macos/OpenClicky/OpenClickyTests/AskLaneControlTests.swift
//
// Esc stops the ask lane at any time, except that a dictation take in progress keeps esc to itself.

using OpenClicky.Core.Answers;
using OpenClicky.Core.Companion;

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
