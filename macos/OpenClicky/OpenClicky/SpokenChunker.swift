//
//  SpokenChunker.swift
//  OpenClicky
//
//  Cuts Claude's streamed answer into pieces the voice can start on before the answer is finished.
//  The first sentence goes out the moment it ends (once it is at least `firstChunkMinimum`
//  characters, so "Dr." or "1." alone is never a piece), so the voice starts within a second or two
//  of the first words; later sentences are grouped (at least `laterChunkMinimum` characters) so a
//  long answer costs a handful of speech requests, not one per sentence.
//
//  A sentence ends at ". " "! " "? ", at a new line, and at the danda (। ॥), the Urdu full stop and
//  Arabic question mark (۔ ؟) and the CJK stops (。！？) with or without a space after them: Hindi,
//  Marathi, Bengali, Urdu, Arabic, Japanese and Chinese answers were otherwise one piece, spoken only
//  after the whole answer was written. No piece is longer than
//  `maximumPieceLength` UTF-16 units: the backend's /tts speaks at most 400 (a JavaScript length) and
//  silently drops the rest, so a long sentence is cut at a space or comma instead.
//
//  The [POINT:…] tag is never spoken. The ask prompt puts it first, so the chunker holds the text
//  back until the tag is complete and records it in `pointTag`; a tag at the end (older prompts,
//  or a model that ignores the instruction) is found when the stream finishes. The guide's [NEXT]
//  marker (`GuidedStepMarker`) and the [UNSURE] marker (`UnsureMarker`) are never spoken either:
//  they hold no sentence end, so they only ever arrive whole inside a piece, and are taken out of
//  every piece. [UNSURE] opens its answer, so a tag after it still counts as leading (and is then
//  ignored by whoever points: `isUnsure`).
//

import Foundation

struct SpokenChunker {
    /// The [POINT:…] tag, once it has been seen in full (leading, or trailing at `finish()`).
    private(set) var pointTag: String?
    /// The answer carries the [UNSURE] marker: it is not sure what to point at, so it must not point.
    private(set) var isUnsure = false

    private let laterChunkMinimum: Int
    private let firstChunkMinimum: Int
    private let maximumPieceLength: Int
    /// How many characters of the accumulated answer have already been handed out (or skipped as the tag).
    private var consumedCharacterCount = 0
    private var hasEmittedFirstChunk = false
    private var leadingTagResolved = false
    private var latestAccumulatedText = ""

    init(laterChunkMinimum: Int = 120, firstChunkMinimum: Int = 12, maximumPieceLength: Int = 350) {
        self.laterChunkMinimum = laterChunkMinimum
        self.firstChunkMinimum = firstChunkMinimum
        self.maximumPieceLength = maximumPieceLength
    }

    /// Takes the whole answer so far (ClaudeAPI reports accumulated text) and returns the pieces
    /// that are now ready to speak, in order.
    mutating func feed(accumulated accumulatedText: String) -> [String] {
        latestAccumulatedText = accumulatedText
        if !isUnsure && UnsureMarker.containsMarker(accumulatedText) { isUnsure = true }
        guard resolveLeadingTag(in: accumulatedText) else { return [] }

        var readyChunks: [String] = []
        while let chunk = nextCompleteChunk(in: accumulatedText) {
            // An empty chunk is only whitespace that was passed over (or a lone marker).
            let spokenChunk = Self.removingMarkers(from: chunk)
            if !spokenChunk.isEmpty { readyChunks.append(spokenChunk) }
        }
        return readyChunks
    }

    /// The stream ended: whatever is left (minus a trailing tag) is the last piece, or the last few
    /// when it is longer than the voice speaks in one request.
    mutating func finish() -> [String] {
        let accumulatedText = latestAccumulatedText
        if !leadingTagResolved {
            // The answer ended while still looking like an unfinished tag: speak it as text.
            leadingTagResolved = true
        }
        // Without the marker first, so "… [POINT:10,20:x] [NEXT]" still finds the trailing tag.
        var remainingText = Self.removingMarkers(from: String(accumulatedText.dropFirst(consumedCharacterCount)))
        consumedCharacterCount = accumulatedText.count

        // A trailing tag is never spoken, even after a leading one ("[POINT:none] [POINT:10,20:x]");
        // the first tag seen is the one that points.
        if let trailingTagRange = remainingText.range(of: Self.trailingTagPattern, options: .regularExpression) {
            if pointTag == nil {
                pointTag = String(remainingText[trailingTagRange]).trimmingCharacters(in: .whitespacesAndNewlines)
            }
            remainingText.removeSubrange(trailingTagRange)
        }
        var lastChunks: [String] = []
        var unspokenText = Substring(remainingText)
        while Self.spokenLength(of: unspokenText) > maximumPieceLength {
            let cut = lastSentenceBoundaryWithinMaximum(in: unspokenText) ?? hardSplitIndex(in: unspokenText)
            lastChunks.append(unspokenText[..<cut].trimmingCharacters(in: .whitespacesAndNewlines))
            unspokenText = unspokenText[cut...]
        }
        lastChunks.append(unspokenText.trimmingCharacters(in: .whitespacesAndNewlines))
        return lastChunks.map { Self.removingMarkers(from: $0) }.filter { !$0.isEmpty }
    }

    // MARK: - Private

    /// The text without the guide's [NEXT] and without [UNSURE], trimmed.
    private static func removingMarkers(from text: String) -> String {
        UnsureMarker.removingMarkers(from: GuidedStepMarker.removingMarkers(from: text))
    }

    private static let completeLeadingTagPattern = #"^\s*\[POINT:[^\]]*\]"#
    private static let trailingTagPattern = #"\[POINT:[^\]]*\]\s*$"#

    /// True once speech may start: either the answer does not open with a tag, or the tag is complete.
    /// An opening [UNSURE] is looked past, so a tag after it is still taken out and never spoken.
    private mutating func resolveLeadingTag(in accumulatedText: String) -> Bool {
        if leadingTagResolved { return true }
        let unsureMarkerLength = UnsureMarker.leadingMarkerLength(in: accumulatedText) ?? 0
        let textAfterMarker = String(accumulatedText.dropFirst(unsureMarkerLength))
        let trimmedStart = textAfterMarker.drop { $0.isWhitespace }
        if trimmedStart.isEmpty { return false }
        // "[" or "[UNS…" could still become the marker: wait for more text before deciding.
        if unsureMarkerLength == 0 && UnsureMarker.couldStillBecomeMarker(trimmedStart) { return false }
        // "[" or "[POI…" could still become a tag: wait for more text before deciding.
        if "[POINT:".hasPrefix(String(trimmedStart.prefix(7))) || trimmedStart.hasPrefix("[POINT:") {
            guard let tagRange = textAfterMarker.range(of: Self.completeLeadingTagPattern, options: .regularExpression) else {
                return false
            }
            pointTag = String(textAfterMarker[tagRange]).trimmingCharacters(in: .whitespacesAndNewlines)
            consumedCharacterCount = unsureMarkerLength + textAfterMarker.distance(from: textAfterMarker.startIndex, to: tagRange.upperBound)
        }
        leadingTagResolved = true
        return true
    }

    /// The next run of whole sentences long enough to send, or nil when the text so far has none.
    /// Text already longer than the voice speaks is cut without waiting for a sentence end; an empty
    /// string means only whitespace was passed over.
    private mutating func nextCompleteChunk(in accumulatedText: String) -> String? {
        let unconsumedText = accumulatedText.dropFirst(consumedCharacterCount)
        let minimumLength = hasEmittedFirstChunk ? laterChunkMinimum : firstChunkMinimum

        var searchStart = unconsumedText.startIndex
        while let boundary = Self.sentenceBoundary(in: unconsumedText, from: searchStart) {
            let candidate = unconsumedText[unconsumedText.startIndex..<boundary]
            // Grouping further would make a piece longer than the voice speaks: cut below instead.
            if Self.spokenLength(of: candidate) > maximumPieceLength { break }
            if candidate.trimmingCharacters(in: .whitespacesAndNewlines).count >= minimumLength {
                return consume(unconsumedText, upTo: boundary)
            }
            searchStart = boundary
        }
        guard Self.spokenLength(of: unconsumedText) > maximumPieceLength else { return nil }
        let cut = lastSentenceBoundaryWithinMaximum(in: unconsumedText) ?? hardSplitIndex(in: unconsumedText)
        return consume(unconsumedText, upTo: cut)
    }

    private mutating func consume(_ unconsumedText: Substring, upTo end: Substring.Index) -> String {
        consumedCharacterCount += unconsumedText.distance(from: unconsumedText.startIndex, to: end)
        let chunk = unconsumedText[..<end].trimmingCharacters(in: .whitespacesAndNewlines)
        if !chunk.isEmpty { hasEmittedFirstChunk = true }
        return chunk
    }

    /// The last sentence end whose text before it still fits in one piece.
    private func lastSentenceBoundaryWithinMaximum(in text: Substring) -> Substring.Index? {
        var lastFittingBoundary: Substring.Index?
        var searchStart = text.startIndex
        while let boundary = Self.sentenceBoundary(in: text, from: searchStart),
              Self.spokenLength(of: text[text.startIndex..<boundary]) <= maximumPieceLength {
            if !text[text.startIndex..<boundary].allSatisfy(\.isWhitespace) { lastFittingBoundary = boundary }
            searchStart = boundary
        }
        return lastFittingBoundary
    }

    /// Where to cut a run with no sentence end that fits: after the last space or comma within the
    /// maximum (when that is at least halfway, so the piece is worth a request), else at the maximum
    /// itself (CJK without punctuation has no spaces to cut at).
    private func hardSplitIndex(in text: Substring) -> Substring.Index {
        var lengthSoFar = 0
        var index = text.startIndex
        var lastBreakAfterSpaceOrComma: Substring.Index?
        while index < text.endIndex {
            let character = text[index]
            let characterLength = String(character).utf16.count
            if lengthSoFar + characterLength > maximumPieceLength { break }
            lengthSoFar += characterLength
            index = text.index(after: index)
            if character.isWhitespace || Self.clauseBreaks.contains(character), lengthSoFar >= maximumPieceLength / 2 {
                lastBreakAfterSpaceOrComma = index
            }
        }
        // Always move forward, even if a single character were longer than the maximum.
        if index == text.startIndex { return text.index(after: index) }
        return lastBreakAfterSpaceOrComma ?? index
    }

    /// Length the way the backend measures it (JavaScript string length, UTF-16 code units).
    private static func spokenLength(of text: Substring) -> Int {
        text.utf16.count
    }

    /// Ends a sentence only with whitespace after it ("3.5" and "e.g" are not ends).
    private static let latinSentenceEnds: Set<Character> = [".", "!", "?"]
    /// End a sentence on their own: danda, double danda, Urdu full stop, Arabic question mark, ideographic full stop, full-width ! and ?.
    private static let standaloneSentenceEnds: Set<Character> = ["।", "॥", "۔", "؟", "。", "！", "？"]
    private static let clauseBreaks: Set<Character> = [",", ";", ":", "，", "、", "；", "："]
    /// Words that end with a "." without ending the sentence ("Dr. Smith").
    private static let abbreviationsBeforePeriod: Set<String> = ["dr", "mr", "mrs", "ms", "st", "prof", "jr", "sr", "vs", "e.g", "i.e", "etc"]

    /// The index just after a sentence end. A "." inside a number ("3.5"), at the very end of the
    /// text so far (more may follow), after an abbreviation ("Dr.") or after a list number at the
    /// start of a line ("1. Open the menu") is not a boundary.
    private static func sentenceBoundary(in text: Substring, from startIndex: Substring.Index) -> Substring.Index? {
        var index = startIndex
        while index < text.endIndex {
            let character = text[index]
            let nextIndex = text.index(after: index)
            if standaloneSentenceEnds.contains(character) || character == "\n" {
                return nextIndex
            }
            if latinSentenceEnds.contains(character), nextIndex < text.endIndex, text[nextIndex].isWhitespace {
                if character != "." || !isPeriodInsideASentence(at: index, in: text) {
                    return nextIndex
                }
            }
            index = nextIndex
        }
        return nil
    }

    /// True when the "." at `periodIndex` closes an abbreviation or a list number, not a sentence.
    private static func isPeriodInsideASentence(at periodIndex: Substring.Index, in text: Substring) -> Bool {
        var wordStart = periodIndex
        while wordStart > text.startIndex {
            let previousIndex = text.index(before: wordStart)
            let previousCharacter = text[previousIndex]
            guard previousCharacter.isLetter || previousCharacter.isNumber || previousCharacter == "." else { break }
            wordStart = previousIndex
        }
        let word = text[wordStart..<periodIndex].lowercased()
        if abbreviationsBeforePeriod.contains(word) { return true }
        // "1." is a list number only at the start of a line; "in 1990." ends a sentence.
        guard !word.isEmpty, word.allSatisfy(\.isNumber) else { return false }
        let textBeforeWordOnThisLine = text[text.startIndex..<wordStart].reversed().prefix { $0 != "\n" }
        return textBeforeWordOnThisLine.allSatisfy(\.isWhitespace)
    }
}
