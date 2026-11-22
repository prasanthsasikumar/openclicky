//
//  PlateStore.swift
//  OpenClicky
//
//  The day's cards on disk: `~/Library/Application Support/OpenClicky/Plate/today.json`, and the
//  batch before it as `previous.json`, which is what the next morning's request reads back.
//

import Foundation

struct PlateStore {
    let folder: URL

    static var standard: PlateStore {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        return PlateStore(folder: support.appendingPathComponent("OpenClicky/Plate", isDirectory: true))
    }

    private var todayURL: URL { folder.appendingPathComponent("today.json") }
    private var previousURL: URL { folder.appendingPathComponent("previous.json") }

    func loadCurrent() -> PlateBatch? { load(todayURL) }
    func loadPrevious() -> PlateBatch? { load(previousURL) }

    /// Saves the batch. A batch for a new day moves the one on disk to `previous.json` first.
    func save(_ batch: PlateBatch) {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        if let current = loadCurrent(), current.dayKey != batch.dayKey {
            write(current, to: previousURL)
        }
        write(batch, to: todayURL)
    }

    private func load(_ url: URL) -> PlateBatch? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(PlateBatch.self, from: data)
    }

    private func write(_ batch: PlateBatch, to url: URL) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(batch) else { return }
        try? data.write(to: url, options: .atomic)
    }
}
