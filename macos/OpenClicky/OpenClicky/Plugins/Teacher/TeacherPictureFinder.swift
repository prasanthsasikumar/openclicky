//
//  TeacherPictureFinder.swift
//  OpenClicky
//
//  When a lesson cannot be drawn (a neuron, the Taj Mahal, a real prism's rainbow), the Teacher
//  shows a real picture: Wikimedia Commons first (diagrams and photos, freely licensed, no key),
//  then the lead image of the matching Wikipedia article. Only the search words leave the Mac.
//  The response parsing is pure and unit-tested.
//

import AppKit
import Foundation

struct TeacherPicture {
    let image: NSImage
    let caption: String
    /// "Wikimedia Commons" or "Wikipedia", shown under the picture.
    let credit: String
}

enum TeacherPictureFinder {
    /// Wikimedia asks every client to name itself.
    static let userAgent = "OpenClicky/\(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1") (https://openclicky.org)"
    static let thumbnailWidth = 900

    static func find(query: String, caption: String) async -> TeacherPicture? {
        let trimmedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedQuery.isEmpty else { return nil }
        if let url = try? await firstURL(from: commonsSearchURL(for: trimmedQuery), parse: commonsThumbnailURLs),
           let image = await download(url) {
            return TeacherPicture(image: image, caption: caption, credit: "Wikimedia Commons")
        }
        if let url = try? await firstURL(from: wikipediaSearchURL(for: trimmedQuery), parse: wikipediaThumbnailURLs),
           let image = await download(url) {
            return TeacherPicture(image: image, caption: caption, credit: "Wikipedia")
        }
        AppLog.append("teacher: no picture found for the search")
        return nil
    }

    static func commonsSearchURL(for query: String) -> URL? {
        guard var components = URLComponents(string: "https://commons.wikimedia.org/w/api.php") else { return nil }
        components.queryItems = [
            URLQueryItem(name: "action", value: "query"),
            URLQueryItem(name: "format", value: "json"),
            URLQueryItem(name: "generator", value: "search"),
            URLQueryItem(name: "gsrsearch", value: "\(query) filetype:bitmap|drawing"),
            URLQueryItem(name: "gsrnamespace", value: "6"),
            URLQueryItem(name: "gsrlimit", value: "6"),
            URLQueryItem(name: "prop", value: "imageinfo"),
            URLQueryItem(name: "iiprop", value: "url|mime"),
            URLQueryItem(name: "iiurlwidth", value: String(thumbnailWidth)),
        ]
        return components.url
    }

    static func wikipediaSearchURL(for query: String) -> URL? {
        guard var components = URLComponents(string: "https://en.wikipedia.org/w/api.php") else { return nil }
        components.queryItems = [
            URLQueryItem(name: "action", value: "query"),
            URLQueryItem(name: "format", value: "json"),
            URLQueryItem(name: "generator", value: "search"),
            URLQueryItem(name: "gsrsearch", value: query),
            URLQueryItem(name: "gsrlimit", value: "3"),
            URLQueryItem(name: "prop", value: "pageimages"),
            URLQueryItem(name: "piprop", value: "thumbnail"),
            URLQueryItem(name: "pithumbsize", value: String(thumbnailWidth)),
        ]
        return components.url
    }

    /// Commons results in search order (`index`), still images only, as their thumbnail URLs
    /// (an SVG's thumbnail is a PNG).
    static func commonsThumbnailURLs(from data: Data) -> [URL] {
        searchPages(from: data).compactMap { page in
            guard let info = (page["imageinfo"] as? [[String: Any]])?.first,
                  let mime = info["mime"] as? String,
                  ["image/png", "image/jpeg", "image/svg+xml", "image/webp"].contains(mime),
                  let thumbnail = info["thumburl"] as? String else { return nil }
            return URL(string: thumbnail)
        }
    }

    static func wikipediaThumbnailURLs(from data: Data) -> [URL] {
        searchPages(from: data).compactMap { page in
            guard let thumbnail = (page["thumbnail"] as? [String: Any])?["source"] as? String else { return nil }
            return URL(string: thumbnail)
        }
    }

    /// `query.pages` is keyed by page id; the search order is each page's `index`.
    private static func searchPages(from data: Data) -> [[String: Any]] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let pages = (root["query"] as? [String: Any])?["pages"] as? [String: Any] else { return [] }
        return pages.values.compactMap { $0 as? [String: Any] }
            .sorted { ($0["index"] as? Int ?? .max) < ($1["index"] as? Int ?? .max) }
    }

    private static func firstURL(from searchURL: URL?, parse: (Data) -> [URL]) async throws -> URL? {
        guard let searchURL else { return nil }
        var request = URLRequest(url: searchURL, timeoutInterval: 6)
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return parse(data).first
    }

    private static func download(_ url: URL) async -> NSImage? {
        var request = URLRequest(url: url, timeoutInterval: 8)
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return NSImage(data: data)
    }
}
