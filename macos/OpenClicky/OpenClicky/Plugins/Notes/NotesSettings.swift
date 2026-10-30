//
//  NotesSettings.swift
//  OpenClicky
//
//  The Notes plugin's rows in Settings.
//

import SwiftUI

extension SettingsCatalog {
    func notesSettingsItems() -> [SettingsItem] {
        [pluginItem(IslandPluginRegistry.notesID, page: .tools, keywords: ["notepad", "scratch", "jot", "text"])].compactMap { $0 }
    }
}
