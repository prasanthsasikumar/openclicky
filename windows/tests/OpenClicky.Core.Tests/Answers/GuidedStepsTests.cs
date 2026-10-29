// Port of macos/OpenClicky/OpenClickyTests/GuidedStepsTests.swift (+ Windows key mapping and the watcher).

using OpenClicky.Core.Answers;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Answers;

public class GuidedStepsTests
{
    [Fact]
    public void TheMarkerIsFoundHoweverItIsWritten()
    {
        Assert.True(GuidedStepMarker.ContainsMarker("click the paperclip. [NEXT]"));
        Assert.True(GuidedStepMarker.ContainsMarker("click the paperclip. [ next ]"));
        Assert.False(GuidedStepMarker.ContainsMarker("that's the next button."));
        Assert.Equal("[POINT:1,2:clip] click the paperclip.", GuidedStepMarker.RemovingMarkers("[POINT:1,2:clip] click the paperclip. [NEXT]\n"));
    }

    [Fact]
    public void TheMarkerIsNeverSpoken()
    {
        var chunker = new SpokenChunker();
        Assert.Equal(new[] { "click the paperclip at the top." }, chunker.Feed("[POINT:612,88:attach] click the paperclip at the top. [NEXT]"));
        Assert.Empty(chunker.Finish());
        Assert.Equal("[POINT:612,88:attach]", chunker.PointTag);
    }

    [Fact]
    public void AMarkerOnItsOwnLineIsNotSpokenEither()
    {
        var chunker = new SpokenChunker();
        Assert.Equal(new[] { "click the paperclip at the top." }, chunker.Feed("click the paperclip at the top.\n[NEXT]\n"));
        Assert.Empty(chunker.Finish());
    }

    [Fact]
    public void ATrailingPointTagBeforeTheMarkerStillPoints()
    {
        var chunker = new SpokenChunker();
        chunker.Feed("click the paperclip at the top [POINT:612,88:attach] [NEXT]");
        Assert.Equal(new[] { "click the paperclip at the top" }, chunker.Finish());
        Assert.Equal("[POINT:612,88:attach]", chunker.PointTag);
    }

    [Fact]
    public void AClickReturnOrTabFinishesAStepAfterTheScreenSettles()
    {
        Assert.Equal(new GuidedStepReaction.AskForNextStep(GuidedStepRules.SettleSecondsAfterClick), GuidedStepRules.Reaction(GuidedStepInput.Click, askIsPending: false));
        Assert.Equal(new GuidedStepReaction.AskForNextStep(GuidedStepRules.SettleSecondsAfterStepKey), GuidedStepRules.Reaction(GuidedStepInput.StepKey, askIsPending: false));
    }

    [Fact]
    public void TypingAloneIsNotAStepButTypingAfterAClickWaitsLonger()
    {
        Assert.Equal(new GuidedStepReaction.Ignore(), GuidedStepRules.Reaction(GuidedStepInput.OtherKey, askIsPending: false));
        Assert.Equal(new GuidedStepReaction.PostponePendingAsk(GuidedStepRules.SettleSecondsWhileTyping), GuidedStepRules.Reaction(GuidedStepInput.OtherKey, askIsPending: true));
    }

    [Fact]
    public void EscEndsTheGuide()
    {
        Assert.Equal(new GuidedStepReaction.EndGuide(), GuidedStepRules.Reaction(GuidedStepInput.EscapeKey, askIsPending: false));
        Assert.Equal(new GuidedStepReaction.EndGuide(), GuidedStepRules.Reaction(GuidedStepInput.EscapeKey, askIsPending: true));
    }

    [Fact]
    public void TheNextStepRequestRepeatsTheGoal()
    {
        var request = GuidedStepRules.NextStepRequest("how do i attach a file");
        Assert.StartsWith(GuidedStepRules.NextStepRequestOpening, request);
        Assert.Contains("how do i attach a file", request);
        Assert.DoesNotContain("—", request);
    }

    [Fact]
    public void WindowsKeysMapLikeTheMacKeyCodes()
    {
        Assert.Equal(GuidedStepInput.StepKey, GuidedStepInputMapping.ForKeyDown(0x0D));
        Assert.Equal(GuidedStepInput.StepKey, GuidedStepInputMapping.ForKeyDown(0x09));
        Assert.Equal(GuidedStepInput.EscapeKey, GuidedStepInputMapping.ForKeyDown(0x1B));
        Assert.Equal(GuidedStepInput.OtherKey, GuidedStepInputMapping.ForKeyDown(0x41));
        Assert.Equal(GuidedStepInput.Click, GuidedStepInputMapping.ForLeftMouseUp());
    }

    private sealed class FakeInputSource : IGuidedStepInputSource
    {
        public event Action<GuidedStepInput, ScreenPoint?>? InputReceived;
        public bool IsStarted { get; private set; }
        public void Start() => IsStarted = true;
        public void Stop() => IsStarted = false;
        public void Send(GuidedStepInput input, ScreenPoint? clickLocation = null) => InputReceived?.Invoke(input, clickLocation);
    }

    private static Task NoWait(TimeSpan wait, CancellationToken token) => wait.TotalSeconds > 100 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;

    [Fact]
    public async Task TheWatcherReportsAStepAfterAClickAndEndsOnEscape()
    {
        var source = new FakeInputSource();
        var stepDone = new TaskCompletionSource<ScreenPoint?>();
        var watcher = new GuidedStepWatcher(source, clickLocation => stepDone.TrySetResult(clickLocation), _ => { }, NoWait);
        watcher.Start();
        Assert.True(source.IsStarted);
        source.Send(GuidedStepInput.Click, new ScreenPoint(640, 360));
        // The click that finished the step comes with where it landed.
        Assert.Equal(new ScreenPoint(640, 360), await stepDone.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(source.IsStarted);

        var escapeSource = new FakeInputSource();
        GuidedStepWatcher.EndReason? endReason = null;
        var escapeWatcher = new GuidedStepWatcher(escapeSource, _ => { }, reason => endReason = reason, NoWait);
        escapeWatcher.Start();
        escapeSource.Send(GuidedStepInput.EscapeKey);
        Assert.Equal(GuidedStepWatcher.EndReason.Escape, endReason);
        Assert.False(escapeSource.IsStarted);
    }

    [Fact]
    public async Task ReturnOrTabAfterAClickFinishesTheStepWithNoPosition()
    {
        var source = new FakeInputSource();
        var stepDone = new TaskCompletionSource<ScreenPoint?>();
        // The first wait (after the click) never ends; the one after Return does.
        var waits = 0;
        Task FirstWaitHangs(TimeSpan wait, CancellationToken token) =>
            wait.TotalSeconds > 100 || Interlocked.Increment(ref waits) == 1 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        var watcher = new GuidedStepWatcher(source, clickLocation => stepDone.TrySetResult(clickLocation), _ => { }, FirstWaitHangs);
        watcher.Start();
        source.Send(GuidedStepInput.Click, new ScreenPoint(10, 10));
        source.Send(GuidedStepInput.StepKey);
        Assert.Null(await stepDone.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // MARK: Did the click land where the pointer pointed?

    [Fact]
    public void AClickNearThePointedSpotIsOnTarget()
    {
        var pointed = new ScreenPoint(400, 300);
        Assert.Equal(new GuidedStepClickCheck.Verdict.OnTarget(), GuidedStepClickCheck.Check(pointed, new ScreenPoint(400, 300)));
        // Pointing is known to be up to about 100 points off: that is still on target.
        Assert.Equal(new GuidedStepClickCheck.Verdict.OnTarget(), GuidedStepClickCheck.Check(pointed, new ScreenPoint(480, 360)));
        Assert.Equal(new GuidedStepClickCheck.Verdict.OnTarget(), GuidedStepClickCheck.Check(pointed, new ScreenPoint(520, 300)));
    }

    [Fact]
    public void AClickFarFromThePointedSpotIsOffTargetByAboutHowFar()
    {
        var pointed = new ScreenPoint(400, 300);
        Assert.Equal(new GuidedStepClickCheck.Verdict.OffTarget(120), GuidedStepClickCheck.Check(pointed, new ScreenPoint(521, 300)));
        Assert.Equal(new GuidedStepClickCheck.Verdict.OffTarget(300), GuidedStepClickCheck.Check(pointed, new ScreenPoint(400, 0)));
        Assert.Equal(new GuidedStepClickCheck.Verdict.OffTarget(250), GuidedStepClickCheck.Check(pointed, new ScreenPoint(550, 500)));
    }

    /// <summary>Windows works in physical pixels: on a 150% monitor 150 pixels are 100 points, still on target.</summary>
    [Fact]
    public void TheDistanceIsInPointsWhateverTheMonitorsScale()
    {
        var pointed = new ScreenPoint(400, 300);
        Assert.Equal(new GuidedStepClickCheck.Verdict.OnTarget(), GuidedStepClickCheck.Check(pointed, new ScreenPoint(550, 300), pixelsPerPoint: 1.5));
        Assert.Equal(new GuidedStepClickCheck.Verdict.OffTarget(200), GuidedStepClickCheck.Check(pointed, new ScreenPoint(700, 300), pixelsPerPoint: 1.5));
    }

    [Fact]
    public void NoClickOrNothingPointedAtIsUnknown()
    {
        Assert.Equal(new GuidedStepClickCheck.Verdict.Unknown(), GuidedStepClickCheck.Check(new ScreenPoint(1, 1), null));
        Assert.Equal(new GuidedStepClickCheck.Verdict.Unknown(), GuidedStepClickCheck.Check(null, new ScreenPoint(1, 1)));
    }

    [Fact]
    public void AnOffTargetClickAsksTheModelToCheckAndStillRepeatsTheGoal()
    {
        var request = GuidedStepRules.NextStepRequest("turn on dark mode", new GuidedStepClickCheck.Verdict.OffTarget(180));
        Assert.StartsWith(GuidedStepRules.OffTargetRequestOpening, request);
        Assert.Contains("about 180 points away from where you pointed", request);
        Assert.Contains("gently point me back", request);
        Assert.EndsWith("what i asked was: turn on dark mode)", request);
        Assert.DoesNotContain("—", request);
        Assert.DoesNotContain("—", GuidedStepClickCheck.OffTargetCaption);
        Assert.Contains(GuidedStepRules.OffTargetRequestOpening, AskLanePrompts.CompanionVoiceResponseSystemPrompt);
    }

    [Fact]
    public void OnTargetAndUnknownClicksKeepTodaysRequest()
    {
        var plain = GuidedStepRules.NextStepRequest("turn on dark mode");
        Assert.Equal(plain, GuidedStepRules.NextStepRequest("turn on dark mode", new GuidedStepClickCheck.Verdict.OnTarget()));
        Assert.Equal(plain, GuidedStepRules.NextStepRequest("turn on dark mode", new GuidedStepClickCheck.Verdict.Unknown()));
        Assert.StartsWith(GuidedStepRules.NextStepRequestOpening, plain);
    }
}
