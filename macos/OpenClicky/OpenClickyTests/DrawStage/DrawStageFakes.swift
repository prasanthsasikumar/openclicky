//
//  DrawStageFakes.swift
//  OpenClickyTests
//
//  A stand-in for the stage window: records what the app posts to the page and whether it is
//  shown, and lets a test play the page's side (ready, line, finished…) through the controller.
//

import AppKit
import Foundation
@testable import OpenClicky

@MainActor
final class FakeDrawStageSurface: DrawStageSurface {
    private(set) var posted: [String] = []
    private(set) var shownOn: [CGRect] = []
    private(set) var hideCount = 0
    private(set) var isShowing = false
    var snapshotImage: NSImage?
    /// Sees every message as it is posted (a fake page answers through it).
    var onPost: ((String) -> Void)?

    func post(_ json: String) {
        posted.append(json)
        onPost?(json)
    }

    func show(on displayFrame: CGRect) {
        shownOn.append(displayFrame)
        isShowing = true
    }

    func hide() {
        hideCount += 1
        isShowing = false
    }

    func snapshot(of bounds: DrawStageBounds) async -> NSImage? { snapshotImage }

    /// The posted messages' types, in order ("beginStream", "feed", …).
    var postedTypes: [String] {
        posted.compactMap { json in
            (try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])?["type"] as? String
        }
    }

    /// The posted messages of one type, as parsed objects.
    func postedObjects(ofType type: String) -> [[String: Any]] {
        posted.compactMap { json in
            guard let object = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any],
                  object["type"] as? String == type else { return nil }
            return object
        }
    }
}

/// A stand-in for the page itself, behind a controller and a fake surface: it says `ready`, turns
/// every `say("…")` in a fed program into a `line` event as it arrives, reports `programRan` when the
/// feed is done, and `finished` once every line it reported has ended. Errors it is told to raise
/// come out as `error` events after the first line.
@MainActor
final class FakeDrawStagePage {
    let controller = DrawStageController()
    let surface = FakeDrawStageSurface()
    private(set) var linesReported: [String] = []
    private var linesEnded = Set<Int>()
    private var feedDone = false
    private var finishedSent = false
    /// An error the page raises once, after its first line (a statement that failed).
    var errorAfterFirstLine: String?

    init(ready: Bool = true) {
        controller.surface = surface
        surface.onPost = { [weak self] json in self?.pageReceived(json) }
        if ready { controller.receive(#"{"type":"ready"}"#) }
    }

    private func pageReceived(_ json: String) {
        guard let object = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any],
              let type = object["type"] as? String else { return }
        switch type {
        case "beginStream", "continueStream":
            if type == "beginStream" { linesReported = []; linesEnded = [] }
            feedDone = false
            finishedSent = false
        case "feed":
            let code = object["code"] as? String ?? ""
            for words in Self.sayLines(in: code).dropFirst(linesReported.count) {
                let k = linesReported.count
                linesReported.append(words)
                controller.receive(#"{"type":"line","k":\#(k),"text":"\#(words)"}"#)
                if k == 0, let error = errorAfterFirstLine { controller.receive(#"{"type":"error","text":"\#(error)"}"#) }
            }
            if object["done"] as? Bool == true {
                feedDone = true
                controller.receive(#"{"type":"programRan","lines":\#(linesReported.count)}"#)
                finishIfDone()
            }
        case "lineEnded":
            if let k = object["k"] as? Int { linesEnded.insert(k) }
            finishIfDone()
        case "finishNow":
            feedDone = true
            finishIfDone(force: true)
        default:
            break
        }
    }

    private func finishIfDone(force: Bool = false) {
        guard !finishedSent, feedDone, force || linesEnded.count >= linesReported.count else { return }
        finishedSent = true
        controller.receive(#"{"type":"finished"}"#)
    }

    /// The words of every complete say("…") in a program, in order.
    static func sayLines(in program: String) -> [String] {
        guard let regex = try? NSRegularExpression(pattern: #"say\("([^"]*)"\)"#) else { return [] }
        return regex.matches(in: program, range: NSRange(program.startIndex..., in: program)).compactMap { match in
            Range(match.range(at: 1), in: program).map { String(program[$0]) }
        }
    }
}

/// A speech queue that records what it plays, each piece taking a moment.
@MainActor
enum FakeSpeech {
    static func make(played: @escaping @MainActor (String) -> Void, failOn: String? = nil) -> SpeechQueue {
        SpeechQueue(
            fetchAudio: { text in Data(text.utf8) },
            playAudio: { data in
                let text = String(decoding: data, as: UTF8.self)
                if text == failOn { throw FakeSpeechFailure() }
                played(text)
                try await Task.sleep(nanoseconds: 10_000_000)
            }
        )
    }

    struct FakeSpeechFailure: Error {}
}
