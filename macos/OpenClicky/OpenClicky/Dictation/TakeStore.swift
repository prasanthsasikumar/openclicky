//
//  TakeStore.swift
//  OpenClicky
//
//  Every take, on this Mac, in SQLite: what was heard, what was written, where it went. The system
//  sqlite3 library, so there is no dependency; one serial queue, so there is no contention. The
//  store is what the record page's counts, the history page and its search read from.
//

import Foundation
import SQLite3

/// One take, as recorded.
struct TakeRecord: Identifiable, Equatable {
    enum Mode: String { case dictate, edit, clipboard }
    enum Status: String { case complete, failed, cancelled }
    enum PasteOutcome: String { case verified, posted, leftInOrb, leftOnPasteboard, none }

    var id: UUID
    var createdAt: Date
    var mode: Mode
    var status: Status
    var rawText: String
    var formattedText: String
    var appBundleID: String?
    var appName: String?
    var language: String?
    var engine: String
    var durationSeconds: Double
    var pasteOutcome: PasteOutcome
    var pinned: Bool
    var failureReason: String?

    init(id: UUID = UUID(), createdAt: Date = Date(), mode: Mode = .dictate, status: Status = .complete,
         rawText: String, formattedText: String, appBundleID: String? = nil, appName: String? = nil,
         language: String? = nil, engine: String = "", durationSeconds: Double = 0,
         pasteOutcome: PasteOutcome = .none, pinned: Bool = false, failureReason: String? = nil) {
        self.id = id; self.createdAt = createdAt; self.mode = mode; self.status = status
        self.rawText = rawText; self.formattedText = formattedText; self.appBundleID = appBundleID
        self.appName = appName; self.language = language; self.engine = engine
        self.durationSeconds = durationSeconds; self.pasteOutcome = pasteOutcome; self.pinned = pinned
        self.failureReason = failureReason
    }

    /// What history shows: the written text, or the raw words when nothing was written.
    var displayText: String { formattedText.isEmpty ? rawText : formattedText }

    var wordCount: Int { displayText.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).count }
}

struct TakeStoreError: Error, LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// The counts the record page shows.
struct TakeStats: Equatable {
    var wordsToday: Int
    var takesToday: Int
    var wordsThisWeek: Int
    var takesThisWeek: Int
    var mostUsedAppToday: String?
    var allTimeTakes: Int
}

final class TakeStore: @unchecked Sendable {
    static let defaultFileURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/OpenClicky/dictation.sqlite")

    private var database: OpaquePointer?
    private let queue = DispatchQueue(label: "org.openclicky.take-store")
    let fileURL: URL

    init(fileURL: URL = TakeStore.defaultFileURL) throws {
        self.fileURL = fileURL
        try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard sqlite3_open_v2(fileURL.path, &database, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nil) == SQLITE_OK else {
            throw TakeStoreError(message: "could not open \(fileURL.path)")
        }
        try execute("PRAGMA journal_mode = WAL")
        try execute("""
        CREATE TABLE IF NOT EXISTS takes (
            id TEXT PRIMARY KEY,
            created_at REAL NOT NULL,
            mode TEXT NOT NULL,
            status TEXT NOT NULL,
            raw_text TEXT NOT NULL,
            formatted_text TEXT NOT NULL,
            app_bundle_id TEXT,
            app_name TEXT,
            language TEXT,
            engine TEXT NOT NULL DEFAULT '',
            duration_seconds REAL NOT NULL DEFAULT 0,
            paste_outcome TEXT NOT NULL DEFAULT 'none',
            pinned INTEGER NOT NULL DEFAULT 0,
            failure_reason TEXT
        )
        """)
        try execute("CREATE INDEX IF NOT EXISTS takes_created_at ON takes (created_at DESC)")
        try execute("""
        CREATE TABLE IF NOT EXISTS take_revisions (
            id TEXT PRIMARY KEY,
            take_id TEXT NOT NULL REFERENCES takes(id) ON DELETE CASCADE,
            created_at REAL NOT NULL,
            previous_text TEXT NOT NULL,
            new_text TEXT NOT NULL,
            editor TEXT NOT NULL
        )
        """)
    }

    deinit {
        if let database { sqlite3_close(database) }
    }

    // MARK: writes

    func insert(_ take: TakeRecord) throws {
        try queue.sync {
            let statement = try prepare("""
            INSERT OR REPLACE INTO takes (id, created_at, mode, status, raw_text, formatted_text, app_bundle_id, app_name, language, engine, duration_seconds, paste_outcome, pinned, failure_reason)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """)
            defer { sqlite3_finalize(statement) }
            bind(statement, 1, take.id.uuidString)
            sqlite3_bind_double(statement, 2, take.createdAt.timeIntervalSince1970)
            bind(statement, 3, take.mode.rawValue)
            bind(statement, 4, take.status.rawValue)
            bind(statement, 5, take.rawText)
            bind(statement, 6, take.formattedText)
            bind(statement, 7, take.appBundleID)
            bind(statement, 8, take.appName)
            bind(statement, 9, take.language)
            bind(statement, 10, take.engine)
            sqlite3_bind_double(statement, 11, take.durationSeconds)
            bind(statement, 12, take.pasteOutcome.rawValue)
            sqlite3_bind_int(statement, 13, take.pinned ? 1 : 0)
            bind(statement, 14, take.failureReason)
            try step(statement)
        }
    }

    /// Rewrites a take's text (an edit from history or Hey Clicky), keeping the previous text as a revision.
    func revise(takeID: UUID, newText: String, editor: String) throws {
        try queue.sync {
            guard let current = try fetchLocked(id: takeID) else { return }
            let revision = try prepare("INSERT INTO take_revisions (id, take_id, created_at, previous_text, new_text, editor) VALUES (?, ?, ?, ?, ?, ?)")
            defer { sqlite3_finalize(revision) }
            bind(revision, 1, UUID().uuidString)
            bind(revision, 2, takeID.uuidString)
            sqlite3_bind_double(revision, 3, Date().timeIntervalSince1970)
            bind(revision, 4, current.displayText)
            bind(revision, 5, newText)
            bind(revision, 6, editor)
            try step(revision)
            let update = try prepare("UPDATE takes SET formatted_text = ? WHERE id = ?")
            defer { sqlite3_finalize(update) }
            bind(update, 1, newText)
            bind(update, 2, takeID.uuidString)
            try step(update)
        }
    }

    func setPinned(_ pinned: Bool, takeID: UUID) throws {
        try queue.sync {
            let update = try prepare("UPDATE takes SET pinned = ? WHERE id = ?")
            defer { sqlite3_finalize(update) }
            sqlite3_bind_int(update, 1, pinned ? 1 : 0)
            bind(update, 2, takeID.uuidString)
            try step(update)
        }
    }

    func delete(takeID: UUID) throws {
        try queue.sync {
            let statement = try prepare("DELETE FROM takes WHERE id = ?")
            defer { sqlite3_finalize(statement) }
            bind(statement, 1, takeID.uuidString)
            try step(statement)
        }
    }

    func deleteAll() throws {
        try queue.sync {
            try execute("DELETE FROM take_revisions")
            try execute("DELETE FROM takes")
        }
    }

    // MARK: reads

    func fetch(id: UUID) throws -> TakeRecord? {
        try queue.sync { try fetchLocked(id: id) }
    }

    /// Newest first. `query` matches words in either text, case-insensitively; `mode` narrows the kind.
    func recent(limit: Int = 500, query: String? = nil, mode: TakeRecord.Mode? = nil, appBundleID: String? = nil) throws -> [TakeRecord] {
        try queue.sync {
            var sql = "SELECT * FROM takes WHERE 1 = 1"
            var arguments: [String] = []
            if let query, !query.trimmingCharacters(in: .whitespaces).isEmpty {
                for word in query.split(whereSeparator: { $0.isWhitespace }) {
                    sql += " AND (raw_text LIKE ? OR formatted_text LIKE ?)"
                    let pattern = "%" + Self.escapeLike(String(word)) + "%"
                    arguments.append(pattern); arguments.append(pattern)
                }
            }
            if let mode { sql += " AND mode = ?"; arguments.append(mode.rawValue) }
            if let appBundleID { sql += " AND app_bundle_id = ?"; arguments.append(appBundleID) }
            sql += " ORDER BY created_at DESC LIMIT \(max(1, limit))"
            let statement = try prepare(sql)
            defer { sqlite3_finalize(statement) }
            for (index, argument) in arguments.enumerated() { bind(statement, Int32(index + 1), argument) }
            var rows: [TakeRecord] = []
            while sqlite3_step(statement) == SQLITE_ROW { rows.append(Self.row(statement)) }
            return rows
        }
    }

    /// The apps takes have gone into, most used first.
    func appsUsed() throws -> [(bundleID: String, name: String?, count: Int)] {
        try queue.sync {
            let statement = try prepare("SELECT app_bundle_id, app_name, COUNT(*) FROM takes WHERE app_bundle_id IS NOT NULL GROUP BY app_bundle_id ORDER BY COUNT(*) DESC")
            defer { sqlite3_finalize(statement) }
            var rows: [(String, String?, Int)] = []
            while sqlite3_step(statement) == SQLITE_ROW {
                rows.append((Self.text(statement, 0) ?? "", Self.text(statement, 1), Int(sqlite3_column_int(statement, 2))))
            }
            return rows
        }
    }

    /// Words dictated on each of the last `days` days, oldest first (today last).
    func wordsPerDay(days: Int = 7, now: Date = Date(), calendar: Calendar = .current) throws -> [(day: Date, words: Int)] {
        let startOfToday = calendar.startOfDay(for: now)
        let firstDay = calendar.date(byAdding: .day, value: -(days - 1), to: startOfToday) ?? startOfToday
        let takes = try recent(limit: 20000).filter { $0.createdAt >= firstDay && $0.mode != .clipboard && $0.status == .complete }
        var byDay: [Date: Int] = [:]
        for take in takes { byDay[calendar.startOfDay(for: take.createdAt), default: 0] += take.wordCount }
        return (0..<days).compactMap { offset in
            guard let day = calendar.date(byAdding: .day, value: offset, to: firstDay) else { return nil }
            return (day, byDay[day] ?? 0)
        }
    }

    func stats(now: Date = Date(), calendar: Calendar = .current) throws -> TakeStats {
        let startOfToday = calendar.startOfDay(for: now)
        let startOfWeek = calendar.date(byAdding: .day, value: -6, to: startOfToday) ?? startOfToday
        let today = try recent(limit: 5000).filter { $0.createdAt >= startOfToday && $0.mode != .clipboard && $0.status == .complete }
        let week = try recent(limit: 20000).filter { $0.createdAt >= startOfWeek && $0.mode != .clipboard && $0.status == .complete }
        let appCounts = Dictionary(grouping: today.compactMap { $0.appName ?? $0.appBundleID }, by: { $0 }).mapValues(\.count)
        let allTime: Int = try queue.sync {
            let statement = try prepare("SELECT COUNT(*) FROM takes WHERE mode != 'clipboard'")
            defer { sqlite3_finalize(statement) }
            return sqlite3_step(statement) == SQLITE_ROW ? Int(sqlite3_column_int(statement, 0)) : 0
        }
        return TakeStats(
            wordsToday: today.reduce(0) { $0 + $1.wordCount },
            takesToday: today.count,
            wordsThisWeek: week.reduce(0) { $0 + $1.wordCount },
            takesThisWeek: week.count,
            mostUsedAppToday: appCounts.max { $0.value < $1.value }?.key,
            allTimeTakes: allTime)
    }

    // MARK: sqlite plumbing

    private func fetchLocked(id: UUID) throws -> TakeRecord? {
        let statement = try prepare("SELECT * FROM takes WHERE id = ?")
        defer { sqlite3_finalize(statement) }
        bind(statement, 1, id.uuidString)
        return sqlite3_step(statement) == SQLITE_ROW ? Self.row(statement) : nil
    }

    private static func row(_ statement: OpaquePointer) -> TakeRecord {
        TakeRecord(
            id: UUID(uuidString: text(statement, 0) ?? "") ?? UUID(),
            createdAt: Date(timeIntervalSince1970: sqlite3_column_double(statement, 1)),
            mode: TakeRecord.Mode(rawValue: text(statement, 2) ?? "") ?? .dictate,
            status: TakeRecord.Status(rawValue: text(statement, 3) ?? "") ?? .complete,
            rawText: text(statement, 4) ?? "",
            formattedText: text(statement, 5) ?? "",
            appBundleID: text(statement, 6),
            appName: text(statement, 7),
            language: text(statement, 8),
            engine: text(statement, 9) ?? "",
            durationSeconds: sqlite3_column_double(statement, 10),
            pasteOutcome: TakeRecord.PasteOutcome(rawValue: text(statement, 11) ?? "") ?? .none,
            pinned: sqlite3_column_int(statement, 12) != 0,
            failureReason: text(statement, 13))
    }

    private static func text(_ statement: OpaquePointer, _ column: Int32) -> String? {
        guard let pointer = sqlite3_column_text(statement, column) else { return nil }
        return String(cString: pointer)
    }

    /// LIKE wildcards typed into the search box are not wildcards to the user; they are dropped.
    private static func escapeLike(_ word: String) -> String {
        word.replacingOccurrences(of: "%", with: "").replacingOccurrences(of: "_", with: "")
    }

    private func prepare(_ sql: String) throws -> OpaquePointer {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK, let statement else {
            throw TakeStoreError(message: "sqlite prepare failed: \(lastErrorMessage()) — \(sql.prefix(80))")
        }
        return statement
    }

    private func step(_ statement: OpaquePointer) throws {
        let result = sqlite3_step(statement)
        guard result == SQLITE_DONE || result == SQLITE_ROW else {
            throw TakeStoreError(message: "sqlite step failed: \(lastErrorMessage())")
        }
    }

    private func execute(_ sql: String) throws {
        guard sqlite3_exec(database, sql, nil, nil, nil) == SQLITE_OK else {
            throw TakeStoreError(message: "sqlite exec failed: \(lastErrorMessage()) — \(sql.prefix(80))")
        }
    }

    private func bind(_ statement: OpaquePointer, _ index: Int32, _ value: String?) {
        if let value {
            sqlite3_bind_text(statement, index, value, -1, unsafeBitCast(-1, to: sqlite3_destructor_type.self))
        } else {
            sqlite3_bind_null(statement, index)
        }
    }

    private func lastErrorMessage() -> String {
        database.map { String(cString: sqlite3_errmsg($0)) } ?? "no database"
    }
}
