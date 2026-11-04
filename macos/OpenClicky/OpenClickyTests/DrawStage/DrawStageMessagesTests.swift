//
//  DrawStageMessagesTests.swift
//  OpenClickyTests
//
//  The JSON between the app and the drawing stage's page, both ways: what the app builds must be
//  what stage.js reads, and what the page sends must come out as a DrawStageEvent (or nothing).
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@Suite struct DrawStageMessagesTests {
    private func object(_ json: String) throws -> [String: Any] {
        try #require(try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
    }

    @Test func beginStreamCarriesTheCanvasAndPace() throws {
        let message = try object(DrawStageMessages.beginStream(canvasWidth: 1280, canvasHeight: 720, pace: .speech))
        #expect(message["type"] as? String == "beginStream")
        #expect(message["w"] as? Int == 1280)
        #expect(message["h"] as? Int == 720)
        #expect(message["pace"] as? String == "speech")
        #expect(message["keepPlace"] == nil)
    }

    @Test func feedAndLineAndSimpleRoundTrip() throws {
        let feed = try object(DrawStageMessages.feed(programSoFar: "step(1)", done: true))
        #expect(feed["type"] as? String == "feed")
        #expect(feed["code"] as? String == "step(1)")
        #expect(feed["done"] as? Bool == true)

        let line = try object(DrawStageMessages.line("lineStarted", k: 3))
        #expect(line["type"] as? String == "lineStarted")
        #expect(line["k"] as? Int == 3)

        let sleep = try object(DrawStageMessages.simple("sleep"))
        #expect(sleep.count == 1)
        #expect(sleep["type"] as? String == "sleep")

        let continued = try object(DrawStageMessages.continueStream(pace: .estimate))
        #expect(continued["type"] as? String == "continueStream")
        #expect(continued["pace"] as? String == "estimate")
    }

    @Test func photoWithoutAnImageSaysFailed() throws {
        let failed = try object(DrawStageMessages.photo(id: "p1", photo: nil))
        #expect(failed["type"] as? String == "photo")
        #expect(failed["id"] as? String == "p1")
        #expect(failed["failed"] as? Bool == true)

        let pngBytes = Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00])
        let found = try object(DrawStageMessages.photo(id: "p2", photo: DrawStagePhoto(imageData: pngBytes, mediaType: "image/png", credit: "Wikimedia Commons")))
        #expect((found["data"] as? String)?.hasPrefix("data:image/png;base64,") == true)
        #expect(found["credit"] as? String == "Wikimedia Commons")
        #expect(DrawStageMessages.mediaType(of: pngBytes) == "image/png")
        #expect(DrawStageMessages.mediaType(of: Data([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0])) == "image/jpeg")
    }

    @Test func parsesEveryPageEventShape() {
        #expect(DrawStageMessages.parseEvent(#"{"type":"line","k":2,"text":"hi"}"#)
                == DrawStageEvent(type: "line", k: 2, text: "hi", id: nil, query: nil, bounds: nil, rects: nil))
        #expect(DrawStageMessages.parseEvent(#"{"type":"programRan","lines":5}"#)?.k == 5)
        #expect(DrawStageMessages.parseEvent(#"{"type":"bounds","x":10,"y":20,"w":300,"h":200,"pageWidth":1440,"pageHeight":900}"#)?.bounds
                == DrawStageBounds(x: 10, y: 20, width: 300, height: 200, pageWidth: 1440, pageHeight: 900))
        #expect(DrawStageMessages.parseEvent(#"{"type":"figures","rects":[{"x":1,"y":2,"w":3,"h":4}]}"#)?.rects == [CGRect(x: 1, y: 2, width: 3, height: 4)])
        #expect(DrawStageMessages.parseEvent(#"{"type":"photoWanted","id":"p1","query":"heart","text":"a heart"}"#)?.query == "heart")
        #expect(DrawStageMessages.parseEvent(#"{"type":"finished"}"#)?.type == "finished")
    }

    @Test func unreadablePageMessagesAreIgnored() {
        for json in ["", "[]", "{}", #"{"type":3}"#, "not json", #"{"k":1}"#] {
            #expect(DrawStageMessages.parseEvent(json) == nil, "\(json)")
        }
    }

    @Test func anEmptyBoundsIsEmpty() {
        #expect(DrawStageBounds(x: 0, y: 0, width: 0, height: 0, pageWidth: 1440, pageHeight: 900).isEmpty)
        #expect(!DrawStageBounds(x: 5, y: 5, width: 10, height: 10, pageWidth: 1440, pageHeight: 900).isEmpty)
    }
}
