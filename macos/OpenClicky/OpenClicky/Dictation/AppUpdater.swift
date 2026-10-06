//
//  AppUpdater.swift
//  OpenClicky
//
//  Sparkle, started: daily checks against the feed in Info.plist (`SUFeedURL`, an appcast on the
//  GitHub release), updates verified with the EdDSA key beside it (`SUPublicEDKey`), and a
//  "check now" for the About page. Nothing starts when the feed is not configured, which is what
//  a development build looks like.
//

import Combine
import Foundation
import Sparkle

@MainActor
final class AppUpdater: ObservableObject {
    static let shared = AppUpdater()

    @Published private(set) var isConfigured = false
    @Published private(set) var lastCheckDescription = "never checked"

    private var controller: SPUStandardUpdaterController?

    var feedURL: String? { Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") as? String }

    func start() {
        guard controller == nil else { return }
        guard let feedURL, !feedURL.isEmpty,
              let publicKey = Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") as? String, !publicKey.isEmpty else {
            isConfigured = false
            return
        }
        let controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: nil, userDriverDelegate: nil)
        do {
            try controller.updater.start()
            self.controller = controller
            isConfigured = true
            refreshLastCheck()
        } catch {
            AppLog.append("sparkle could not start: \(error.localizedDescription)")
            isConfigured = false
        }
    }

    func checkNow() {
        guard let controller else { return }
        controller.checkForUpdates(nil)
        refreshLastCheck()
    }

    var automaticallyChecks: Bool {
        get { controller?.updater.automaticallyChecksForUpdates ?? false }
        set { controller?.updater.automaticallyChecksForUpdates = newValue }
    }

    private func refreshLastCheck() {
        if let date = controller?.updater.lastUpdateCheckDate {
            lastCheckDescription = "last checked " + date.formatted(date: .abbreviated, time: .shortened).lowercased()
        } else {
            lastCheckDescription = "never checked"
        }
    }
}
