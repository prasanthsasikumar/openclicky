// The update flow's rules (Core/Updates/UpdateFlow): the update window's steps and words, when the ready card shows after
// Later, and the note on the first start of a new version.

using OpenClicky.Core.Updates;

namespace OpenClicky.Core.Tests.Updates;

public class UpdateFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheWindowWalksDownloadInstallOpenThenDone()
    {
        var download = UpdateProgress.Downloading("0.12.3", 0.424);
        Assert.Equal(UpdateStep.Download, download.Step);
        Assert.Equal("Downloading… 42%", download.Detail);
        Assert.Contains("0.12.3", download.Title);
        Assert.False(download.HasDone(UpdateStep.Download));

        var install = UpdateProgress.Installing("0.12.3");
        Assert.Null(install.Fraction);
        Assert.True(install.HasDone(UpdateStep.Download));
        Assert.False(install.HasDone(UpdateStep.Install));

        var open = UpdateProgress.Opening("0.12.3");
        Assert.True(open.HasDone(UpdateStep.Install));
        Assert.False(open.HasDone(UpdateStep.Open));

        var done = UpdateProgress.Finished("0.12.3");
        Assert.All(UpdateFlow.Steps, step => Assert.True(done.HasDone(step.Step)));
        Assert.Equal(["Download", "Install", "Open"], UpdateFlow.Steps.Select(step => step.Name));
    }

    [Fact]
    public void ADownloadsShareIsKeptBetweenNoneAndAll()
    {
        Assert.Equal(0, UpdateProgress.Downloading("1.0", -0.5).Fraction);
        Assert.Equal(1, UpdateProgress.Downloading("1.0", 1.7).Fraction);
        Assert.Equal("Downloading… 100%", UpdateProgress.Downloading("1.0", 1.7).Detail);
    }

    [Fact]
    public void LaterKeepsTheCardAwayForADayForThatVersionOnly()
    {
        Assert.True(UpdateReadyCard.ShouldShow("0.12.3", null, null, Now));
        Assert.False(UpdateReadyCard.ShouldShow("0.12.3", "0.12.3", Now.AddHours(-3), Now));
        Assert.True(UpdateReadyCard.ShouldShow("0.12.3", "0.12.3", Now.AddDays(-1).AddMinutes(-1), Now));
        // A newer version is offered again at once.
        Assert.True(UpdateReadyCard.ShouldShow("0.12.4", "0.12.3", Now.AddHours(-3), Now));
        // A clock set back (Later "in the future") doesn't hide it for good.
        Assert.True(UpdateReadyCard.ShouldShow("0.12.3", "0.12.3", Now.AddDays(3), Now));
        Assert.Equal("OpenClicky 0.12.3 is ready", UpdateReadyCard.Title("0.12.3"));
    }

    [Theory]
    [InlineData("0.12.2", "0.12.3", "OpenClicky was updated to 0.12.3")]
    [InlineData("0.12.3", "0.12.3", null)]
    [InlineData(null, "0.12.3", null)]
    [InlineData("0.12.2", null, null)]
    [InlineData(" 0.12.3 ", "0.12.3", null)]
    public void TheFirstStartOfANewVersionSaysSoOnce(string? lastRun, string? current, string? message) =>
        Assert.Equal(message, UpdatedNotice.Message(lastRun, current));

    [Fact]
    public void ReleaseNotesAreTheVersionsReleasePage() =>
        Assert.Equal("https://github.com/x/openclicky/releases/tag/v0.12.3", UpdateFlow.ReleaseNotesUrl("https://github.com/x/openclicky/", "0.12.3"));
}
