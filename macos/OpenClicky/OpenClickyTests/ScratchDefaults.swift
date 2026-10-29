import Foundation

/// A throwaway UserDefaults suite for one test. Every suite is a plist in ~/Library/Preferences, and
/// the test host is killed rather than exiting, so nothing can clean up after the last test. Instead
/// a suite's name carries the test process's id, and the first suite a run makes removes the plists
/// of processes that have ended.
enum ScratchDefaults {
    private static let prefix = "openclicky-tests-"
    /// Names from before the process id was in them; removed once they are an hour old, as a test
    /// run from an older checkout may still be using them.
    private static let unmarkedPrefixes = [prefix, "openclicky-cleanup-readers-", "openclicky-profile-tests-"]

    private static let removeEndedRuns: Void = {
        let fileManager = FileManager.default
        let preferences = fileManager.homeDirectoryForCurrentUser.appendingPathComponent("Library/Preferences")
        guard let files = try? fileManager.contentsOfDirectory(at: preferences, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        let hourAgo = Date().addingTimeInterval(-3600)
        for file in files where file.pathExtension == "plist" {
            let name = file.deletingPathExtension().lastPathComponent
            if name.hasPrefix(prefix + "pid"), let pid = Int32(name.dropFirst(prefix.count + 3).prefix { $0 != "-" }) {
                if kill(pid, 0) != 0 && errno == ESRCH { try? fileManager.removeItem(at: file) }
            } else if unmarkedPrefixes.contains(where: name.hasPrefix),
                      let modified = try? file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate,
                      modified < hourAgo {
                try? fileManager.removeItem(at: file)
            }
        }
    }()

    static func make() -> UserDefaults {
        _ = removeEndedRuns
        return UserDefaults(suiteName: "\(prefix)pid\(getpid())-\(UUID().uuidString)")!
    }
}
