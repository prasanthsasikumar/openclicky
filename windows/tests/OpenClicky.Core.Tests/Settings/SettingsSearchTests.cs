// Ports SettingsSearchTests.swift: which items a query matches, how results group into pages, and
// which words get the highlighter. Plus the catalog's own rules (relevance, every page has rows,
// ids are unique) that the Mac checked by drawing the pages.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Tests.Settings;

public sealed class SettingsSearchTests
{
    private static SettingsItem Item(string id, DictationSettingsPage page, string section, string title, string detail = "", string[]? keywords = null) => new()
    {
        Id = id, Page = page, Section = section, Title = title, Detail = detail, Keywords = keywords ?? [],
    };

    [Fact]
    public void MatchesTitleDetailKeywordsAndPageCaseInsensitively()
    {
        var openAtLogin = Item("login", DictationSettingsPage.General, "Startup", "Open at login", "Start OpenClicky when you log in.", ["startup"]);
        Assert.True(openAtLogin.Matches("Login"));
        Assert.True(openAtLogin.Matches("log in"));
        Assert.True(openAtLogin.Matches("startup"));
        Assert.True(openAtLogin.Matches("general"));
        Assert.False(openAtLogin.Matches("microphone"));
    }

    [Fact]
    public void EveryWordOfTheQueryMustMatch()
    {
        var sounds = Item("sounds", DictationSettingsPage.Orb, "Feedback", "Sounds", "Tones when a take starts.");
        Assert.True(sounds.Matches("take tones"));
        Assert.False(sounds.Matches("take haptics"));
    }

    [Fact]
    public void ResultsGroupByPageInOrder()
    {
        var items = new[]
        {
            Item("a", DictationSettingsPage.Orb, "Look", "Style"),
            Item("b", DictationSettingsPage.Orb, "Look", "Theme"),
            Item("c", DictationSettingsPage.Clicky, "Pointer", "Show the pointer"),
        };
        var groups = SettingsSectionGroup.Groups(items, item => item.Page.Title());
        Assert.Equal(["The orb", "Clicky"], groups.Select(group => group.Title));
        Assert.Equal(2, groups[0].Items.Count);
    }

    [Fact]
    public void ASearchOnlyItemIsListedUnderItsOwnPageName()
    {
        var dictionary = Item("d", DictationSettingsPage.Dictation, "How it writes", "Dictionary", keywords: ["spelling"]) with { SearchOnlyGroup = "How it writes" };
        Assert.True(dictionary.Matches("spelling"));
        Assert.True(dictionary.Matches("how it writes"));
        Assert.False(dictionary.Matches("dictation key"));
        Assert.Equal(["How it writes"], SettingsSectionGroup.Groups([dictionary], item => item.SearchGroupTitle).Select(group => group.Title));
    }

    [Fact]
    public void PagesWhoseRowsAreNamedForTheQueryComeFirst()
    {
        var dictionary = Item("d", DictationSettingsPage.Dictation, "How it writes", "Dictionary") with { SearchOnlyGroup = "How it writes" };
        var matches = new[]
        {
            Item("p", DictationSettingsPage.Privacy, "What it keeps", "Delete all takes", "Your dictionary stays."),
            dictionary,
        };
        Assert.Equal(["How it writes", "Privacy"], SettingsSectionGroup.SearchResultGroups(matches, "dictionary").Select(group => group.Title));
        Assert.Equal(["Privacy", "How it writes"], SettingsSectionGroup.SearchResultGroups(matches, "stays").Select(group => group.Title));
    }

    [Fact]
    public void AFooterJoinsTheSectionBeforeIt()
    {
        var footer = Item("f", DictationSettingsPage.Privacy, "Where things live", "Privacy summary") with { Chrome = SettingsItemChrome.Footer };
        var groups = SettingsSectionGroup.Groups([Item("a", DictationSettingsPage.Privacy, "Where things live", "History"), footer]);
        Assert.Single(groups);
        Assert.Equal(["a"], groups[0].Items.Select(item => item.Id));
        Assert.Equal(["f"], groups[0].Footers.Select(item => item.Id));
    }

    [Fact]
    public void HighlightMarksEveryOccurrence()
    {
        var ranges = SettingsHighlight.Ranges("tap Ctrl + Win twice; CTRL again", "ctrl");
        Assert.Equal(2, ranges.Count);
        Assert.Equal(new SettingsHighlight.Range(4, 4), ranges[0]);
        Assert.Equal(new SettingsHighlight.Range(22, 4), ranges[1]);
    }

    [Fact]
    public void HighlightIgnoresAccentsAndMergesOverlaps()
    {
        Assert.Single(SettingsHighlight.Ranges("Siobhán", "siobhan"));
        var ranges = SettingsHighlight.Ranges("keyboard", "key keyb");
        Assert.Equal([new SettingsHighlight.Range(0, 4)], ranges);
    }

    // MARK: the catalog

    private static SettingsCatalog Catalog(DictationSettings settings, OpenClickyShellSettings? shell = null, AccountKind kind = AccountKind.SignedOut) =>
        new(settings, () => shell ?? new OpenClickyShellSettings(), () => new AccountCapabilities(kind));

    [Fact]
    public void EveryItemIdIsUniqueAndEveryPageHasRows()
    {
        var catalog = Catalog(new DictationSettings(Preferences.InMemory()));
        var ids = catalog.AllItems.Select(item => item.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var page in DictationSettingsPages.AllCases) Assert.NotEmpty(catalog.PageItems(page));
    }

    [Fact]
    public void TheDictationKeyLivesOnDictationAndTheRarerShortcutsFoldUnderMore()
    {
        var catalog = Catalog(new DictationSettings(Preferences.InMemory()));
        Assert.Equal(DictationSettingsPage.Dictation, catalog.AllItems.Single(item => item.Id == "keyboard.dictationKey").Page);
        var talk = catalog.AllItems.Single(item => item.Id == "keyboard.talk");
        Assert.Equal(DictationSettingsPage.Clicky, talk.Page);
        Assert.False(talk.IsAdvanced);
        Assert.True(catalog.AllItems.Single(item => item.Id == "keyboard.cancel").IsAdvanced);
        Assert.DoesNotContain(catalog.AllItems, item => item.Id is "keyboard.dictate" or "keyboard.where" or "general.region" or "about.checkNow" or "account.usage.offline");
        Assert.DoesNotContain(catalog.AllItems, item => item.Id.Contains("fn", StringComparison.Ordinal) || item.Id.Contains("haptics", StringComparison.Ordinal));
    }

    [Fact]
    public void SarvamAndTheScriptRowShowOnlyWhenInUse()
    {
        var settings = new DictationSettings(Preferences.InMemory());
        var catalog = Catalog(settings);
        Assert.Equal(AppRegion.World, settings.Region);
        Assert.DoesNotContain(catalog.PageItems(DictationSettingsPage.Dictation), item => item.Id == "dictation.engine.sarvam");
        Assert.DoesNotContain(catalog.AllItems.Where(item => item.IsRelevant()), item => item.Id == "dictation.script");

        settings.Engine = DictationEngineChoice.Sarvam;
        Assert.Contains(catalog.PageItems(DictationSettingsPage.Dictation), item => item.Id == "dictation.engine.sarvam");

        settings.LanguageCode = "hi-IN";
        Assert.Contains(catalog.PageItems(DictationSettingsPage.Dictation), item => item.Id == "dictation.script");
        settings.LanguageCode = "en-US";
        Assert.DoesNotContain(catalog.PageItems(DictationSettingsPage.Dictation), item => item.Id == "dictation.script");
    }

    [Fact]
    public void HandsFreeAndFastVoiceNeedAnOwnOpenAIKey()
    {
        var settings = new DictationSettings(Preferences.InMemory());
        var signedOut = Catalog(settings, kind: AccountKind.Account);
        Assert.DoesNotContain(signedOut.PageItems(DictationSettingsPage.Clicky), item => item.Id == "keyboard.handsFree");
        Assert.Contains(signedOut.PageItems(DictationSettingsPage.Clicky), item => item.Id == "clicky.fastVoiceNeedsKey");

        var ownKeys = Catalog(settings, kind: AccountKind.OwnKeys);
        Assert.Contains(ownKeys.PageItems(DictationSettingsPage.Clicky), item => item.Id == "keyboard.handsFree");
        Assert.Contains(ownKeys.PageItems(DictationSettingsPage.Clicky), item => item.Id == "clicky.fastVoice");
        Assert.DoesNotContain(ownKeys.PageItems(DictationSettingsPage.Clicky), item => item.Id == "clicky.fastVoiceNeedsKey");
    }

    [Fact]
    public void TheSarvamUsageRowShowsOnlyWithASarvamKey()
    {
        var settings = new DictationSettings(Preferences.InMemory());
        Assert.DoesNotContain(Catalog(settings).PageItems(DictationSettingsPage.Account), item => item.Id == "account.usage.sarvam");
        var shell = new OpenClickyShellSettings { SarvamKey = "sk_123" };
        Assert.Contains(Catalog(settings, shell).PageItems(DictationSettingsPage.Account), item => item.Id == "account.usage.sarvam");
    }

    [Fact]
    public void SearchFindsYourPagesButTheirRowsStayOffTheSettingsPages()
    {
        var catalog = Catalog(new DictationSettings(Preferences.InMemory()));
        var matches = catalog.SearchMatches("snippet");
        Assert.Contains(matches, item => item.Id == "writing.snippets" && item.OpensSection == DictationSection.Writing);
        Assert.DoesNotContain(catalog.PageItems(DictationSettingsPage.Dictation), item => item.SearchOnlyGroup is not null);
        Assert.Empty(catalog.SearchMatches("   "));
    }

    [Fact]
    public void TheShortcutsShowTheWindowsKeys()
    {
        Assert.Equal(["R Ctrl", "Win"], SettingsCatalog.KeysFor("keyboard.talk", DictationHotkey.RightControl).Keys);
        Assert.Equal(["Ctrl", "Win"], SettingsCatalog.KeysFor("keyboard.talk", DictationHotkey.RightAlt).Keys);
        Assert.Equal(["right Alt", "Ctrl"], SettingsCatalog.KeysFor("keyboard.edit", DictationHotkey.RightAlt).Keys);
        Assert.Equal("or right Ctrl twice", SettingsCatalog.KeysFor("keyboard.cancel", DictationHotkey.RightControl).Gesture);
        Assert.Contains("left Ctrl", SettingsCatalog.DictationKeyDetail(DictationHotkey.RightControl), StringComparison.Ordinal);
    }
}
