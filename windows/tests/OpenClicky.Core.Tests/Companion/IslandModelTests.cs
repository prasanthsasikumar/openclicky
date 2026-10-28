using OpenClicky.Core.Companion;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Tests.Companion;

public class IslandModelTests
{
    private static readonly IslandGeometry Geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1512, 982), new ScreenRect(0, 0, 1512, 934));

    private static (IslandModel Model, ManualDelays Clock) Make()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var clock = new ManualDelays();
        return (new IslandModel(Geometry, HUDPlacement.Notch, clock.Delay), clock);
    }

    private static PermissionPrompt MicrophonePrompt => new(PermissionStep.Microphone, new PermissionStatus());

    private static FirstRunTourCard TourCard(string? status = null) =>
        FirstRunTourCards.Make(FirstRunTourStep.Talk, new FirstRunTourCardInputs(DictationHotkey.RightControl, CanAsk: true, UsesAgent: false, Status: status));

    [Fact]
    public void HoveringPeeksAtOnceAndLeavingClosesItAfterTheGrace()
    {
        var (model, clock) = Make();
        var layoutChanges = 0;
        model.LayoutChanged += () => layoutChanges++;
        model.SetHovering(true);
        Assert.Equal(IslandExpansion.Peek, model.Expansion);
        Assert.Equal(1, layoutChanges);
        model.SetHovering(false);
        clock.Advance(0.39);
        Assert.Equal(IslandExpansion.Peek, model.Expansion);
        clock.Advance(0.02);
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
    }

    /// <summary>Hovering only teases; a click on the peek opens the panel, and a second click goes back to the peek.</summary>
    [Fact]
    public void AClickOnThePeekOpensThePanel()
    {
        var (model, _) = Make();
        model.SetHovering(true);
        model.TogglePinned();
        Assert.Equal(IslandExpansion.Full, model.Expansion);
        model.TogglePinned();
        Assert.Equal(IslandExpansion.Peek, model.Expansion);
    }

    [Fact]
    public void SkimmingTheEdgeDoesNotFlicker()
    {
        var (model, clock) = Make();
        model.SetHovering(true);
        model.SetHovering(false);
        clock.Advance(0.2);
        model.SetHovering(true);
        clock.Advance(1);
        Assert.Equal(IslandExpansion.Peek, model.Expansion);
    }

    [Fact]
    public void AClickPinsThePanelOpenUntilClosed()
    {
        var (model, clock) = Make();
        model.TogglePinned();
        Assert.Equal(IslandExpansion.Full, model.Expansion);
        model.SetHovering(true);
        model.SetHovering(false);
        clock.Advance(1);
        Assert.Equal(IslandExpansion.Full, model.Expansion);
        model.Close();
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
    }

    [Fact]
    public void BusyShowsTheStripAndTheCaptionWaitsForIt()
    {
        var (model, clock) = Make();
        model.SetBusy(true);
        clock.Advance(0.5);
        Assert.Equal(IslandExpansion.Compact, model.Expansion);
        model.SetCaption("hi, i'm openclicky");
        Assert.False(model.IsShowingCaption);
        model.SetBusy(false);
        Assert.True(model.IsShowingCaption);
        clock.Advance(0.5);
        Assert.Equal(IslandExpansion.Compact, model.Expansion);
        Assert.Equal(70 + Geometry.NotchHeight, model.Layout.Height);
        model.SetCaption(null);
        clock.Advance(0.5);
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
    }

    [Fact]
    public void ThePermissionCardOutranksEverythingButTheComposer()
    {
        var (model, _) = Make();
        model.TogglePinned();
        model.SetPermissionPrompt(MicrophonePrompt);
        Assert.Equal(IslandExpansion.Permission, model.Expansion);
        // Setup in progress: a click does not pin the panel over the card.
        model.TogglePinned();
        model.SetHovering(true);
        Assert.Equal(IslandExpansion.Permission, model.Expansion);
        model.OpenComposer();
        Assert.Equal(IslandExpansion.Composer, model.Expansion);
        model.CloseComposer();
        Assert.Equal(IslandExpansion.Permission, model.Expansion);
        model.SetPermissionPrompt(null);
        Assert.NotEqual(IslandExpansion.Permission, model.Expansion);
    }

    [Fact]
    public void TheTourCardYieldsToTheBusyStripAndComesBack()
    {
        var (model, clock) = Make();
        model.SetTourCard(TourCard());
        Assert.Equal(IslandExpansion.Tour, model.Expansion);
        model.SetBusy(true);
        clock.Advance(0.5);
        Assert.Equal(IslandExpansion.Compact, model.Expansion);
        model.SetBusy(false);
        Assert.Equal(IslandExpansion.Tour, model.Expansion);
        var heightWithoutStatus = model.Layout.Height;
        model.SetTourCard(TourCard("✓ that’s asking clicky."));
        Assert.Equal(heightWithoutStatus + 38, model.Layout.Height);
        // The tour's end settles slowly, once.
        model.SetTourCard(null);
        Assert.True(model.IsSettlingGracefully);
        clock.Advance(1.9);
        Assert.False(model.IsSettlingGracefully);
    }

    [Fact]
    public void DismissKeepsThePanelClosedUntilThePointerLeaves()
    {
        var (model, _) = Make();
        model.SetHovering(true);
        model.Dismiss();
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
        model.SetHovering(true);
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
        model.SetHovering(false);
        model.SetHovering(true);
        Assert.Equal(IslandExpansion.Peek, model.Expansion);
    }

    [Fact]
    public void SpeakingKeepsTheStripUnderTheHoveringPointer()
    {
        var (model, clock) = Make();
        model.SetBusy(true);
        model.SetSpeaking(true);
        model.SetHovering(true);
        clock.Advance(0.5);
        Assert.Equal(IslandExpansion.Compact, model.Expansion);
    }

    [Fact]
    public void TheGoodbyeTakesTheIslandOver()
    {
        var (model, _) = Make();
        model.TogglePinned();
        model.BeginGoodbye();
        Assert.Equal(IslandExpansion.Compact, model.Expansion);
        model.SetHovering(true);
        Assert.Equal(IslandExpansion.Compact, model.Expansion);
    }

    [Fact]
    public void TheGoodbyeCaptionResizesTheStripItTakesOver()
    {
        var (model, _) = Make();
        model.BeginGoodbye();
        var changes = 0;
        model.LayoutChanged += () => changes++;
        model.SetCaption("");
        Assert.Equal(1, changes);
        Assert.True(model.IsShowingCaption);
        Assert.Equal(400, model.Layout.Width);
        Assert.Equal(70 + Geometry.NotchHeight, model.Layout.Height);
    }

    [Fact]
    public void TheConnectCardStaysWhileHovered()
    {
        var (model, _) = Make();
        model.SetConnectPrompt(new AppConnectPrompt("gmail", "Gmail", "gmail", null, new[] { "Compose" }));
        model.SetHovering(true);
        Assert.Equal(IslandExpansion.Connect, model.Expansion);
        Assert.True(model.ContentState.ConnectHasExamplePrompts);
        // The same words are the same card: no reconcile, no new layout.
        var changes = 0;
        model.LayoutChanged += () => changes++;
        model.SetConnectPrompt(new AppConnectPrompt("gmail", "Gmail", "gmail", null, new[] { "Compose" }));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void HiddenAndIdleTheIslandIsNothing()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var clock = new ManualDelays();
        var model = new IslandModel(Geometry, HUDPlacement.Hidden, clock.Delay);
        Assert.True(model.IsInvisible);
        model.SetBusy(true);
        clock.Advance(0.5);
        Assert.False(model.IsInvisible);
    }
}

public class IslandCompanionModelTests
{
    private static IslandCompanionModel Make(ManualDelays? clock = null)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var delays = clock ?? new ManualDelays();
        return new IslandCompanionModel(delays.Delay, () => delays.Now.TotalSeconds);
    }

    [Fact]
    public void TheComposerSendsTrimmedTextAndClosesEitherWay()
    {
        var hub = Make();
        var sent = new List<string>();
        var closes = 0;
        hub.ComposerSubmitted += sent.Add;
        hub.CloseComposerRequested += () => closes++;
        hub.SubmitComposerCommand.Execute("  what's this?  ");
        hub.SubmitComposerCommand.Execute("   ");
        Assert.Equal(new[] { "what's this?" }, sent);
        Assert.Equal(2, closes);
    }

    [Fact]
    public void TheDockButtonWorksOnlyWhileIdle()
    {
        var hub = Make();
        var requests = new List<bool>();
        hub.DockToggleRequested += requests.Add;
        hub.ToggleDockCommand.Execute(null);
        hub.IsCursorDocked = true;
        hub.ToggleDockCommand.Execute(null);
        hub.VoiceState = CompanionVoiceState.Listening;
        hub.ToggleDockCommand.Execute(null);
        Assert.Equal(new[] { true, false }, requests);
    }

    [Fact]
    public void DerivedWordsFollowTheState()
    {
        var hub = Make();
        var changed = new List<string?>();
        hub.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);
        hub.VoiceState = CompanionVoiceState.Responding;
        hub.LastTranscript = "where is the save button";
        Assert.Contains(nameof(IslandCompanionModel.CompactStatusText), changed);
        Assert.Contains(nameof(IslandCompanionModel.DetailLine), changed);
        Assert.Equal("speaking", hub.CompactStatusText);
        Assert.Equal("“where is the save button”", hub.DetailLine);
        Assert.True(hub.IsBusy);
        Assert.True(hub.IsSpeaking);
    }

    [Fact]
    public void TheCaptionTypesIntoTheIslandsAndClears()
    {
        var clock = new ManualDelays();
        var hub = Make(clock);
        hub.ShowCaption("bye", holdSeconds: 1);
        Assert.Equal("", hub.CaptionText);
        clock.Advance(0.09);
        Assert.Equal("bye", hub.CaptionText);
        clock.Advance(1.01);
        Assert.Null(hub.CaptionText);
        hub.ShowGoodbye("see you");
        Assert.True(hub.IsSayingGoodbye);
    }
}
