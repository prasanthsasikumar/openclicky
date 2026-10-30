//
//  ClipboardHistory.swift
//  OpenClicky
//
//  The Clipboard plugin's list, kept in memory only: what was copied, newest first, at most 30,
//  each copy once. Pure; reading and writing the pasteboard lives in `ClipboardPasteboard`.
//

import AppKit
import Foundation

/// What one copy held, reduced to what can be shown in a row and put back on the pasteboard.
enum ClipboardContent: Equatable {
    case text(String)
    case link(URL)
    case files([URL])
    /// The image's bytes in the type it was copied as (PNG or TIFF), and its size in pixels.
    case image(Data, type: NSPasteboard.PasteboardType, pixelSize: CGSize)

    var symbolName: String {
        switch self {
        case .text: return "text.alignleft"
        case .link: return "link"
        case .files(let urls): return urls.count > 1 ? "doc.on.doc" : "doc"
        case .image: return "photo"
        }
    }

    /// One line for the row: the text's first line, the link without its scheme, the file's name,
    /// or "Image 1200 × 800".
    var label: String {
        switch self {
        case .text(let text):
            let firstLine = text.split(whereSeparator: \.isNewline).first.map(String.init) ?? text
            let collapsed = firstLine.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            return collapsed.isEmpty ? "Text" : String(collapsed.prefix(200))
        case .link(let url):
            let withoutScheme = url.absoluteString.replacingOccurrences(of: "^[a-zA-Z][a-zA-Z0-9+.-]*://", with: "", options: .regularExpression)
            return String(withoutScheme.prefix(200))
        case .files(let urls):
            guard let first = urls.first else { return "Files" }
            return urls.count > 1 ? "\(first.lastPathComponent) and \(urls.count - 1) more" : first.lastPathComponent
        case .image(_, _, let pixelSize):
            guard pixelSize.width > 0, pixelSize.height > 0 else { return "Image" }
            return "Image \(Int(pixelSize.width)) × \(Int(pixelSize.height))"
        }
    }
}

struct ClipboardEntry: Identifiable, Equatable {
    let id: UUID
    let content: ClipboardContent
    let copiedAt: Date
}

struct ClipboardHistory: Equatable {
    static let capacity = 30
    /// Password managers mark the copies they make with these types; such a copy is never kept.
    /// The same list the dictation history's clipboard monitor uses.
    static let transientTypes: [NSPasteboard.PasteboardType] = [
        NSPasteboard.PasteboardType("org.nspasteboard.TransientType"),
        NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"),
        NSPasteboard.PasteboardType("com.agilebits.onepassword"),
    ]

    private(set) var entries: [ClipboardEntry] = []

    static func isTransient(_ types: [NSPasteboard.PasteboardType]) -> Bool {
        types.contains { transientTypes.contains($0) }
    }

    /// Keeps a copy at the top. The same content copied again moves up, keeping its id (so a row
    /// copied back from the list stays the same row), rather than repeating; past the capacity the
    /// oldest goes. Returns false when the copy was not kept (transient).
    @discardableResult
    mutating func add(_ content: ClipboardContent, pasteboardTypes: [NSPasteboard.PasteboardType] = [], at date: Date = Date(), id: UUID = UUID()) -> Bool {
        guard !Self.isTransient(pasteboardTypes) else { return false }
        let keptID = entries.first { $0.content == content }?.id ?? id
        entries.removeAll { $0.content == content }
        entries.insert(ClipboardEntry(id: keptID, content: content, copiedAt: date), at: 0)
        if entries.count > Self.capacity { entries.removeLast(entries.count - Self.capacity) }
        return true
    }

    mutating func removeAll() {
        entries.removeAll()
    }
}
