//
//  DrawStageController.swift
//  OpenClicky
//
//  The drawing stage from the app's side: a model-written program is fed to the page as it
//  streams, the page's events (each spoken line, the end of the program, a clicked part, a photo
//  it wants) reach whoever is drawing an answer, and messages sent before the page says `ready`
//  wait for it. The window itself is a DrawStageSurface (DrawStageWindow in the app; a fake in
//  tests), so none of this needs WebKit. Windows twin: DrawStageController.cs.
//

import AppKit
import Foundation

/// What the window gives the controller: the page, and showing or hiding it on a screen.
@MainActor
protocol DrawStageSurface: AnyObject {
    /// App → page: one JSON message (DrawStageMessages).
    func post(_ json: String)
    /// Covers that screen and wakes the page.
    func show(on displayFrame: CGRect)
    /// Puts the page to sleep and takes the window off the screen; the drawing stays in the page.
    func hide()
    /// A picture of the drawing's box, for the bubble.
    func snapshot(of bounds: DrawStageBounds) async -> NSImage?
    var isShowing: Bool { get }
}

@MainActor
final class DrawStageController {
    static let shared = DrawStageController()

    /// The window; nil until the app has wired one (tests give a fake).
    var surface: DrawStageSurface?
    /// The page has loaded and said so; messages wait for this.
    private(set) var isReady = false
    /// The stage window is on screen (a hidden stage still holds its drawing).
    private(set) var isShowing = false
    /// Where the stage was last shown, so a hidden drawing comes back to the same screen.
    private var displayFrame: CGRect?

    // Events, set by the answer being drawn for its duration:
    /// A say() ran: line k and its words. The voice can fetch its audio now.
    var lineAdded: ((Int, String) -> Void)?
    /// Every statement has run, so every line is known.
    var programRan: ((Int) -> Void)?
    /// The program is done and everything is drawn.
    var finished: (() -> Void)?
    /// The page's warnings and errors, and a stopped program ("error: …").
    var logged: ((String) -> Void)?
    /// A part of a 3D figure was clicked, by its name.
    var figurePartClicked: ((String) -> Void)?
    /// Where the window should take the mouse, in page pixels.
    var figuresChanged: (([CGRect]) -> Void)?
    /// Finds a photo for a query and caption (the Teacher's Wikimedia search); nil finds none.
    var findPhoto: ((String, String) async -> DrawStagePhoto?)?

    private var waitingForReady: [String] = []
    private var measuring: CheckedContinuation<DrawStageBounds?, Never>?
    private var measureTimeout: Task<Void, Never>?

    // MARK: - App → page

    /// Loads the page ahead of a question (the talk key going down), so the first drawing doesn't wait for it.
    func prepare() {
        (surface as? DrawStagePreparing)?.load()
    }

    /// A new program on a canvas of that size, drawn on that screen. The voice paces it unless a
    /// smoke run asks for the page's own estimate.
    func begin(displayFrame: CGRect, canvasWidth: Int, canvasHeight: Int, pace: DrawStagePace = .speech) {
        self.displayFrame = displayFrame
        show()
        send(DrawStageMessages.beginStream(canvasWidth: canvasWidth, canvasHeight: canvasHeight, pace: pace))
    }

    /// A follow-up's program, run after the drawing on the stage, which stays.
    func continueDrawing() {
        show()
        send(DrawStageMessages.continueStream(pace: .speech))
    }

    func feed(_ programSoFar: String, done: Bool) {
        send(DrawStageMessages.feed(programSoFar: programSoFar, done: done))
    }

    func lineStarted(_ k: Int) { send(DrawStageMessages.line("lineStarted", k: k)) }
    func lineEnded(_ k: Int) { send(DrawStageMessages.line("lineEnded", k: k)) }

    /// Draw everything still waiting, quickly: the voice was cut short.
    func finishNow() { send(DrawStageMessages.simple("finishNow")) }

    /// Wipes the drawing and hides the stage.
    func clear(reason: String) {
        AppLog.append("draw: stage cleared (\(reason))")
        send(DrawStageMessages.simple("clear"))
        hide(reason: reason)
    }

    /// Takes the stage off the screen; the drawing stays in the page for `show()` to bring back.
    func hide(reason: String) {
        guard isShowing else { return }
        isShowing = false
        surface?.hide()
    }

    /// Shows the stage on the screen it was last begun on.
    func show() {
        guard let surface else { return }
        let frame = displayFrame ?? NSScreen.main?.frame ?? .zero
        surface.show(on: frame)
        isShowing = true
    }

    /// The drawing's box in page pixels; nil when the page does not answer within 2 s.
    func measure() async -> DrawStageBounds? {
        measuring?.resume(returning: nil)
        measuring = nil
        return await withCheckedContinuation { continuation in
            measuring = continuation
            send(DrawStageMessages.simple("measure"))
            measureTimeout?.cancel()
            measureTimeout = Task { [weak self] in
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                guard !Task.isCancelled, let self, let waiting = self.measuring else { return }
                self.measuring = nil
                waiting.resume(returning: nil)
            }
        }
    }

    /// A display was added, removed or resized: the stage's frame no longer fits, so it is cleared.
    func displaysChanged() {
        guard isShowing || displayFrame != nil else { return }
        clear(reason: "displays changed")
        displayFrame = nil
    }

    private func send(_ json: String) {
        guard surface != nil else { return }
        if isReady { surface?.post(json) } else { waitingForReady.append(json) }
    }

    // MARK: - Page → app

    /// The surface hands every page message here.
    func receive(_ json: String) {
        guard let event = DrawStageMessages.parseEvent(json) else { return }
        switch event.type {
        case "ready":
            isReady = true
            let queued = waitingForReady
            waitingForReady.removeAll()
            queued.forEach { surface?.post($0) }
        case "line":
            if let k = event.k { lineAdded?(k, event.text ?? "") }
        case "programRan":
            programRan?(event.k ?? 0)
        case "finished":
            finished?()
        case "figurePart":
            if let part = event.text, !part.isEmpty { figurePartClicked?(part) }
        case "figures":
            figuresChanged?(event.rects ?? [])
        case "bounds":
            measureTimeout?.cancel()
            let waiting = measuring
            measuring = nil
            waiting?.resume(returning: event.bounds)
        case "photoWanted":
            if let id = event.id { sendPhoto(id: id, query: event.query ?? "", caption: event.text ?? "") }
        case "error":
            logged?("error: \(event.text ?? "")")
        case "log":
            logged?(event.text ?? "")
        default:
            break
        }
    }

    /// The page asked for a photo: found in the background, then posted (or marked as not found).
    private func sendPhoto(id: String, query: String, caption: String) {
        Task { [weak self] in
            guard let self else { return }
            let trimmedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
            let photo = trimmedQuery.isEmpty ? nil : await findPhoto?(trimmedQuery, caption)
            send(DrawStageMessages.photo(id: id, photo: photo))
        }
    }
}

/// A surface that can load its page early (the real window); a fake has nothing to load.
@MainActor
protocol DrawStagePreparing: AnyObject {
    func load()
}

extension DrawStagePhoto {
    /// A photo the Teacher's Wikimedia search found, as PNG bytes for the page's data URL.
    init?(picture: TeacherPicture) {
        guard let tiff = picture.image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]) else { return nil }
        self.init(imageData: png, mediaType: "image/png", credit: picture.credit)
    }
}
