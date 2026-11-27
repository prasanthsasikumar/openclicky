//
//  HandheldTabCycling.swift
//  OpenClicky
//
//  Tab cycling, a mode a handheld's button switches on and off (asked for on 2026-11-27). While
//  it is on, each click of the wheel is ⌃Tab (down: the next tab) or ⌃⇧Tab (up: the one before)
//  in the app in front, which is how Terminal, browsers and most tabbed apps move between tabs.
//
//  The wheel is an ordinary scroll wheel to the Mac, so the mode takes scroll events from the
//  whole session with an event tap and gives none of the wheel's clicks to the app under the
//  pointer. A trackpad's smooth scrolling passes through untouched. The tap exists only while
//  the mode is on, and the mode always ends: the same button again, 30 seconds with the wheel
//  left alone, the device going, the Mac sleeping, or the plugin switched off.
//

import AppKit
import CoreGraphics
import Foundation

@MainActor
final class HandheldTabCycling {
    static let shared = HandheldTabCycling()

    static let idleSecondsBeforeItEnds: TimeInterval = 30
    private static let tabKeyCode: UInt16 = 48
    static let nextTab = HandheldKeyCombination(keyCode: tabKeyCode, modifierFlags: CGEventFlags.maskControl.rawValue, displayText: "⌃Tab")
    static let previousTab = HandheldKeyCombination(keyCode: tabKeyCode, modifierFlags: CGEventFlags.maskControl.union(.maskShift).rawValue, displayText: "⌃⇧Tab")

    /// One click of the wheel is one tab, whatever amount the click scrolls by.
    static func keyCombination(forScrollDeltaY scrollDeltaY: CGFloat) -> HandheldKeyCombination? {
        switch WindowSwitcherModel.steps(forScrollDeltaY: scrollDeltaY) {
        case 1: return nextTab
        case -1: return previousTab
        default: return nil
        }
    }

    /// Only a wheel's clicks are taken. A trackpad's smooth scrolling goes on to the app.
    static func takes(scrollIsContinuous: Bool) -> Bool {
        !scrollIsContinuous
    }

    private(set) var isOn = false
    private var eventTap: CFMachPort?
    private var eventTapRunLoopSource: CFRunLoopSource?
    private var idleTimer: Timer?
    private var postKeyCombination: ((HandheldKeyCombination) -> Void)?
    private var say: ((String) -> Void)?

    /// Switches the mode: on when it is off, off when it is on. `say` shows a short line to the person.
    func toggle(postKeyCombination: @escaping (HandheldKeyCombination) -> Void, say: @escaping (String) -> Void) {
        if isOn {
            stop(reason: "the button")
            return
        }
        self.postKeyCombination = postKeyCombination
        self.say = say
        guard startEventTap() else {
            AppLog.append("handheld: tab cycling not started (no event tap; accessibility permission?)")
            say("tab cycling needs the Accessibility permission")
            return
        }
        isOn = true
        restartIdleTimer()
        AppLog.append("handheld: tab cycling on")
        say("tab cycling on: turn the wheel")
    }

    func stop(reason: String) {
        guard isOn else { return }
        isOn = false
        idleTimer?.invalidate()
        idleTimer = nil
        if let eventTap {
            CGEvent.tapEnable(tap: eventTap, enable: false)
            CFMachPortInvalidate(eventTap)
        }
        if let eventTapRunLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), eventTapRunLoopSource, .commonModes)
        }
        eventTap = nil
        eventTapRunLoopSource = nil
        AppLog.append("handheld: tab cycling off (\(reason))")
        say?("tab cycling off")
    }

    private func restartIdleTimer() {
        idleTimer?.invalidate()
        idleTimer = Timer.scheduledTimer(withTimeInterval: Self.idleSecondsBeforeItEnds, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.stop(reason: "the wheel was left alone") }
        }
    }

    private func startEventTap() -> Bool {
        let callback: CGEventTapCallBack = { _, eventType, event, userInfo in
            guard let userInfo else { return Unmanaged.passUnretained(event) }
            let tabCycling = Unmanaged<HandheldTabCycling>.fromOpaque(userInfo).takeUnretainedValue()
            // The tap's source is on the main run loop, so this is the main thread.
            let eventIsTaken = MainActor.assumeIsolated { tabCycling.handle(eventType: eventType, event: event) }
            return eventIsTaken ? nil : Unmanaged.passUnretained(event)
        }
        guard let eventTap = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
            eventsOfInterest: CGEventMask(1 << CGEventType.scrollWheel.rawValue),
            callback: callback, userInfo: Unmanaged.passUnretained(self).toOpaque()),
              let runLoopSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, eventTap, 0) else {
            return false
        }
        self.eventTap = eventTap
        eventTapRunLoopSource = runLoopSource
        CFRunLoopAddSource(CFRunLoopGetMain(), runLoopSource, .commonModes)
        CGEvent.tapEnable(tap: eventTap, enable: true)
        return true
    }

    /// True when the event is a click of a wheel and was turned into a tab key; the app under the
    /// pointer then never sees it.
    private func handle(eventType: CGEventType, event: CGEvent) -> Bool {
        if eventType == .tapDisabledByTimeout || eventType == .tapDisabledByUserInput {
            // macOS switches a slow tap off; the mode is still on, so the tap goes back on.
            if let eventTap { CGEvent.tapEnable(tap: eventTap, enable: true) }
            return false
        }
        guard isOn, eventType == .scrollWheel,
              Self.takes(scrollIsContinuous: event.getIntegerValueField(.scrollWheelEventIsContinuous) != 0),
              let keyCombination = Self.keyCombination(forScrollDeltaY: CGFloat(event.getIntegerValueField(.scrollWheelEventDeltaAxis1))) else {
            return false
        }
        postKeyCombination?(keyCombination)
        restartIdleTimer()
        return true
    }
}
