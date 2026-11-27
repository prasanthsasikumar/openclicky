//
//  WindowSwitcherPanel.swift
//  OpenClicky
//
//  The window switcher on screen: a see-through panel over the display the pointer is on, with
//  the stack of window cards in its middle. It covers the display so that the wheel's scroll
//  events, which macOS sends to whatever is under the pointer, arrive here while it is open and
//  nowhere else. It never takes the keyboard: the app in front keeps it until a window is chosen.
//

import AppKit
import ApplicationServices
import SwiftUI

@MainActor
final class HandheldWindowSwitcher {
    static let shared = HandheldWindowSwitcher()

    /// Left alone this long, the switcher closes without choosing.
    private static let idleSeconds: TimeInterval = 6
    private static let escapeKeyCode: UInt16 = 53

    private let model = WindowSwitcherModel()
    private var panel: NSPanel?
    private var idleTimer: Timer?
    private var escapeMonitor: Any?

    /// The wheel button's action: opens the stack, or with it open goes to the window in front.
    func toggle(reduceMotion: Bool) {
        if model.isOpen {
            confirm()
        } else {
            open(reduceMotion: reduceMotion)
        }
    }

    /// Closes the stack without going to any window, if it is open (tab cycling is taking the wheel).
    func close() {
        guard model.isOpen else { return }
        dismiss()
    }

    private func open(reduceMotion: Bool) {
        model.open(with: SwitcherWindowList.current())
        guard model.isOpen else {
            AppLog.append("handheld: no windows to switch between")
            return
        }
        let pointerLocation = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(pointerLocation) } ?? NSScreen.main
        guard let screen else { return }

        let newPanel = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        newPanel.isOpaque = false
        newPanel.backgroundColor = .clear
        newPanel.hasShadow = false
        newPanel.level = .popUpMenu
        newPanel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        newPanel.isReleasedWhenClosed = false
        let hostingView = WindowSwitcherHostingView(rootView: WindowSwitcherView(
            model: model, reduceMotion: reduceMotion,
            onChoose: { [weak self] windowNumber in
                self?.model.select(windowNumber: windowNumber)
                self?.confirm()
            },
            onDismiss: { [weak self] in self?.dismiss() }))
        hostingView.onScroll = { [weak self] scrollDeltaY in
            self?.model.move(by: WindowSwitcherModel.steps(forScrollDeltaY: scrollDeltaY))
            self?.restartIdleTimer()
        }
        newPanel.contentView = hostingView
        newPanel.setFrame(screen.frame, display: true)
        newPanel.orderFrontRegardless()
        panel = newPanel

        escapeMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] keyEvent in
            guard keyEvent.keyCode == Self.escapeKeyCode else { return }
            Task { @MainActor in self?.dismiss() }
        }
        restartIdleTimer()
        AppLog.append("handheld: window switcher opened with \(model.windows.count) windows")
    }

    private func confirm() {
        let chosenWindow = model.confirm()
        closePanel()
        guard let chosenWindow else { return }
        Self.bringForward(chosenWindow)
    }

    private func dismiss() {
        model.dismiss()
        closePanel()
    }

    private func closePanel() {
        idleTimer?.invalidate()
        idleTimer = nil
        if let escapeMonitor { NSEvent.removeMonitor(escapeMonitor) }
        escapeMonitor = nil
        panel?.orderOut(nil)
        panel = nil
    }

    private func restartIdleTimer() {
        idleTimer?.invalidate()
        idleTimer = Timer.scheduledTimer(withTimeInterval: Self.idleSeconds, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.dismiss() }
        }
    }

    /// Raises the chosen window through Accessibility (the window of that app with that title;
    /// the app's first window when titles cannot be told apart) and activates its app.
    private static func bringForward(_ window: SwitcherWindow) {
        let appElement = AXUIElementCreateApplication(window.ownerPID)
        AXUIElementSetMessagingTimeout(appElement, 0.25)
        var windowsValue: CFTypeRef?
        if AXUIElementCopyAttributeValue(appElement, kAXWindowsAttribute as CFString, &windowsValue) == .success,
           let windowElements = windowsValue as? [AXUIElement] {
            let windowWithTheTitle = windowElements.first { windowElement in
                AXUIElementSetMessagingTimeout(windowElement, 0.25)
                var titleValue: CFTypeRef?
                return AXUIElementCopyAttributeValue(windowElement, kAXTitleAttribute as CFString, &titleValue) == .success
                    && (titleValue as? String) == window.title
            }
            if let windowToRaise = windowWithTheTitle ?? windowElements.first {
                AXUIElementPerformAction(windowToRaise, kAXRaiseAction as CFString)
            }
        }
        NSRunningApplication(processIdentifier: window.ownerPID)?.activate()
        AppLog.append("handheld: switched to a window of \(window.appName)")
    }
}

/// Hands the wheel's scroll events to the switcher. They come here because the panel is what is
/// under the pointer.
private final class WindowSwitcherHostingView: NSHostingView<WindowSwitcherView> {
    var onScroll: ((CGFloat) -> Void)?

    override func scrollWheel(with event: NSEvent) {
        onScroll?(event.scrollingDeltaY)
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

struct WindowSwitcherView: View {
    @ObservedObject var model: WindowSwitcherModel
    let reduceMotion: Bool
    let onChoose: (Int) -> Void
    let onDismiss: () -> Void
    @State private var hasAppeared = false

    var body: some View {
        ZStack {
            // A click anywhere off the cards closes the switcher without choosing.
            Color.black.opacity(hasAppeared ? 0.28 : 0)
                .contentShape(Rectangle())
                .onTapGesture { onDismiss() }

            ZStack {
                ForEach(model.windows) { window in
                    let index = model.windows.firstIndex(of: window) ?? 0
                    let offset = WindowSwitcherLayout.offset(of: index, selectedIndex: model.selectedIndex, count: model.windows.count)
                    WindowSwitcherCard(window: window, isInFront: offset == 0)
                        .scaleEffect(WindowSwitcherLayout.scale(forOffset: offset))
                        .opacity(WindowSwitcherLayout.opacity(forOffset: offset))
                        .offset(y: CGFloat(offset) * WindowSwitcherLayout.cardSpacing)
                        .zIndex(Double(-abs(offset)))
                        .onTapGesture { onChoose(window.windowNumber) }
                        .pointerCursor()
                }
            }
            .scaleEffect(hasAppeared ? 1 : 0.94)
            .opacity(hasAppeared ? 1 : 0)
            .animation(Motion.settle(reduceMotion: reduceMotion), value: model.selectedIndex)
        }
        .ignoresSafeArea()
        .onAppear {
            withAnimation(Motion.pop(reduceMotion: reduceMotion)) { hasAppeared = true }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Window switcher")
    }
}

private struct WindowSwitcherCard: View {
    let window: SwitcherWindow
    let isInFront: Bool

    var body: some View {
        HStack(spacing: 14) {
            if let appIcon = NSRunningApplication(processIdentifier: window.ownerPID)?.icon {
                Image(nsImage: appIcon).resizable().frame(width: 36, height: 36)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(window.title)
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(.white)
                    .lineLimit(1)
                if window.title != window.appName {
                    Text(window.appName)
                        .font(.system(size: 12))
                        .foregroundStyle(.white.opacity(0.6))
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 18)
        .frame(width: WindowSwitcherLayout.cardWidth, height: WindowSwitcherLayout.cardHeight)
        .background(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(Color.black.opacity(isInFront ? 0.92 : 0.78))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .strokeBorder(Color.white.opacity(isInFront ? 0.55 : 0.12), lineWidth: isInFront ? 1.5 : 1)
        )
        .shadow(color: .black.opacity(isInFront ? 0.45 : 0.2), radius: isInFront ? 18 : 8, y: 6)
        .accessibilityLabel("\(window.title), \(window.appName)")
    }
}
