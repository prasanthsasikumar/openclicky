//
//  WebSearchURL.swift
//  OpenClicky
//
//  `web_search`: "search for rotary engines", "search YouTube for lo-fi". A closed set of sites,
//  each with its own search page, so the model never hands over a URL of its own making.
//

import Foundation

nonisolated enum WebSearchSite: String, CaseIterable {
    case web
    case youtube
    case wikipedia
    case maps
    case amazon

    /// The site's search page for `query`. Built from `percentEncodedQuery` rather than query
    /// items: `URLComponents` leaves `+` bare, and every search page reads a bare `+` as a space.
    static func url(for site: WebSearchSite, query: String) -> URL {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        let encodedQuery = query.addingPercentEncoding(withAllowedCharacters: allowed) ?? ""
        let text: String
        switch site {
        case .web: text = "https://www.google.com/search?q=\(encodedQuery)"
        case .youtube: text = "https://www.youtube.com/results?search_query=\(encodedQuery)"
        case .wikipedia: text = "https://en.wikipedia.org/w/index.php?search=\(encodedQuery)"
        case .maps: text = "https://www.google.com/maps/search/\(encodedQuery)"
        case .amazon: text = "https://www.amazon.com/s?k=\(encodedQuery)"
        }
        // The ASCII specials are encoded above; letters outside ASCII are encoded by URL(string:)
        // itself, so the string is always a URL.
        return URL(string: text)!
    }
}
