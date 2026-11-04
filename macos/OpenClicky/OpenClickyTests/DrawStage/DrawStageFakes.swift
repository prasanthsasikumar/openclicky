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

    func post(_ json: String) { posted.append(json) }

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
