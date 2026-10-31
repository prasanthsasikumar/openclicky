// Ports SettingsLayoutTests.swift: every row has one place, each page folds at most one group and
// last, and the merged pointer picker reads and writes the two settings beneath it.

using OpenClicky.Core.Pointing;
using OpenClicky.Core.Settings;

namespace OpenClicky.Core.Tests.Settings;

public sealed class SettingsLayoutTests
{
    private static SettingsItem Item(string id, string section = "") => new() { Id = id, Section = section, Title = id };

    [Fact]
    public void NoRowIsPlacedTwice()
    {
        var allIds = SettingsLayout.Groups.SelectMany(group => group.ItemIds).ToList();
        Assert.Equal(allIds.Count, allIds.Distinct().Count());
    }

    [Fact]
    public void EveryPageHasRowsAndFoldsAtMostOneGroupLast()
    {
        foreach (var page in DictationSettingsPages.AllCases)
        {
            var pageGroups = SettingsLayout.Groups.Where(group => group.Page == page).ToList();
            Assert.NotEmpty(pageGroups);
            var foldedIndices = Enumerable.Range(0, pageGroups.Count).Where(index => pageGroups[index].IsAdvanced).ToList();
            Assert.True(foldedIndices.Count <= 1, $"{page} folds more than one group");
            if (foldedIndices.Count == 1)
            {
                Assert.All(pageGroups.Skip(foldedIndices[0] + 1), group => Assert.False(group.ShowsOnPage));
            }
        }
    }

    [Fact]
    public void ArrangingPlacesRowsInLayoutOrderAndDropsUnplacedOnes()
    {
        var arranged = SettingsLayout.Arrange([
            Item("orb.visible"), Item("keyboard.talk"), Item("dictation.engine.parakeet"), Item("not.a.row"), Item("dictation.engine.offline"),
        ]);
        Assert.Equal(["dictation.engine.parakeet", "dictation.engine.offline", "orb.visible", "keyboard.talk"], arranged.Select(item => item.Id));
        Assert.Equal([DictationSettingsPage.Dictation, DictationSettingsPage.Dictation, DictationSettingsPage.Dictation, DictationSettingsPage.Clicky],
            arranged.Select(item => item.Page));
        Assert.Equal([false, false, true, false], arranged.Select(item => item.IsAdvanced));
        Assert.Equal("More", arranged[2].Section);
    }

    [Fact]
    public void AGroupWithoutATitleKeepsTheRowsOwnSection()
    {
        var arranged = SettingsLayout.Arrange([Item("calendar.calendar.abc", section: "Outlook")]);
        Assert.Equal(DictationSettingsPage.Calendars, arranged[0].Page);
        Assert.Equal("Outlook", arranged[0].Section);
    }

    [Fact]
    public void IncognitoIsFoundBySearchButNotDrawnOnPrivacy()
    {
        var incognito = SettingsLayout.Arrange([Item("privacy.incognito")])[0];
        Assert.Equal(DictationSettingsPage.Privacy, incognito.Page);
        Assert.False(incognito.ShowsOnPage);
    }

    [Fact]
    public void AFoldedGroupIsMarkedOnlyOnAPage()
    {
        var arranged = SettingsLayout.Arrange([Item("keyboard.cancel"), Item("keyboard.edit")]);
        Assert.True(Assert.Single(SettingsSectionGroup.Groups(arranged, foldsAdvanced: true)).IsAdvanced);
        Assert.False(Assert.Single(SettingsSectionGroup.Groups(arranged)).IsAdvanced);
    }

    [Fact]
    public void ThePointerPickerReadsTheTwoSettings()
    {
        Assert.Equal(PointerShowsChoice.WhileTalking, PointerShowsChoices.From(isPointerShown: false, PointerPresence.Always));
        Assert.Equal(PointerShowsChoice.Always, PointerShowsChoices.From(isPointerShown: true, PointerPresence.Always));
        Assert.Equal(PointerShowsChoice.WhileMoving, PointerShowsChoices.From(isPointerShown: true, PointerPresence.WhileMoving));
        Assert.Equal(PointerShowsChoice.OnShake, PointerShowsChoices.From(isPointerShown: true, PointerPresence.OnShake));
        Assert.Null(PointerShowsChoice.WhileTalking.Presence());
        Assert.Equal(PointerPresence.OnShake, PointerShowsChoice.OnShake.Presence());
    }
}
