using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Companion;

public class OverlayModelTests
{
    // Two monitors side by side, in pixels: the primary (with the island) and one to its right.
    private static readonly ScreenRect Primary = new(0, 0, 3024, 1964);
    private static readonly ScreenRect Right = new(3024, 0, 1920, 1080);
    private static readonly ScreenPoint DockPoint = new(1512, 36);

    private static (OverlayModel Model, ManualDelays Clock, Preferences Preferences) Make()
    {
        // Continuations run inline when the clock advances (no xUnit context to post them to).
        SynchronizationContext.SetSynchronizationContext(null);
        var clock = new ManualDelays();
        var preferences = Preferences.InMemory();
        var model = new OverlayModel(clock.Delay, preferences, () => clock.Now.TotalSeconds)
        {
            DockTarget = () => (DockPoint, Primary),
            ScreenFrameAt = point => Right.Contains(point) ? Right : Primary,
            MouseLocation = () => new ScreenPoint(4000, 500),
        };
        return (model, clock, preferences);
    }

    [Fact]
    public void PointingTwiceAtTheSameSpotStillBumpsTheToken()
    {
        var (model, _, _) = Make();
        model.PointAt(new ScreenPoint(100, 200), Primary, "  this one  ");
        model.PointAt(new ScreenPoint(100, 200), Primary, "  this one  ");
        Assert.Equal(2, model.DetectedElementPointToken);
        Assert.Equal(new ScreenPoint(100, 200), model.DetectedElementScreenLocation);
        Assert.Equal("this one", model.DetectedElementBubbleText);
        model.PointAt(new ScreenPoint(1, 1), Primary, "   ");
        Assert.Null(model.DetectedElementBubbleText);
    }

    [Fact]
    public void PointingWhileThinkingShowsThePointer()
    {
        var (model, _, _) = Make();
        model.VoiceState = CompanionVoiceState.Processing;
        model.PointAt(new ScreenPoint(10, 10), Primary, null);
        Assert.Equal(CompanionVoiceState.Idle, model.VoiceState);
    }

    [Fact]
    public void AFlightBackToTheMouseOnAnotherDisplayHasTwoLegs()
    {
        var (model, _, _) = Make();
        model.MouseLocation = () => DockPoint;
        model.BeginCursorFlight(CursorFlightPurpose.ToMouse, Right);
        var firstLeg = Assert.IsType<CursorFlightLeg>(model.CursorFlightLeg);
        Assert.False(firstLeg.IsFinalLeg);
        Assert.Equal(Right, firstLeg.ScreenFrame);
        Assert.Equal(Right.Left, firstLeg.EndScreenLocation.X);

        // A second request while flying is ignored.
        model.BeginCursorFlight(CursorFlightPurpose.ToMouse, Primary);
        Assert.Same(firstLeg, model.CursorFlightLeg);

        model.ContinueCursorFlight(firstLeg);
        var finalLeg = Assert.IsType<CursorFlightLeg>(model.CursorFlightLeg);
        Assert.True(finalLeg.IsFinalLeg);
        Assert.Equal(Primary, finalLeg.ScreenFrame);
        Assert.Equal(DockPoint, finalLeg.EndScreenLocation);
        Assert.NotEqual(firstLeg.Sequence, finalLeg.Sequence);

        // Only the overlay animating the leg can finish it.
        model.FinishCursorFlight(Right);
        Assert.NotNull(model.CursorFlightLeg);
        model.FinishCursorFlight(Primary);
        Assert.Null(model.CursorFlightLeg);
    }

    [Fact]
    public void AFlightBackToTheMouseOnTheSameDisplayIsOneLeg()
    {
        var (model, _, _) = Make();
        model.BeginCursorFlight(CursorFlightPurpose.ToMouse, Right);
        Assert.True(model.CursorFlightLeg!.IsFinalLeg);
        Assert.Equal(new ScreenPoint(4000, 500), model.CursorFlightLeg.EndScreenLocation);
    }

    [Fact]
    public void TheCaptionTypesOutHoldsThenFades()
    {
        var (model, clock, _) = Make();
        model.StreamCursorCaption("hi there", holdSeconds: 2);
        Assert.True(model.IsCursorCaptionVisible);
        Assert.Equal("", model.CursorCaptionText);
        clock.Advance(0.03);
        Assert.Equal("h", model.CursorCaptionText);
        clock.Advance(0.03 * 7);
        Assert.Equal("hi there", model.CursorCaptionText);
        clock.Advance(1.9);
        Assert.Equal(1.0, model.CursorCaptionOpacity);
        clock.Advance(0.2);
        Assert.Equal(0.0, model.CursorCaptionOpacity);
        Assert.True(model.IsCursorCaptionVisible);
        clock.Advance(0.35);
        Assert.False(model.IsCursorCaptionVisible);
        Assert.Equal("", model.CursorCaptionText);
    }

    /// <summary>Captions of the spoken answer: each piece shows whole, the last stays a moment after the voice, then fades.</summary>
    [Fact]
    public void ASpokenCaptionShowsWholeAndFadesAfterTheVoice()
    {
        var (model, clock, _) = Make();
        model.ShowSpokenCaption("the save button is at the top.");
        Assert.Equal("the save button is at the top.", model.CursorCaptionText);
        Assert.True(model.IsShowingSpokenCaption);
        clock.Advance(30);
        Assert.Equal(1.0, model.CursorCaptionOpacity);

        model.ShowSpokenCaption("click it to save.");
        model.FadeSpokenCaptionAfter(2.5);
        clock.Advance(2.4);
        Assert.Equal("click it to save.", model.CursorCaptionText);
        Assert.Equal(1.0, model.CursorCaptionOpacity);
        clock.Advance(0.2);
        Assert.Equal(0.0, model.CursorCaptionOpacity);
        clock.Advance(0.35);
        Assert.False(model.IsCursorCaptionVisible);
        Assert.False(model.IsShowingSpokenCaption);
    }

    [Fact]
    public void TheNextPieceCancelsTheFade()
    {
        var (model, clock, _) = Make();
        model.ShowSpokenCaption("first.");
        model.FadeSpokenCaptionAfter(1);
        clock.Advance(0.5);
        model.ShowSpokenCaption("second.");
        clock.Advance(5);
        Assert.Equal("second.", model.CursorCaptionText);
        Assert.Equal(1.0, model.CursorCaptionOpacity);
    }

    /// <summary>Stopping the answer takes its caption away; a caption that isn't the answer's ("listening…") stays.</summary>
    [Fact]
    public void StoppingTheAnswerTakesOnlyItsOwnCaptionAway()
    {
        var (model, clock, _) = Make();
        model.ShowSpokenCaption("the answer.");
        model.DismissSpokenCaption();
        Assert.Equal(0.0, model.CursorCaptionOpacity);

        model.ShowLiveCaption("listening…");
        Assert.False(model.IsShowingSpokenCaption);
        model.DismissSpokenCaption();
        model.FadeSpokenCaptionAfter(1);
        clock.Advance(5);
        Assert.Equal("listening…", model.CursorCaptionText);
        Assert.Equal(1.0, model.CursorCaptionOpacity);
    }

    [Fact]
    public void ANewCaptionReplacesOneStillTyping()
    {
        var (model, clock, _) = Make();
        model.StreamCursorCaption("first caption", holdSeconds: 1);
        clock.Advance(0.06);
        model.StreamCursorCaption("second", holdSeconds: 1);
        clock.Advance(0.03 * 6);
        Assert.Equal("second", model.CursorCaptionText);
    }

    [Fact]
    public void TransientModeFadesTheOverlayOutASecondAfterEverythingEnds()
    {
        var (model, clock, _) = Make();
        model.SetClickyCursorEnabled(false);
        var isSpeaking = true;
        model.IsAnswerAudioPlaying = () => isSpeaking;
        model.ShowOverlayOnAllScreens();
        var fades = new List<bool>();
        model.OverlayHideRequested += fade => fades.Add(fade);
        model.ScheduleTransientHideIfNeeded();
        clock.Advance(5);
        Assert.Empty(fades);
        isSpeaking = false;
        clock.Advance(0.2);
        clock.Advance(0.9);
        Assert.Empty(fades);
        clock.Advance(0.2);
        Assert.Equal(new[] { true }, fades);
        Assert.False(model.IsOverlayVisible);
    }

    [Fact]
    public void ANewInteractionCancelsTheTransientHide()
    {
        var (model, clock, _) = Make();
        model.SetClickyCursorEnabled(false);
        model.ShowOverlayOnAllScreens();
        var fades = 0;
        model.OverlayHideRequested += _ => fades++;
        model.ScheduleTransientHideIfNeeded();
        clock.Advance(0.5);
        model.CancelTransientHide();
        clock.Advance(3);
        Assert.Equal(0, fades);
        Assert.True(model.IsOverlayVisible);
    }

    [Fact]
    public void TheSettingsTourKeepsThePointerOnTargetAndClicks()
    {
        var (model, _, _) = Make();
        model.BeginSettingsTourPointing();
        Assert.True(model.PointerStaysOnTarget);
        model.PointForSettingsTour(new ScreenPoint(3100, 40), "general");
        Assert.Equal(Right, model.DetectedElementDisplayFrame);
        model.ShowPointerClick();
        Assert.Equal(1, model.PointerClickToken);
        model.EndSettingsTourPointing();
        Assert.False(model.PointerStaysOnTarget);
        Assert.Equal(2, model.DetectedElementPointToken);
        Assert.Equal("general", model.DetectedElementBubbleText);
    }
}

public class TypewriterTests
{
    [Fact]
    public void TextElementsKeepEmojiAndAccentsWhole()
    {
        Assert.Equal(new List<string> { "a", "👍🏽", "é" }, Typewriter.TextElements("a👍🏽é"));
    }

    [Fact]
    public async Task TypingShowsEveryBeginningUpToTheWholeMessage()
    {
        var shown = new List<string>();
        await Typewriter.TypeAsync("abc", shown.Add, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("abc", shown[^1]);
        Assert.Equal("a", shown[0]);
    }
}
