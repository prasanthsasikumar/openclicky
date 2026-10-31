//
//  AppIconColorTests.swift
//  OpenClickyTests
//
//  The app icon's colour: every colour has its picture in the bundle, Signal hands the Dock back
//  the bundle's icon, the pick is kept, and General places the row.
//

import AppKit
import Testing
@testable import OpenClicky

@MainActor
struct AppIconColorTests {
    @Test func everyColourHasItsPicture() {
        for color in AppIconColor.allCases {
            #expect(NSImage(named: color.imageName) != nil, "missing \(color.imageName)")
        }
    }

    @Test func signalIsTheBundlesOwnIcon() {
        #expect(AppIconColor.signal.dockImage == nil)
        #expect(AppIconColor.aqua.dockImage != nil)
    }

    @Test func thePickIsKeptAndStartsAsSignal() {
        let defaults = ScratchDefaults.make()
        #expect(DictationSettings(defaults: defaults).appIconColor == .signal)
        let settings = DictationSettings(defaults: defaults)
        settings.appIconColor = .ultraviolet
        #expect(DictationSettings(defaults: defaults).appIconColor == .ultraviolet)
        settings.appIconColor = .signal
    }

    @Test func generalPlacesTheRow() {
        let general = SettingsLayout.groups.filter { $0.page == .general && $0.showsOnPage }
        #expect(general.contains { $0.itemIDs.contains("general.appIcon") })
    }
}
