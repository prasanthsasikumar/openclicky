//
//  OpenClickyApp.swift
//  OpenClicky
//
//  Menu bar-only companion app. No dock icon, no main window — just an
//  always-available status item in the macOS menu bar. Clicking the icon
//  opens a floating panel with companion voice controls.
//

import ServiceManagement
import SwiftUI
import Sparkle

@main
struct OpenClickyApp: App {
    @NSApplicationDelegateAdaptor(CompanionAppDelegate.self) var appDelegate

    var body: some Scene {
        // The app lives entirely in the menu bar panel managed by the AppDelegate.
        // This empty Settings scene satisfies SwiftUI's requirement for at least
        // one scene but is never shown (LSUIElement=true removes the app menu).
        Settings {
            EmptyView()
        }
    }
}

/// Manages the companion lifecycle: creates the menu bar panel and starts
/// the companion voice pipeline on launch.
@MainActor
final class CompanionAppDelegate: NSObject, NSApplicationDelegate {
    private var menuBarPanelManager: MenuBarPanelManager?
    private let companionManager = CompanionManager()
    private var sparkleUpdaterController: SPUStandardUpdaterController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // OpenClicky: headless check of the agent lane (gate + Codex run via the CLI) without any
        // GUI, permissions, or screen capture. Used by scripted builds:
        //   OpenClicky.app/Contents/MacOS/OpenClicky --openclicky-smoke-run "create a file called x.txt containing 'y'"
        let launchArguments = CommandLine.arguments
        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-run"), argumentIndex + 1 < launchArguments.count {
            let transcript = launchArguments[argumentIndex + 1]
            Task { @MainActor in
                let agentClient = OpenClickyAgentClient()
                let lane = await agentClient.classifyLane(for: transcript)
                print("smoke: lane=\(lane)")
                var exitCode: Int32 = 0
                if lane == "agent" {
                    do {
                        let result = try await agentClient.runAgent(task: transcript, screenshotPath: nil, threadId: nil) { milestone in
                            print("smoke: ▸ \(milestone)")
                        }
                        print("smoke: status=\(result.status) thread=\(result.threadId ?? "-")")
                        print("smoke: text=\(result.text)")
                        print("smoke: artifacts=\(result.artifacts)")
                        if result.status != "completed" { exitCode = 2 }
                    } catch {
                        print("smoke: error=\(error.localizedDescription)")
                        exitCode = 1
                    }
                }
                exit(exitCode)
            }
            return
        }

        print("🎯 OpenClicky: Starting...")
        print("🎯 OpenClicky: Version \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "unknown")")

        UserDefaults.standard.register(defaults: ["NSInitialToolTipDelay": 0])

        ClickyAnalytics.configure()
        ClickyAnalytics.trackAppOpened()

        menuBarPanelManager = MenuBarPanelManager(companionManager: companionManager)
        companionManager.start()
        // Auto-open the panel if the user still needs to do something:
        // either they haven't onboarded yet, or permissions were revoked.
        if !companionManager.hasCompletedOnboarding || !companionManager.allPermissionsGranted {
            menuBarPanelManager?.showPanelOnLaunch()
        }
        // OpenClicky: launching at login is opt-in (`registerAsLoginItem` in ~/.openclicky/shell.json).
        if OpenClickyConfiguration.settings.registerAsLoginItem == true {
            registerAsLoginItemIfNeeded()
        }
        // startSparkleUpdater()
    }

    func applicationWillTerminate(_ notification: Notification) {
        companionManager.stop()
    }

    /// Registers the app as a login item so it launches automatically on
    /// startup. Uses SMAppService which shows the app in System Settings >
    /// General > Login Items, letting the user toggle it off if they want.
    private func registerAsLoginItemIfNeeded() {
        let loginItemService = SMAppService.mainApp
        if loginItemService.status != .enabled {
            do {
                try loginItemService.register()
                print("🎯 OpenClicky: Registered as login item")
            } catch {
                print("⚠️ OpenClicky: Failed to register as login item: \(error)")
            }
        }
    }

    private func startSparkleUpdater() {
        let updaterController = SPUStandardUpdaterController(
            startingUpdater: false,
            updaterDelegate: nil,
            userDriverDelegate: nil
        )
        self.sparkleUpdaterController = updaterController

        do {
            try updaterController.updater.start()
        } catch {
            print("⚠️ OpenClicky: Sparkle updater failed to start: \(error)")
        }
    }
}
