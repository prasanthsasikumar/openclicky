//
//  TakeAudioStoreTests.swift
//  OpenClickyTests
//

import Foundation
import Testing
@testable import OpenClicky

struct TakeAudioStoreTests {
    private func makeStore() -> TakeAudioStore {
        TakeAudioStore(directoryURL: FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-audio-\(UUID().uuidString)"))
    }

    @Test func retainedAudioIsAWavThatCanBeFoundAndDiscarded() throws {
        let store = makeStore()
        defer { store.discardAll() }
        let id = UUID()
        try store.retain(takeID: id, pcm16: Data(repeating: 1, count: 3200))
        #expect(store.hasAudio(for: id))
        let wav = try Data(contentsOf: store.url(for: id))
        #expect(String(decoding: wav.prefix(4), as: UTF8.self) == "RIFF")
        #expect(wav.count == 44 + 3200)
        store.discard(takeID: id)
        #expect(!store.hasAudio(for: id))
    }

    @Test func pruningKeepsTheNewestTakes() throws {
        let store = makeStore()
        defer { store.discardAll() }
        var ids: [UUID] = []
        for index in 0..<(TakeAudioStore.maxRetainedTakes + 5) {
            let id = UUID()
            ids.append(id)
            try store.retain(takeID: id, pcm16: Data(repeating: UInt8(index % 7), count: 64))
            // Distinct modification dates, oldest first.
            try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSince1970: Double(1_000 + index))], ofItemAtPath: store.url(for: id).path)
        }
        store.pruneIfNeeded()
        #expect(!store.hasAudio(for: ids[0]))
        #expect(!store.hasAudio(for: ids[4]))
        #expect(store.hasAudio(for: ids[5]))
        #expect(store.hasAudio(for: ids.last!))
    }

    @Test func captureCollectsThenForgets() {
        let store = makeStore()
        store.beginCapture()
        #expect(store.endCapture().isEmpty)
    }
}
