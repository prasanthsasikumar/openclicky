// Port of macos/OpenClicky/OpenClickyTests/AskLaneControlTests.swift
//
// Esc stops the ask lane at any time, except that a dictation take in progress keeps esc to itself.
// A question pauses before its screen is sent, unless the pause is off or a guide asks for itself.
// An [UNSURE] reply is never spoken with its marker and never points, even with a tag.

using OpenClicky.Core.Answers;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Speech;

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

    // MARK: [UNSURE]

    [Fact]
    public void TheUnsureMarkerIsFoundHoweverItIsWritten()
    {
        Assert.True(UnsureMarker.ContainsMarker("[UNSURE] look for a gear icon."));
        Assert.True(UnsureMarker.ContainsMarker("[ unsure ] look for a gear icon."));
        Assert.False(UnsureMarker.ContainsMarker("i'm not sure that's the one."));
        Assert.Equal("look for a gear icon.", UnsureMarker.RemovingMarkers("[UNSURE] look for a gear icon."));
        Assert.DoesNotContain("—", UnsureMarker.IslandStatus);
        Assert.Contains(UnsureMarker.Marker, AskLanePrompts.CompanionVoiceResponseSystemPrompt);
    }

    [Fact]
    public void TheUnsureMarkerIsNeverSpokenAndNothingPoints()
    {
        var chunker = new SpokenChunker();
        Assert.Equal(new[] { "i can't spot it." }, chunker.Feed("[UNSURE] i can't spot it. look for a gear icon in your start menu."));
        Assert.Equal(new[] { "look for a gear icon in your start menu." }, chunker.Finish());
        Assert.True(chunker.IsUnsure);
        Assert.Null(chunker.PointTag);
    }

    /// <summary>A model that sends a tag with [UNSURE] anyway: the tag is never spoken, and the answer is still marked unsure so the speaker ignores it.</summary>
    [Fact]
    public void APointTagAfterTheUnsureMarkerIsNotSpoken()
    {
        var chunker = new SpokenChunker();
        Assert.Equal(new[] { "it might be the gear icon." }, chunker.Feed("[UNSURE] [POINT:400,300:gear] it might be the gear icon. "));
        Assert.True(chunker.IsUnsure);
        Assert.Equal("[POINT:400,300:gear]", chunker.PointTag);
    }

    [Fact]
    public void AStreamThatCouldStillBeTheUnsureMarkerWaits()
    {
        var chunker = new SpokenChunker();
        Assert.Empty(chunker.Feed("[UNS"));
        Assert.Empty(chunker.Feed("[UNSURE]"));
        Assert.Empty(chunker.Feed("[UNSURE] [POINT:4"));
        Assert.Equal(new[] { "tell me what you see." }, chunker.Feed("[UNSURE] [POINT:400,300:gear] tell me what you see. "));
    }

    [Fact]
    public void AnAnswerWithoutTheMarkerIsNotUnsure()
    {
        var chunker = new SpokenChunker();
        chunker.Feed("[POINT:400,300:gear] click the gear. ");
        Assert.False(chunker.IsUnsure);
        Assert.Equal("[POINT:400,300:gear]", chunker.PointTag);
    }

    [Fact]
    public async Task AnUnsureAnswerNeverMovesThePointer()
    {
        async Task<List<string>> PointedTags(string answer)
        {
            var pointedTags = new List<string>();
            var speaker = new StreamingAnswerSpeaker(
                new SpeechQueue((_, _) => Task.FromResult(new byte[] { 1 }), (_, _) => Task.CompletedTask),
                () => { },
                pointedTags.Add);
            var playback = speaker.PlayAsync();
            speaker.Receive(answer);
            speaker.Finish([]);
            await playback;
            return pointedTags;
        }
        Assert.Empty(await PointedTags("[UNSURE] [POINT:400,300:gear] it might be the gear icon."));
        Assert.Equal(new[] { "[POINT:400,300:gear]" }, await PointedTags("[POINT:400,300:gear] click the gear icon."));
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
