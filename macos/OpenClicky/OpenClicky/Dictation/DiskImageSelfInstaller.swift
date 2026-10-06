//
//  DiskImageSelfInstaller.swift
//  OpenClicky
//
//  Launched from the disk image, the app offers to put itself in /Applications and run from
//  there: one click instead of a drag, and the copy that gets the permissions is the one that
//  stays. The decision is pure (`plan(bundlePath:applicationsPath:)`) so it can be tested; the
//  copy keeps the previous app aside until the new one has launched.
//

import AppKit
import Foundation

enum DiskImageSelfInstaller {

    enum Plan: Equatable {
        /// Running from /Applications (or anywhere that is not a mounted image): nothing to do.
        case alreadyInstalled
        /// Running from a mounted volume: offer to copy to `destination`.
        case install(destination: String, replacesExisting: Bool)
    }

    static func plan(bundlePath: String, applicationsPath: String = "/Applications", fileExists: (String) -> Bool = { FileManager.default.fileExists(atPath: $0) }) -> Plan {
        let standardized = (bundlePath as NSString).standardizingPath
        guard standardized.hasPrefix("/Volumes/") else { return .alreadyInstalled }
        let destination = (applicationsPath as NSString).appendingPathComponent((standardized as NSString).lastPathComponent)
        guard destination != standardized else { return .alreadyInstalled }
        return .install(destination: destination, replacesExisting: fileExists(destination))
    }

    /// Asks, copies, relaunches the installed copy and quits this one. Returns true when this
    /// process is about to terminate.
    @MainActor
    static func offerToInstallIfNeeded() -> Bool {
        guard case let .install(destination, replacesExisting) = plan(bundlePath: Bundle.main.bundlePath) else { return false }
        let alert = NSAlert()
        alert.messageText = "move openclicky to applications?"
        alert.informativeText = replacesExisting
            ? "a copy is already in /Applications. this build replaces it, keeps your settings, and opens from there."
            : "openclicky runs best from /Applications: the permissions you grant stay with the copy you keep."
        alert.addButton(withTitle: replacesExisting ? "Replace and Open" : "Move to Applications")
        alert.addButton(withTitle: "Not Now")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return false }
        do {
            try install(from: Bundle.main.bundlePath, to: destination, replacesExisting: replacesExisting)
        } catch {
            let failure = NSAlert()
            failure.messageText = "openclicky couldn't install itself"
            failure.informativeText = "\(error.localizedDescription)\n\ndrag openclicky to applications instead."
            failure.runModal()
            return false
        }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: destination), configuration: configuration) { _, _ in
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { NSApp.terminate(nil) }
        }
        return true
    }

    static func install(from source: String, to destination: String, replacesExisting: Bool, fileManager: FileManager = .default) throws {
        let backup = destination + ".previous"
        if replacesExisting {
            try? fileManager.removeItem(atPath: backup)
            try fileManager.moveItem(atPath: destination, toPath: backup)
        }
        do {
            try fileManager.copyItem(atPath: source, toPath: destination)
        } catch {
            if replacesExisting { try? fileManager.moveItem(atPath: backup, toPath: destination) }
            throw error
        }
        // The copy carries the quarantine flag from the download; the user already said yes.
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        process.arguments = ["-dr", "com.apple.quarantine", destination]
        try? process.run()
        process.waitUntilExit()
        try? fileManager.removeItem(atPath: backup)
        AppLog.append("self-install: copied to \(destination) (replaced existing: \(replacesExisting))")
    }
}
