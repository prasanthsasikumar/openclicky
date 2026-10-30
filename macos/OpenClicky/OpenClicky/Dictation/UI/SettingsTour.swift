//
//  SettingsTour.swift
//  OpenClicky
//
//  A one-minute tour of Settings: Clicky flies to the key settings in the real window, "clicks"
//  each page in the sidebar (a ripple, then the page opens), and says what it is for in recorded
//  narration, so it sounds the same with no key and no network. It plays once, the first time
//  the window opens on its own (not when something sent the person to a particular page), and
//  General › Startup › Settings tour plays it again. Esc, closing the window or a new question stops it.
//
//  Rows tell `SettingsTourTargets` where they are (`.settingsTourTarget(_:)`), in the window's
//  coordinates; the tour turns that into a screen point for the pointer, and asks the page to
//  scroll a row into view first.
//

import AppKit
import Combine
import SwiftUI

// MARK: - Where things are

/// Where the tour's targets sit in the window, as the rows report it, and the row a page should
/// scroll to. Frames are in the hosting view's coordinates (SwiftUI's `.global`, origin top left).
@MainActor
final class SettingsTourTargets: ObservableObject {
    static let shared = SettingsTourTargets()

    /// The row the visible page should scroll into view (the tour sets it before pointing).
    @Published var scrollRequest: String?

    private(set) var framesInWindow: [String: CGRect] = [:]

    func record(_ frame: CGRect, for targetID: String) {
        framesInWindow[targetID] = frame
    }

    /// The target's centre on screen (AppKit coordinates), or nil when it isn't laid out in `window`.
    func screenPoint(for targetID: String, in window: NSWindow) -> CGPoint? {
        guard let frame = framesInWindow[targetID], let contentView = window.contentView,
              frame.width > 0, frame.height > 0 else { return nil }
        let visibleContent = contentView.bounds
        let centreFromTop = CGPoint(x: frame.minX + min(frame.width * 0.3, 120), y: frame.midY)
        guard visibleContent.contains(CGPoint(x: centreFromTop.x, y: centreFromTop.y)) else { return nil }
        let pointInContentView = contentView.isFlipped
            ? centreFromTop
            : CGPoint(x: centreFromTop.x, y: visibleContent.height - centreFromTop.y)
        let pointInWindow = contentView.convert(pointInContentView, to: nil)
        return window.convertPoint(toScreen: pointInWindow)
    }

    nonisolated static func sidebarTargetID(for destination: DictationDestination) -> String {
        "sidebar.page.\(destination.sidebarPage.rawValue)"
    }
}

extension View {
    /// Reports this view's place in the window to the Settings tour under `targetID`.
    func settingsTourTarget(_ targetID: String) -> some View {
        background(
            GeometryReader { proxy in
                Color.clear
                    .onAppear { SettingsTourTargets.shared.record(proxy.frame(in: .global), for: targetID) }
                    .onChange(of: proxy.frame(in: .global)) { newFrame in
                        SettingsTourTargets.shared.record(newFrame, for: targetID)
                    }
            }
        )
    }
}

// MARK: - The steps

struct SettingsTourStep {
    /// The page the step is on; the sidebar is "clicked" when it differs from the page showing.
    let destination: DictationDestination
    /// The row to point at (a `SettingsItem` id), or nil to point at the page's sidebar row.
    let targetID: String?
    /// What the pointer's bubble says.
    let bubble: String
    let line: RecordedVoice.Line

    /// The page's name in the sidebar, for the bubble while the pointer goes to "click" it (Your
    /// words is under Dictation).
    var destinationTitle: String { destination.sidebarPage.title }

    /// About a minute in all: what someone needs to find their way, in sidebar order where it can be.
    static let all: [SettingsTourStep] = [
        SettingsTourStep(destination: .settings(.dictation), targetID: nil,
                         bubble: "six pages (esc stops the tour)", line: .settingsTourIntro),
        SettingsTourStep(destination: .settings(.dictation), targetID: "keyboard.dictationKey",
                         bubble: "the key you hold", line: .settingsTourDictation),
        SettingsTourStep(destination: .settings(.dictation), targetID: "dictation.language",
                         bubble: "your language", line: .settingsTourLanguage),
        SettingsTourStep(destination: .section(.words), targetID: nil,
                         bubble: "everything you said", line: .settingsTourWords),
        SettingsTourStep(destination: .settings(.clicky), targetID: "clicky.speechVoice",
                         bubble: "how i sound", line: .settingsTourVoice),
        SettingsTourStep(destination: .settings(.clicky), targetID: "clicky.pointer",
                         bubble: "when i show up", line: .settingsTourPointer),
        SettingsTourStep(destination: .settings(.clicky), targetID: "keyboard.talk",
                         bubble: "every shortcut", line: .settingsTourShortcuts),
        SettingsTourStep(destination: .settings(.account), targetID: "account.keys",
                         bubble: "your own keys", line: .settingsTourKeys),
        SettingsTourStep(destination: .settings(.general), targetID: "general.settingsTour",
                         bubble: "watch it again here", line: .settingsTourDone),
    ]
}

// MARK: - Playing it

@MainActor
final class SettingsTourController: ObservableObject {
    /// Set once the tour has played (or been stopped), so it doesn't start on its own again.
    nonisolated static let hasPlayedKey = "openclicky.settingsTourPlayed"

    @Published private(set) var isRunning = false

    private weak var companionManager: CompanionManager?
    private var runTask: Task<Void, Never>?
    private var escapeMonitors: [Any] = []

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
    }

    static var hasPlayed: Bool { UserDefaults.standard.bool(forKey: hasPlayedKey) }

    /// Plays the tour in `window`, whose pages `model` switches.
    func start(in window: NSWindow, model: DictationWindowModel) {
        guard !isRunning, let companionManager else { return }
        UserDefaults.standard.set(true, forKey: Self.hasPlayedKey)
        isRunning = true
        AppLog.append("settings tour: started")
        watchForEscape()
        runTask = Task { [weak self] in
            await self?.run(in: window, model: model, companionManager: companionManager)
            self?.finish(stopped: Task.isCancelled)
        }
    }

    /// Esc, the window closing, or a new question.
    func stop() {
        guard isRunning else { return }
        runTask?.cancel()
        companionManager?.recordedVoice.stop()
    }

    private func run(in window: NSWindow, model: DictationWindowModel, companionManager: CompanionManager) async {
        companionManager.beginSettingsTourPointing()
        // The window has just opened: let it lay out before the first flight.
        guard await pause(seconds: 0.8) else { return }
        for step in SettingsTourStep.all {
            if model.destination != step.destination || model.isSearching {
                let sidebarID = SettingsTourTargets.sidebarTargetID(for: step.destination)
                if let sidebarPoint = SettingsTourTargets.shared.screenPoint(for: sidebarID, in: window) {
                    companionManager.pointForSettingsTour(at: sidebarPoint, bubble: step.destinationTitle)
                    guard await pause(seconds: 1.0) else { return }
                    companionManager.showPointerClick()
                    guard await pause(seconds: 0.25) else { return }
                }
                model.select(step.destination)
                guard await pause(seconds: 0.6) else { return }
            }
            let targetID = step.targetID ?? SettingsTourTargets.sidebarTargetID(for: step.destination)
            if step.targetID != nil {
                SettingsTourTargets.shared.scrollRequest = targetID
                guard await pause(seconds: 0.5) else { return }
            }
            if let targetPoint = SettingsTourTargets.shared.screenPoint(for: targetID, in: window) {
                companionManager.pointForSettingsTour(at: targetPoint, bubble: step.bubble)
            } else {
                AppLog.append("settings tour: \(targetID) not on screen, said without pointing")
            }
            guard await say(step.line, companionManager: companionManager) else { return }
            guard await pause(seconds: 0.35) else { return }
        }
    }

    /// Plays a recorded line to its end. False once the tour was stopped.
    private func say(_ line: RecordedVoice.Line, companionManager: CompanionManager) async -> Bool {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            let played = companionManager.recordedVoice.play(line) { continuation.resume() }
            // A missing recording reports nothing: give the bubble a moment instead.
            if !played {
                DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { continuation.resume() }
            }
        }
        return !Task.isCancelled
    }

    /// Waits, and says whether the tour is still on.
    private func pause(seconds: Double) async -> Bool {
        try? await Task.sleep(for: .seconds(seconds))
        return !Task.isCancelled
    }

    private func finish(stopped: Bool) {
        isRunning = false
        runTask = nil
        escapeMonitors.forEach(NSEvent.removeMonitor)
        escapeMonitors = []
        companionManager?.endSettingsTourPointing()
        AppLog.append(stopped ? "settings tour: stopped" : "settings tour: finished")
    }

    private func watchForEscape() {
        let escapeKeyCode: UInt16 = 53
        if let localMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { [weak self] event in
            guard event.keyCode == escapeKeyCode, let self, self.isRunning else { return event }
            self.stop()
            return nil
        }) {
            escapeMonitors.append(localMonitor)
        }
        if let globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown, handler: { [weak self] event in
            guard event.keyCode == escapeKeyCode else { return }
            Task { @MainActor in self?.stop() }
        }) {
            escapeMonitors.append(globalMonitor)
        }
    }
}
