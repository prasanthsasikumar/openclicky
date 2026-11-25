//
//  PlateContext.swift
//  OpenClicky
//
//  What this Mac knows about the person's recent work, as the few lines the suggestions are
//  written from: the next days of the calendar (when the calendar tile has access), the notes
//  pad, and the projects and titles of the Claude Code and Codex sessions of the last three days.
//  Titles only: no prompt, no reply and no file content is read out of a session log beyond the
//  tail the Coding agents plugin already reads. Dictation history is never included.
//

import Foundation

struct PlateContext: Equatable {
    struct Session: Equatable {
        var agent: String
        var project: String
        var title: String?
        var lastEventAt: Date
    }

    struct Event: Equatable {
        var title: String
        var start: Date
        var isAllDay: Bool
    }

    var sessions: [Session] = []
    var events: [Event] = []
    var notes: String = ""

    static let sessionWindow: TimeInterval = 3 * 24 * 3600
    static let maximumSessions = 24
    static let maximumEvents = 12
    static let maximumNoteCharacters = 600

    var isEmpty: Bool { sessions.isEmpty && events.isEmpty && notes.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    /// The digest the model reads. Sessions are grouped by project, newest project first, with
    /// each title once.
    func digest(now: Date, calendar: Calendar = .current, locale: Locale = .current) -> String {
        var sections: [String] = []
        let dateFormatter = DateFormatter()
        dateFormatter.calendar = calendar
        dateFormatter.locale = locale
        dateFormatter.dateFormat = "EEEE d MMMM"
        sections.append("Today is \(dateFormatter.string(from: now)).")

        if !sessions.isEmpty {
            var order: [String] = []
            var titlesByProject: [String: [String]] = [:]
            for session in sessions.sorted(by: { $0.lastEventAt > $1.lastEventAt }).prefix(Self.maximumSessions) {
                if titlesByProject[session.project] == nil {
                    order.append(session.project)
                    titlesByProject[session.project] = []
                }
                if let title = session.title?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty,
                   !(titlesByProject[session.project] ?? []).contains(title) {
                    titlesByProject[session.project]?.append(title)
                }
            }
            let lines = order.map { project -> String in
                let titles = titlesByProject[project] ?? []
                return titles.isEmpty ? "- \(project)" : "- \(project): " + titles.prefix(5).joined(separator: "; ")
            }
            sections.append("Projects they worked on with coding agents in the last three days, newest first, with what each session was about:\n" + lines.joined(separator: "\n"))
        }

        if !events.isEmpty {
            let timeFormatter = DateFormatter()
            timeFormatter.calendar = calendar
            timeFormatter.locale = locale
            timeFormatter.dateFormat = "EEE d MMM HH:mm"
            let dayFormatter = DateFormatter()
            dayFormatter.calendar = calendar
            dayFormatter.locale = locale
            dayFormatter.dateFormat = "EEE d MMM"
            let lines = events.sorted(by: { $0.start < $1.start }).prefix(Self.maximumEvents).map { event in
                "- \(event.isAllDay ? dayFormatter.string(from: event.start) + " (all day)" : timeFormatter.string(from: event.start)): \(event.title)"
            }
            sections.append("Their calendar for the coming days:\n" + lines.joined(separator: "\n"))
        }

        let trimmedNotes = notes.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedNotes.isEmpty {
            sections.append("Their notes pad:\n" + String(trimmedNotes.prefix(Self.maximumNoteCharacters)))
        }
        return sections.joined(separator: "\n\n")
    }
}

/// Reads the session logs for the digest. Off the main actor: three days of logs is a few
/// hundred small tail reads.
enum PlateSessionScan {
    static let tailBytes = 65_536
    static let maximumFiles = 60

    static func recentSessions(roots: AgentLogRoots, now: Date, window: TimeInterval = PlateContext.sessionWindow) -> [PlateContext.Session] {
        let cutoff = now.addingTimeInterval(-window)
        let fileManager = FileManager.default
        var files: [(url: URL, modifiedAt: Date, isClaude: Bool)] = []

        for folder in children(of: roots.claudeProjects) where isDirectory(folder) {
            for file in children(of: folder) where file.pathExtension == "jsonl" {
                if let modifiedAt = modificationDate(of: file), modifiedAt > cutoff { files.append((file, modifiedAt, true)) }
            }
        }
        // Codex: sessions/<year>/<month>/<day>/rollout-*.jsonl.
        if let walker = fileManager.enumerator(at: roots.codexSessions, includingPropertiesForKeys: [.contentModificationDateKey], options: [.skipsHiddenFiles]) {
            for case let file as URL in walker where file.pathExtension == "jsonl" && file.lastPathComponent.hasPrefix("rollout-") {
                if let modifiedAt = modificationDate(of: file), modifiedAt > cutoff { files.append((file, modifiedAt, false)) }
            }
        }

        var sessions: [PlateContext.Session] = []
        for file in files.sorted(by: { $0.modifiedAt > $1.modifiedAt }).prefix(maximumFiles) {
            let text = tail(of: file.url)
            let parse = file.isClaude ? AgentSessionParser.parseClaudeTail(text) : AgentSessionParser.parseCodexTail(text)
            if !file.isClaude && AgentSessionParser.isOwnScratchRollout(cwd: parse.cwd) { continue }
            guard let cwd = parse.cwd, !cwd.isEmpty else { continue }
            let project = URL(fileURLWithPath: cwd).lastPathComponent
            guard !project.isEmpty, project != "/" else { continue }
            sessions.append(PlateContext.Session(agent: file.isClaude ? "claude" : "codex", project: project, title: parse.title, lastEventAt: file.modifiedAt))
        }
        return sessions
    }

    private static func children(of folder: URL) -> [URL] {
        (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey, .isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
    }

    private static func isDirectory(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false
    }

    private static func modificationDate(of url: URL) -> Date? {
        try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
    }

    private static func tail(of url: URL) -> String {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return "" }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        try? handle.seek(toOffset: size > UInt64(tailBytes) ? size - UInt64(tailBytes) : 0)
        let data = (try? handle.readToEnd()) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }
}

extension PlateContext {
    /// Everything the digest draws on, gathered now.
    @MainActor static func gather(now: Date = Date()) async -> PlateContext {
        var context = PlateContext()
        let roots = AgentLogRoots.standard
        context.sessions = await Task.detached(priority: .utility) { PlateSessionScan.recentSessions(roots: roots, now: now) }.value
        // The calendar only when its tile is on and already has access: this never asks for it.
        let calendarStore = CalendarStore.shared
        if calendarStore.isEnabled {
            context.events = calendarStore.days.prefix(3).flatMap(\.events).map { Event(title: $0.title, start: $0.start, isAllDay: $0.isAllDay) }
            var seen = Set<String>()
            context.events = context.events.filter { seen.insert("\($0.title)|\($0.start.timeIntervalSince1970)").inserted }
        }
        context.notes = NotesStore.shared.text
        return context
    }
}
