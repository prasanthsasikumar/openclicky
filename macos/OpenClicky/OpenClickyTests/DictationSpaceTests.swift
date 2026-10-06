//
//  DictationSpaceTests.swift
//  OpenClickyTests
//
//  Styles, dictionary and shortcuts: seeded defaults, app assignment, and the JSON files.
//

import Foundation
import Testing
@testable import OpenClicky

struct DictationSpaceTests {
    @Test func theSeededStylesClaimKnownAppsAndFallBackToOther() {
        let space = DictationSpace.empty
        #expect(space.style(forAppBundleID: "com.apple.dt.Xcode").id == "developer")
        #expect(space.style(forAppBundleID: "com.tinyspeck.slackmacgap").id == "work-messaging")
        #expect(space.style(forAppBundleID: "com.example.unknown").id == "other")
        // Browsers carry Slack, GitHub and Notion as much as Gmail: they are "other apps", not email.
        #expect(space.style(forAppBundleID: "com.google.Chrome").id == "other")
        #expect(space.style(forAppBundleID: nil).id == "other")
    }

    @Test func assigningAnAppMovesItBetweenStyles() {
        var space = DictationSpace.empty
        space.assign(appBundleID: "com.apple.dt.Xcode", toStyleID: "email")
        #expect(space.style(forAppBundleID: "com.apple.dt.Xcode").id == "email")
        #expect(!space.styles.first { $0.id == "developer" }!.appBundleIDs.contains("com.apple.dt.Xcode"))
    }

    @Test @MainActor func theStoreRoundTripsThroughItsFiles() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-space-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = DictationSpaceStore(directoryURL: directory)
        #expect(store.space.styles.count == 5)
        store.update { space in
            space.dictionary.append(DictionaryTerm(written: "FlowsXR", heardAs: ["flows xr"]))
            space.shortcuts.append(SpokenShortcut(trigger: "my sign-off", replacement: "Warm regards"))
            space.assign(appBundleID: "com.example.app", toStyleID: "email")
        }
        #expect(FileManager.default.fileExists(atPath: directory.appendingPathComponent("dictionary.json").path))
        let reloaded = DictationSpaceStore(directoryURL: directory)
        #expect(reloaded.space == store.space)
        #expect(reloaded.space.dictionary.first?.heardAs == ["flows xr"])
        #expect(reloaded.space.style(forAppBundleID: "com.example.app").id == "email")
    }
}
