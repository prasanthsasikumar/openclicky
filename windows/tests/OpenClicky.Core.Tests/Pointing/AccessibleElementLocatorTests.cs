// Port of AccessibleElementLocatorTests.swift, with UI Automation control types for roles.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class AccessibleElementLocatorTests
{
    private static AccessibleElementCandidate Candidate(string title, double x, double y, string role = "Button", params string[] rows) =>
        new(role, title, rows, new ScreenPoint(x, y));

    /// <summary>A settings page listing three repositories, each row carrying its own "Edit" button.</summary>
    private static AccessibleElementCandidate[] RepositoryRows => new[]
    {
        Candidate("Edit", 900, 300, "Button", "clicky-experiments", "Your repositories"),
        Candidate("Edit", 900, 420, "Button", "openclicky", "Your repositories"),
        Candidate("Edit", 900, 540, "Button", "dotfiles", "Your repositories"),
    };

    [Fact]
    public void TheRowNamedInTheRequestWinsOverTheNearerRow()
    {
        var match = AccessibleElementLocator.BestMatch("Edit", "how do I edit the openclicky repository",
            new ScreenPoint(880, 320), RepositoryRows, 400, 128);
        Assert.Equal("openclicky", match?.ContainerTitles[0]);
    }

    [Fact]
    public void WithoutDistinguishingContextSimilarCandidatesStayUnresolved()
    {
        var match = AccessibleElementLocator.BestMatch("Edit", "point at the edit button", new ScreenPoint(880, 320), RepositoryRows, 400, 128);
        Assert.Null(match);
    }

    [Fact]
    public void DistanceStillDecidesWhenTheCandidatesAreFarApart()
    {
        var farApart = new[] { RepositoryRows[0], Candidate("Edit", 900, 1400, "Button", "dotfiles") };
        var match = AccessibleElementLocator.BestMatch("Edit", null, new ScreenPoint(880, 300), farApart, 2000, 128);
        Assert.Equal("clicky-experiments", match?.ContainerTitles[0]);
    }

    [Theory]
    [InlineData("Text", "Button")]
    [InlineData("AXStaticText", "AXButton")]
    public void AnActionableControlBeatsAStaticLabelWithTheSameCaption(string labelRole, string buttonRole)
    {
        var labelAndButton = new[] { Candidate("Edit", 505, 300, labelRole), Candidate("Edit", 560, 300, buttonRole) };
        var match = AccessibleElementLocator.BestMatch("Edit", null, new ScreenPoint(500, 300), labelAndButton, 400, 128);
        Assert.Equal(buttonRole, match?.Role);
    }

    [Fact]
    public void CaptionsThatAreNotOnScreenAndGenericWordsMatchNothing()
    {
        Assert.Null(AccessibleElementLocator.BestMatch("Export", null, new ScreenPoint(880, 320), RepositoryRows, 400, 128));
        Assert.Null(AccessibleElementLocator.BestMatch("button", null, new ScreenPoint(880, 320), new[] { Candidate("button", 900, 300) }, 400, 128));
    }

    [Fact]
    public void CandidatesBeyondTheRadiusAreIgnored()
    {
        Assert.Null(AccessibleElementLocator.BestMatch("Edit", null, new ScreenPoint(100, 100), new[] { RepositoryRows[1] }, 300, 128));
    }

    [Fact]
    public void ChildrenInheritTheThreeNearestCaptionedContainers()
    {
        var titles = AccessibleElementLocator.ChildContainerTitles("Group", "row", new[] { "a", "b", "c" });
        Assert.Equal(new[] { "row", "a", "b" }, titles);
        Assert.Equal(new[] { "a" }, AccessibleElementLocator.ChildContainerTitles("Button", "Edit", new[] { "a" }));
        Assert.Equal(new[] { "a" }, AccessibleElementLocator.ChildContainerTitles("Group", "", new[] { "a" }));
    }
}
