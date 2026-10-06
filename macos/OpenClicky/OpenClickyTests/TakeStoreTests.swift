//
//  TakeStoreTests.swift
//  OpenClickyTests
//
//  The SQLite take store in a temp file: inserts, search, revisions, stats.
//

import Foundation
import Testing
@testable import OpenClicky

struct TakeStoreTests {
    private func makeStore() throws -> TakeStore {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-takes-\(UUID().uuidString).sqlite")
        return try TakeStore(fileURL: url)
    }

    @Test func insertedTakesComeBackNewestFirst() throws {
        let store = try makeStore()
        let older = TakeRecord(createdAt: Date(timeIntervalSinceNow: -100), rawText: "tech week starts today", formattedText: "Tech Week starts in San Francisco today.", appBundleID: "com.google.Chrome", appName: "Google Chrome", engine: "offline")
        let newer = TakeRecord(rawText: "three kilograms of atta", formattedText: "3 kg of atta.", appBundleID: "com.apple.Notes", appName: "Notes", engine: "sarvam")
        try store.insert(older)
        try store.insert(newer)
        let rows = try store.recent()
        #expect(rows.map(\.id) == [newer.id, older.id])
        #expect(rows.first?.engine == "sarvam")
    }

    @Test func searchMatchesWordsInEitherText() throws {
        let store = try makeStore()
        try store.insert(TakeRecord(rawText: "tech week starts today", formattedText: "Tech Week starts in San Francisco today."))
        try store.insert(TakeRecord(rawText: "three kilograms of atta", formattedText: "3 kg of atta."))
        #expect(try store.recent(query: "francisco").count == 1)
        #expect(try store.recent(query: "kilograms").count == 1)
        #expect(try store.recent(query: "today week").count == 1)
        #expect(try store.recent(query: "nothing%").count == 0)
    }

    @Test func revisionsKeepThePreviousTextAndUpdateTheTake() throws {
        let store = try makeStore()
        let take = TakeRecord(rawText: "hello", formattedText: "Hello.")
        try store.insert(take)
        try store.revise(takeID: take.id, newText: "Hello there.", editor: "history")
        #expect(try store.fetch(id: take.id)?.formattedText == "Hello there.")
    }

    @Test func statsCountWordsTodayAndTheBusiestApp() throws {
        let store = try makeStore()
        try store.insert(TakeRecord(rawText: "", formattedText: "one two three", appBundleID: "com.google.Chrome", appName: "Google Chrome"))
        try store.insert(TakeRecord(rawText: "", formattedText: "four five", appBundleID: "com.google.Chrome", appName: "Google Chrome"))
        try store.insert(TakeRecord(rawText: "", formattedText: "six", appBundleID: "com.apple.Notes", appName: "Notes"))
        try store.insert(TakeRecord(createdAt: Date(timeIntervalSinceNow: -3 * 86_400), rawText: "", formattedText: "old words here"))
        try store.insert(TakeRecord(mode: .clipboard, rawText: "", formattedText: "copied text not counted"))
        let stats = try store.stats()
        #expect(stats.wordsToday == 6)
        #expect(stats.takesToday == 3)
        #expect(stats.wordsThisWeek == 9)
        #expect(stats.mostUsedAppToday == "Google Chrome")
        #expect(stats.allTimeTakes == 4)
    }

    @Test func deleteAllEmptiesTheStore() throws {
        let store = try makeStore()
        try store.insert(TakeRecord(rawText: "a", formattedText: "A."))
        try store.deleteAll()
        #expect(try store.recent().isEmpty)
    }
}
