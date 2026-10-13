// Ports of PointingTests.swift (screenshot pixel → global point; the Mac's y-flip is dropped because Windows global
// space is y-down), OverlayScreenLayoutTests.swift, and SpokenChunkerTests.swift's PointTagPositionTests plus the
// tag case of AskLaneFailureTests (CompanionManager.parsePointingCoordinates → PointTagParser).

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class PointingTests
{
    private static ScreenCaptureGeometry Capture(double originX = 0, double originY = 0) =>
        new(new ScreenRect(originX, originY, 2560, 1600), 1280, 800);

    [Fact]
    public void ScalesScreenshotPixelsToDisplayUnits()
    {
        Assert.Equal(new ScreenPoint(1280, 800), Capture().ScreenLocation(new ScreenPoint(640, 400)));
    }

    [Fact]
    public void KeepsTheTopLeftOriginWithoutAFlip()
    {
        // The Mac flipped to AppKit's bottom-left origin; Windows is top-left like the screenshot.
        Assert.Equal(new ScreenPoint(0, 0), Capture().ScreenLocation(ScreenPoint.Zero));
        Assert.Equal(new ScreenPoint(2560, 1600), Capture().ScreenLocation(new ScreenPoint(1280, 800)));
    }

    [Fact]
    public void ClampsOutOfRangeCoordinates()
    {
        Assert.Equal(new ScreenPoint(2560, 0), Capture().ScreenLocation(new ScreenPoint(99999, -5)));
    }

    [Fact]
    public void NonFiniteCoordinatesDoNotTrap()
    {
        Assert.Equal(new ScreenPoint(0, 0), Capture().ScreenLocation(new ScreenPoint(double.NaN, double.NaN)));
        Assert.Equal(new ScreenPoint(2560, 0), Capture().ScreenLocation(new ScreenPoint(double.PositiveInfinity, double.NegativeInfinity)));
    }

    [Fact]
    public void OffsetsBySecondaryDisplayOrigin()
    {
        Assert.Equal(new ScreenPoint(3840, 500), Capture(2560, -300).ScreenLocation(new ScreenPoint(640, 400)));
    }

    [Fact]
    public void ScreenshotPointIsTheInverseOfScreenLocation()
    {
        var capture = Capture(2560, -300);
        Assert.Equal(new ScreenPoint(640, 400), capture.ScreenshotPoint(capture.ScreenLocation(new ScreenPoint(640, 400))));
    }
}

public class OverlayScreenLayoutTests
{
    private static readonly ScreenRect BuiltIn = new(0, 0, 1512, 982);
    private static readonly ScreenRect ExternalAbove = new(-964, -1440, 3440, 1440);
    private static readonly ScreenRect ExternalLeft = new(-3440, 0, 3440, 1440);

    [Fact]
    public void SameScreensNeedNoRebuild() =>
        Assert.False(OverlayScreenLayout.ScreensChanged(new[] { BuiltIn, ExternalAbove }, new[] { BuiltIn, ExternalAbove }));

    [Fact]
    public void OrderDoesNotMatter() =>
        Assert.False(OverlayScreenLayout.ScreensChanged(new[] { BuiltIn, ExternalAbove }, new[] { ExternalAbove, BuiltIn }));

    [Fact]
    public void AMovedDisplayNeedsARebuild() =>
        Assert.True(OverlayScreenLayout.ScreensChanged(new[] { BuiltIn, ExternalAbove }, new[] { BuiltIn, ExternalLeft }));

    [Fact]
    public void APluggedInDisplayNeedsARebuild() =>
        Assert.True(OverlayScreenLayout.ScreensChanged(new[] { BuiltIn }, new[] { BuiltIn, ExternalAbove }));

    [Fact]
    public void AnUnpluggedDisplayNeedsARebuild() =>
        Assert.True(OverlayScreenLayout.ScreensChanged(new[] { BuiltIn, ExternalAbove }, new[] { BuiltIn }));
}

public class PointTagPositionTests
{
    [Fact]
    public void ALeadingTagIsReadLikeATrailingOne()
    {
        var leading = PointTagParser.ParsePointingCoordinates("[POINT:120,40:code] that's your sign-in code.");
        Assert.Equal(new ScreenPoint(120, 40), leading.Coordinate);
        Assert.Equal("code", leading.ElementLabel);
        Assert.Equal("that's your sign-in code.", leading.SpokenText);
        var trailing = PointTagParser.ParsePointingCoordinates("that's your sign-in code. [POINT:120,40:code]");
        Assert.Equal(leading.Coordinate, trailing.Coordinate);
        Assert.Equal(leading.SpokenText, trailing.SpokenText);
        Assert.Equal("html is markup.", PointTagParser.ParsePointingCoordinates("[POINT:none] html is markup.").SpokenText);
    }

    [Fact]
    public void ATagOnItsOwnIsRead()
    {
        var tagAlone = PointTagParser.ParsePointingCoordinates("[POINT:120,40:code]");
        Assert.Equal(new ScreenPoint(120, 40), tagAlone.Coordinate);
        Assert.Equal("code", tagAlone.ElementLabel);
        Assert.Equal("", tagAlone.SpokenText);
        Assert.Null(PointTagParser.ParsePointingCoordinates("  [POINT:none]  ").Coordinate);
    }

    [Fact]
    public void TwoTagsWithNothingElseDoNotRecurse()
    {
        var pointThenNone = PointTagParser.ParsePointingCoordinates("[POINT:120,40:code] [POINT:none]");
        Assert.Equal(new ScreenPoint(120, 40), pointThenNone.Coordinate);
        Assert.Equal("code", pointThenNone.ElementLabel);
        Assert.Equal("", pointThenNone.SpokenText);

        var noneThenPoint = PointTagParser.ParsePointingCoordinates("[POINT:none] [POINT:10,20:x]");
        Assert.Null(noneThenPoint.Coordinate);
        Assert.Equal("none", noneThenPoint.ElementLabel);
        Assert.Equal("", noneThenPoint.SpokenText);

        var twoPoints = PointTagParser.ParsePointingCoordinates("[POINT:10,20:x]\n[POINT:30,40:y]");
        Assert.Equal(new ScreenPoint(10, 20), twoPoints.Coordinate);
        Assert.Equal("", twoPoints.SpokenText);

        var twoNones = PointTagParser.ParsePointingCoordinates("[POINT:none] [POINT:none]");
        Assert.Null(twoNones.Coordinate);
        Assert.Equal("", twoNones.SpokenText);
    }

    [Fact]
    public void ALeadingAndATrailingTagAroundTextKeepTheTextAndTheFirstTag()
    {
        var both = PointTagParser.ParsePointingCoordinates("[POINT:10,20:save] click save. [POINT:30,40:other]");
        Assert.Equal(new ScreenPoint(10, 20), both.Coordinate);
        Assert.Equal("click save.", both.SpokenText);
    }

    [Fact]
    public void ATagInTheMiddleOrAnUnclosedTagIsLeftAsText()
    {
        var middle = PointTagParser.ParsePointingCoordinates("click [POINT:10,20:x] there.");
        Assert.Null(middle.Coordinate);
        Assert.Equal("click [POINT:10,20:x] there.", middle.SpokenText);
        var unclosed = PointTagParser.ParsePointingCoordinates("[POINT:10,20:x");
        Assert.Null(unclosed.Coordinate);
        Assert.Equal("[POINT:10,20:x", unclosed.SpokenText);
    }

    [Fact]
    public void TheScreenNumberIsRead()
    {
        var other = PointTagParser.ParsePointingCoordinates("[POINT:400,300:terminal:screen2] that's over on your other monitor.");
        Assert.Equal(new ScreenPoint(400, 300), other.Coordinate);
        Assert.Equal("terminal", other.ElementLabel);
        Assert.Equal(2, other.ScreenNumber);
        Assert.Equal("that's over on your other monitor.", other.SpokenText);
    }

    /// <summary>From AskLaneFailureTests: an answer that is only a tag has nothing to speak.</summary>
    [Fact]
    public void AnAnswerThatIsOnlyATagHasNothingToSpeak()
    {
        Assert.Equal("", PointTagParser.ParsePointingCoordinates("[POINT:none]").SpokenText);
    }
}
