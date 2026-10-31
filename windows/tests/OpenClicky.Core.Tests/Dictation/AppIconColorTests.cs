// Ports AppIconColorTests.swift: five colours in the Mac's order, saved as their lower-case names,
// Signal until one is picked and after a reset.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;

namespace OpenClicky.Core.Tests.Dictation;

public sealed class AppIconColorTests
{
    [Fact]
    public void FiveColoursInTheMacsOrder()
    {
        Assert.Equal(["Signal", "Ultraviolet", "Sunset", "Paper", "Aqua"], AppIconColors.AllCases.Select(color => color.Title()));
    }

    [Fact]
    public void ThePickIsSavedAsItsLowerCaseNameAndSignalIsTheDefault()
    {
        var preferences = Preferences.InMemory();
        var settings = new DictationSettings(preferences);
        Assert.Equal(AppIconColor.Signal, settings.AppIconColor);
        settings.AppIconColor = AppIconColor.Ultraviolet;
        Assert.Equal("ultraviolet", preferences.GetString("general.appIconColor"));
        Assert.Equal(AppIconColor.Ultraviolet, new DictationSettings(preferences).AppIconColor);
        preferences.Set("general.appIconColor", "chartreuse");
        Assert.Equal(AppIconColor.Signal, new DictationSettings(preferences).AppIconColor);
    }

    [Fact]
    public void PaperIsTheOneWithARedMark()
    {
        Assert.Equal("#F0452B", AppIconColor.Paper.Palette().MarkHex);
        Assert.All(AppIconColors.AllCases.Where(color => color != AppIconColor.Paper), color => Assert.Equal("#FFFFFF", color.Palette().MarkHex));
        Assert.Equal(("#FF6A3F", "#D4163A"), (AppIconColor.Signal.Palette().TopLeftHex, AppIconColor.Signal.Palette().BottomRightHex));
    }
}
