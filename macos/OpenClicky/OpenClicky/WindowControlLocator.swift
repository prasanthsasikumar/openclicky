//
//  WindowControlLocator.swift
//  OpenClicky
//
//  `point_at` for a window's close, minimize and zoom buttons. They are 12 px discs with no
//  caption, so OCR has nothing to snap to and a screenshot model guesses: asked for the Terminal's
//  yellow button, Claude put it in the menu bar next to "Window" (2026-10-27). macOS knows exactly
//  where they are: the front app's focused window exposes each one as an Accessibility attribute
//  with a frame. Runs first in the pointing chain, before any guessing.
//

import AppKit
import ApplicationServices
import Foundation

nonisolated enum WindowControl: Equatable {
    case close
    case minimize
    case zoom

    var accessibilityAttribute: String {
        switch self {
        case .close: return kAXCloseButtonAttribute
        case .minimize: return kAXMinimizeButtonAttribute
        case .zoom: return kAXZoomButtonAttribute
        }
    }

    var name: String {
        switch self {
        case .close: return "close"
        case .minimize: return "minimize"
        case .zoom: return "zoom"
        }
    }
}

nonisolated enum WindowControlLocator {
    // MARK: - Entry point

    /// The screenshot-pixel centre of the window button the request is about, when the request is
    /// about one and the front app's focused window shows it on the captured display.
    @MainActor
    static func locate(label: String, text: String, userRequest: String?, in capture: CompanionScreenCapture) -> (point: CGPoint, control: WindowControl, appName: String)? {
        guard let control = requestedControl(label: label, text: text, userRequest: userRequest) else { return nil }
        guard AXIsProcessTrusted(), let app = NSWorkspace.shared.frontmostApplication else { return nil }
        let application = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(application, 0.25)
        guard let window = copyElement(application, kAXFocusedWindowAttribute) ?? copyElement(application, kAXMainWindowAttribute),
              let button = copyElement(window, control.accessibilityAttribute),
              let frame = copyFrame(button) else {
            AppLog.append("window control: \(control.name) button of \(app.localizedName ?? "the front app") could not be read")
            return nil
        }
        let primaryDisplayHeight = NSScreen.screens.first?.frame.height ?? capture.displayFrame.height
        let point = capture.screenshotPoint(forGlobalPoint: CGPoint(x: frame.midX, y: frame.midY), primaryDisplayHeight: primaryDisplayHeight)
        guard capture.containsScreenshotPoint(point) else {
            AppLog.append("window control: \(control.name) button of \(app.localizedName ?? "the front app") is off the capture; not pointing")
            return nil
        }
        return (point, control, app.localizedName ?? "the front app")
    }

    // MARK: - Pure part (unit-tested)

    /// Which window button the request asks for, or nil when it is about something else. The
    /// model's label is trusted first ("minimize button"); the user's own words count when they
    /// name the button or the window ("the yellow button", "minimize this window"), never a tab
    /// ("close this tab" is the tab's own ✕, which the tab locator or OCR finds).
    static func requestedControl(label: String, text: String, userRequest: String?) -> WindowControl? {
        if let control = control(named: label), words(in: label).contains("button") { return control }
        if let control = control(named: text), words(in: text).contains("button") { return control }
        guard let userRequest else { return nil }
        let requestWords = words(in: userRequest)
        guard !requestWords.contains("tab"), !requestWords.contains("tabs") else { return nil }
        guard let control = control(named: userRequest) else { return nil }
        let aboutTheWindow = requestWords.contains { ["window", "button", "app", "terminal", "it", "this"].contains($0) }
        return aboutTheWindow ? control : nil
    }

    private static func control(named text: String) -> WindowControl? {
        let textWords = Set(words(in: text))
        let mentionsButton = !textWords.isDisjoint(with: ["button", "dot", "circle", "light", "lights"])
        if !textWords.isDisjoint(with: ["minimize", "minimise", "minimizing", "minimising"]) || (mentionsButton && textWords.contains("yellow")) {
            return .minimize
        }
        if !textWords.isDisjoint(with: ["zoom", "maximize", "maximise", "maximizing", "maximising", "fullscreen", "enlarge"])
            || (textWords.contains("full") && textWords.contains("screen"))
            || (mentionsButton && textWords.contains("green")) {
            return .zoom
        }
        if textWords.contains("close") || textWords.contains("closing") || (mentionsButton && textWords.contains("red")) {
            return .close
        }
        return nil
    }

    private static func words(in text: String) -> [String] {
        text.lowercased()
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
    }

    // MARK: - Accessibility

    private static func copyValue(_ element: AXUIElement, _ attribute: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value
    }

    private static func copyElement(_ element: AXUIElement, _ attribute: String) -> AXUIElement? {
        guard let value = copyValue(element, attribute), CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }

    private static func copyFrame(_ element: AXUIElement) -> CGRect? {
        guard let positionValue = copyValue(element, kAXPositionAttribute), let sizeValue = copyValue(element, kAXSizeAttribute) else { return nil }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(positionValue as! AXValue, .cgPoint, &position),
              AXValueGetValue(sizeValue as! AXValue, .cgSize, &size) else { return nil }
        return CGRect(origin: position, size: size)
    }
}
