//
//  GlobalPushToTalkShortcutMonitor.swift
//  OpenClicky
//
//  Captures push-to-talk keyboard shortcuts while OpenClicky is running in the
//  background. Uses a listen-only CGEvent tap so modifier-only shortcuts like
//  ctrl + option behave more like a real system-wide voice tool.
//

import AppKit
import Combine
import CoreGraphics
import Foundation

final class GlobalPushToTalkShortcutMonitor: ObservableObject {
    /// Talk (control + option) press / release, as before.
    let shortcutTransitionPublisher = PassthroughSubject<BuddyPushToTalkShortcut.ShortcutTransition, Never>()
    /// The other HeyClicky shortcuts: text composer (control ×2), dictation (fn + control held),
    /// hands-free toggle (fn + control ×2).
    let companionShortcutPublisher = PassthroughSubject<CompanionShortcutEvent, Never>()

    /// Recognises all four shortcuts from the tap's modifier and key events (main thread only).
    private var shortcutRecognizer = CompanionShortcutRecognizer()

    private var globalEventTap: CFMachPort?
    private var globalEventTapRunLoopSource: CFRunLoopSource?
    /// Mutated exclusively from the CGEvent tap callback, which runs on
    /// `CFRunLoopGetMain()` and therefore always executes on the main thread.
    /// Published so the overlay can hide immediately on key release without
    /// waiting for the async dictation state pipeline to catch up.
    @Published private(set) var isShortcutCurrentlyPressed = false

    deinit {
        stop()
    }

    func start() {
        // If the event tap is already running, don't restart it.
        // Restarting resets isShortcutCurrentlyPressed, which would kill
        // the waveform overlay mid-press when the permission poller calls
        // refreshAllPermissions → start() every few seconds.
        guard globalEventTap == nil else { return }

        let monitoredEventTypes: [CGEventType] = [.flagsChanged, .keyDown, .keyUp]
        let eventMask = monitoredEventTypes.reduce(CGEventMask(0)) { currentMask, eventType in
            currentMask | (CGEventMask(1) << eventType.rawValue)
        }

        let eventTapCallback: CGEventTapCallBack = { _, eventType, event, userInfo in
            guard let userInfo else {
                return Unmanaged.passUnretained(event)
            }

            let globalPushToTalkShortcutMonitor = Unmanaged<GlobalPushToTalkShortcutMonitor>
                .fromOpaque(userInfo)
                .takeUnretainedValue()

            return globalPushToTalkShortcutMonitor.handleGlobalEventTap(
                eventType: eventType,
                event: event
            )
        }

        guard let globalEventTap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: eventMask,
            callback: eventTapCallback,
            userInfo: Unmanaged.passUnretained(self).toOpaque()
        ) else {
            print("⚠️ Global push-to-talk: couldn't create CGEvent tap")
            return
        }

        guard let globalEventTapRunLoopSource = CFMachPortCreateRunLoopSource(
            kCFAllocatorDefault,
            globalEventTap,
            0
        ) else {
            CFMachPortInvalidate(globalEventTap)
            print("⚠️ Global push-to-talk: couldn't create event tap run loop source")
            return
        }

        self.globalEventTap = globalEventTap
        self.globalEventTapRunLoopSource = globalEventTapRunLoopSource

        CFRunLoopAddSource(CFRunLoopGetMain(), globalEventTapRunLoopSource, .commonModes)
        CGEvent.tapEnable(tap: globalEventTap, enable: true)
    }

    func stop() {
        isShortcutCurrentlyPressed = false
        shortcutRecognizer.reset()

        if let globalEventTapRunLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), globalEventTapRunLoopSource, .commonModes)
            self.globalEventTapRunLoopSource = nil
        }

        if let globalEventTap {
            CFMachPortInvalidate(globalEventTap)
            self.globalEventTap = nil
        }
    }

    private func handleGlobalEventTap(
        eventType: CGEventType,
        event: CGEvent
    ) -> Unmanaged<CGEvent>? {
        if eventType == .tapDisabledByTimeout || eventType == .tapDisabledByUserInput {
            if let globalEventTap {
                CGEvent.tapEnable(tap: globalEventTap, enable: true)
            }
            return Unmanaged.passUnretained(event)
        }

        let recognizerEventKind: CompanionShortcutRecognizer.EventKind?
        switch eventType {
        case .flagsChanged: recognizerEventKind = .flagsChanged
        case .keyDown: recognizerEventKind = .keyDown
        case .keyUp: recognizerEventKind = .keyUp
        default: recognizerEventKind = nil
        }
        guard let recognizerEventKind else { return Unmanaged.passUnretained(event) }

        let eventKeyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        let modifierFlags = NSEvent.ModifierFlags(rawValue: UInt(event.flags.rawValue)).intersection(.deviceIndependentFlagsMask)
        let shortcutEvents = shortcutRecognizer.handle(
            recognizerEventKind,
            keyCode: eventKeyCode,
            modifierFlags: modifierFlags,
            at: ProcessInfo.processInfo.systemUptime
        )

        for shortcutEvent in shortcutEvents {
            switch shortcutEvent {
            case .talkPressed:
                isShortcutCurrentlyPressed = true
                shortcutTransitionPublisher.send(.pressed)
            case .talkReleased:
                isShortcutCurrentlyPressed = false
                shortcutTransitionPublisher.send(.released)
            case .dictatePressed, .dictateReleased, .textComposerRequested, .handsFreeToggleRequested:
                companionShortcutPublisher.send(shortcutEvent)
            }
        }

        return Unmanaged.passUnretained(event)
    }
}
