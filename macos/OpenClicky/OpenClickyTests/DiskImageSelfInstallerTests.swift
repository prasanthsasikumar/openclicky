//
//  DiskImageSelfInstallerTests.swift
//  OpenClickyTests
//

import Foundation
import Testing
@testable import OpenClicky

struct DiskImageSelfInstallerTests {
    @Test func runningFromApplicationsNeedsNothing() {
        #expect(DiskImageSelfInstaller.plan(bundlePath: "/Applications/OpenClicky.app", fileExists: { _ in true }) == .alreadyInstalled)
        #expect(DiskImageSelfInstaller.plan(bundlePath: "/Users/me/Downloads/OpenClicky.app", fileExists: { _ in false }) == .alreadyInstalled)
    }

    @Test func runningFromADiskImageOffersTheCopy() {
        #expect(DiskImageSelfInstaller.plan(bundlePath: "/Volumes/OpenClicky 0.6.0/OpenClicky.app", fileExists: { _ in false }) == .install(destination: "/Applications/OpenClicky.app", replacesExisting: false))
        #expect(DiskImageSelfInstaller.plan(bundlePath: "/Volumes/OpenClicky 0.6.0/OpenClicky.app", fileExists: { $0 == "/Applications/OpenClicky.app" }) == .install(destination: "/Applications/OpenClicky.app", replacesExisting: true))
    }

    @Test func installCopiesAndKeepsNoBackupOnSuccess() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-install-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("Volumes/X/OpenClicky.app")
        let destination = root.appendingPathComponent("Applications/OpenClicky.app")
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        try Data("new".utf8).write(to: source.appendingPathComponent("marker"))
        try Data("old".utf8).write(to: destination.appendingPathComponent("marker"))
        try DiskImageSelfInstaller.install(from: source.path, to: destination.path, replacesExisting: true)
        #expect(try String(contentsOf: destination.appendingPathComponent("marker"), encoding: .utf8) == "new")
        #expect(!FileManager.default.fileExists(atPath: destination.path + ".previous"))
    }
}
