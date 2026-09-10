//
//  ScreenTextLocator.swift
//  OpenClicky
//
//  `point_at` snapping. The Realtime model's pixel coordinates are consistently 30–100 px off (its
//  vision grounding is coarse), so the buddy would land next to the element instead of on it. The
//  tool therefore also asks for the element's visible text; the screenshot the model saw is OCR'd
//  (Vision, upscaled 2× because UI captions are ~11 px tall at 1280 px), and the guess is snapped
//  to the matching text nearest to it. Icon-only elements keep the model's coordinates.
//

import CoreGraphics
import Foundation
import ImageIO
import Vision

/// One line of text found on a capture, in screenshot pixels (origin top-left).
nonisolated struct ScreenTextLine: @unchecked Sendable {
    let text: String
    let box: CGRect
    /// Box of a character range within `text`, when the recognizer can supply one. Without it the
    /// range is placed proportionally along the line.
    var rangeBox: ((Range<String.Index>) -> CGRect?)? = nil

    init(text: String, box: CGRect, rangeBox: ((Range<String.Index>) -> CGRect?)? = nil) {
        self.text = text
        self.box = box
        self.rangeBox = rangeBox
    }

    func box(for range: Range<String.Index>) -> CGRect {
        if let precise = rangeBox?(range) { return precise }
        let count = CGFloat(max(text.count, 1))
        let start = CGFloat(text.distance(from: text.startIndex, to: range.lowerBound)) / count
        let end = CGFloat(text.distance(from: text.startIndex, to: range.upperBound)) / count
        return CGRect(x: box.minX + box.width * start, y: box.minY, width: box.width * (end - start), height: box.height)
    }
}

nonisolated struct ScreenTextMatch: Equatable {
    /// The recognized line the match was found in (as read, so "HNew" or "Now" are possible).
    let text: String
    let center: CGPoint
    /// Distance from the model's guess, in screenshot pixels.
    let distance: CGFloat
}

nonisolated enum ScreenTextLocator {
    /// Words that describe a control rather than name it; never matched on their own.
    static let genericWords: Set<String> = [
        "button", "btn", "menu", "icon", "link", "tab", "field", "box", "option", "item", "toggle", "checkbox",
        "dropdown", "bar", "panel", "window", "dialog", "input", "label", "text", "list", "row", "section", "control",
        "the", "and", "for", "with", "top", "left", "right", "bottom", "here", "this", "that",
    ]

    /// The on-screen occurrence of `hint` nearest to `guess`: the whole hint first, then its
    /// distinctive words. Exact (case-insensitive) matches only: the hint is the model's idea of
    /// the caption, and a near miss is as likely a different element ("reditt" in the menu bar for
    /// a Reddit browser tab) as an OCR typo, so near misses are left to the Claude fallback.
    /// Nil when the hint is empty/generic or nothing matching is within `maxDistance` px.
    static func locate(_ hint: String, near guess: CGPoint, in lines: [ScreenTextLine], maxDistance: CGFloat, ambiguityMargin: CGFloat) -> ScreenTextMatch? {
        let needle = normalize(hint)
        guard needle.count >= 3, !genericWords.contains(needle) else { return nil }

        if let match = nearest(exactMatches(of: needle, in: lines), to: guess, within: maxDistance, ambiguityMargin: ambiguityMargin) { return match }

        let words = needle.split(separator: " ").map(String.init)
            .filter { $0.count >= 3 && !genericWords.contains($0) }
            .sorted { $0.count > $1.count }
        for word in words where word != needle {
            if let match = nearest(exactMatches(of: word, in: lines), to: guess, within: maxDistance, ambiguityMargin: ambiguityMargin) { return match }
        }
        return nil
    }

    // MARK: - Matching

    private static func normalize(_ text: String) -> String {
        text.lowercased()
            .components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }
            .map { $0.trimmingCharacters(in: .punctuationCharacters.union(.symbols)) }
            .filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    private static func exactMatches(of needle: String, in lines: [ScreenTextLine]) -> [ScreenTextMatch] {
        var matches: [ScreenTextMatch] = []
        for line in lines {
            var searchRange = line.text.startIndex..<line.text.endIndex
            while let found = line.text.range(of: needle, options: [.caseInsensitive, .diacriticInsensitive], range: searchRange) {
                let box = line.box(for: found)
                matches.append(ScreenTextMatch(text: line.text, center: CGPoint(x: box.midX, y: box.midY), distance: 0))
                guard found.upperBound < line.text.endIndex else { break }
                searchRange = found.upperBound..<line.text.endIndex
            }
        }
        return matches
    }

    /// The nearest match, unless two of them are close enough together that the model's own error
    /// decides between them. The guess is 30–100 px off, so when the runner-up is within
    /// `ambiguityMargin` px of the winner ("Edit" repeated down a list of rows), distance is noise
    /// rather than evidence: the caller is told nothing was resolved and falls through to the
    /// grounding pass, which is given the user's actual request and can pick the right one.
    private static func nearest(
        _ matches: [ScreenTextMatch],
        to guess: CGPoint,
        within maxDistance: CGFloat,
        ambiguityMargin: CGFloat
    ) -> ScreenTextMatch? {
        let matchesWithinRange = matches
            .map { ScreenTextMatch(text: $0.text, center: $0.center, distance: hypot($0.center.x - guess.x, $0.center.y - guess.y)) }
            .filter { $0.distance <= maxDistance }
            .sorted { $0.distance < $1.distance }

        guard let nearestMatch = matchesWithinRange.first else { return nil }
        if let runnerUpMatch = matchesWithinRange.dropFirst().first,
           runnerUpMatch.distance - nearestMatch.distance < ambiguityMargin {
            return nil
        }
        return nearestMatch
    }

}

/// Vision OCR of a capture, off the main actor. ~400 ms for a 1280 px capture upscaled 2×.
nonisolated enum ScreenTextRecognizer {
    static func recognize(jpeg: Data, upscale: CGFloat = 2) async throws -> [ScreenTextLine] {
        try await Task.detached(priority: .userInitiated) {
            try recognizeSync(jpeg: jpeg, upscale: upscale)
        }.value
    }

    // The default actor isolation is the main actor; this must run on the detached task's thread.
    private static func recognizeSync(jpeg: Data, upscale: CGFloat) throws -> [ScreenTextLine] {
        guard let source = CGImageSourceCreateWithData(jpeg as CFData, nil),
              let decoded = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            throw ScreenTextRecognizerError.undecodableImage
        }
        let originalWidth = CGFloat(decoded.width), originalHeight = CGFloat(decoded.height)
        var image = decoded
        if upscale != 1 {
            let width = Int(originalWidth * upscale), height = Int(originalHeight * upscale)
            guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                          space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else {
                throw ScreenTextRecognizerError.undecodableImage
            }
            context.interpolationQuality = .high
            context.draw(decoded, in: CGRect(x: 0, y: 0, width: width, height: height))
            guard let scaled = context.makeImage() else { throw ScreenTextRecognizerError.undecodableImage }
            image = scaled
        }

        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        // UI captions are not prose: correction turns "Repos" into "Ropes".
        request.usesLanguageCorrection = false
        try VNImageRequestHandler(cgImage: image).perform([request])

        // Vision boxes are normalized with a bottom-left origin; convert to the capture's own pixels.
        let toPixels = { (normalized: CGRect) -> CGRect in
            CGRect(x: normalized.minX * originalWidth, y: (1 - normalized.maxY) * originalHeight,
                   width: normalized.width * originalWidth, height: normalized.height * originalHeight)
        }
        return (request.results ?? []).compactMap { observation in
            guard let candidate = observation.topCandidates(1).first, !candidate.string.isEmpty else { return nil }
            return ScreenTextLine(text: candidate.string, box: toPixels(observation.boundingBox)) { range in
                (try? candidate.boundingBox(for: range)).map { toPixels($0.boundingBox) }
            }
        }
    }
}

enum ScreenTextRecognizerError: Error {
    case undecodableImage
}
