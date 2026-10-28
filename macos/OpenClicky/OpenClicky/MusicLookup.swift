//
//  MusicLookup.swift
//  OpenClicky
//
//  "Play Blinding Lights" in Spotify's desktop app. Spotify plays a track by URI but offers no
//  search of its own, so the song is looked up first: on the person's own Spotify client
//  credentials straight to Spotify, else through the OpenClicky backend's catalogue search (the
//  words only), and when neither answers, Spotify's own search page opens for them to pick.
//

import AppKit
import Foundation

nonisolated struct MusicTrack: Equatable {
    let uri: String
    let name: String
    let artists: [String]
}

nonisolated enum MusicLookupRoute: Equatable {
    case spotify(clientId: String, clientSecret: String)
    case backend(URL)
    case none

    /// Own credentials ("clientId:clientSecret" in shell.json) first; else the backend.
    static func route(credentials: String?, backendURL: URL?) -> MusicLookupRoute {
        if let credentials {
            let parts = credentials.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: ":", maxSplits: 1).map(String.init)
            if parts.count == 2, !parts[0].isEmpty, !parts[1].isEmpty { return .spotify(clientId: parts[0], clientSecret: parts[1]) }
        }
        if let backendURL { return .backend(backendURL) }
        return .none
    }

    var logName: String {
        switch self {
        case .spotify: return "spotify"
        case .backend: return "backend"
        case .none: return "none"
        }
    }
}

nonisolated enum MusicTrackPicker {
    /// The track whose name and artists share the most words (three letters or more) with what
    /// was said — "blinding lights by the weeknd" picks The Weeknd's over a remix by someone else —
    /// else the first result. The confirmation names the pick, so a wrong one is heard.
    static func pick(_ tracks: [MusicTrack], for query: String) -> MusicTrack? {
        let requestWords = Set(words(in: query).filter { $0.count >= 3 })
        var best: (track: MusicTrack, sharedWords: Int)?
        for track in tracks {
            let trackWords = Set(words(in: track.name) + track.artists.flatMap(words))
            let sharedWords = requestWords.intersection(trackWords).count
            if sharedWords > 0, sharedWords > (best?.sharedWords ?? 0) { best = (track, sharedWords) }
        }
        return best?.track ?? tracks.first
    }

    private static func words(in text: String) -> [String] {
        text.lowercased().components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
    }
}

/// The catalogue search, on whichever route applies. Every request waits at most four seconds;
/// any failure is an empty list, and the caller falls back to Spotify's own search.
nonisolated struct MusicLookup {
    let route: MusicLookupRoute
    var session: URLSession = .shared
    private static let requestTimeout: TimeInterval = 4

    /// The client-credentials token, kept until a minute before Spotify says it expires. One per
    /// app: the lookup is built once in `MacActionRunner.live`.
    private final class TokenCache: @unchecked Sendable {
        private let lock = NSLock()
        private var token: (value: String, expiresAt: Date)?
        func current() -> String? {
            lock.withLock { token.flatMap { Date() < $0.expiresAt.addingTimeInterval(-60) ? $0.value : nil } }
        }
        func store(_ value: String, expiresIn: TimeInterval) {
            lock.withLock { token = (value, Date().addingTimeInterval(expiresIn)) }
        }
    }
    private static let tokenCache = TokenCache()

    func search(_ query: String) async -> [MusicTrack] {
        do {
            switch route {
            case .spotify(let clientId, let clientSecret):
                let token = try await spotifyToken(clientId: clientId, clientSecret: clientSecret)
                var components = URLComponents(string: "https://api.spotify.com/v1/search")!
                components.queryItems = [
                    URLQueryItem(name: "q", value: query), URLQueryItem(name: "type", value: "track"), URLQueryItem(name: "limit", value: "5"),
                    URLQueryItem(name: "market", value: Locale.current.region?.identifier ?? "US"),
                ]
                var request = URLRequest(url: components.url!, timeoutInterval: Self.requestTimeout)
                request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
                return try Self.decodeSpotifyResults(try await data(for: request))
            case .backend(let backendURL):
                var components = URLComponents(url: backendURL.appendingPathComponent("music/search"), resolvingAgainstBaseURL: false)!
                components.queryItems = [URLQueryItem(name: "q", value: query)]
                return try Self.decodeBackendResults(try await data(for: URLRequest(url: components.url!, timeoutInterval: Self.requestTimeout)))
            case .none:
                return []
            }
        } catch {
            AppLog.append("music lookup failed (\(route.logName)): \(error.localizedDescription)")
            return []
        }
    }

    private func spotifyToken(clientId: String, clientSecret: String) async throws -> String {
        if let token = Self.tokenCache.current() { return token }
        var request = URLRequest(url: URL(string: "https://accounts.spotify.com/api/token")!, timeoutInterval: Self.requestTimeout)
        request.httpMethod = "POST"
        request.setValue("Basic \(Data("\(clientId):\(clientSecret)".utf8).base64EncodedString())", forHTTPHeaderField: "Authorization")
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data("grant_type=client_credentials".utf8)
        struct TokenReply: Decodable { let access_token: String; let expires_in: Double }
        let reply = try JSONDecoder().decode(TokenReply.self, from: try await data(for: request))
        Self.tokenCache.store(reply.access_token, expiresIn: reply.expires_in)
        return reply.access_token
    }

    private func data(for request: URLRequest) async throws -> Data {
        let (data, response) = try await session.data(for: request)
        if let status = (response as? HTTPURLResponse)?.statusCode, status >= 300 {
            throw URLError(.badServerResponse, userInfo: [NSLocalizedDescriptionKey: "status \(status)"])
        }
        return data
    }

    /// The backend's `{tracks:[{uri,name,artists:[String],…}]}`.
    static func decodeBackendResults(_ data: Data) throws -> [MusicTrack] {
        struct Reply: Decodable { let tracks: [Track] }
        struct Track: Decodable { let uri: String; let name: String; let artists: [String] }
        return try JSONDecoder().decode(Reply.self, from: data).tracks.map { MusicTrack(uri: $0.uri, name: $0.name, artists: $0.artists) }
    }

    /// Spotify's own `{tracks:{items:[{uri,name,artists:[{name}],…}]}}`.
    static func decodeSpotifyResults(_ data: Data) throws -> [MusicTrack] {
        struct Reply: Decodable { let tracks: Items }
        struct Items: Decodable { let items: [Track] }
        struct Track: Decodable { let uri: String; let name: String; let artists: [Artist] }
        struct Artist: Decodable { let name: String }
        return try JSONDecoder().decode(Reply.self, from: data).tracks.items.map { MusicTrack(uri: $0.uri, name: $0.name, artists: $0.artists.map(\.name)) }
    }
}

/// Spotify's desktop app through its scripting interface (the Media plugin's pattern: a utility
/// queue, one script at a time, a three-second timeout).
nonisolated enum SpotifyPlayer {
    static let bundleIdentifier = "com.spotify.client"
    private static let queue = DispatchQueue(label: "openclicky.spotify.applescript", qos: .utility)

    static func isRunning() -> Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: bundleIdentifier).isEmpty
    }

    /// Opens Spotify and waits until it answers a script, or `seconds` pass.
    @MainActor
    static func launchAndWait(seconds: TimeInterval = 6) async -> Bool {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) else { return false }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = false
        NSWorkspace.shared.openApplication(at: url, configuration: configuration, completionHandler: nil)
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if isRunning(), await run("tell application \"Spotify\" to player state") != nil { return true }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
        return false
    }

    static func play(uri: String) async -> Bool {
        await run("tell application \"Spotify\" to play track \"\(MailScripts.escaped(uri))\"") != nil
    }

    /// `spotify:search:<query>`: Spotify's own search page, for when the lookup found nothing.
    static func searchURL(for query: String) -> URL {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return URL(string: "spotify:search:\(query.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")")!
    }

    static func openSearch(for query: String) {
        NSWorkspace.shared.open(searchURL(for: query))
    }

    private static func run(_ source: String) async -> String? {
        await withCheckedContinuation { continuation in
            queue.async {
                var errorInfo: NSDictionary?
                let result = NSAppleScript(source: "with timeout of 3 seconds\n\(source)\nend timeout")?.executeAndReturnError(&errorInfo)
                if result == nil { AppLog.append("spotify script failed: \(errorInfo?[NSAppleScript.errorMessage] ?? "unknown")") }
                continuation.resume(returning: result.map { $0.stringValue ?? "" })
            }
        }
    }
}
