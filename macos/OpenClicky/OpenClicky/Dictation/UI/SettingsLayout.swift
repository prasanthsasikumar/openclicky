//
//  SettingsLayout.swift
//  OpenClicky
//
//  Where every setting lives, in one table: six pages in the sidebar (Dictation, Clicky, Island,
//  Keys & Account, Privacy, General) and the pages they open (The orb, Calendars, Your words,
//  How it writes). From the Claude Design "OpenClicky settings — redesign mockups" (its
//  settings-data.js is the same table). The rows themselves are built in the Settings*Page.swift
//  files and the plugin folders; this file only decides their page, their group and their order.
//
//  Each page puts what people come for above the fold and folds what is rarely changed into one
//  group at the bottom ("More"), which opens in place. Search ignores the fold.
//

import Foundation

enum SettingsLayout {
    /// One group of rows on a page, in the order they are drawn.
    struct Group {
        let page: DictationSettingsPage
        /// The label above the group; empty for a page's unlabelled first group. Nil keeps the
        /// section the row's builder gave it (one group per calendar account).
        let title: String?
        /// The page's folded group at the bottom.
        var isAdvanced = false
        /// False for rows only search finds.
        var showsOnPage = true
        /// Row ids in order; an id ending in "*" takes every row whose id starts with the rest.
        let itemIDs: [String]
    }

    static let groups: [Group] = dictationGroups + clickyGroups + islandGroups + accountGroups + privacyGroups + generalGroups

    private static let dictationGroups: [Group] = [
        Group(page: .dictation, title: "", itemIDs: [
            "keyboard.dictationKey", "keyboard.fnWarning", "dictation.engine.*", "dictation.language", "dictation.cleanUp",
        ]),
        Group(page: .dictation, title: "Your writing", itemIDs: ["dictation.openWords", "dictation.openWriting"]),
        Group(page: .dictation, title: "Microphone", itemIDs: ["dictation.input", "dictation.micTest"]),
        Group(page: .dictation, title: "More", isAdvanced: true, itemIDs: [
            "dictation.script", "dictation.inactivityTimeout", "keyboard.edit", "keyboard.cancel", "dictation.pasteLast",
            "dictation.sounds", "dictation.haptics", "keyboard.fnFree", "orb.visible", "dictation.openOrb",
        ]),
        Group(page: .orb, title: "Look", itemIDs: ["orb.look", "orb.theme", "orb.size"]),
        Group(page: .orb, title: "When it appears", itemIDs: [
            "orb.hidesWhenIdle", "orb.restsExpanded", "orb.opensBoxWhenPasteUnverified", "orb.isDraggable", "orb.tooltips",
        ]),
    ]

    private static let clickyGroups: [Group] = [
        Group(page: .clicky, title: "", itemIDs: ["keyboard.talk", "clicky.speechVoice", "clicky.language", "clicky.answerSource"]),
        Group(page: .clicky, title: "Hands-free", itemIDs: [
            "clicky.fastVoice", "clicky.realtimeProvider", "clicky.fastVoiceNeedsKey", "clicky.alwaysListening",
        ]),
        Group(page: .clicky, title: "Pointer", itemIDs: ["clicky.pointer"]),
        Group(page: .clicky, title: "More", isAdvanced: true, itemIDs: [
            "keyboard.type", "keyboard.handsFree", "clicky.model", "clicky.onThisMac", "clicky.emailApp", "clicky.emailNeverSent",
            "plugins.teacher", "clicky.agentMode", "clicky.workspace",
        ]),
    ]

    private static let islandGroups: [Group] = [
        Group(page: .island, title: "", itemIDs: ["general.hudPlacement", "appearance.peekDirection", "plugins.islandDefault"]),
        Group(page: .island, title: "Tiles", itemIDs: ["plugins.media", "calendar.enabled", "weather.enabled", "plugins.system"]),
        Group(page: .island, title: "Tools", itemIDs: ["plugins.shelf", "plugins.clipboard", "plugins.notes", "plugins.timer", "plugins.agents"]),
        Group(page: .island, title: "More", isAdvanced: true, itemIDs: [
            "appearance.style", "plugins.islandOrder", "calendar.quickAdd", "island.openCalendars", "weather.unit", "weather.location",
            "timer.focusMinutes", "timer.breakMinutes", "timer.longBreakMinutes",
        ]),
        Group(page: .calendars, title: nil, itemIDs: ["calendar.calendars", "calendar.calendar.*"]),
    ]

    private static let accountGroups: [Group] = [
        Group(page: .account, title: "", itemIDs: ["account.profile", "account.plan", "account.usage.backend"]),
        Group(page: .account, title: "Your keys", itemIDs: ["account.keys", "account.keysNote"]),
        Group(page: .account, title: "Plans", itemIDs: ["account.claudeCodePlan", "account.codexPlan", "account.plansNote"]),
        Group(page: .account, title: "Advanced", isAdvanced: true, itemIDs: [
            "account.usage.sarvam", "account.backendHost", "account.backendToken",
        ]),
    ]

    private static let privacyGroups: [Group] = [
        Group(page: .privacy, title: "Permissions", itemIDs: [
            "privacy.microphone", "privacy.accessibility", "privacy.speechRecognition", "calendar.access",
        ]),
        Group(page: .privacy, title: "Your screen", itemIDs: ["privacy.readNearbyText", "privacy.pauseBeforeSendingScreen"]),
        Group(page: .privacy, title: "What it keeps", itemIDs: [
            "privacy.memoryOnThisMac", "privacy.clipboardHistory", "privacy.retryFailedTakes", "privacy.deleteTakes", "privacy.summary",
        ]),
        // Incognito's switch is in the sidebar's foot; search still finds it here.
        Group(page: .privacy, title: "Sidebar", showsOnPage: false, itemIDs: ["privacy.incognito"]),
        Group(page: .privacy, title: "Where things live", isAdvanced: true, itemIDs: [
            "privacy.whereHistory", "privacy.whereSpace", "privacy.whereKeys",
        ]),
    ]

    private static let generalGroups: [Group] = [
        Group(page: .general, title: "", itemIDs: [
            "general.openAtLogin", "general.menuBarIcon", "general.reduceMotion", "general.appIcon",
        ]),
        // Appearance is the toolbar's button; search still finds it here.
        Group(page: .general, title: "Window", showsOnPage: false, itemIDs: ["general.appearance"]),
        Group(page: .general, title: "Tours", itemIDs: ["general.walkthrough", "general.settingsTour"]),
        Group(page: .general, title: "Updates", itemIDs: ["about.autoUpdate", "about.app"]),
        Group(page: .general, title: "Help", itemIDs: ["about.source", "about.reportBug"]),
    ]

    /// Whether `itemID` is one the pattern names ("dictation.engine.*" names every engine row).
    static func pattern(_ pattern: String, matches itemID: String) -> Bool {
        pattern.hasSuffix("*") ? itemID.hasPrefix(pattern.dropLast()) : itemID == pattern
    }

    /// The rows in layout order, each given its page, group and fold. A row the table doesn't
    /// name is not drawn: a new row has to be placed here to appear.
    static func arrange(_ unplacedItems: [SettingsItem]) -> [SettingsItem] {
        var arrangedItems: [SettingsItem] = []
        for group in groups {
            for itemPattern in group.itemIDs {
                for var item in unplacedItems where pattern(itemPattern, matches: item.id) {
                    item.page = group.page
                    if let title = group.title { item.section = title }
                    item.isAdvanced = group.isAdvanced
                    item.showsOnPage = group.showsOnPage
                    arrangedItems.append(item)
                }
            }
        }
        #if DEBUG
        let forgottenIDs = unplacedItems.map(\.id).filter { itemID in !groups.contains { $0.itemIDs.contains { pattern($0, matches: itemID) } } }
        if !forgottenIDs.isEmpty { print("SettingsLayout: rows with no place, not drawn: \(forgottenIDs)") }
        let builtTwiceIDs = Dictionary(grouping: unplacedItems.map(\.id), by: { $0 }).filter { $0.value.count > 1 }.keys
        if !builtTwiceIDs.isEmpty { print("SettingsLayout: rows built twice, drawn twice: \(builtTwiceIDs.sorted())") }
        #endif
        return arrangedItems
    }
}
