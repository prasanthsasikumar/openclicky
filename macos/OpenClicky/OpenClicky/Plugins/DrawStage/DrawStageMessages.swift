//
//  DrawStageMessages.swift
//  OpenClicky
//
//  The messages between the app and the drawing stage's page (draw-stage/stage.js, shared with the
//  Windows app), as JSON objects with a "type". The app sends beginStream, continueStream, feed,
//  lineStarted, lineEnded, finishNow, clear, measure, sleep, wake and photo; the page sends ready,
//  line, programRan, lineDue, finished, figures, figurePart, bounds, photoWanted, log and error.
//  The protocol is documented in draw-stage/README.md; the Windows twin is DrawStageMessages.cs.
//

import CoreGraphics
import Foundation

/// How the page times each beat's strokes: by its own estimate of the line's length (about 2.6 words
/// a second), or by the voice (the app sends lineStarted / lineEnded as each line is heard).
enum DrawStagePace: String {
    case estimate
    case speech
}

/// What the page has drawn, as a box in page pixels on a page of `pageWidth` × `pageHeight` pixels;
/// empty when nothing is drawn.
struct DrawStageBounds: Equatable {
    let x: Double
    let y: Double
    let width: Double
    let height: Double
    let pageWidth: Double
    let pageHeight: Double

    var isEmpty: Bool { width < 1 || height < 1 || pageWidth < 1 || pageHeight < 1 }
}

/// A photo found for the page: the image bytes, their media type, and who to credit.
struct DrawStagePhoto {
    let imageData: Data
    let mediaType: String
    let credit: String
}

/// One message from the page. `k` carries "k" (line messages) or "lines" (programRan); `id` and
/// `query` come with a photo it wants (photoWanted); `bounds` with "bounds"; `rects` with "figures".
struct DrawStageEvent: Equatable {
    let type: String
    let k: Int?
    let text: String?
    let id: String?
    let query: String?
    let bounds: DrawStageBounds?
    let rects: [CGRect]?
}

enum DrawStageMessages {
    /// A new program on a canvas of that size; the old drawing is wiped.
    static func beginStream(canvasWidth: Int, canvasHeight: Int, pace: DrawStagePace) -> String {
        json(["type": "beginStream", "w": max(1, canvasWidth), "h": max(1, canvasHeight), "pace": pace.rawValue])
    }

    /// A follow-up's program, run after the one on the stage: the drawing and the program's names stay.
    static func continueStream(pace: DrawStagePace) -> String {
        json(["type": "continueStream", "pace": pace.rawValue])
    }

    static func feed(programSoFar: String, done: Bool) -> String {
        json(["type": "feed", "code": programSoFar, "done": done])
    }

    /// "lineStarted" or "lineEnded" for line `k`.
    static func line(_ type: String, k: Int) -> String {
        json(["type": type, "k": k])
    }

    /// A message with nothing but its type: finishNow, clear, measure, sleep, wake.
    static func simple(_ type: String) -> String {
        json(["type": type])
    }

    /// The photo the page asked for (photoWanted) as a data URL with its credit; without one, that none was found.
    static func photo(id: String, photo: DrawStagePhoto?) -> String {
        guard let photo else { return json(["type": "photo", "id": id, "failed": true]) }
        return json([
            "type": "photo", "id": id, "credit": photo.credit,
            "data": "data:\(photo.mediaType);base64,\(photo.imageData.base64EncodedString())",
        ])
    }

    /// "image/png", "image/gif", "image/webp", else "image/jpeg", from the file's first bytes.
    static func mediaType(of data: Data) -> String {
        let bytes = [UInt8](data.prefix(12))
        if bytes.count > 8, bytes[0] == 0x89, bytes[1] == 0x50 { return "image/png" }
        if bytes.count > 4, bytes[0] == 0x47, bytes[1] == 0x49 { return "image/gif" }
        if bytes.count > 11, bytes[8] == 0x57, bytes[9] == 0x45, bytes[10] == 0x42, bytes[11] == 0x50 { return "image/webp" }
        return "image/jpeg"
    }

    /// A page message; nil when it isn't a JSON object with a string "type".
    static func parseEvent(_ json: String) -> DrawStageEvent? {
        guard let data = json.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["type"] as? String else { return nil }
        func string(_ key: String) -> String? { object[key] as? String }
        func number(_ key: String) -> Double {
            guard let value = object[key] as? NSNumber, value.doubleValue.isFinite else { return 0 }
            return value.doubleValue
        }
        // Line messages carry "k"; programRan carries its count of lines as "lines".
        let k = (object["k"] ?? object["lines"]) as? NSNumber
        let bounds = type == "bounds"
            ? DrawStageBounds(x: number("x"), y: number("y"), width: number("w"), height: number("h"), pageWidth: number("pageWidth"), pageHeight: number("pageHeight"))
            : nil
        var rects: [CGRect]?
        if let rectObjects = object["rects"] as? [[String: Any]] {
            rects = rectObjects.map { rect in
                func side(_ key: String) -> Double { (rect[key] as? NSNumber)?.doubleValue ?? 0 }
                return CGRect(x: side("x"), y: side("y"), width: side("w"), height: side("h"))
            }
        }
        return DrawStageEvent(type: type, k: k?.intValue, text: string("text"), id: string("id"), query: string("query"), bounds: bounds, rects: rects)
    }

    private static func json(_ object: [String: Any]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: object) else { return "{}" }
        return String(decoding: data, as: UTF8.self)
    }
}
