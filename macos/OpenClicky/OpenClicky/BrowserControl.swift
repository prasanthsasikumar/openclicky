//
//  BrowserControl.swift
//  OpenClicky
//
//  Tabs by voice. A voice click on a search result opened the page in a background tab, which is
//  no use to someone who cannot see or reach the tab strip, and "show me that tab" had nothing to
//  do it with (2026-10-29). So a clicked web link is followed in the tab it is in (a new tab only
//  when asked), and `switch_tab` goes to a tab by name or position.
//
//  Chrome, Brave, Edge and Safari through their AppleScript dictionaries, the same ones
//  `BrowserTabLocator` reads (the first use asks to allow controlling the browser).
//

import AppKit
import ApplicationServices

/// Which tab `switch_tab` goes to.
enum BrowserTabTarget: Equatable {
    case named(String)
    case next
    case previous
    case newest
    case first
}

enum BrowserTabSwitchResult: Equatable {
    case switched(title: String)
    case noBrowser
    case noSuchTab
    case notAllowed
}

nonisolated enum BrowserControl {

    /// Safari names the selected tab `current tab` and its title `name`; the Chromium browsers
    /// say `active tab` and `title`, and choose a tab by `active tab index`.
    static func isSafari(_ bundleIdentifier: String) -> Bool {
        bundleIdentifier.hasPrefix("com.apple.Safari")
    }

    /// The 1-based index the target names among `tabCount` tabs, the active one being
    /// `activeIndex`, or nil when it names nothing (next past the last tab wraps, as ⌃Tab does).
    static func tabIndex(for target: BrowserTabTarget, tabs: [BrowserTab], activeIndex: Int) -> Int? {
        guard !tabs.isEmpty else { return nil }
        switch target {
        case .next: return activeIndex % tabs.count + 1
        case .previous: return activeIndex <= 1 ? tabs.count : activeIndex - 1
        // New tabs open beside the one they came from, but the last tab is what "the newest" means
        // to someone who cannot see the strip, and a link opened by OpenClicky goes there too.
        case .newest: return tabs.count
        case .first: return 1
        case .named(let words):
            // "cars and bids" is carsandbids.com: the words said, run together, inside the site name.
            let joinedWords = words.lowercased().components(separatedBy: CharacterSet.alphanumerics.inverted).joined()
            if joinedWords.count >= 4, let index = tabs.firstIndex(where: { ($0.url?.host?.lowercased() ?? "").contains(joinedWords) }) {
                return index + 1
            }
            return BrowserTabLocator.bestMatch(for: [words], in: tabs).map { $0 + 1 }
        }
    }

    /// A string literal AppleScript reads back exactly.
    static func appleScriptLiteral(_ text: String) -> String {
        "\"" + text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }
}

// MARK: - Live

@MainActor
extension BrowserControl {

    /// The scriptable browser in front, or else the first one with a window on screen.
    static func browser(frontOnly: Bool) -> NSRunningApplication? {
        let isScriptable = { (app: NSRunningApplication) in
            app.bundleIdentifier.map { BrowserTabLocator.scriptableBrowsers[$0] != nil } ?? false
        }
        if let front = NSWorkspace.shared.frontmostApplication, isScriptable(front) { return front }
        guard !frontOnly else { return nil }
        let windowOwners = Set(OnScreenWindows.current().filter { $0.layer == 0 }.map(\.ownerPID))
        return NSWorkspace.shared.runningApplications.first { isScriptable($0) && windowOwners.contains($0.processIdentifier) }
    }

    /// The http(s) link under a point (CG global coordinates), read through Accessibility: the
    /// element there or one of its first few ancestors is an AXLink with an AXURL.
    static func webLink(atEventPoint point: CGPoint, inAppWithPID pid: pid_t) -> URL? {
        let application = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(application, 0.25)
        var hit: AXUIElement?
        guard AXUIElementCopyElementAtPosition(application, Float(point.x), Float(point.y), &hit) == .success, var element = hit else { return nil }
        for _ in 0..<6 {
            var role: CFTypeRef?
            AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role)
            if role as? String == "AXLink" {
                var linkValue: CFTypeRef?
                AXUIElementCopyAttributeValue(element, kAXURLAttribute as CFString, &linkValue)
                let url = (linkValue as? URL) ?? (linkValue as? String).flatMap(URL.init(string:))
                guard let url, ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
                return url
            }
            var parent: CFTypeRef?
            guard AXUIElementCopyAttributeValue(element, kAXParentAttribute as CFString, &parent) == .success,
                  let parent, CFGetTypeID(parent) == AXUIElementGetTypeID() else { return nil }
            element = parent as! AXUIElement
        }
        return nil
    }

    /// Loads the link in the browser's selected tab, or in a new tab that becomes the selected one.
    static func open(_ url: URL, inNewTab: Bool, browser: NSRunningApplication) -> Bool {
        guard let bundleIdentifier = browser.bundleIdentifier else { return false }
        let link = appleScriptLiteral(url.absoluteString)
        let body: String
        switch (isSafari(bundleIdentifier), inNewTab) {
        case (true, false): body = "set URL of current tab of front window to \(link)"
        case (true, true): body = "tell front window to set current tab to (make new tab with properties {URL:\(link)})"
        case (false, false): body = "set URL of active tab of front window to \(link)"
        case (false, true): body = "tell front window to make new tab with properties {URL:\(link)}"
        }
        return run("tell application id \(appleScriptLiteral(bundleIdentifier))\n\(body)\nend tell") != nil
    }

    /// Selects the tab the target names in the browser in front (or the one on screen, which is
    /// then brought to the front).
    static func switchTab(to target: BrowserTabTarget) -> BrowserTabSwitchResult {
        guard let browser = browser(frontOnly: false), let bundleIdentifier = browser.bundleIdentifier,
              let tabsDescription = BrowserTabLocator.scriptableBrowsers[bundleIdentifier] else { return .noBrowser }
        let tabs = BrowserTabLocator.tabList(bundleIdentifier: bundleIdentifier, browser: tabsDescription)
        guard !tabs.isEmpty else { return .notAllowed }
        let application = appleScriptLiteral(bundleIdentifier)
        let activeIndexExpression = isSafari(bundleIdentifier) ? "index of current tab of front window" : "active tab index of front window"
        let activeIndex = run("tell application id \(application) to get \(activeIndexExpression)").flatMap { Int($0) } ?? 1
        guard let index = tabIndex(for: target, tabs: tabs, activeIndex: activeIndex) else { return .noSuchTab }
        let select = isSafari(bundleIdentifier)
            ? "tell front window to set current tab to tab \(index)"
            : "set active tab index of front window to \(index)"
        guard run("tell application id \(application)\n\(select)\nend tell") != nil else { return .notAllowed }
        browser.activate()
        return .switched(title: tabs[index - 1].title)
    }

    /// Runs a script, returning its result as text ("" for none), or nil when it failed.
    private static func run(_ source: String) -> String? {
        var errorInfo: NSDictionary?
        guard let script = NSAppleScript(source: source) else { return nil }
        let result = script.executeAndReturnError(&errorInfo)
        if let errorInfo {
            AppLog.append("browser control: AppleScript failed \(errorInfo[NSAppleScript.errorMessage] ?? "unknown")")
            return nil
        }
        return result.stringValue ?? ""
    }
}
