//
//  MirrorPluginTests.swift
//  OpenClickyTests
//
//  The Mirror tool: a plugin with a button in the band, the words it shows when the camera is
//  not allowed, and the one place a plugin is switched on or off (shared by Settings and the
//  Teacher's own switch on the island).
//

import AVFoundation
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct MirrorPluginTests {
    @Test func theMirrorIsAToolPlugin() {
        let mirror = IslandPluginRegistry.all.first { $0.id == IslandPluginRegistry.mirrorID }
        #expect(mirror != nil)
        #expect(mirror?.isPlugin == true)
        #expect(mirror?.title == "Mirror")
        #expect(IslandPluginRegistry.toolIDs.last == IslandPluginRegistry.mirrorID)
    }

    @Test func theCameraNoteSaysWhyThereIsNoPicture() {
        #expect(MirrorPermissionNote.text(for: .authorized) == nil)
        #expect(MirrorPermissionNote.text(for: .notDetermined) == nil)
        #expect(MirrorPermissionNote.text(for: .denied)?.contains("System Settings") == true)
        #expect(MirrorPermissionNote.text(for: .restricted)?.contains("not allowed") == true)
    }

    @Test func aPluginIsSwitchedOnAndOffInOnePlace() {
        let defaults = ScratchDefaults.make()
        var settings = IslandComponentSettings(defaults: defaults)
        settings.enabledPlugins = []            // a person who switched the defaults off
        #expect(!settings.enabledPlugins.contains(IslandPluginRegistry.teacherID))
        settings.setPlugin(IslandPluginRegistry.teacherID, enabled: true)
        #expect(settings.enabledPlugins == [IslandPluginRegistry.teacherID])
        settings.setPlugin(IslandPluginRegistry.teacherID, enabled: true)
        #expect(settings.enabledPlugins == [IslandPluginRegistry.teacherID], "switching on twice keeps one entry")
        settings.setPlugin(IslandPluginRegistry.mirrorID, enabled: true)
        settings.setPlugin(IslandPluginRegistry.teacherID, enabled: false)
        #expect(settings.enabledPlugins == [IslandPluginRegistry.mirrorID])
    }
}
