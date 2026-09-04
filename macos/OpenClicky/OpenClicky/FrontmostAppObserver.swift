//
//  FrontmostAppObserver.swift
//  OpenClicky
//
//  What the user is looking at when they release push-to-talk: the frontmost app and, for browsers,
//  the front tab's URL (Accessibility API) so `AppSkillMatcher` can pick the matching app skill.
//  Synchronous and bounded — it runs in the key-up tail, never in front of the mic open.
//

import AppKit
import ApplicationServices
import Foundation

enum FrontmostAppObserver {
    static let browserBundleIdentifiers: Set<String> = [
        "com.apple.Safari",
        "com.apple.SafariTechnologyPreview",
        "com.google.Chrome",
        "company.thebrowser.Browser",
        "com.microsoft.edgemac",
        "com.brave.Browser",
        "org.mozilla.firefox",
    ]

    /// The frontmost app (never our own overlay/HUD, which are non-activating panels anyway).
    static func current(excludingBundleIdentifier own: String? = Bundle.main.bundleIdentifier) -> FrontAppContext {
        var context = FrontAppContext()
        guard let app = NSWorkspace.shared.frontmostApplication else { return context }
        if let own, app.bundleIdentifier == own {
            // Our own process is somehow active (e.g. a smoke run from the terminal); look for the next app.
            if let other = NSWorkspace.shared.runningApplications.first(where: { $0.isActive && $0.bundleIdentifier != own }) {
                return describe(other)
            }
            context.bundleIdentifier = app.bundleIdentifier
            context.appName = app.localizedName
            return context
        }
        return describe(app)
    }

    private static func describe(_ app: NSRunningApplication) -> FrontAppContext {
        var context = FrontAppContext()
        context.bundleIdentifier = app.bundleIdentifier
        context.appName = app.localizedName
        context.isBrowser = app.bundleIdentifier.map(browserBundleIdentifiers.contains) ?? false
        guard AXIsProcessTrusted() else { return context }
        let application = AXUIElementCreateApplication(app.processIdentifier)
        // A hung or busy browser must not stall the push-to-talk turn: cap every AX round trip.
        AXUIElementSetMessagingTimeout(application, 0.25)
        guard let window = copyElement(application, kAXFocusedWindowAttribute) ?? copyElement(application, kAXMainWindowAttribute) else {
            return context
        }
        AXUIElementSetMessagingTimeout(window, 0.25)
        context.windowTitle = copyString(window, kAXTitleAttribute)
        if context.isBrowser {
            context.url = frontTabURL(of: window)
        }
        return context
    }

    // MARK: - Accessibility helpers

    /// Safari exposes the document URL on the window; Chromium browsers on the AXWebArea (`AXURL`).
    private static func frontTabURL(of window: AXUIElement) -> URL? {
        if let document = copyString(window, "AXDocument"), let url = URL(string: document), url.host != nil {
            return url
        }
        if let webArea = findWebArea(from: window, maxDepth: 8, maxNodes: 400) {
            if let url = copyURL(webArea, "AXURL") { return url }
            if let text = copyString(webArea, "AXURL"), let url = URL(string: text) { return url }
        }
        return nil
    }

    /// Breadth-first search for the first AXWebArea, bounded so a huge accessibility tree cannot stall the turn.
    private static func findWebArea(from root: AXUIElement, maxDepth: Int, maxNodes: Int) -> AXUIElement? {
        var queue: [(AXUIElement, Int)] = [(root, 0)]
        var visited = 0
        while !queue.isEmpty, visited < maxNodes {
            let (element, depth) = queue.removeFirst()
            visited += 1
            if copyString(element, kAXRoleAttribute) == "AXWebArea" { return element }
            guard depth < maxDepth else { continue }
            for child in copyChildren(element) { queue.append((child, depth + 1)) }
        }
        return nil
    }

    private static func copyValue(_ element: AXUIElement, _ attribute: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value
    }

    private static func copyElement(_ element: AXUIElement, _ attribute: String) -> AXUIElement? {
        guard let value = copyValue(element, attribute), CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }

    private static func copyString(_ element: AXUIElement, _ attribute: String) -> String? {
        guard let value = copyValue(element, attribute) else { return nil }
        if let string = value as? String { return string }
        if let url = value as? URL { return url.absoluteString }
        return nil
    }

    private static func copyURL(_ element: AXUIElement, _ attribute: String) -> URL? {
        guard let value = copyValue(element, attribute) else { return nil }
        if let url = value as? URL { return url }
        if let string = value as? String { return URL(string: string) }
        return nil
    }

    private static func copyChildren(_ element: AXUIElement) -> [AXUIElement] {
        guard let value = copyValue(element, kAXChildrenAttribute), let array = value as? [AXUIElement] else { return [] }
        return array
    }
}
