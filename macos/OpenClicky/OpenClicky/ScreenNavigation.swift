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
    /// A screenful, gliding over a second and a half so the eye can follow the page ("scroll down
    /// slowly").
    case slow
    /// Keeps moving at reading pace until stop_scrolling, esc, another app in front, or the person
    /// moving the pointer ("keep scrolling").
    case continuous
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
    /// Under the Dock or the menu bar: a click there lands on them, not on the page.
    case underScreenEdge
    case notAllowed
}

nonisolated enum ScreenNavigation {

    /// Scroll-wheel steps in pixels, (vertical, horizontal). Positive vertical moves the content
    /// down (scrolls up), the way a wheel turned towards the person does.
    static func scrollSteps(direction: ScrollDirection, amount: ScrollAmount, viewport: CGSize) -> [(vertical: Int32, horizontal: Int32)] {
        let span = direction == .up || direction == .down ? viewport.height : viewport.width
        let pageDistance = Int32(max(span * 0.8, 100).rounded())
        let distances: [Int32]
        switch amount {
        case .page: distances = [pageDistance]
        case .little: distances = [Int32(max(span * 0.25, 40).rounded())]
        // Ten big steps reach the end of any page a person would scroll by voice.
        case .end: distances = Array(repeating: 5_000, count: 10)
        case .slow: distances = glideSteps(distance: pageDistance, count: slowGlideStepCount)
        // One tick; the live loop repeats it.
        case .continuous: distances = [continuousStepPixels]
        }
        switch direction {
        case .up: return distances.map { ($0, 0) }
        case .down: return distances.map { (-$0, 0) }
        case .left: return distances.map { (0, $0) }
        case .right: return distances.map { (0, -$0) }
        }
    }

    /// `distance` split into `count` steps that start and end gently (smoothstep), summing to it
    /// exactly, so a slow scroll reads as a glide rather than a crawl at one speed.
    static func glideSteps(distance: Int32, count: Int) -> [Int32] {
        guard count > 1 else { return [distance] }
        var previous: Int32 = 0
        return (1...count).map { index in
            let t = Double(index) / Double(count)
            let reached = Int32((Double(distance) * t * t * (3 - 2 * t)).rounded())
            defer { previous = reached }
            return reached - previous
        }
    }

    /// The slow glide: 45 steps 33 ms apart, about a second and a half.
    static let slowGlideStepCount = 45
    static let slowGlideStepNanoseconds: UInt64 = 33_000_000
    /// Reading pace: 2 px 30 times a second, 60 px a second.
    static let continuousStepPixels: Int32 = 2
    static let continuousStepNanoseconds: UInt64 = 33_000_000
    /// A continuous scroll nobody stopped ends on its own after this long.
    static let continuousScrollLimitSeconds: TimeInterval = 600

    /// Whether a continuous scroll should end: another app came to the front, or the pointer moved
    /// away from where the scroll put it (the person took over with the trackpad or mouse).
    static func continuousScrollShouldStop(frontPID: pid_t?, scrollingPID: pid_t, pointer: CGPoint, scrollPoint: CGPoint) -> Bool {
        frontPID != scrollingPID || hypot(pointer.x - scrollPoint.x, pointer.y - scrollPoint.y) > 40
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
        "sign out", "log out", "uninstall", "publish", "post", "place order", "book now",
    ]

    /// Whether the model's label names a picture rather than words: a video, thumbnail, image.
    static func namesAPicture(_ label: String) -> Bool {
        let words = Set(label.lowercased().components(separatedBy: CharacterSet.alphanumerics.inverted))
        return !words.isDisjoint(with: ["video", "videos", "thumbnail", "image", "picture", "photo", "play", "player"])
    }

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

    /// The continuous scroll running now, if any, and a count that tells a finished loop whether
    /// it is still the current one.
    private static var continuousScroll: Task<Void, Never>?
    private static var continuousScrollGeneration = 0

    /// Scrolls the window in front. Scroll events go to the window under the pointer, so when the
    /// pointer is elsewhere (on the island, another app) it is moved to the middle of that window.
    /// A slow scroll returns once the glide is over; a continuous one returns at once and keeps
    /// going. Any new scroll ends a continuous one first.
    static func scrollFrontWindow(_ direction: ScrollDirection, _ amount: ScrollAmount) async -> ScrollResult {
        stopContinuousScroll()
        guard AXIsProcessTrusted() else { return .notAllowed }
        guard let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != ownPID,
              let frame = focusedWindowFrame(of: app.processIdentifier) else { return .noWindow }
        let pointer = CGEvent(source: nil)?.location ?? .zero
        let target = frame.contains(pointer) ? pointer : CGPoint(x: frame.midX, y: frame.midY)
        if target != pointer { CGWarpMouseCursorPosition(target) }
        let steps = scrollSteps(direction: direction, amount: amount, viewport: frame.size)
        switch amount {
        case .page, .little, .end:
            steps.forEach { postScroll($0, at: target) }
        case .slow:
            for step in steps {
                postScroll(step, at: target)
                try? await Task.sleep(nanoseconds: slowGlideStepNanoseconds)
            }
        case .continuous:
            guard let step = steps.first else { return .scrolled }
            let scrollingPID = app.processIdentifier
            continuousScrollGeneration += 1
            let generation = continuousScrollGeneration
            continuousScroll = Task { @MainActor in
                let startedAt = Date()
                while !Task.isCancelled, Date().timeIntervalSince(startedAt) < continuousScrollLimitSeconds {
                    if continuousScrollShouldStop(frontPID: NSWorkspace.shared.frontmostApplication?.processIdentifier, scrollingPID: scrollingPID,
                                                  pointer: CGEvent(source: nil)?.location ?? target, scrollPoint: target) {
                        AppLog.append("scroll: continuous scroll ended (another app or the pointer moved)")
                        break
                    }
                    postScroll(step, at: target)
                    try? await Task.sleep(nanoseconds: continuousStepNanoseconds)
                }
                if continuousScrollGeneration == generation { continuousScroll = nil }
            }
        }
        return .scrolled
    }

    /// Ends a continuous scroll; false when none was running.
    @discardableResult
    static func stopContinuousScroll() -> Bool {
        guard let task = continuousScroll else { return false }
        continuousScroll = nil
        task.cancel()
        return true
    }

    private static func postScroll(_ step: (vertical: Int32, horizontal: Int32), at point: CGPoint) {
        guard let event = CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 2, wheel1: step.vertical, wheel2: step.horizontal, wheel3: 0) else { return }
        event.location = point
        event.post(tap: .cghidEventTap)
    }

    /// One left click at an AppKit screen point. The pointer goes there (the buddy has just flown
    /// there too), the way a person's click would leave it. A web link in a browser is followed in
    /// its own tab instead: a page may open a link in a background tab, which someone using their
    /// voice cannot reach. `inNewTab` asks for a new tab (a link opens there, selected; anything
    /// else gets ⌘-click).
    static func click(atAppKitPoint point: CGPoint, inNewTab: Bool = false) -> ScreenClickResult {
        stopContinuousScroll()
        guard AXIsProcessTrusted() else { return .notAllowed }
        // The Dock owns a full-screen window, so window bounds cannot say what it covers; the
        // screen's visible frame (without the Dock and menu bar) can.
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(point) }), !screen.visibleFrame.insetBy(dx: 0, dy: 4).contains(point) {
            return .underScreenEdge
        }
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
