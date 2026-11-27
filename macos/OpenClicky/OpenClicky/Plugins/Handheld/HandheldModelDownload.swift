//
//  HandheldModelDownload.swift
//  OpenClicky
//
//  A device's 3D model is not in the app: megabytes that only an owner of that device would use.
//  The Handheld page offers to download it (from this project's repository, `assets/handheld/`),
//  checks it against a checksum pinned in the device's description, and keeps it in the person's
//  own folder, where `HandheldDeviceModelFile` looks. Nothing is fetched until the person asks.
//

import Combine
import Foundation

/// Where a device's model is downloaded from, and what it must be.
struct HandheldModelDownload: Equatable {
    let url: URL
    let sha256: String
    let bytes: Int64

    /// The size as the page says it ("13 MB").
    var sizeText: String {
        "\(Int((Double(bytes) / 1_000_000).rounded())) MB"
    }
}

@MainActor
final class HandheldModelInstaller: ObservableObject {
    static let shared = HandheldModelInstaller(fetch: HandheldModelInstaller.fetchFromTheNetwork)

    /// Downloads a URL to a temporary file and returns where it is.
    typealias Fetch = @Sendable (URL) async throws -> URL

    @Published private var stateByDeviceID: [String: ModelInstallState] = [:]
    private let fetch: Fetch

    init(fetch: @escaping Fetch) {
        self.fetch = fetch
    }

    func state(forDeviceID deviceID: String) -> ModelInstallState {
        stateByDeviceID[deviceID] ?? .notInstalled
    }

    /// Downloads the model, and keeps it at `destination` only when it matches its checksum.
    /// `onInstalled` runs once the file is in place.
    func install(_ download: HandheldModelDownload, deviceID: String, to destination: URL, onInstalled: () -> Void) async {
        guard !state(forDeviceID: deviceID).isBusy else { return }
        stateByDeviceID[deviceID] = .downloading(fraction: 0)
        do {
            let downloadedFile = try await fetch(download.url)
            guard try ModelDownload.sha256(of: downloadedFile) == download.sha256 else {
                try? FileManager.default.removeItem(at: downloadedFile)
                AppLog.append("handheld: the model for \(deviceID) didn't match its checksum, thrown away")
                stateByDeviceID[deviceID] = .failed("the download didn't match its checksum")
                return
            }
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: downloadedFile, to: destination)
            AppLog.append("handheld: the model for \(deviceID) was downloaded")
            stateByDeviceID[deviceID] = .installed
            onInstalled()
        } catch {
            AppLog.append("handheld: the model for \(deviceID) could not be downloaded: \(error)")
            stateByDeviceID[deviceID] = .failed(error.localizedDescription)
        }
    }

    private static let fetchFromTheNetwork: Fetch = { url in
        let (temporaryFile, response) = try await URLSession.shared.download(from: url)
        guard let httpResponse = response as? HTTPURLResponse, (200..<300).contains(httpResponse.statusCode) else {
            throw ModelDownloadError.downloadFailed("the server answered \((response as? HTTPURLResponse)?.statusCode ?? 0)")
        }
        // The system deletes its temporary file when this returns, so it is moved aside first.
        let keptFile = FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-handheld-model-\(UUID().uuidString).tmp")
        try FileManager.default.moveItem(at: temporaryFile, to: keptFile)
        return keptFile
    }
}
