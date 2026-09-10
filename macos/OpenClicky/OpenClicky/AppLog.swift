//
//  AppLog.swift
//  OpenClicky
//
//  A plain-text log at ~/Library/Logs/OpenClicky/app.log. The app is launched from Finder, so its
//  stdout goes nowhere; when the buddy points at the wrong thing, this is where the `point_at` line
//  (the model's guess, what it snapped to, or what Claude answered) can be read afterwards.
//

import Foundation

nonisolated enum AppLog {
    static let fileURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Logs/OpenClicky/app.log")
    /// Truncated at launch when it has grown past this.
    private static let maxBytesBeforeTruncation: UInt64 = 2_000_000

    private static let queue = DispatchQueue(label: "org.openclicky.app-log", qos: .utility)
    private static let timestamp: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return formatter
    }()
    private static let handle: FileHandle? = {
        let fileManager = FileManager.default
        do {
            try fileManager.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let size = (try? fileManager.attributesOfItem(atPath: fileURL.path)[.size] as? UInt64) ?? 0
            if !fileManager.fileExists(atPath: fileURL.path) || size > maxBytesBeforeTruncation {
                try Data().write(to: fileURL)
            }
            let handle = try FileHandle(forWritingTo: fileURL)
            handle.seekToEndOfFile()
            return handle
        } catch {
            return nil
        }
    }()

    static func append(_ line: String) {
        let stamped = "\(timestamp.string(from: Date())) \(line)\n"
        queue.async {
            guard let handle, let data = stamped.data(using: .utf8) else { return }
            handle.write(data)
        }
    }
}
