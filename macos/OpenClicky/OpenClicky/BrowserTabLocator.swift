//
//  BrowserTabLocator.swift
//  OpenClicky
//
//  `point_at` for browser tabs. A tab is identified by a 16 px favicon and a truncated title, which
//  no screenshot model resolves reliably ("where is the Reddit tab" sent the Realtime model, Claude
//  and OCR to a menu bar item spelled "reditt"). The browser knows: its AppleScript tab list gives
//  title + URL in strip order (first use asks the user to allow OpenClicky to control the browser),
//  and Accessibility exposes one AXTabButton per tab with an exact frame, in the same order.
//

import AppKit
import ApplicationServices
import Foundation

nonisolated struct BrowserTab: Equatable {
    let title: String
    let url: URL?
}

/// One on-screen window as CGWindowList reports it: owner process and bounds in CG global coordinates.
nonisolated struct OnScreenWindow: Equatable {
    let ownerPID: pid_t
    let bounds: CGRect
}

nonisolated enum BrowserTabLocator {
    /// Browsers whose AppleScript dictionary lists `tabs of front window` with a URL and a title.
    private static let scriptableBrowsers: [String: (tabsExpression: String, titleProperty: String)] = [
        "com.google.Chrome": ("tabs of front window", "title"),
        "com.brave.Browser": ("tabs of front window", "title"),
        "com.microsoft.edgemac": ("tabs of front window", "title"),
        "com.apple.Safari": ("tabs of front window", "name"),
        "com.apple.SafariTechnologyPreview": ("tabs of front window", "name"),
    ]

    /// Words that appear in every request or describe the control rather than name the site.
    private static let genericWords: Set<String> = [
        "tab", "tabs", "browser", "window", "page", "site", "website", "open", "opened", "where", "which", "what",
        "the", "this", "that", "one", "point", "show", "find", "click", "please", "and", "for", "with", "new",
        "settings", "home", "search", "login", "sign", "dashboard", "chrome", "safari", "google", "www", "com",
    ]

    // MARK: - Entry point

    /// The screenshot-pixel centre of the tab the request is about, when the request mentions a
    /// tab and a running scriptable browser has a matching tab whose button is visible in the
    /// captured screenshot. The browser need not be the app in front: a terminal over the lower
    /// part of Chrome still leaves the tab strip showing, and that is what the user is looking at.
    @MainActor
    static func locate(label: String, text: String, userRequest: String?, in capture: CompanionScreenCapture) -> (point: CGPoint, tab: BrowserTab)? {
        guard mentionsTab([label, text, userRequest]) else { return nil }
        let windows = onScreenWindows()
        let browsers = NSWorkspace.shared.runningApplications
            .filter { app in app.bundleIdentifier.map { scriptableBrowsers[$0] != nil } ?? false }
            // Only browsers with a window on screen (no AppleScript round trip, and no permission
            // prompt, for a browser idling without windows); the active one first.
            .filter { app in windows.contains { $0.ownerPID == app.processIdentifier } }
            .sorted { $0.isActive && !$1.isActive }
        let primaryDisplayHeight = NSScreen.screens.first?.frame.height ?? capture.displayFrame.height
        for app in browsers {
            guard let bundleIdentifier = app.bundleIdentifier, let browser = scriptableBrowsers[bundleIdentifier] else { continue }
            let tabs = tabList(bundleIdentifier: bundleIdentifier, browser: browser)
            guard let index = bestMatch(for: [label, text, userRequest], in: tabs) else { continue }
            guard let frame = frame(ofTabAt: index, in: tabButtonFrames(app: app), tabCount: tabs.count) else {
                AppLog.append("browser tab \"\(tabs[index].title.prefix(40))\" matched but its button frame could not be read")
                continue
            }
            let globalCenter = CGPoint(x: frame.midX, y: frame.midY)
            let point = capture.screenshotPoint(forGlobalPoint: globalCenter, primaryDisplayHeight: primaryDisplayHeight)
            // The screenshot is the pointer's display; the tab must be on it and not under another window.
            guard capture.containsScreenshotPoint(point), !isHidden(point: globalCenter, ofWindowOwnedBy: app.processIdentifier, in: windows) else {
                AppLog.append("browser tab \"\(tabs[index].title.prefix(40))\" is off the capture or covered; not pointing")
                continue
            }
            return (point, tabs[index])
        }
        return nil
    }

    /// Whether `point` (CG global coordinates) in the window of `ownerPID` is covered by another
    /// app's window above it, or lies in none of that app's windows. `windows` is front to back.
    static func isHidden(point: CGPoint, ofWindowOwnedBy ownerPID: pid_t, in windows: [OnScreenWindow]) -> Bool {
        let ownPID = ProcessInfo.processInfo.processIdentifier
        for window in windows where window.bounds.contains(point) {
            if window.ownerPID == ownerPID { return false }
            if window.ownerPID != ownPID { return true }
        }
        return true
    }

    /// Normal-level windows currently on screen, front to back (CG global coordinates).
    private static func onScreenWindows() -> [OnScreenWindow] {
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { return [] }
        return list.compactMap { info in
            guard let layer = info[kCGWindowLayer as String] as? Int, layer == 0,
                  let pid = info[kCGWindowOwnerPID as String] as? pid_t,
                  let boundsDictionary = info[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsDictionary),
                  (info[kCGWindowAlpha as String] as? Double ?? 1) > 0 else { return nil }
            return OnScreenWindow(ownerPID: pid, bounds: bounds)
        }
    }

    // MARK: - Pure parts (unit-tested)

    static func mentionsTab(_ texts: [String?]) -> Bool {
        texts.contains { text in
            words(in: text ?? "").contains { $0 == "tab" || $0 == "tabs" }
        }
    }

    /// Index of the tab the request names: a word of the request in the URL's host counts most
    /// ("reddit" in www.reddit.com), a word in the title less. Nil when nothing distinctive matches.
    static func bestMatch(for texts: [String?], in tabs: [BrowserTab]) -> Int? {
        let requestWords = Set(texts.flatMap { words(in: $0 ?? "") }).filter { $0.count >= 3 && !genericWords.contains($0) }
        guard !requestWords.isEmpty else { return nil }
        var best: (index: Int, score: Int)?
        for (index, tab) in tabs.enumerated() {
            let hostWords = Set(words(in: tab.url?.host ?? ""))
            let titleWords = Set(words(in: tab.title))
            var score = 0
            for word in requestWords {
                if hostWords.contains(word) || hostWords.contains(where: { $0.hasPrefix(word) && $0.count <= word.count + 3 }) { score += 3 }
                if titleWords.contains(word) { score += 1 }
            }
            if score > 0, score > (best?.score ?? 0) { best = (index, score) }
        }
        return best?.index
    }

    /// Tab buttons carry no titles, so they pair with the tab list by position, left to right, and
    /// only when there is one button per tab.
    static func frame(ofTabAt index: Int, in buttonFrames: [CGRect], tabCount: Int) -> CGRect? {
        guard buttonFrames.count == tabCount, index >= 0, index < tabCount else { return nil }
        return buttonFrames.sorted { $0.minX < $1.minX }[index]
    }

    private static func words(in text: String) -> [String] {
        text.lowercased()
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
    }

    // MARK: - Browser access

    /// The front window's tabs in strip order via AppleScript (nil URL for a tab whose URL is not a URL).
    @MainActor
    private static func tabList(bundleIdentifier: String, browser: (tabsExpression: String, titleProperty: String)) -> [BrowserTab] {
        // Two lists separated by a marker so titles containing commas survive the round trip.
        let source = """
        tell application id "\(bundleIdentifier)"
            set urlList to URL of \(browser.tabsExpression)
            set titleList to \(browser.titleProperty) of \(browser.tabsExpression)
        end tell
        set AppleScript's text item delimiters to (ASCII character 30)
        return (urlList as text) & (ASCII character 29) & (titleList as text)
        """
        var errorInfo: NSDictionary?
        guard let script = NSAppleScript(source: source),
              let result = script.executeAndReturnError(&errorInfo).stringValue else {
            AppLog.append("browser tabs: AppleScript failed \(errorInfo?[NSAppleScript.errorMessage] ?? "unknown")")
            return []
        }
        let halves = result.components(separatedBy: String(UnicodeScalar(29)))
        guard halves.count == 2 else { return [] }
        let urls = halves[0].components(separatedBy: String(UnicodeScalar(30)))
        let titles = halves[1].components(separatedBy: String(UnicodeScalar(30)))
        guard urls.count == titles.count else { return [] }
        return zip(titles, urls).map { BrowserTab(title: $0, url: URL(string: $1)) }
    }

    /// Frames (CG global coordinates, top-left origin) of the AXTabButtons in the browser's front window.
    private static func tabButtonFrames(app: NSRunningApplication) -> [CGRect] {
        guard AXIsProcessTrusted() else { return [] }
        let application = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(application, 0.25)
        guard let window = copyElement(application, kAXFocusedWindowAttribute) ?? copyElement(application, kAXMainWindowAttribute) else { return [] }
        var frames: [CGRect] = []
        var queue: [(AXUIElement, Int)] = [(window, 0)]
        var visited = 0
        while !queue.isEmpty, visited < 600 {
            let (element, depth) = queue.removeFirst()
            visited += 1
            let role = copyString(element, kAXRoleAttribute)
            if role == "AXRadioButton", copyString(element, kAXSubroleAttribute) == "AXTabButton" {
                if let frame = copyFrame(element) { frames.append(frame) }
                continue
            }
            // Safari's tabs are AXRadioButtons inside an AXTabGroup without the Chrome subrole.
            if role == "AXRadioButton", let frame = copyFrame(element), frame.height < 60 {
                frames.append(frame)
                continue
            }
            guard depth < 12 else { continue }
            for child in copyChildren(element) { queue.append((child, depth + 1)) }
        }
        return frames
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
        copyValue(element, attribute) as? String
    }

    private static func copyChildren(_ element: AXUIElement) -> [AXUIElement] {
        (copyValue(element, kAXChildrenAttribute) as? [AXUIElement]) ?? []
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

extension CompanionScreenCapture {
    /// A point in CG global coordinates (top-left origin of the primary display, as Accessibility
    /// reports frames) → this capture's screenshot pixels (origin top-left of the captured display).
    nonisolated func screenshotPoint(forGlobalPoint globalPoint: CGPoint, primaryDisplayHeight: CGFloat) -> CGPoint {
        let appKitY = primaryDisplayHeight - globalPoint.y
        let scaleX = CGFloat(screenshotWidthInPixels) / max(displayFrame.width, 1)
        let scaleY = CGFloat(screenshotHeightInPixels) / max(displayFrame.height, 1)
        return CGPoint(x: (globalPoint.x - displayFrame.minX) * scaleX, y: (displayFrame.maxY - appKitY) * scaleY)
    }

    nonisolated func containsScreenshotPoint(_ point: CGPoint) -> Bool {
        point.x >= 0 && point.y >= 0 && point.x <= CGFloat(screenshotWidthInPixels) && point.y <= CGFloat(screenshotHeightInPixels)
    }
}
