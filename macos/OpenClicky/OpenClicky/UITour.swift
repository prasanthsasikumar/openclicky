//
//  UITour.swift
//  OpenClicky
//
//  Debug builds only: `--openclicky-ui-tour <file>` walks every screen of the app (the window's
//  sections and settings pages, scrolled top to bottom, in light and dark; the first-run tour's
//  island cards; the island in each placement and state; the orb) and appends one
//  line per shot to <file>: `SHOT <name> <windowNumber>`. A script outside the app captures each
//  window by number (`screencapture -l`), so a design review gets every screen without anyone
//  clicking through them and without anything else on the desktop in the pictures.
//  scripts/capture-ui-tour.sh drives it.
//

#if DEBUG
import AppKit

@MainActor
final class UITour {
    private let companionManager: CompanionManager
    private let logURL: URL
    /// How long a step gets to draw before its shot is announced, and how long the capture gets.
    private let settleNanoseconds: UInt64 = 1_400_000_000
    private let captureNanoseconds: UInt64 = 1_100_000_000

    init(companionManager: CompanionManager, logPath: String) {
        self.companionManager = companionManager
        self.logURL = URL(fileURLWithPath: logPath)
    }

    func run() {
        FileManager.default.createFile(atPath: logURL.path, contents: nil)
        Task { @MainActor in
            // Run under a throwaway home (CFFIXED_USER_HOME, which scripts/capture-ui-tour.sh
            // sets), the history is empty: fill it with made-up takes so the pages look used
            // without showing anyone's real dictations. Never under the real home.
            if ProcessInfo.processInfo.environment["CFFIXED_USER_HOME"] != nil { seedDemoTakes() }
            // Let the launch finish (island, orb, permission checks) before the first shot.
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            // A Debug build often lacks permissions; its cards would cover every island shot.
            companionManager.permissionPromptController.dismiss()
            // OPENCLICKY_TOUR_ONLY=island,orb (window, firstrun, island, orb) runs a part.
            let only = ProcessInfo.processInfo.environment["OPENCLICKY_TOUR_ONLY"].map { Set($0.split(separator: ",").map(String.init)) }
            func includes(_ part: String) -> Bool { only?.contains(part) ?? true }
            if includes("window") {
                await tourWindow(appearanceName: "light", appearance: .light, scrolls: true)
                await tourWindow(appearanceName: "dark", appearance: .dark, scrolls: false)
            }
            if includes("firstrun") { await tourFirstRunCards() }
            if includes("island") { await tourIsland() }
            if includes("orb") { await tourOrb() }
            log("DONE")
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            exit(0)
        }
    }

    // MARK: Demo history

    private func seedDemoTakes() {
        let demoTakes: [(String, String, String, Double)] = [
            ("Can we move the design review to Thursday at 3? I want the new settings layout in front of everyone first.", "Slack", "com.tinyspeck.slackmacgap", 0.2),
            ("Thanks for the notes. I've folded them into the draft and flagged the two open questions in yellow.", "Mail", "com.apple.mail", 0.6),
            ("Add a retry button next to the failed upload so people don't have to start over.", "Linear", "com.linear", 1.1),
            ("Remember to book the meeting room for Friday's demo.", "Notes", "com.apple.Notes", 2.0),
            ("The onboarding should explain the shortcuts before it asks for permissions.", "Notion", "notion.id", 3.5),
            ("Ship it once the tests pass and the changelog is updated.", "Terminal", "com.apple.Terminal", 5.0),
            ("Lunch at the usual place? I can be there by half past twelve.", "Messages", "com.apple.MobileSMS", 7.5),
            ("Please summarise the attached report in three bullet points for the newsletter.", "Safari", "com.apple.Safari", 26),
            ("Rename the function to make it clear it only reads the file and never writes it.", "Xcode", "com.apple.dt.Xcode", 28),
            ("Happy birthday! Hope the year ahead is a good one.", "Messages", "com.apple.MobileSMS", 50),
            ("Draft a short reply saying we'd love to join the panel and asking for the schedule.", "Mail", "com.apple.mail", 52),
            ("Grocery list: oat milk, coffee beans, lemons, and something for dinner on Sunday.", "Reminders", "com.apple.reminders", 75),
            ("Let's keep the island black and make the settings window feel like System Settings.", "Slack", "com.tinyspeck.slackmacgap", 98),
            ("Move the quit button somewhere people can find it without scrolling.", "Linear", "com.linear", 120),
        ]
        let now = Date()
        for (text, appName, bundleID, hoursAgo) in demoTakes {
            let take = TakeRecord(
                createdAt: now.addingTimeInterval(-hoursAgo * 3600),
                rawText: text, formattedText: text, appBundleID: bundleID, appName: appName,
                language: "en", engine: "apple", durationSeconds: Double(text.count) / 14,
                pasteOutcome: .verified)
            try? companionManager.dictationTakeStore?.insert(take)
        }
    }

    // MARK: Main window

    private func tourWindow(appearanceName: String, appearance: DictationAppearance, scrolls: Bool) async {
        let settings = companionManager.dictationSettings
        let originalAppearance = settings.appearance
        settings.appearance = appearance
        defer { settings.appearance = originalAppearance }
        var index = 0
        for section in DictationSection.allCases {
            index += 1
            companionManager.showDictationWindow(section: section)
            await shootWindow(named: String(format: "window-%@-%02d-%@", appearanceName, index, section.rawValue), window: mainWindow, scrolls: scrolls)
        }
        if scrolls, let window = mainWindow {
            // The Keys & Account page at the window's narrowest: its "Add a key" row is the widest
            // there is, and the reason for the minimum width.
            let fullFrame = window.frame
            window.setContentSize(DictationWindowController.minimumSize)
            companionManager.showSettings(.account)
            await shoot("window-\(appearanceName)-00-keys-narrow", window: window)
            window.setFrame(fullFrame, display: true)
        }
        for page in DictationSettingsPage.allCases {
            index += 1
            companionManager.showSettings(page)
            await shootWindow(named: String(format: "settings-%@-%02d-%@", appearanceName, index, page.rawValue), window: mainWindow, scrolls: scrolls)
        }
        // What someone types when they can't find a setting: the results have to lead somewhere.
        if scrolls, let windowModel = (mainWindow?.windowController as? DictationWindowController)?.model {
            for query in ["voice", "language", "dictionary"] {
                windowModel.searchText = query
                await shootWindow(named: "search-\(appearanceName)-\(query)", window: mainWindow, scrolls: false)
            }
            windowModel.searchText = ""
        }
    }

    private var mainWindow: NSWindow? {
        NSApp.windows.first { $0.windowController is DictationWindowController && $0.isVisible }
    }

    // MARK: First-run tour (the island's cards)

    private func tourFirstRunCards() async {
        let tour = companionManager.firstRunTour
        let hud = companionManager.notchHUDManager
        hud.debugCloseAll()
        let cards: [(String, FirstRunTourStep, String?, Bool)] = [
            ("01-meet", .meet, nil, false),
            ("02-dictate", .dictate, nil, false),
            ("03-dictate-heard", .dictate, "✓ heard you: “hello openclicky, this is my first take”", true),
            ("04-dictate-nothing", .dictate, "didn’t catch any words. try again, or pick another microphone.", false),
            ("05-talk", .talk, nil, false),
            ("06-act", .act, nil, false),
            ("06b-account", .account, nil, false),
            ("07-done", .done, nil, false),
            ("08-open-at-login", .openAtLogin, nil, false),
            ("09-open-at-login-on", .openAtLogin, "✓ done. i’ll be here whenever your mac starts.", true),
        ]
        for (name, step, status, isGood) in cards {
            tour.debugShow(step, status: status, isGood: isGood, companionManager: companionManager)
            await shoot("tour-\(name)", window: hud.debugPrimaryWindow)
        }
        tour.debugShow(nil, companionManager: companionManager)
    }

    // MARK: Island

    private func tourIsland() async {
        let hud = companionManager.notchHUDManager
        let originalPlacement = companionManager.hudPlacement
        // A fresh demo home starts the welcome tour, whose card would cover every panel shot.
        companionManager.firstRunTour.debugShow(nil, companionManager: companionManager)
        hud.debugKeepsPanelsOpen = true
        defer {
            hud.debugKeepsPanelsOpen = false
            companionManager.setHUDPlacement(originalPlacement)
        }

        for placement in [HUDPlacement.notch, .right, .left] {
            companionManager.setHUDPlacement(placement)
            let prefix = "island-\(placement.rawValue)"
            hud.debugCloseAll()
            await shoot("\(prefix)-01-collapsed", window: hud.debugPrimaryWindow)
            hud.debugOpenPrimaryPanel()
            await shoot("\(prefix)-02-panel", window: hud.debugPrimaryWindow)
            hud.debugCloseAll()
            if placement != .notch { continue }

            hud.debugPeekPrimary()
            await shoot("\(prefix)-03-peek", window: hud.debugPrimaryWindow)
            hud.debugCloseAll()

            // The slot with the moment's component and the capsule to flip it.
            var takeContext = IslandContext.idle
            takeContext.now = Date()
            takeContext.isTakeLive = true
            takeContext.liveTranscript = "hello from the island, this is what i said so far"
            hud.debugSetPrimaryContext(takeContext)
            hud.debugOpenPrimaryPanel()
            await shoot("\(prefix)-04-panel-transcript", window: hud.debugPrimaryWindow)
            var agentContext = IslandContext.idle
            agentContext.now = Date()
            agentContext.agentActivityText = "editing Hero.swift"
            hud.debugSetPrimaryContext(agentContext)
            await shoot("\(prefix)-04b-panel-agent", window: hud.debugPrimaryWindow)
            hud.debugSetPrimaryContext(.idle)
            // An extra the user can turn on: the System rings.
            hud.debugShowPrimaryComponent(IslandPluginRegistry.systemID)
            await shoot("\(prefix)-04c-panel-system", window: hud.debugPrimaryWindow)
            // The shelf with two things dropped on it (the tour runs under a throwaway home).
            hud.shelf.add([.text("remember the milk"), .link(URL(string: "https://example.com")!)])
            hud.debugShowPrimaryComponent(IslandPluginRegistry.shelfID)
            await shoot("\(prefix)-04d-panel-shelf", window: hud.debugPrimaryWindow)
            hud.shelf.removeAll()
            // Two coding sessions, one live, for the Coding agents component.
            hud.debugSetAgentSessions([
                AgentSession(id: "demo-claude", agent: .claudeCode, project: "openclicky", title: nil, activity: .tool(name: "Edit", detail: "Hero.swift"), lastEventAt: Date()),
                AgentSession(id: "demo-codex", agent: .codex, project: "site", title: nil, activity: .idle, lastEventAt: Date().addingTimeInterval(-180)),
            ])
            hud.debugShowPrimaryComponent(IslandPluginRegistry.agentsID)
            await shoot("\(prefix)-04e-panel-agents", window: hud.debugPrimaryWindow)
            hud.debugSetAgentSessions(nil)
            // A track for the Now playing plugin, with a made-up cover.
            let cover = NSImage(size: NSSize(width: 56, height: 56), flipped: false) { rect in
                NSColor(calibratedRed: 0.86, green: 0.36, blue: 0.24, alpha: 1).setFill()
                rect.fill()
                NSColor(calibratedWhite: 1, alpha: 0.35).setFill()
                NSBezierPath(ovalIn: rect.insetBy(dx: 14, dy: 14)).fill()
                return true
            }
            hud.debugSetMediaSnapshot(MediaSnapshot(player: .spotify, isPlaying: true, title: "Blue Hero", artist: "The Band", album: "Album", position: 83, duration: 245, artworkKey: "demo", artworkURL: nil, sampledAt: Date()), artwork: cover)
            hud.debugShowPrimaryComponent(IslandPluginRegistry.mediaID)
            await shoot("\(prefix)-04f-panel-media", window: hud.debugPrimaryWindow)
            hud.debugSetMediaSnapshot(nil, artwork: nil)
            hud.debugResetPrimaryComponents()
            hud.debugCloseAll()

            companionManager.debugSetVoiceState(.listening)
            await shoot("\(prefix)-05-busy-listening", window: hud.debugPrimaryWindow)
            companionManager.debugSetVoiceState(.processing)
            await shoot("\(prefix)-06-busy-thinking", window: hud.debugPrimaryWindow)
            companionManager.debugSetVoiceState(.idle)

            hud.openTextComposer()
            await shoot("\(prefix)-07-text-composer", window: hud.debugPrimaryWindow)
            hud.closeTextComposer()

            hud.showCaption(Farewell.all[0].text, holdSeconds: 6)
            try? await Task.sleep(nanoseconds: UInt64(Farewell.all[0].text.count) * 30_000_000)
            await shoot("\(prefix)-08-goodbye-caption", window: hud.debugPrimaryWindow)
            hud.clearCaption()
        }
    }

    // MARK: Orb

    private func tourOrb() async {
        let settings = companionManager.dictationSettings
        let wasVisible = settings.orbVisible
        settings.orbVisible = true
        defer { settings.orbVisible = wasVisible }
        await shoot("orb-idle", window: NSApp.windows.first { String(describing: type(of: $0)) == "OrbWindow" && $0.isVisible })
    }

    // MARK: Shots

    /// One shot, or (with `scrolls`) one shot per screenful of the window's largest scroll view.
    private func shootWindow(named name: String, window: NSWindow?, scrolls: Bool) async {
        try? await Task.sleep(nanoseconds: settleNanoseconds)
        guard scrolls, let window, let scrollView = Self.largestScrollableView(in: window.contentView) else {
            await shoot(name, window: window)
            return
        }
        let visibleHeight = scrollView.contentView.bounds.height
        let documentHeight = scrollView.documentView?.bounds.height ?? visibleHeight
        let step = max(120, visibleHeight - 80)
        var offset: CGFloat = 0
        var part = 1
        while true {
            Self.scroll(scrollView, toTopOffset: offset, documentHeight: documentHeight)
            await shoot(part == 1 ? name : "\(name)-part\(part)", window: window, settles: part > 1)
            if offset + visibleHeight >= documentHeight - 1 || part >= 8 { break }
            offset = min(offset + step, documentHeight - visibleHeight)
            part += 1
        }
        Self.scroll(scrollView, toTopOffset: 0, documentHeight: documentHeight)
    }

    private func shoot(_ name: String, window: NSWindow?, settles: Bool = true) async {
        if settles { try? await Task.sleep(nanoseconds: settleNanoseconds) }
        guard let window else {
            log("MISSING \(name)")
            return
        }
        log("SHOT \(name) \(window.windowNumber)")
        try? await Task.sleep(nanoseconds: captureNanoseconds)
    }

    private func log(_ line: String) {
        guard let handle = try? FileHandle(forWritingTo: logURL) else { return }
        handle.seekToEndOfFile()
        handle.write(Data((line + "\n").utf8))
        try? handle.close()
    }

    private static func largestScrollableView(in view: NSView?) -> NSScrollView? {
        guard let view else { return nil }
        var best: NSScrollView?
        var stack: [NSView] = [view]
        while let next = stack.popLast() {
            if let scrollView = next as? NSScrollView,
               let documentView = scrollView.documentView,
               documentView.bounds.height > scrollView.contentView.bounds.height + 4 {
                let area = scrollView.frame.width * scrollView.frame.height
                if area > (best.map { $0.frame.width * $0.frame.height } ?? 0) { best = scrollView }
            }
            stack.append(contentsOf: next.subviews)
        }
        return best
    }

    private static func scroll(_ scrollView: NSScrollView, toTopOffset offset: CGFloat, documentHeight: CGFloat) {
        let isFlipped = scrollView.documentView?.isFlipped ?? true
        let visibleHeight = scrollView.contentView.bounds.height
        // A scroll view under a toolbar starts above zero, by the toolbar's inset.
        let top = -scrollView.contentInsets.top
        let y = isFlipped ? top + offset : max(0, documentHeight - visibleHeight - offset)
        scrollView.contentView.scroll(to: NSPoint(x: 0, y: y))
        scrollView.reflectScrolledClipView(scrollView.contentView)
    }
}
#endif
