// Marking part of the screen before asking: where the mark is in a screenshot, its close-up, how long it waits for
// its question, and what the question then says.

using OpenClicky.Core.Answers;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Companion;

public class ScreenMarkerTests
{
    private sealed class FakeSurface : IScreenMarkerSurface
    {
        public PixelRect? Drawn { get; set; }
        public int Hides { get; private set; }

        public void BeginMarking(Action<PixelRect?> done) => done(Drawn);

        public void HideMark() => Hides++;
    }

    // Two monitors side by side: the right one at 200% with a half-size screenshot.
    private static readonly CompanionScreenCapture Left = new([1], "image/jpeg", "screen 1 of 2", true, 1920, 1080,
        new PixelRect(0, 0, 1920, 1080), 1, "DISPLAY1", 1920, 1080);
    private static readonly CompanionScreenCapture Right = new([1], "image/jpeg", "screen 2 of 2", false, 1920, 1080,
        new PixelRect(1920, 0, 3840, 2160), 2, "DISPLAY2", 1920, 1080);

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMarkIsFoundInTheScreenshotOfItsMonitorInThatScreenshotsPixels()
    {
        var mark = new MarkedArea(new PixelRect(1920 + 400, 200, 800, 600), Now);
        var (area, capture) = mark.In([Left, Right])!.Value;
        Assert.Same(Right, capture);
        Assert.Equal(new PixelRect(200, 100, 400, 300), area);
    }

    [Fact]
    public void AMarkAcrossTwoMonitorsGoesWithTheOneHoldingMostOfIt()
    {
        var mark = new MarkedArea(new PixelRect(1800, 100, 400, 200), Now);
        var (area, capture) = mark.In([Left, Right])!.Value;
        Assert.Same(Right, capture);
        Assert.Equal(0, area.Left);
        Assert.Null(new MarkedArea(new PixelRect(-500, -500, 100, 100), Now).In([Left, Right]));
    }

    [Fact]
    public void TheCloseUpKeepsAMarginAndStaysInsideTheImage()
    {
        Assert.Equal(new PixelRect(76, 76, 448, 248), MarkedArea.CloseUpRect(new PixelRect(100, 100, 400, 200), 1920, 1080));
        Assert.Equal(new PixelRect(0, 0, 34, 34), MarkedArea.CloseUpRect(new PixelRect(0, 0, 10, 10), 1920, 1080));
        Assert.Equal(new PixelRect(1886, 1046, 34, 34), MarkedArea.CloseUpRect(new PixelRect(1910, 1070, 10, 10), 1920, 1080));
    }

    [Fact]
    public void AMarkWaitsForOneQuestionAndGoesOnceAnswered()
    {
        var surface = new FakeSurface { Drawn = new PixelRect(10, 10, 300, 120) };
        var marker = new ScreenMarker { Surface = surface };
        MarkedArea? marked = null;
        marker.Marked += mark => marked = mark;
        marker.Begin(() => Now);
        Assert.NotNull(marked);
        Assert.Same(marked, marker.ForQuestion(Now.AddMinutes(1)));
        marker.ClearIfCurrent(marked);
        Assert.Null(marker.Current);
        Assert.Equal(1, surface.Hides);
    }

    [Fact]
    public void AMarkLeftTooLongIsDropped()
    {
        var marker = new ScreenMarker { Surface = new FakeSurface { Drawn = new PixelRect(10, 10, 300, 120) } };
        marker.Begin(() => Now);
        Assert.Null(marker.ForQuestion(Now + MarkedArea.Lifetime + TimeSpan.FromSeconds(1)));
        Assert.Null(marker.Current);
    }

    [Fact]
    public void ADotOrAGiveUpIsNoMark()
    {
        var surface = new FakeSurface { Drawn = new PixelRect(10, 10, 4, 4) };
        var marker = new ScreenMarker { Surface = surface };
        var marks = 0;
        marker.Marked += _ => marks++;
        marker.Begin(() => Now);
        surface.Drawn = null;
        marker.Begin(() => Now);
        Assert.Equal(0, marks);
        Assert.Null(marker.Current);
    }

    [Fact]
    public void ANewerMarkIsNotTakenAwayWithTheOneAnswered()
    {
        var surface = new FakeSurface { Drawn = new PixelRect(10, 10, 300, 120) };
        var marker = new ScreenMarker { Surface = surface };
        marker.Begin(() => Now);
        var answered = marker.Current;
        surface.Drawn = new PixelRect(500, 500, 200, 200);
        marker.Begin(() => Now);
        marker.ClearIfCurrent(answered);
        Assert.Equal(new PixelRect(500, 500, 200, 200), marker.Current!.Bounds);
    }

    [Fact]
    public void TheQuestionSaysWhereTheMarkIs()
    {
        var line = MarkedArea.PromptLine(new PixelRect(200, 100, 400, 300), "screen 2 of 2");
        Assert.Contains("x 200 to 600, y 100 to 400", line);
        Assert.Contains("screen 2 of 2", line);
        var asked = AskLanePrompts.AskUserPrompt("how does this work?", null, markedAreaLine: line);
        Assert.StartsWith("how does this work?\n\n[Before asking, the user marked", asked);
        Assert.Contains("[" + line + "]", DrawStageLesson.UserPrompt("how does this work?", line));
        Assert.DoesNotContain("marked", DrawStageLesson.UserPrompt("how does this work?"));
    }

    [Fact]
    public void TheShortcutIsHeldOnlyWhileDrawnAnswersAreOnAndNotTurnedOff()
    {
        Assert.Equal(MarkerShortcut.CtrlAltM, MarkerShortcuts.Wanted(null, drawingsOn: true));
        Assert.Null(MarkerShortcuts.Wanted(null, drawingsOn: false));
        Assert.Equal(MarkerShortcut.CtrlAltShiftM, MarkerShortcuts.Wanted("ctrlAltShiftM", drawingsOn: true));
        Assert.Null(MarkerShortcuts.Wanted("off", drawingsOn: true));
        foreach (var (shortcut, _) in MarkerShortcuts.Choices) Assert.Equal(shortcut, MarkerShortcuts.From(MarkerShortcuts.RawValue(shortcut)));
    }
}
