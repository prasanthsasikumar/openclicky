//
//  DrawStageControllerTests.swift
//  OpenClickyTests
//
//  The controller between the app and the page: messages wait for the page's `ready`, the page's
//  events reach the handlers an answer sets, a photo the page wants is found and sent back.
//

import AppKit
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct DrawStageControllerTests {
    private let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)

    private func makeController() -> (DrawStageController, FakeDrawStageSurface) {
        let fake = FakeDrawStageSurface()
        let controller = DrawStageController()
        controller.surface = fake
        return (controller, fake)
    }

    @Test func messagesWaitForReady() {
        let (controller, fake) = makeController()
        controller.begin(displayFrame: screen, canvasWidth: 1280, canvasHeight: 800)
        controller.feed("step(1)", done: false)
        #expect(fake.posted.isEmpty)
        #expect(fake.isShowing)
        controller.receive(#"{"type":"ready"}"#)
        #expect(fake.postedTypes == ["beginStream", "feed"])
        #expect(controller.isReady)
        controller.lineStarted(0)
        #expect(fake.postedTypes.last == "lineStarted")
    }

    @Test func pageEventsReachTheirHandlers() {
        let (controller, _) = makeController()
        var lines: [(Int, String)] = []
        var ranLines: Int?
        var finishedCount = 0
        var logged: [String] = []
        var parts: [String] = []
        var figures: [[CGRect]] = []
        controller.lineAdded = { lines.append(($0, $1)) }
        controller.programRan = { ranLines = $0 }
        controller.finished = { finishedCount += 1 }
        controller.logged = { logged.append($0) }
        controller.figurePartClicked = { parts.append($0) }
        controller.figuresChanged = { figures.append($0) }

        controller.receive(#"{"type":"line","k":2,"text":"hi"}"#)
        controller.receive(#"{"type":"programRan","lines":5}"#)
        controller.receive(#"{"type":"finished"}"#)
        controller.receive(#"{"type":"error","text":"x is not defined"}"#)
        controller.receive(#"{"type":"log","text":"a warning"}"#)
        controller.receive(#"{"type":"figurePart","text":"lift fan"}"#)
        controller.receive(#"{"type":"figures","rects":[{"x":1,"y":2,"w":3,"h":4}]}"#)
        controller.receive("not json")

        #expect(lines.count == 1 && lines[0].0 == 2 && lines[0].1 == "hi")
        #expect(ranLines == 5)
        #expect(finishedCount == 1)
        #expect(logged == ["error: x is not defined", "a warning"])
        #expect(parts == ["lift fan"])
        #expect(figures == [[CGRect(x: 1, y: 2, width: 3, height: 4)]])
    }

    @Test func aPhotoRequestIsAnsweredWithTheFinderResult() async throws {
        let (controller, fake) = makeController()
        controller.receive(#"{"type":"ready"}"#)
        let pngBytes = Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00])
        controller.findPhoto = { query, caption in
            query == "heart" ? DrawStagePhoto(imageData: pngBytes, mediaType: "image/png", credit: "Wikimedia Commons · \(caption)") : nil
        }
        controller.receive(#"{"type":"photoWanted","id":"p1","query":"heart","text":"a heart"}"#)
        controller.receive(#"{"type":"photoWanted","id":"p2","query":"nothing","text":""}"#)
        try await waitUntil { fake.postedObjects(ofType: "photo").count == 2 }
        let photos = fake.postedObjects(ofType: "photo")
        let found = try #require(photos.first { $0["id"] as? String == "p1" })
        #expect((found["data"] as? String)?.hasPrefix("data:image/png;base64,") == true)
        #expect(found["credit"] as? String == "Wikimedia Commons · a heart")
        let missing = try #require(photos.first { $0["id"] as? String == "p2" })
        #expect(missing["failed"] as? Bool == true)
    }

    @Test func measureAnswersWithBounds() async {
        let (controller, fake) = makeController()
        controller.receive(#"{"type":"ready"}"#)
        async let measured = controller.measure()
        try? await Task.sleep(nanoseconds: 20_000_000)
        #expect(fake.postedTypes == ["measure"])
        controller.receive(#"{"type":"bounds","x":10,"y":20,"w":300,"h":200,"pageWidth":1440,"pageHeight":900}"#)
        let bounds = await measured
        #expect(bounds == DrawStageBounds(x: 10, y: 20, width: 300, height: 200, pageWidth: 1440, pageHeight: 900))
    }

    @Test func clearWipesAndHidesTheStage() {
        let (controller, fake) = makeController()
        controller.receive(#"{"type":"ready"}"#)
        controller.begin(displayFrame: screen, canvasWidth: 1280, canvasHeight: 800)
        controller.clear(reason: "esc")
        #expect(fake.postedTypes == ["beginStream", "clear"])
        #expect(fake.hideCount == 1)
        #expect(!controller.isShowing)
        #expect(controller.isReady)
    }

    @Test func hideKeepsTheDrawingAndShowBringsItBack() {
        let (controller, fake) = makeController()
        controller.receive(#"{"type":"ready"}"#)
        controller.begin(displayFrame: screen, canvasWidth: 1280, canvasHeight: 800)
        controller.hide(reason: "bubble")
        #expect(fake.postedTypes == ["beginStream"])
        #expect(fake.hideCount == 1)
        controller.show()
        #expect(fake.shownOn.count == 2)
        #expect(controller.isShowing)
    }

    @Test func aDisplayChangeHidesTheStage() {
        let (controller, fake) = makeController()
        controller.receive(#"{"type":"ready"}"#)
        controller.begin(displayFrame: screen, canvasWidth: 1280, canvasHeight: 800)
        controller.displaysChanged()
        #expect(fake.hideCount == 1)
        #expect(fake.postedTypes.last == "clear")
    }

    private func waitUntil(timeoutSeconds: Double = 2, _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeoutSeconds)
        while !condition() {
            if Date() > deadline { Issue.record("timed out waiting"); return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
    }
}
