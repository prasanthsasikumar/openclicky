//
//  OpenClickyApp.swift
//  OpenClicky
//
//  Menu bar-only companion app. No dock icon, no main window — just an
//  always-available status item in the macOS menu bar. Clicking the icon
//  opens a floating panel with companion voice controls.
//

import AVFoundation
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

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-talk-file"), argumentIndex + 1 < launchArguments.count {
            // Headless push-to-talk check with a recorded utterance instead of the microphone:
            // connect, press, stream the file as mic audio in real time, release, print the reply.
            let filePath = launchArguments[argumentIndex + 1]
            setvbuf(stdout, nil, _IONBF, 0)
            print("smoke: push-to-talk with \(filePath)")
            Task { @MainActor in
                let client = RealtimeVoiceClient()
                var finished = false
                client.onEvent = { print("smoke: ▸ \($0)") }
                client.onTranscript = { role, text in print("smoke: \(role == .user ? "you" : "openclicky"): \(text)") }
                client.onAgentTask = { task in "smoke agent would run: \(task)" }
                client.screenContextProvider = { await CompanionScreenCaptureUtility.captureCursorScreenContext() }
                client.onPointAt = { screenshotPoint, label, capture in
                    // No overlay in the smoke harness: print what the buddy would fly to.
                    let screenLocation = CompanionManager.screenLocation(forScreenshotPoint: screenshotPoint, in: capture)
                    let formatted = { (value: CGFloat) in String(format: "%.0f", value) }
                    print("🎯 Element pointing: (\(formatted(screenshotPoint.x)), \(formatted(screenshotPoint.y))) → \"\(label)\" → screen (\(formatted(screenLocation.x)), \(formatted(screenLocation.y)))")
                }
                client.onResponseFinished = { finished = true }
                do {
                    let pcm = try Self.loadPCM16Mono24k(path: filePath)
                    let started = Date()
                    try await client.connectIfNeeded(mode: .pushToTalk)
                    print("smoke: connected in \(Int(Date().timeIntervalSince(started) * 1000)) ms")
                    // Installed after connect (like a skill toggled / app switch mid-session) so the
                    // key-down refresh has a changed prompt to send: expect `instructions updated (N chars)`.
                    client.instructionsProvider = {
                        RealtimeVoiceClient.defaultInstructions + "\n\n## Skill: smoke\nAlways mention the word 'harness' once."
                    }
                    client.beginPushToTalk()
                    var waitedForMic = 0
                    while !client.isCapturing && waitedForMic < 40 {
                        try? await Task.sleep(nanoseconds: 50_000_000)
                        waitedForMic += 1
                    }
                    let chunk = 4800 // 100 ms
                    var offset = 0
                    while offset < pcm.count {
                        let end = min(offset + chunk, pcm.count)
                        client.injectMicrophoneAudio(pcm16: pcm.subdata(in: offset..<end))
                        offset = end
                        try? await Task.sleep(nanoseconds: 100_000_000)
                    }
                    client.endPushToTalk()
                    var waited = 0.0
                    while !finished && waited < 20 {
                        try? await Task.sleep(nanoseconds: 250_000_000)
                        waited += 0.25
                    }
                    print("smoke: \(await client.debugSummary())")
                    try? await Task.sleep(nanoseconds: 3_000_000_000)
                    print("smoke: after idle → \(await client.debugSummary())")
                    client.disconnect(reason: finished ? "smoke done" : "smoke timed out waiting for the reply")
                    exit(finished ? 0 : 1)
                } catch {
                    print("smoke: error=\(error.localizedDescription)")
                    exit(1)
                }
            }
            return
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-talk"), argumentIndex + 1 < launchArguments.count {
            // Headless Realtime check: connect, greet, listen for N seconds, print transcripts, exit.
            let seconds = Double(launchArguments[argumentIndex + 1]) ?? 10
            setvbuf(stdout, nil, _IONBF, 0)
            print("smoke: starting (\(seconds)s)")
            Task { @MainActor in
                let client = RealtimeVoiceClient()
                client.onEvent = { print("smoke: ▸ \($0)") }
                client.onTranscript = { role, text in print("smoke: \(role == .user ? "you" : "openclicky"): \(text)") }
                client.onAgentTask = { task in "smoke agent would run: \(task)" }
                do {
                    try await withThrowingTaskGroup(of: Void.self) { group in
                        group.addTask { try await client.connectIfNeeded(mode: .alwaysOn) }
                        group.addTask { try await Task.sleep(nanoseconds: 20_000_000_000); throw RealtimeVoiceError.backend("connect timed out after 20 s") }
                        try await group.next()
                        group.cancelAll()
                    }
                    client.startListeningContinuously()
                    client.requestResponse(instructions: "Greet the user in English in one short sentence as OpenClicky.")
                    var elapsed = 0.0
                    while elapsed < seconds {
                        try? await Task.sleep(nanoseconds: 3_000_000_000)
                        elapsed += 3
                        print("smoke: \(await client.debugSummary())")
                    }
                    client.disconnect(reason: "smoke done")
                    exit(0)
                } catch {
                    print("smoke: error=\(error.localizedDescription)")
                    exit(1)
                }
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


extension CompanionAppDelegate {
    /// Reads any audio file and returns PCM16 mono 24 kHz bytes (the Realtime input format).
    static func loadPCM16Mono24k(path: String) throws -> Data {
        let file = try AVAudioFile(forReading: URL(fileURLWithPath: path))
        guard let source = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else {
            throw RealtimeVoiceError.audio("cannot allocate a buffer for \(path)")
        }
        try file.read(into: source)
        let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 24_000, channels: 1, interleaved: true)!
        let monoFormat = AVAudioFormat(standardFormatWithSampleRate: file.processingFormat.sampleRate, channels: 1)!
        guard let mono = AVAudioPCMBuffer(pcmFormat: monoFormat, frameCapacity: source.frameLength),
              let from = source.floatChannelData?[0], let to = mono.floatChannelData?[0] else {
            throw RealtimeVoiceError.audio("unsupported sample format in \(path)")
        }
        to.update(from: from, count: Int(source.frameLength))
        mono.frameLength = source.frameLength
        guard let converter = AVAudioConverter(from: monoFormat, to: target),
              let out = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: AVAudioFrameCount(Double(source.frameLength) * 24_000 / monoFormat.sampleRate) + 64) else {
            throw RealtimeVoiceError.audio("cannot convert \(path)")
        }
        var consumed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if consumed { status.pointee = .noDataNow; return nil }
            consumed = true; status.pointee = .haveData; return mono
        }
        if let error { throw error }
        return Data(bytes: out.int16ChannelData![0], count: Int(out.frameLength) * 2)
    }
}
