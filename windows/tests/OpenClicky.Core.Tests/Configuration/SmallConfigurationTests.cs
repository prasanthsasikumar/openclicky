// Ports AppRegionTests.swift (the parts in Configuration), DeviceIdentityTests.swift, and covers
// ReplyLanguage, OpenClickyPaths and AppLog.

using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Tests.Configuration;

public sealed class AppRegionTests
{
    [Fact]
    public void AnIndianPcIsIndiaByItsRegionOrItsTimeZone()
    {
        Assert.Equal(AppRegion.India, AppRegions.Detected("IN", "Europe/London"));
        Assert.Equal(AppRegion.India, AppRegions.Detected("US", "Asia/Kolkata"));
        Assert.Equal(AppRegion.India, AppRegions.Detected("US", "India Standard Time"));
        Assert.Equal(AppRegion.World, AppRegions.Detected("NZ", "Pacific/Auckland"));
        Assert.Equal(AppRegion.World, AppRegions.Detected(null, "America/New_York"));
    }

    [Fact]
    public void ALanguageInUseStaysOfferedWhateverTheRegion()
    {
        Assert.Contains(ReplyLanguage.ChoicesFor(AppRegion.World, "ta"), choice => choice.Code == "ta");
        Assert.DoesNotContain(ReplyLanguage.ChoicesFor(AppRegion.World, "en"), choice => choice.Code == "hi");
        Assert.Equal(ReplyLanguage.Choices.Count, ReplyLanguage.ChoicesFor(AppRegion.India, "en").Count);
    }

    [Fact]
    public void RegionsHaveTheMacsRawValuesAndTitles()
    {
        Assert.Equal("india", AppRegion.India.RawValue());
        Assert.Equal(AppRegion.World, RawValues.FromRawValue<AppRegion>("world"));
        Assert.Equal("Rest of the world", AppRegion.World.Title());
    }
}

public sealed class DeviceIdentityTests
{
    [Fact]
    public void TheHashIsSaltedSha256InLowercaseHex()
    {
        // printf 'openclicky-device-v1:ABC' | shasum -a 256
        Assert.Equal("09dfa12193ca778d98a1c1d3b219ba1f2e98a5d1fe755d048e0b1b4f8faff226", DeviceIdentity.Hash("ABC"));
    }

    [Fact]
    public void ThisPcHasAStable64CharacterHash()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = DeviceIdentity.Current;
        Assert.NotNull(first);
        Assert.Equal(64, first!.Length);
        Assert.All(first, character => Assert.Contains(character, "0123456789abcdef"));
        Assert.Equal(first, DeviceIdentity.Current);
    }
}

public sealed class ReplyLanguageTests
{
    [Fact]
    public void ARegionalCodeIsSentAsItsLanguage()
    {
        Assert.Equal("en", ReplyLanguage.NormalizedCode(null));
        Assert.Equal("en", ReplyLanguage.NormalizedCode("  "));
        Assert.Equal("en", ReplyLanguage.NormalizedCode("en-US"));
        Assert.Equal("hi", ReplyLanguage.NormalizedCode("HI_in"));
    }

    [Fact]
    public void ThePromptNamesTheLanguageInEnglish()
    {
        Assert.Equal("Hindi", ReplyLanguage.EnglishName("hi"));
        Assert.Equal("English", ReplyLanguage.EnglishName("en"));
        Assert.StartsWith("Always speak and write in Hindi. Every reply is in Hindi:", ReplyLanguage.Instruction("hi"));
        Assert.EndsWith("say in Hindi that you did not catch that and ask them to say it again.", ReplyLanguage.Instruction("hi"));
    }
}

public sealed class OpenClickyPathsTests
{
    [Fact]
    public void ATildeIsTheHomeFolder()
    {
        var home = OpenClickyPaths.HomeDirectory;
        Assert.Equal(home, OpenClickyPaths.ExpandTilde("~"));
        Assert.Equal(Path.Combine(home, "Desktop"), OpenClickyPaths.ExpandTilde("~/Desktop"));
        Assert.Equal("C:\\plain", OpenClickyPaths.ExpandTilde("C:\\plain"));
    }

    [Fact]
    public void ThePathsFollowTheGuide()
    {
        Assert.EndsWith(Path.Combine(".openclicky", "shell.json"), OpenClickyPaths.ShellSettingsFile);
        Assert.EndsWith(Path.Combine("OpenClicky", "Logs", "app.log"), OpenClickyPaths.LogFile);
        Assert.EndsWith(Path.Combine("OpenClicky", "preferences.json"), OpenClickyPaths.PreferencesFile);
        Assert.EndsWith(Path.Combine("OpenClicky", "dictation.sqlite"), OpenClickyPaths.DictationDatabaseFile);
    }
}

public sealed class AppLogTests
{
    [Fact]
    public void ATestRunWritesItsOwnLog()
    {
        Assert.StartsWith(Path.GetTempPath(), AppLog.FilePath);
        var marker = $"test-line-{Guid.NewGuid()}";
        AppLog.Write(marker);
        using var stream = new FileStream(AppLog.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var text = new StreamReader(stream).ReadToEnd();
        Assert.Contains(marker, text);
    }
}
