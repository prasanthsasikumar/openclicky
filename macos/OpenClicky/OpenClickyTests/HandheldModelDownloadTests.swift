//
//  HandheldModelDownloadTests.swift
//  OpenClickyTests
//
//  The device's 3D model is not in the app: 13 MB that only an owner of the device would use.
//  The Handheld page offers to download it, once, into the person's own folder.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct HandheldModelDownloadTests {
    private func scratchFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("HandheldModelDownloadTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// A file standing in for a finished download, and the download that describes it.
    private func downloadedFile(in folder: URL, contents: String, claimedContents: String? = nil) throws -> (file: URL, download: HandheldModelDownload) {
        let file = folder.appendingPathComponent("downloaded-\(UUID().uuidString).tmp")
        try Data(contents.utf8).write(to: file)
        let described = folder.appendingPathComponent("described.tmp")
        try Data((claimedContents ?? contents).utf8).write(to: described)
        let download = HandheldModelDownload(url: URL(string: "https://example.invalid/model.glb")!,
                                             sha256: try ModelDownload.sha256(of: described), bytes: 13_302_608)
        return (file, download)
    }

    @Test func theWhisPromptModelComesFromThisProjectsRepositoryWithAPinnedChecksum() throws {
        let download = try #require(WhisPromptHandheld.device.modelDownload)
        #expect(download.url.absoluteString == "https://raw.githubusercontent.com/prasanthsasikumar/openclicky/main/assets/handheld/handheld-whisprompt.glb")
        #expect(download.sha256 == "127ebaa8da72223825b3480511cca7a98f123377431c3b7f44a68e51ecddab28")
        #expect(download.bytes == 13_302_608)
        #expect(download.sizeText == "13 MB")
    }

    @Test func aDownloadThatMatchesLandsInThePersonsFolder() async throws {
        let folder = try scratchFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let (file, download) = try downloadedFile(in: folder, contents: "the model")
        let destination = folder.appendingPathComponent("home/.openclicky/handheld/whisprompt.glb")
        let installer = HandheldModelInstaller(fetch: { _ in file })
        var finishedCount = 0

        await installer.install(download, deviceID: "whisprompt", to: destination) { finishedCount += 1 }

        #expect(try String(contentsOf: destination, encoding: .utf8) == "the model")
        #expect(installer.state(forDeviceID: "whisprompt") == .installed)
        #expect(finishedCount == 1)
    }

    @Test func aDownloadThatDoesNotMatchItsChecksumIsThrownAway() async throws {
        let folder = try scratchFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let (file, download) = try downloadedFile(in: folder, contents: "something else", claimedContents: "the model")
        let destination = folder.appendingPathComponent("home/.openclicky/handheld/whisprompt.glb")
        let installer = HandheldModelInstaller(fetch: { _ in file })

        await installer.install(download, deviceID: "whisprompt", to: destination) {}

        #expect(!FileManager.default.fileExists(atPath: destination.path))
        #expect(!FileManager.default.fileExists(atPath: file.path))
        #expect(installer.state(forDeviceID: "whisprompt") == .failed("the download didn't match its checksum"))
    }

    @Test func aDownloadThatFailsSaysSoAndCanBeTriedAgain() async throws {
        let folder = try scratchFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let (_, download) = try downloadedFile(in: folder, contents: "the model")
        let installer = HandheldModelInstaller(fetch: { _ in throw ModelDownloadError.downloadFailed("the server answered 404") })

        await installer.install(download, deviceID: "whisprompt", to: folder.appendingPathComponent("whisprompt.glb")) {}

        #expect(installer.state(forDeviceID: "whisprompt") == .failed("the download failed: the server answered 404."))
        #expect(installer.state(forDeviceID: "another") == .notInstalled)
    }

    // MARK: the offer on the page

    private func relevantIDs(modelIsAvailable: Bool) -> [String] {
        let scratchSuiteName = "HandheldModelDownloadTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: scratchSuiteName)!
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        defaults.set(true, forKey: HandheldPlugin.enabledKey)
        let source = FakeHandheldReportSource()
        let plugin = HandheldPlugin(defaults: defaults, makeSource: { source }, makerAppIsRunning: { false })
        plugin.attach(to: RecordingHandheldActionHost())
        source.connectWhisPrompt()
        return HandheldSettings.items(plugin: plugin, deviceModelIsAvailable: { _ in modelIsAvailable })
            .filter { $0.isRelevant() }.map(\.id)
    }

    @Test func thePageOffersThePictureOnlyUntilItIsThere() {
        #expect(relevantIDs(modelIsAvailable: false).contains("handheld.modelOffer"))
        #expect(!relevantIDs(modelIsAvailable: true).contains("handheld.modelOffer"))
    }

    @Test func theOfferSitsAboveTheButtons() {
        let group = SettingsLayout.groups.first { $0.itemIDs.contains("handheld.device") }
        #expect(group?.itemIDs.prefix(2) == ["handheld.device", "handheld.modelOffer"])
    }
}
