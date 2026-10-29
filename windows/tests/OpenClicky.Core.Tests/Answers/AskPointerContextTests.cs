// Port of macos/OpenClicky/OpenClickyTests/AskPointerContextTests.swift
//
// "What is this?" with the mouse on something: where the pointer is in the screenshot the model
// sees, the close-up around it, and the second ask of the same question in a row. The JPEG crop is
// the platform's; here the square it cuts is checked.

using OpenClicky.Core.Answers;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Tests.Answers;

public class AskPointerContextTests
{
    private static CompanionScreenCapture Capture(string label, PixelRect frame, double scale = 2, bool isCursorScreen = true) =>
        new([], "image/jpeg", label, isCursorScreen, frame.Width, frame.Height, frame, 1, @"\\.\DISPLAY1",
            (int)(frame.Width * scale), (int)(frame.Height * scale));

    [Fact]
    public void ThePointerIsPlacedInTheScreenshotItIsOnFromTheTopLeft()
    {
        var laptop = Capture("screen1", new PixelRect(0, 0, 1512, 982));
        var external = Capture("screen2", new PixelRect(1512, 0, 2560, 1440), scale: 1, isCursorScreen: false);
        // 100 px in from the left, 100 px down from the top of the laptop screen (Windows' y goes down).
        Assert.Equal(new PointerInCapture(200, 200, "screen1"), PointerInCapture.Locate(new PixelPoint(100, 100), [laptop, external]));
        Assert.Equal(new PointerInCapture(100, 40, "screen2"), PointerInCapture.Locate(new PixelPoint(1612, 40), [laptop, external]));
        Assert.Null(PointerInCapture.Locate(new PixelPoint(-5, 10), [laptop, external]));
    }

    /// <summary>A screenshot scaled down to 1280 wide: the pointer is placed in the image the model sees.</summary>
    [Fact]
    public void ThePointerFollowsADownscaledScreenshot()
    {
        var monitor = Capture("screen1", new PixelRect(-2560, 0, 2560, 1440), scale: 0.5);
        Assert.Equal(new PointerInCapture(640, 360, "screen1"), PointerInCapture.Locate(new PixelPoint(-1280, 720), [monitor]));
    }

    [Fact]
    public void ThePromptLineSaysWhereThePointerIsAndWhatThisMeans()
    {
        var line = new PointerInCapture(200, 200, "screen1").PromptLine;
        Assert.Contains("(200, 200)", line);
        Assert.Contains("screen1", line);
        Assert.Contains("under the pointer", line.ToLowerInvariant());
    }

    [Fact]
    public void ACloseUpIsCutAroundThePointerAndKeptInsideTheImage()
    {
        var pointer = new PointerInCapture(300, 200, "s");
        Assert.Equal(new PixelRect(200, 100, 200, 200), PointerCloseUp.CropRect(pointer, 600, 400, sideInPixels: 200));
        // Near a corner it is pushed back inside, still a full square.
        Assert.Equal(new PixelRect(0, 200, 200, 200), PointerCloseUp.CropRect(new PointerInCapture(10, 390, "s"), 600, 400, sideInPixels: 200));
        // Never bigger than the image.
        Assert.Equal(new PixelRect(100, 0, 400, 400), PointerCloseUp.CropRect(pointer, 600, 400, sideInPixels: 480));
        Assert.Null(PointerCloseUp.CropRect(new PointerInCapture(1, 1, "s"), 0, 0, sideInPixels: 200));
    }

    [Fact]
    public void TheCloseUpIsLabelledAsNotForPointing() =>
        Assert.Contains("never use this image's coordinates", PointerCloseUp.Label());

    [Fact]
    public void TheSameQuestionAgainSoonAfterIsARepeat()
    {
        Assert.True(RepeatedQuestion.IsRepeat("what is this?", "What is this", 20));
        Assert.True(RepeatedQuestion.IsRepeat("what's this", "what is this", 20));
        Assert.True(RepeatedQuestion.IsRepeat("what is that", "what is this", 20));
        Assert.False(RepeatedQuestion.IsRepeat("open my mail", "what is this", 20));
        Assert.False(RepeatedQuestion.IsRepeat("what is this", "what is this", 400));
        Assert.False(RepeatedQuestion.IsRepeat("what is this", null, 5));
        Assert.False(RepeatedQuestion.IsRepeat("", "", 5));
    }

    [Fact]
    public void TheQuestionCarriesThePointerAndTheRepeatNotice()
    {
        var pointer = new PointerInCapture(40, 50, "screen1");
        var plain = AskLanePrompts.AskUserPrompt("what is this", null, pointer: pointer, isRepeatedQuestion: false);
        Assert.StartsWith("what is this", plain);
        Assert.Contains("(40, 50)", plain);
        Assert.DoesNotContain(RepeatedQuestion.PromptLine, plain);
        var again = AskLanePrompts.AskUserPrompt("what is this", null, pointer: pointer, isRepeatedQuestion: true);
        Assert.Contains(RepeatedQuestion.PromptLine, again);
        Assert.Equal("hi", AskLanePrompts.AskUserPrompt("hi", null));
        // Each line of context in its own brackets, the highlight first.
        var withSelection = AskLanePrompts.AskUserPrompt("what does this mean", null, "ephemeral", pointer);
        Assert.Equal("what does this mean\n\n[" + AskLanePrompts.SelectedTextContextLine("ephemeral") + "]\n[" + pointer.PromptLine + "]", withSelection);
    }
}
