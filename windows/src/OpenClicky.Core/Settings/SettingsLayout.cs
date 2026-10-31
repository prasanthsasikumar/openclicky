// Ports Dictation/UI/SettingsLayout.swift: where every setting lives, in one table. Six pages in the
// sidebar (Dictation, Clicky, Island, Keys & Account, Privacy, General) and the pages they open (The
// orb, Calendars, Your words, How it writes). The rows are built in SettingsCatalog and drawn by the
// window's Settings*Page.cs files; this file only decides their page, their group and their order.
//
// Each page puts what people come for above the fold and folds what is rarely changed into one
// group at the bottom ("More"), which opens in place. Search ignores the fold. The Mac-only rows (the
// fn key, haptics, the macOS permissions, answering on this Mac) have no Windows twin and aren't
// listed; the Windows-only ones (tap to keep listening, the tray icon) sit where their Mac
// neighbours do.

namespace OpenClicky.Core.Settings;

public static class SettingsLayout
{
    /// <summary>One group of rows on a page, in the order they are drawn.</summary>
    public sealed record Group(DictationSettingsPage Page, string? Title, IReadOnlyList<string> ItemIds)
    {
        // Title: the label above the group; empty for a page's unlabelled first group. Null keeps
        // the section the row's builder gave it (one group per calendar account).

        /// <summary>The page's folded group at the bottom.</summary>
        public bool IsAdvanced { get; init; }

        /// <summary>False for rows only search finds.</summary>
        public bool ShowsOnPage { get; init; } = true;
    }

    private static readonly IReadOnlyList<Group> DictationGroups =
    [
        new(DictationSettingsPage.Dictation, "", ["keyboard.dictationKey", "keyboard.tapKeepsListening", "dictation.engine.*", "dictation.language", "dictation.cleanUp"]),
        new(DictationSettingsPage.Dictation, "Your writing", ["dictation.openWords", "dictation.openWriting"]),
        new(DictationSettingsPage.Dictation, "Microphone", ["dictation.input", "dictation.micTest"]),
        new(DictationSettingsPage.Dictation, "More",
            ["dictation.script", "dictation.inactivityTimeout", "keyboard.edit", "keyboard.cancel", "dictation.pasteLast",
             "dictation.sounds", "orb.visible", "dictation.openOrb"]) { IsAdvanced = true },
        new(DictationSettingsPage.Orb, "Look", ["orb.look", "orb.theme", "orb.size"]),
        new(DictationSettingsPage.Orb, "When it appears",
            ["orb.hidesWhenIdle", "orb.restsExpanded", "orb.opensBoxWhenPasteUnverified", "orb.isDraggable", "orb.tooltips"]),
    ];

    private static readonly IReadOnlyList<Group> ClickyGroups =
    [
        new(DictationSettingsPage.Clicky, "", ["keyboard.talk", "clicky.speechVoice", "clicky.language", "clicky.answerSource"]),
        new(DictationSettingsPage.Clicky, "Hands-free", ["clicky.fastVoice", "clicky.realtimeProvider", "clicky.fastVoiceNeedsKey", "clicky.alwaysListening"]),
        new(DictationSettingsPage.Clicky, "Pointer", ["clicky.pointer"]),
        new(DictationSettingsPage.Clicky, "More",
            ["keyboard.type", "keyboard.handsFree", "clicky.model", "clicky.emailApp", "clicky.emailNeverSent",
             "plugins.teacher", "clicky.agentMode", "clicky.workspace"]) { IsAdvanced = true },
    ];

    private static readonly IReadOnlyList<Group> IslandGroups =
    [
        new(DictationSettingsPage.Island, "", ["general.hudPlacement", "appearance.peekDirection", "plugins.islandDefault"]),
        new(DictationSettingsPage.Island, "Tiles", ["plugins.media", "calendar.enabled", "weather.enabled", "plugins.system"]),
        new(DictationSettingsPage.Island, "Tools", ["plugins.shelf", "plugins.clipboard", "plugins.notes", "plugins.timer", "plugins.agents"]),
        new(DictationSettingsPage.Island, "More",
            ["appearance.style", "plugins.islandOrder", "calendar.quickAdd", "island.openCalendars", "weather.unit", "weather.location",
             "timer.focusMinutes", "timer.breakMinutes", "timer.longBreakMinutes"]) { IsAdvanced = true },
        new(DictationSettingsPage.Calendars, null, ["calendar.calendars", "calendar.calendar.*"]),
    ];

    private static readonly IReadOnlyList<Group> AccountGroups =
    [
        new(DictationSettingsPage.Account, "", ["account.profile", "account.plan", "account.usage.backend"]),
        new(DictationSettingsPage.Account, "Your keys", ["account.keys", "account.keysNote"]),
        new(DictationSettingsPage.Account, "Plans", ["account.claudeCodePlan", "account.codexPlan", "account.plansNote"]),
        new(DictationSettingsPage.Account, "Advanced", ["account.usage.sarvam", "account.backendHost", "account.backendToken"]) { IsAdvanced = true },
    ];

    private static readonly IReadOnlyList<Group> PrivacyGroups =
    [
        new(DictationSettingsPage.Privacy, "Permissions", ["privacy.microphone", "calendar.access"]),
        new(DictationSettingsPage.Privacy, "Your screen", ["privacy.readNearbyText", "privacy.pauseBeforeSendingScreen"]),
        new(DictationSettingsPage.Privacy, "What it keeps",
            ["privacy.memoryOnThisMac", "privacy.clipboardHistory", "privacy.retryFailedTakes", "privacy.deleteTakes", "privacy.summary"]),
        // Incognito's switch is in the sidebar's foot; search still finds it here.
        new(DictationSettingsPage.Privacy, "Sidebar", ["privacy.incognito"]) { ShowsOnPage = false },
        new(DictationSettingsPage.Privacy, "Where things live", ["privacy.whereHistory", "privacy.whereSpace", "privacy.whereKeys"]) { IsAdvanced = true },
    ];

    private static readonly IReadOnlyList<Group> GeneralGroups =
    [
        new(DictationSettingsPage.General, "", ["general.openAtLogin", "general.trayIcon", "general.reduceMotion", "general.appIcon"]),
        // Appearance is the title bar's button; search still finds it here.
        new(DictationSettingsPage.General, "Window", ["general.appearance"]) { ShowsOnPage = false },
        new(DictationSettingsPage.General, "Tours", ["general.walkthrough", "general.settingsTour"]),
        new(DictationSettingsPage.General, "Updates", ["about.autoUpdate", "about.app"]),
        new(DictationSettingsPage.General, "Help", ["about.source", "about.reportBug"]),
    ];

    public static IReadOnlyList<Group> Groups { get; } =
        [.. DictationGroups, .. ClickyGroups, .. IslandGroups, .. AccountGroups, .. PrivacyGroups, .. GeneralGroups];

    /// <summary>Whether <paramref name="itemId"/> is one the pattern names ("dictation.engine.*" names every engine row).</summary>
    public static bool PatternMatches(string pattern, string itemId) =>
        pattern.EndsWith('*') ? itemId.StartsWith(pattern[..^1], StringComparison.Ordinal) : itemId == pattern;

    /// <summary>
    /// The rows in layout order, each given its page, group and fold. A row the table doesn't name
    /// is not drawn: a new row has to be placed here to appear.
    /// </summary>
    public static IReadOnlyList<SettingsItem> Arrange(IReadOnlyList<SettingsItem> unplacedItems)
    {
        var arrangedItems = new List<SettingsItem>();
        foreach (var group in Groups)
        {
            foreach (var itemPattern in group.ItemIds)
            {
                foreach (var item in unplacedItems.Where(item => PatternMatches(itemPattern, item.Id)))
                {
                    arrangedItems.Add(item with
                    {
                        Page = group.Page,
                        Section = group.Title ?? item.Section,
                        IsAdvanced = group.IsAdvanced,
                        ShowsOnPage = group.ShowsOnPage,
                    });
                }
            }
        }
#if DEBUG
        var forgottenIds = unplacedItems.Select(item => item.Id)
            .Where(itemId => !Groups.Any(group => group.ItemIds.Any(pattern => PatternMatches(pattern, itemId)))).ToList();
        if (forgottenIds.Count > 0) System.Diagnostics.Debug.WriteLine($"SettingsLayout: rows with no place, not drawn: {string.Join(", ", forgottenIds)}");
#endif
        return arrangedItems;
    }
}
