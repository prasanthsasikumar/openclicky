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
            Assert.Equal(DictationDestination.Of(page), model.Destination);
        }
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
    public void YourPagesAreTwoAndOpenOnYourWords()
    {
        // Record and history are one page; dictionary, snippets and styles are another.
        Assert.Equal(["Your words", "How it writes"], DictationSections.AllCases.Select(section => section.SidebarTitle()));
        Assert.Equal(DictationDestination.Of(DictationSection.Words), new DictationWindowModel().Destination);
    }

    [Fact]
    public void TheSidebarListsYourPagesAfterVoiceAndBeforeTheOrb()
    {
        Assert.Equal(
            ["General", "Dictation", "Clicky", "Voice", "Your words", "How it writes", "The Orb", "Shortcuts", "Keys & Plans", "Account", "Privacy & Security", "About"],
            DictationDestination.SidebarOrder.Select(destination => destination.Title));
    }

    [Fact]
    public void ThereAreTenSettingsPages()
    {
        Assert.Equal(
            ["General", "Dictation", "Clicky", "Voice", "The Orb", "Shortcuts", "Keys & Plans", "Account", "Privacy & Security", "About"],
            DictationSettingsPages.AllCases.Select(page => page.Title()));
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
