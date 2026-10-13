// Port of ScreenTextLocatorTests.swift. Not ported: recognizesSmallButtonCaptionInARenderedScreenshot — it renders a
// caption with AppKit and runs Vision OCR; the Windows recogniser (Windows.Media.Ocr) is OS code in
// OpenClicky.Windows and is not reachable from Core tests.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class ScreenTextLocatorTests
{
    private static ScreenTextLine Line(string text, double x, double y, double width, double height = 14) =>
        new(text, new ScreenRect(x, y, width, height));

    [Fact]
    public void ClearlyNearestMatchWins()
    {
        var lines = new[] { Line("Star", 800, 500, 30), Line("Star", 800, 1400, 30) };
        var match = ScreenTextLocator.Locate("star", new ScreenPoint(700, 520), lines, 1000, 128);
        Assert.Equal(new ScreenPoint(815, 507), match?.Center);
    }

    [Fact]
    public void MatchesAtSimilarDistancesAreLeftUnresolved()
    {
        var lines = new[] { Line("Star", 800, 500, 30), Line("Star", 800, 600, 30) };
        Assert.Null(ScreenTextLocator.Locate("star", new ScreenPoint(700, 590), lines, 300, 128));
    }

    [Fact]
    public void ASecondMatchOutsideTheRadiusDoesNotMakeItAmbiguous()
    {
        var lines = new[] { Line("Edit", 400, 300, 30), Line("Edit", 400, 360, 30) };
        var match = ScreenTextLocator.Locate("Edit", new ScreenPoint(395, 305), lines, 40, 128);
        Assert.Equal(307, match?.Center.Y);
    }

    [Fact]
    public void SubstringInsideNoisyOcrLineUsesTheSubRangeBox()
    {
        // The button glyph gets read as a letter ("HNew"); the caption is still inside the line, and the recogniser's
        // per-range box wins over the whole-line box.
        var noisy = new ScreenTextLine("HNew", new ScreenRect(217, 271, 40, 16),
            (start, length) => start == 1 ? new ScreenRect(227, 271, 30, 16) : null);
        var match = ScreenTextLocator.Locate("New", new ScreenPoint(160, 250), new[] { noisy }, 300, 128);
        Assert.Equal(new ScreenPoint(242, 279), match?.Center);
    }

    [Fact]
    public void SubstringWithoutRangeBoxIsPlacedProportionally()
    {
        // "New" is the last three of twenty characters: its box is the right 3/20 of the line.
        var lines = new[] { Line("Top repositories New", 100, 200, 200) };
        var match = ScreenTextLocator.Locate("new", new ScreenPoint(150, 200), lines, 300, 128);
        Assert.NotNull(match);
        Assert.True(Math.Abs(match!.Center.X - 285) < 1);
        Assert.Equal(207, match.Center.Y);
    }

    [Fact]
    public void NearMissesAreNotSnappedTo()
    {
        var lines = new[] { Line("reditt", 1310, 8, 40, 12), Line("Home", 317, 225, 58, 21) };
        Assert.Null(ScreenTextLocator.Locate("reddit", new ScreenPoint(1300, 20), lines, 300, 128));
        Assert.Null(ScreenTextLocator.Locate("New", new ScreenPoint(160, 250), new[] { Line("Now", 217, 271, 41) }, 300, 128));
    }

    [Fact]
    public void ExactMatchIsFoundEvenWhenANearMissIsCloser()
    {
        var lines = new[] { Line("Now", 100, 100, 30), Line("New", 400, 100, 30) };
        var match = ScreenTextLocator.Locate("New", new ScreenPoint(110, 100), lines, 1000, 128);
        Assert.Equal("New", match?.Text);
    }

    [Fact]
    public void DescriptiveHintFallsBackToItsDistinctiveWord()
    {
        var lines = new[] { Line("New", 217, 271, 40, 16), Line("Button styles", 700, 271, 90) };
        var match = ScreenTextLocator.Locate("new repo button", new ScreenPoint(160, 250), lines, 300, 128);
        Assert.Equal("New", match?.Text);
        Assert.Null(ScreenTextLocator.Locate("button", new ScreenPoint(700, 271), lines, 300, 128));
    }

    [Fact]
    public void FarAwayMatchesAndEmptyHintsAreIgnored()
    {
        var lines = new[] { Line("New", 1200, 800, 30) };
        Assert.Null(ScreenTextLocator.Locate("New", new ScreenPoint(100, 100), lines, 300, 128));
        Assert.Null(ScreenTextLocator.Locate("   ", new ScreenPoint(1200, 800), lines, 300, 128));
        Assert.Null(ScreenTextLocator.Locate("Ne", new ScreenPoint(1200, 800), lines, 300, 128));
    }

    [Fact]
    public void MatchingIgnoresCaseDiacriticsAndPunctuation()
    {
        var lines = new[] { Line("Café menu", 100, 100, 90) };
        Assert.NotNull(ScreenTextLocator.Locate("\"cafe\"", new ScreenPoint(110, 100), lines, 300, 128));
    }
}
