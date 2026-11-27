//
//  TerminalWindowFinder.swift
//  OpenClicky
//
//  Brings forward the terminal a coding agent's session runs in. A session's log names its
//  project folder but not its window, so this is a best effort: the terminal or editor window
//  whose title names the project, else the first terminal app that is running.
//

import AppKit
import ApplicationServices
import Foundation

enum TerminalWindowFinder {
    struct WindowCandidate: Equatable {
        let ownerBundleIdentifier: String
        let ownerPID: pid_t
        let title: String
    }

    /// The apps a coding agent runs in: terminals first, then editors with a terminal pane.
    static let terminalAndEditorBundleIdentifiers: [String] = [
        "com.apple.Terminal", "com.googlecode.iterm2", "dev.warp.Warp-Stable", "com.mitchellh.ghostty",
        "com.github.wez.wezterm", "net.kovidgoyal.kitty", "org.alacritty",
        "com.microsoft.VSCode", "com.todesktop.230313mzl4w4u92", "dev.zed.Zed",
    ]

    /// The first window of a terminal or editor whose title names the project as a whole name
    /// ("open" does not find "openclicky-site").
    static func bestWindow(forProject project: String, among windows: [WindowCandidate]) -> WindowCandidate? {
        guard !project.isEmpty else { return nil }
        return windows.first { window in
            terminalAndEditorBundleIdentifiers.contains(window.ownerBundleIdentifier) && title(window.title, namesProject: project)
        }
    }

    /// Letters, digits and the punctuation folder names are made of all count as part of a name.
    private static func isPartOfAName(_ character: Character) -> Bool {
        character.isLetter || character.isNumber || character == "-" || character == "_" || character == "."
    }

    private static func title(_ title: String, namesProject project: String) -> Bool {
        var searchRange = title.startIndex..<title.endIndex
        while let foundRange = title.range(of: project, options: .caseInsensitive, range: searchRange) {
            let characterBefore = foundRange.lowerBound > title.startIndex ? title[title.index(before: foundRange.lowerBound)] : nil
            let characterAfter = foundRange.upperBound < title.endIndex ? title[foundRange.upperBound] : nil
            let startsAName = characterBefore.map { !isPartOfAName($0) } ?? true
            let endsAName = characterAfter.map { !isPartOfAName($0) } ?? true
            if startsAName && endsAName { return true }
            searchRange = foundRange.upperBound..<title.endIndex
        }
        return false
    }

    /// Raises the session's window and activates its app; with no window naming the project, the
    /// first running terminal app is activated. Needs Accessibility to read window titles.
    @MainActor
    static func bringForward(_ session: AgentSession) {
        let runningApps = terminalAndEditorBundleIdentifiers.flatMap { NSRunningApplication.runningApplications(withBundleIdentifier: $0) }
        var windowElementsByCandidate: [(candidate: WindowCandidate, windowElement: AXUIElement)] = []
        for runningApp in runningApps {
            guard let bundleIdentifier = runningApp.bundleIdentifier else { continue }
            let appElement = AXUIElementCreateApplication(runningApp.processIdentifier)
            AXUIElementSetMessagingTimeout(appElement, 0.25)
            var windowsValue: CFTypeRef?
            guard AXUIElementCopyAttributeValue(appElement, kAXWindowsAttribute as CFString, &windowsValue) == .success,
                  let windowElements = windowsValue as? [AXUIElement] else { continue }
            for windowElement in windowElements {
                var titleValue: CFTypeRef?
                guard AXUIElementCopyAttributeValue(windowElement, kAXTitleAttribute as CFString, &titleValue) == .success,
                      let windowTitle = titleValue as? String else { continue }
                windowElementsByCandidate.append((WindowCandidate(ownerBundleIdentifier: bundleIdentifier, ownerPID: runningApp.processIdentifier, title: windowTitle), windowElement))
            }
        }

        if let bestCandidate = bestWindow(forProject: session.project, among: windowElementsByCandidate.map(\.candidate)),
           let match = windowElementsByCandidate.first(where: { $0.candidate == bestCandidate }) {
            AXUIElementPerformAction(match.windowElement, kAXRaiseAction as CFString)
            NSRunningApplication(processIdentifier: bestCandidate.ownerPID)?.activate()
            AppLog.append("agents: brought the session's window forward")
        } else if let firstTerminalApp = runningApps.first {
            firstTerminalApp.activate()
            AppLog.append("agents: no window names the project; activated \(firstTerminalApp.localizedName ?? "the terminal")")
        } else {
            AppLog.append("agents: no terminal is running")
        }
    }
}
