//
//  ClipboardSettings.swift
//  OpenClicky
//
//  The Clipboard plugin's rows in Settings.
//

import SwiftUI

extension SettingsCatalog {
    func clipboardSettingsItems() -> [SettingsItem] {
        [pluginItem(IslandPluginRegistry.clipboardID, keywords: ["copy", "paste", "pasteboard", "history"])].compactMap { $0 }
    }
}
