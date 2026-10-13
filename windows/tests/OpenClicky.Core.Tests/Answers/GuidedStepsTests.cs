// Port of macos/OpenClicky/OpenClickyTests/GuidedStepsTests.swift (+ Windows key mapping and the watcher).

using OpenClicky.Core.Answers;

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
        public event Action<GuidedStepInput>? InputReceived;
        public bool IsStarted { get; private set; }
        public void Start() => IsStarted = true;
        public void Stop() => IsStarted = false;
        public void Send(GuidedStepInput input) => InputReceived?.Invoke(input);
    }

    [Fact]
    public async Task TheWatcherReportsAStepAfterAClickAndEndsOnEscape()
    {
        var source = new FakeInputSource();
        var stepDone = new TaskCompletionSource();
        Task NoWait(TimeSpan wait, CancellationToken token) => wait.TotalSeconds > 100 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        var watcher = new GuidedStepWatcher(source, () => stepDone.TrySetResult(), _ => { }, NoWait);
        watcher.Start();
        Assert.True(source.IsStarted);
        source.Send(GuidedStepInput.Click);
        await stepDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(source.IsStarted);

        var escapeSource = new FakeInputSource();
        GuidedStepWatcher.EndReason? endReason = null;
        var escapeWatcher = new GuidedStepWatcher(escapeSource, () => { }, reason => endReason = reason, NoWait);
        escapeWatcher.Start();
        escapeSource.Send(GuidedStepInput.EscapeKey);
        Assert.Equal(GuidedStepWatcher.EndReason.Escape, endReason);
        Assert.False(escapeSource.IsStarted);
    }
}
