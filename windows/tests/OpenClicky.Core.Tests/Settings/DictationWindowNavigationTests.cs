// Ports DictationWindowNavigationTests.swift and DictationWindowLayoutTests.swift: your pages and the
// settings pages share one sidebar; every entry point lands on the page it asks for, a settings
// search gives way to a page; and the window follows the Appearance setting now and when it changes.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;

namespace OpenClicky.Core.Tests.Settings;

public sealed class DictationWindowNavigationTests
{
    [Fact]
    public void EverySectionAndSettingsPageCanBeSelected()
    {
        var model = new DictationWindowModel();
        foreach (var section in DictationSections.AllCases)
        {
            model.Select(section);
            Assert.Equal(DictationDestination.Of(section), model.Destination);
        }
        foreach (var page in DictationSettingsPages.AllCases)
        {
            model.Select(DictationDestination.Of(page));
            // A page shown one section at a time opens on its first section.
            var expected = SettingsLayout.SectionsOf(page) is [var first, ..] ? DictationDestination.Of(page, first.Title!) : DictationDestination.Of(page);
            Assert.Equal(expected, model.Destination);
        }
    }

    [Fact]
    public void ClickyOpensIntoItsSectionsAndEachRowHasOne()
    {
        var sections = SettingsLayout.SectionsOf(DictationSettingsPage.Clicky).Select(section => section.Title).ToList();
        Assert.Equal(["Talking", "How it answers", "Hands-free", "Pointer", "Drawings", "Your models", "More"], sections);
        Assert.Empty(SettingsLayout.SectionsOf(DictationSettingsPage.Dictation));

        var model = new DictationWindowModel();
        model.Select(DictationSettingsPage.Clicky);
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Clicky, "Talking"), model.Destination);
        Assert.Equal("Talking", model.Destination.Title);
        Assert.Equal(DictationSettingsPage.Clicky, model.Destination.SidebarPage);
        model.Select(DictationDestination.Of(DictationSettingsPage.Clicky, "Drawings"));
        Assert.Equal("settings(clicky/Drawings)", model.Destination.ToString());

        // A row's own section: the drawing switch is with the drawings now, and a page without sections is just the page.
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Clicky, "Drawings"), SettingsLayout.DestinationOf("plugins.teacher"));
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Clicky, "Your models"), SettingsLayout.DestinationOf("models.ollama"));
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Dictation), SettingsLayout.DestinationOf("dictation.language"));
        Assert.Equal("sidebar.page.clicky.your-models",
            SettingsTourTargets.SidebarTargetId(DictationDestination.Of(DictationSettingsPage.Clicky, "Your models")));
    }

    [Fact]
    public void ASettingsPageWinsOverASection()
    {
        var model = new DictationWindowModel();
        model.Open(DictationSection.Words, DictationSettingsPage.Dictation);
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Dictation), model.Destination);
    }

    [Fact]
    public void OpeningWithNothingKeepsWhereTheWindowWas()
    {
        var model = new DictationWindowModel();
        model.Select(DictationDestination.Of(DictationSettingsPage.Orb));
        model.SearchText = "paste";
        model.Open();
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Orb), model.Destination);
        Assert.Equal("paste", model.SearchText);
    }

    [Fact]
    public void OpeningAPageClearsALeftoverSearch()
    {
        // Otherwise "change engine" or the island's "open settings…" would leave results on screen.
        var model = new DictationWindowModel { SearchText = "microphone" };
        model.Open(settingsPage: DictationSettingsPage.Account);
        Assert.False(model.IsSearching);
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Account), model.Destination);
    }

    [Fact]
    public void WhitespaceIsNotASearch()
    {
        var model = new DictationWindowModel { SearchText = "   " };
        Assert.False(model.IsSearching);
        model.SearchText = "  key ";
        Assert.Equal("key", model.TrimmedSearchText);
    }

    [Fact]
    public void TheWindowOpensOnDictation()
    {
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Dictation), new DictationWindowModel().Destination);
        Assert.Equal(["Your words", "How it writes"], DictationSections.AllCases.Select(section => section.SidebarTitle()));
    }

    [Fact]
    public void TheSidebarListsSixPages()
    {
        Assert.Equal(["Dictation", "Clicky", "Island", "Keys & Account", "Privacy", "General"],
            DictationDestination.SidebarOrder.Select(destination => destination.Title));
    }

    [Fact]
    public void PagesOpenedFromARowKeepTheirParentSelected()
    {
        Assert.Equal(DictationSettingsPage.Dictation, DictationDestination.Of(DictationSettingsPage.Orb).SidebarPage);
        Assert.Equal(DictationSettingsPage.Dictation, DictationDestination.Of(DictationSection.Words).SidebarPage);
        Assert.Equal(DictationSettingsPage.Dictation, DictationDestination.Of(DictationSection.Writing).SidebarPage);
        Assert.Equal(DictationSettingsPage.Island, DictationDestination.Of(DictationSettingsPage.Calendars).SidebarPage);
        Assert.Equal(DictationSettingsPage.Privacy, DictationDestination.Of(DictationSettingsPage.Privacy).SidebarPage);
        Assert.Equal("Dictation › The orb", DictationSettingsPage.Orb.SearchTitle());
    }

    [Fact]
    public void BackReturnsThroughTheRowsOpened()
    {
        var model = new DictationWindowModel();
        model.Select(DictationSettingsPage.Dictation);
        Assert.False(model.CanGoBack);
        model.Push(DictationDestination.Of(DictationSettingsPage.Orb));
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Orb), model.Destination);
        Assert.True(model.CanGoBack);
        model.GoBack();
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.Dictation), model.Destination);
        Assert.False(model.CanGoBack);
    }

    [Fact]
    public void ASidebarClickForgetsTheWayBack()
    {
        var model = new DictationWindowModel();
        model.Push(DictationDestination.Of(DictationSection.Words));
        model.Select(DictationSettingsPage.Clicky);
        Assert.False(model.CanGoBack);
    }

    [Fact]
    public void PagesReadBackFromTheirRawValues()
    {
        foreach (var page in DictationSettingsPages.AllCases) Assert.Equal(page, DictationSettingsPages.FromRawValue(page.RawValue()));
        Assert.Null(DictationSettingsPages.FromRawValue("nowhere"));
    }
}

public sealed class DictationWindowLayoutTests
{
    [Fact]
    public void AWindowFollowsTheAppearanceSettingNowAndWhenItChanges()
    {
        var settings = new DictationSettings(Preferences.InMemory()) { Appearance = DictationAppearance.Dark };
        var applied = new List<DictationAppearance>();
        using (WindowAppearance.Follow(settings, applied.Add))
        {
            Assert.Equal([DictationAppearance.Dark], applied);
            settings.Appearance = DictationAppearance.Light;
            settings.Appearance = DictationAppearance.System;
            settings.Sounds = false;
        }
        settings.Appearance = DictationAppearance.Dark;
        Assert.Equal([DictationAppearance.Dark, DictationAppearance.Light, DictationAppearance.System], applied);
    }

    [Fact]
    public void SystemFollowsWindowsAndTheOthersAreFixed()
    {
        Assert.True(WindowAppearance.IsDark(DictationAppearance.Dark, systemIsDark: false));
        Assert.False(WindowAppearance.IsDark(DictationAppearance.Light, systemIsDark: true));
        Assert.True(WindowAppearance.IsDark(DictationAppearance.System, systemIsDark: true));
        Assert.False(WindowAppearance.IsDark(DictationAppearance.System, systemIsDark: false));
    }
}
