//
//  ScreenNavigation.swift
//  OpenClicky
//
//  Moving around an app by voice: scroll the window in front, click what the person named, find
//  words on the page, find a file. Before this the fast lane could open a page but not move
//  through it: "scroll down" was answered with how to use a trackpad and "click the first link"
//  with a point at it (2026-10-29, Chrome, hands-free).
//
//  Every system call here needs the Accessibility permission OpenClicky already asks for. The
//  pure parts (scroll steps, coordinate flip, which clicks are refused, the Spotlight query) are
//  unit-tested; the event posting is not.
//

import AppKit
import ApplicationServices

enum ScrollDirection: String, CaseIterable {
    case up, down, left, right
}

enum ScrollAmount: String, CaseIterable {
    /// Most of a screenful, the way Page Down moves.
    case page
    /// A quarter of the window.
    case little
    /// All the way: the top for up, the bottom for down.
    case end
}

enum ScrollResult: Equatable {
    case scrolled
    /// No ordinary window in front (or OpenClicky itself is in front).
    case noWindow
    case notAllowed
}

enum FindOnPageResult: Equatable {
    case typed
    /// ⌘F did not put the focus in a find field, so nothing was typed: typing into whatever had
    /// the focus could have written into the person's document.
    case noFindField
    case noApp
    case notAllowed
}

enum ScreenClickResult: Equatable {
    case clicked
    /// A web link in a browser, loaded in its own tab (or a new selected tab when asked).
    case followedLink(URL, inNewTab: Bool)
    /// The spot is on one of OpenClicky's own windows.
    case ownWindow
    case notAllowed
}

nonisolated enum ScreenNavigation {

    /// Scroll-wheel steps in pixels, (vertical, horizontal). Positive vertical moves the content
    /// down (scrolls up), the way a wheel turned towards the person does.
    static func scrollSteps(direction: ScrollDirection, amount: ScrollAmount, viewport: CGSize) -> [(vertical: Int32, horizontal: Int32)] {
        let span = direction == .up || direction == .down ? viewport.height : viewport.width
        let distance: CGFloat
        let count: Int
        switch amount {
        case .page: distance = max(span * 0.8, 100); count = 1
        case .little: distance = max(span * 0.25, 40); count = 1
        // Ten big steps reach the end of any page a person would scroll by voice.
        case .end: distance = 5_000; count = 10
        }
        let step = Int32(distance.rounded())
        switch direction {
        case .up: return Array(repeating: (step, 0), count: count)
        case .down: return Array(repeating: (-step, 0), count: count)
        case .left: return Array(repeating: (0, step), count: count)
        case .right: return Array(repeating: (0, -step), count: count)
        }
    }

    /// AppKit screen point (bottom-left origin of the primary display) to the CG global
    /// coordinates events take (top-left origin).
    static func eventPoint(fromAppKit point: CGPoint, primaryScreenHeight: CGFloat) -> CGPoint {
        CGPoint(x: point.x, y: primaryScreenHeight - point.y)
    }

    /// Words on a control that commit something a mishearing must never commit. Such a control is
    /// pointed at and left for the person to click. Whole words only: "Posts" and "Ordering" pass.
    static let riskyClickWords: [String] = [
        "send", "buy", "pay", "purchase", "order", "checkout", "check out", "delete", "remove",
        "erase", "trash", "submit", "confirm", "transfer", "donate", "subscribe", "unsubscribe",
        "sign out", "log out", "uninstall", "publish", "post", "place order", "book now", "accept",
    ]

    static func clickIsRisky(label: String, text: String) -> Bool {
        let words = "\(label) \(text)".lowercased()
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
        let joined = " " + words.joined(separator: " ") + " "
        return riskyClickWords.contains { joined.contains(" \($0) ") }
    }

    /// The topmost ordinary window under `point` (CG coordinates), from a front-to-back list.
    static func ownerOfWindow(at point: CGPoint, in windows: [OnScreenWindow]) -> pid_t? {
        windows.first { $0.layer == 0 && $0.bounds.contains(point) }?.ownerPID
    }

    /// A Spotlight query for files whose names contain every word the person said, in any order
    /// ("tax pdf" finds "Tax return 2025.pdf"). Quotes, backslashes and wildcards are dropped from
    /// the words so nothing said can change the query's shape.
    static func spotlightQuery(for words: String) -> String? {
        let terms = words.lowercased()
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
            .prefix(6)
        guard !terms.isEmpty else { return nil }
        return terms.map { "kMDItemFSName == \"*\($0)*\"cd" }.joined(separator: " && ")
    }

    /// Spotlight paths worth showing a person: nothing hidden, nothing inside a Library, an app
    /// bundle or a dependency folder. Names that are exactly the request, then ones that start
    /// with it, then ones where every word said starts a word of the name ("riti" is "Riti", not
    /// "Writing") come first; otherwise Spotlight's order is kept.
    static func rankFoundFiles(_ paths: [String], query: String, limit: Int = 5) -> [URL] {
        let wanted = query.lowercased()
        let usable = paths.filter { path in
            let parts = path.split(separator: "/")
            return !parts.contains { $0.hasPrefix(".") }
                && !path.contains("/Library/")
                && !path.contains(".app/")
                && !path.contains("/node_modules/")
        }
        let score = { (path: String) -> Int in
            let name = URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent.lowercased()
            if name == wanted { return 0 }
            if name.hasPrefix(wanted) { return 1 }
            let nameWords = name.components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
            let wantedWords = wanted.components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
            if wantedWords.allSatisfy({ wantedWord in nameWords.contains { $0.hasPrefix(wantedWord) } }) { return 2 }
            return 3
        }
        let ranked = usable.enumerated().sorted { lhs, rhs in
            let (left, right) = (score(lhs.element), score(rhs.element))
            return left == right ? lhs.offset < rhs.offset : left < right
        }
        return ranked.prefix(limit).map { URL(fileURLWithPath: $0.element) }
    }
}

// MARK: - Live

@MainActor
extension ScreenNavigation {

    private static var ownPID: pid_t { ProcessInfo.processInfo.processIdentifier }

    /// Scrolls the window in front. Scroll events go to the window under the pointer, so when the
    /// pointer is elsewhere (on the island, another app) it is moved to the middle of that window.
    static func scrollFrontWindow(_ direction: ScrollDirection, _ amount: ScrollAmount) -> ScrollResult {
        guard AXIsProcessTrusted() else { return .notAllowed }
        guard let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != ownPID,
              let frame = focusedWindowFrame(of: app.processIdentifier) else { return .noWindow }
        let pointer = CGEvent(source: nil)?.location ?? .zero
        let target = frame.contains(pointer) ? pointer : CGPoint(x: frame.midX, y: frame.midY)
        if target != pointer { CGWarpMouseCursorPosition(target) }
        for step in scrollSteps(direction: direction, amount: amount, viewport: frame.size) {
            guard let event = CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 2, wheel1: step.vertical, wheel2: step.horizontal, wheel3: 0) else { continue }
            event.location = target
            event.post(tap: .cghidEventTap)
        }
        return .scrolled
    }

    /// One left click at an AppKit screen point. The pointer goes there (the buddy has just flown
    /// there too), the way a person's click would leave it. A web link in a browser is followed in
    /// its own tab instead: a page may open a link in a background tab, which someone using their
    /// voice cannot reach. `inNewTab` asks for a new tab (a link opens there, selected; anything
    /// else gets ⌘-click).
    static func click(atAppKitPoint point: CGPoint, inNewTab: Bool = false) -> ScreenClickResult {
        guard AXIsProcessTrusted() else { return .notAllowed }
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        let location = eventPoint(fromAppKit: point, primaryScreenHeight: primaryHeight)
        let ownerPID = ownerOfWindow(at: location, in: OnScreenWindows.current())
        if ownerPID == ownPID { return .ownWindow }
        if let browser = BrowserControl.browser(frontOnly: true), ownerPID == browser.processIdentifier,
           let link = BrowserControl.webLink(atEventPoint: location, inAppWithPID: browser.processIdentifier) {
            CGWarpMouseCursorPosition(location)
            if BrowserControl.open(link, inNewTab: inNewTab, browser: browser) { return .followedLink(link, inNewTab: inNewTab) }
        }
        let source = CGEventSource(stateID: .hidSystemState)
        for type in [CGEventType.mouseMoved, .leftMouseDown, .leftMouseUp] {
            guard let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: location, mouseButton: .left) else { continue }
            if type != .mouseMoved {
                event.setIntegerValueField(.mouseEventClickState, value: 1)
                event.flags = inNewTab ? .maskCommand : []
            }
            event.post(tap: .cghidEventTap)
        }
        return .clicked
    }

    /// ⌘F in the app in front, then the words and Return. The words are typed only once the focus
    /// has moved into a text field that was not focused before (the find bar); an app without
    /// find would otherwise take them into its document.
    static func findOnPage(_ text: String) async -> FindOnPageResult {
        guard AXIsProcessTrusted() else { return .notAllowed }
        guard let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != ownPID else { return .noApp }
        let before = focusedElement()
        postKey(3, flags: .maskCommand)  // F
        var findField: AXUIElement?
        for _ in 0..<8 {
            try? await Task.sleep(nanoseconds: 100_000_000)
            if let now = focusedElement(), before.map({ !CFEqual($0, now) }) ?? true, isTextField(now) {
                findField = now
                break
            }
        }
        guard findField != nil else { return .noFindField }
        // The bar takes focus a moment before it takes keys.
        try? await Task.sleep(nanoseconds: 150_000_000)
        postKey(0, flags: .maskCommand)  // A: replace what the find bar held before
        // Live dictation's typing: the text rides on the key events, the clipboard stays theirs.
        guard FrontAppTextInserter.typeAsKeystrokes(text) else { return .notAllowed }
        try? await Task.sleep(nanoseconds: 100_000_000)
        postKey(36, flags: [])           // Return: jump to the first match
        return .typed
    }

    /// Spotlight file names under the home folder that contain every word said.
    static func findFiles(named words: String) async -> [URL] {
        guard let query = spotlightQuery(for: words) else { return [] }
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let output: String = await Task.detached {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/mdfind")
            process.arguments = ["-onlyin", home, query]
            let pipe = Pipe()
            process.standardOutput = pipe
            process.standardError = FileHandle.nullDevice
            guard (try? process.run()) != nil else { return "" }
            let timeout = DispatchWorkItem { if process.isRunning { process.terminate() } }
            DispatchQueue.global().asyncAfter(deadline: .now() + 3, execute: timeout)
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            timeout.cancel()
            return String(decoding: data.prefix(200_000), as: UTF8.self)
        }.value
        let paths = output.split(separator: "\n").prefix(400).map(String.init)
        return rankFoundFiles(paths, query: words)
    }

    private static func focusedWindowFrame(of pid: pid_t) -> CGRect? {
        let application = AXUIElementCreateApplication(pid)
        for attribute in [kAXFocusedWindowAttribute, kAXMainWindowAttribute] {
            var window: CFTypeRef?
            guard AXUIElementCopyAttributeValue(application, attribute as CFString, &window) == .success,
                  let window, CFGetTypeID(window) == AXUIElementGetTypeID() else { continue }
            let element = window as! AXUIElement
            var positionValue: CFTypeRef?
            var sizeValue: CFTypeRef?
            guard AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &positionValue) == .success,
                  AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &sizeValue) == .success,
                  let positionValue, let sizeValue else { continue }
            var origin = CGPoint.zero
            var size = CGSize.zero
            AXValueGetValue(positionValue as! AXValue, .cgPoint, &origin)
            AXValueGetValue(sizeValue as! AXValue, .cgSize, &size)
            guard size.width > 50, size.height > 50 else { continue }
            return CGRect(origin: origin, size: size)
        }
        return nil
    }

    private static func focusedElement() -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(AXUIElementCreateSystemWide(), kAXFocusedUIElementAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }

    private static func isTextField(_ element: AXUIElement) -> Bool {
        var role: CFTypeRef?
        AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role)
        return ["AXTextField", "AXSearchField", "AXComboBox"].contains(role as? String ?? "")
    }

    private static func postKey(_ keyCode: CGKeyCode, flags: CGEventFlags) {
        let source = CGEventSource(stateID: .combinedSessionState)
        for isDown in [true, false] {
            guard let event = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: isDown) else { continue }
            event.flags = flags
            event.post(tap: .cghidEventTap)
        }
    }
}
