//
//  TeacherBoard.swift
//  OpenClicky
//
//  The Teacher plugin: "teach me how Pythagoras works" draws the lesson on the screen, in the
//  largest empty space, while Clicky explains it; what a drawing cannot show comes up as a real
//  picture. Both voice lanes get two tools while the plugin is on (`draw_on_screen`,
//  `show_picture`); `TeacherPlugin` is the only thing the rest of the app calls. The board is a
//  click-through panel under the buddy's overlay, with a small close button beside it; it goes
//  away on esc, the close button, the island's "clear", or the next question.
//

import AppKit
import Combine
import SwiftUI

/// What the rest of the app knows of the Teacher: whether it is on, its tools and prompt, and how
/// to run a tool call or clear the board.
enum TeacherPlugin {
    static let id = "teacher"
    static let drawToolName = "draw_on_screen"
    static let pictureToolName = "show_picture"
    static let toolNames: Set<String> = [drawToolName, pictureToolName]

    /// Lessons happen only with the plugin on in Settings › Island AND the island row's own
    /// switch on. The row's switch pauses lessons without taking the row away, so it is there
    /// to switch back on; only Settings removes the row.
    static var isEnabled: Bool { isEnabled(defaults: .standard) }

    static func isEnabled(defaults: UserDefaults) -> Bool {
        IslandComponentSettings(defaults: defaults).enabledPlugins.contains(id) && lessonsAreOn(defaults: defaults)
    }

    static let lessonsKey = "openclicky.teacher.lessonsOn"

    /// The island row's switch: on unless switched off.
    static func lessonsAreOn(defaults: UserDefaults = .standard) -> Bool {
        defaults.object(forKey: lessonsKey) as? Bool ?? true
    }

    static func setLessonsOn(_ on: Bool, defaults: UserDefaults = .standard) {
        defaults.set(on, forKey: lessonsKey)
    }

    static let promptInstruction = """
    teaching: when they ask you to teach or explain an idea that a picture makes clearer (a theorem, a shape, a graph, how light bends, a cycle, forces, a circuit), draw it on their screen with \(drawToolName) first, before you explain, then explain it part by part in the order you drew it. it appears in handwriting in an empty part of their screen, so label every part (a, b, c, angles, names) and use arrows with short notes to point things out. keep it simple, like a good teacher on a whiteboard: one clear diagram, no more than about twenty five commands. a lesson may be longer than your usual answers, up to about eighty words, walking through the drawing; don't use point_at or point tags for it, the pointer follows the labels on its own. when a drawing can't show it well (a real animal, an organ, a place, a famous painting, detailed apparatus), call \(pictureToolName) with a short search phrase instead. don't draw for ordinary questions or for how-to steps on screen; those use pointing. never list or read out what you would draw instead of calling the tool; the drawing may already be appearing, so explain the idea while it does.
    """

    static let sketchGrammar = """
    One command per line (or separated by ";"). Board units, origin top-left, y grows downward; default board 400 by 300.
    board W H — optional first line to change the board's size.
    line x1 y1 x2 y2 · arrow x1 y1 x2 y2 (head at the second point) · curve x1 y1 x2 y2 x3 y3 … (smooth freehand) · shape x1 y1 x2 y2 x3 y3 … (closed outline) · circle cx cy r · arc cx cy r from to (degrees, 0 right, 90 down) · box x y w h · right_angle vx vy ax ay bx by (square mark at vertex v between the arms to a and b) · angle vx vy ax ay bx by (arc marking that angle; use this, not arc, to mark an angle) · dot x y · text x y "words" (handwriting centred on x y) · pause seconds.
    After the numbers, any of: a colour (ink, red, blue, green, orange, purple, grey), dashed, fill, thick, thin; for text, big or small, and left to start the words at x instead of centring them on it.
    Plan it like a textbook figure, using most of the board: first the setting (the surface or boundary between two materials, the block, the triangle, the axes), then what moves or acts across it (rays, forces, arrows), then labels last. Every line must be physically and mathematically right; work out the coordinates before you write them. Put each label just beside the thing it names, never on top of a line, and keep words short. A title goes at the very top, with nothing else within 50 units below it. Use at most 25 commands: a few clear parts beat many small ones. For a comparison, draw the two side by side, each with its own small heading.
    Example, Pythagoras:
    shape 80 240 320 240 80 60
    right_angle 80 240 320 240 80 60
    text 200 266 "b" blue big
    text 58 150 "a" blue big
    text 222 124 "c" red big
    arrow 330 95 240 122 red
    text 335 80 "hypotenuse" red
    text 200 28 "a² + b² = c²" big
    """

    static func realtimeToolDefinitions() -> [[String: Any]] {
        [
            ["type": "function", "name": drawToolName, "description": realtimeDrawToolDescription, "parameters": realtimeDrawParameters],
            ["type": "function", "name": pictureToolName, "description": pictureToolDescription, "parameters": pictureParameters],
        ]
    }

    static func anthropicToolDefinitions() -> [[String: Any]] {
        [
            ["name": drawToolName, "description": drawToolDescription, "input_schema": drawParameters],
            ["name": pictureToolName, "description": pictureToolDescription, "input_schema": pictureParameters],
        ]
    }

    private static let drawToolDescription = "Draw a teaching diagram on the user's screen, in handwriting, in an empty area; it draws itself stroke by stroke while you explain. " + sketchGrammar
    /// The voice model describes the drawing and `TeacherSketchWriter` writes it: asked for
    /// coordinates itself, the Realtime model drew refraction with the ray down the normal.
    private static let realtimeDrawToolDescription = "Draw a teaching diagram on the user's screen, in handwriting, in an empty area. Describe what to draw in words, never coordinates; a drawing model draws it stroke by stroke, appearing a few seconds after the call."
    private static let realtimeDrawParameters: [String: Any] = [
        "type": "object",
        "properties": [
            "title": ["type": "string", "description": "What the drawing shows, 1-5 words, e.g. 'Refraction of light'."],
            "what_to_draw": ["type": "string", "description": "One or two sentences: the setting, each part and its label, and what must be right, e.g. 'light going from air into water: the boundary, the dashed normal, the incident ray, the refracted ray bending toward the normal, angles i and r marked'."],
        ],
        "required": ["title", "what_to_draw"],
    ]
    private static let pictureToolDescription = "Show a real picture (from Wikimedia Commons or Wikipedia) on the user's screen when a drawing cannot show the idea well."
    private static let drawParameters: [String: Any] = [
        "type": "object",
        "properties": [
            "title": ["type": "string", "description": "What the drawing shows, 1-5 words, e.g. 'Pythagoras theorem'."],
            "sketch": ["type": "string", "description": "The drawing commands."],
        ],
        "required": ["title", "sketch"],
    ]
    private static let pictureParameters: [String: Any] = [
        "type": "object",
        "properties": [
            "query": ["type": "string", "description": "A short search phrase in English, e.g. 'neuron diagram'."],
            "caption": ["type": "string", "description": "A few words written under the picture."],
        ],
        "required": ["query", "caption"],
    ]

    /// Runs a teacher tool call and returns what the model is told; nil when `toolName` is not one
    /// of the Teacher's tools. `capture` is the screenshot the question carried (the cursor's
    /// screen), used to find empty space.
    @MainActor static func perform(toolName: String, arguments: [String: Any], capture: CompanionScreenCapture?) async -> String? {
        guard toolNames.contains(toolName) else { return nil }
        guard isEnabled else { return "the teacher plugin is off; explain in words" }
        let board = TeacherBoard.shared
        // The app already started this question's lesson (`startLesson`): one drawing per question.
        if board.questionHasLesson {
            return "the drawing for this is already appearing on their screen; explain the idea part by part, as a teacher walking through a whiteboard diagram"
        }
        board.questionHasLesson = true
        switch toolName {
        case drawToolName:
            let source = arguments["sketch"] as? String ?? ""
            let title = (arguments["title"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            let whatToDraw = (arguments["what_to_draw"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !whatToDraw.isEmpty {
                // Written in the background, for the same reason as a picture search below.
                let requestToken = board.beginPictureSearch()
                Task { @MainActor in
                    guard let written = await TeacherSketchWriter.write(title: title, whatToDraw: whatToDraw),
                          let sketch = TeacherSketchParser.parse(written).sketch else {
                        AppLog.append("teacher: no drawing for the description")
                        return
                    }
                    if !board.show(sketch: sketch, title: title, capture: capture, requestToken: requestToken) {
                        AppLog.append("teacher: drawing arrived after the board moved on; not shown")
                    }
                }
                return "the drawing appears on their screen in a few seconds; start explaining now, part by part in the order you described it"
            }
            let result = TeacherSketchParser.parse(source)
            guard let sketch = result.sketch else {
                AppLog.append("teacher: nothing drawable in the sketch (\(result.skippedLines.count) lines skipped)")
                return "could not read the drawing; use the commands described in the tool, one per line"
            }
            if !result.skippedLines.isEmpty {
                AppLog.append("teacher: skipped \(result.skippedLines.count) sketch lines: \(result.skippedLines.prefix(3).joined(separator: " | "))")
            }
            board.show(sketch: sketch, title: title, capture: capture)
            return "drawing it on their screen now (\(sketch.strokes.count) strokes); explain it part by part in the order you drew it"
        default:
            let query = arguments["query"] as? String ?? ""
            let caption = (arguments["caption"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            // Searched in the background: the Realtime lane handles tool calls on its receive loop,
            // and a few seconds of waiting there would stall the voice that is still arriving.
            let requestToken = board.beginPictureSearch()
            Task { @MainActor in
                guard let picture = await TeacherPictureFinder.find(query: query, caption: caption) else { return }
                if !board.show(picture: picture, capture: capture, requestToken: requestToken) {
                    AppLog.append("teacher: picture arrived after the board moved on; not shown")
                }
            }
            return "a picture of \(caption.isEmpty ? query : caption) appears on their screen in a moment; describe what it shows"
        }
    }

    /// Starts the lesson for a question as soon as it is known, without waiting for the model to
    /// call `draw_on_screen` (the Realtime model sometimes describes the drawing in words instead).
    /// Only for teaching questions (`TeacherQuestion`), and only where the drawing is written fast
    /// (`TeacherSketchWriter.canStartLessonsEarly`); a tool call that comes later is told the
    /// drawing is on its way.
    @MainActor static func startLesson(for question: String, capture: CompanionScreenCapture?) {
        let board = TeacherBoard.shared
        guard isEnabled, !board.questionHasLesson, TeacherQuestion.isTeachingQuestion(question),
              TeacherSketchWriter.canStartLessonsEarly else { return }
        board.questionHasLesson = true
        AppLog.append("teacher: a teaching question; starting the lesson")
        let requestToken = board.beginPictureSearch()
        let title = String(question.trimmingCharacters(in: .whitespacesAndNewlines).prefix(60))
        // "Solve this": the writer reads the problem off the same screenshot the question carried.
        let screenshotJPEG = TeacherQuestion.needsTheScreen(question) ? capture?.imageData : nil
        Task { @MainActor in
            switch await TeacherSketchWriter.lesson(for: question, screenshotJPEG: screenshotJPEG) {
            case .sketch(let written)?:
                let result = TeacherSketchParser.parse(written)
                guard let sketch = result.sketch else {
                    AppLog.append("teacher: nothing drawable in the written lesson (\(result.skippedLines.count) lines skipped)")
                    return
                }
                if !board.show(sketch: sketch, title: title, capture: capture, requestToken: requestToken) {
                    AppLog.append("teacher: lesson arrived after the board moved on; not shown")
                }
            case .picture(let query)?:
                guard let picture = await TeacherPictureFinder.find(query: query, caption: query) else { return }
                if !board.show(picture: picture, capture: capture, requestToken: requestToken) {
                    AppLog.append("teacher: picture arrived after the board moved on; not shown")
                }
            case nil:
                AppLog.append("teacher: no lesson written for the question")
            }
        }
    }

    /// When they cannot hear (`TeacherListening`), a reply that brought no lesson is written on the
    /// screen as a handwritten note, so a muted or noisy room still gets the answer.
    @MainActor static func writeAnswerIfTheyCannotHear(_ answer: String, questionWasTyped: Bool, capture: CompanionScreenCapture?) {
        let board = TeacherBoard.shared
        guard isEnabled, !board.questionHasLesson else { return }
        let output = TeacherListening.currentOutput()
        guard TeacherListening.answersShouldBeWritten(questionWasTyped: questionWasTyped, output: output),
              let note = TeacherNote.sketch(for: answer.trimmingCharacters(in: .whitespacesAndNewlines)) else { return }
        board.questionHasLesson = true
        AppLog.append("teacher: writing the answer down (typed \(questionWasTyped), volume \(output.volume.map { String(format: "%.0f%%", $0 * 100) } ?? "unknown"), muted \(output.isMuted))")
        board.show(sketch: note, title: "the answer", capture: capture)
    }

    @MainActor @discardableResult static func clearBoard(reason: String) -> Bool {
        TeacherBoard.shared.clear(reason: reason)
    }
}

/// The board's state and its window. One per app.
@MainActor
final class TeacherBoard: ObservableObject {
    static let shared = TeacherBoard()

    struct Lesson {
        enum Content {
            case sketch(TeacherSketch)
            case picture(TeacherPicture)
        }

        let title: String
        let content: Content
        let style: TeacherBoardStyle
        let startedAt: Date
    }

    @Published private(set) var lesson: Lesson?
    /// A drawing or picture was asked for since the board was last cleared (each question clears
    /// it), whether by the app or by the model; a second request for the same question is ignored.
    var questionHasLesson = false

    /// The buddy flies to each label as it is written: (AppKit screen point, display frame).
    var onLabelWritten: ((CGPoint, CGRect) -> Void)?

    private var boardPanel: NSPanel?
    private var closeButtonPanel: NSPanel?
    private var labelPointingTask: Task<Void, Never>?
    /// Bumped by every show and clear, so a picture that arrives after the board moved on is dropped.
    private var requestToken = 0

    /// False when the board moved on (another lesson, a clear) while this one was being written.
    @discardableResult
    func show(sketch: TeacherSketch, title: String, capture: CompanionScreenCapture?, requestToken expectedToken: Int? = nil) -> Bool {
        if let expectedToken, expectedToken != requestToken { return false }
        requestToken &+= 1
        let drawingBounds = TeacherSketchLayout.drawingBounds(of: sketch)
        let placement = TeacherBoardPlacement.placement(
            aspectRatio: drawingBounds.width / max(drawingBounds.height, 1), in: capture, mouseLocation: NSEvent.mouseLocation)
        let lesson = Lesson(title: title.isEmpty ? "drawing" : title, content: .sketch(sketch), style: placement.style, startedAt: Date())
        AppLog.append("teacher: drawing \"\(lesson.title)\" (\(sketch.strokes.count) strokes, \(placement.style)) at \(Int(placement.frame.width))×\(Int(placement.frame.height)) (\(Int(placement.frame.minX)), \(Int(placement.frame.minY)))")
        present(lesson, in: placement.frame)
        pointAtLabels(of: sketch, in: placement.frame, style: placement.style, startedAt: lesson.startedAt)
        return true
    }

    /// The token a background job (a picture search, a drawing being written) shows its result with.
    func beginPictureSearch() -> Int {
        requestToken
    }

    /// False when something else was shown or the board cleared while the picture downloaded.
    @discardableResult
    func show(picture: TeacherPicture, capture: CompanionScreenCapture?, requestToken expectedToken: Int) -> Bool {
        guard expectedToken == requestToken else { return false }
        requestToken &+= 1
        let imageSize = picture.image.size
        // Room for the caption and credit under the picture.
        let aspectRatio = max(imageSize.width, 1) / (max(imageSize.height, 1) * 1.18)
        let placement = TeacherBoardPlacement.placement(aspectRatio: aspectRatio, in: capture, mouseLocation: NSEvent.mouseLocation, forcePaper: false)
        let lesson = Lesson(title: picture.caption.isEmpty ? "picture" : picture.caption, content: .picture(picture), style: .paper, startedAt: Date())
        AppLog.append("teacher: showing a picture from \(picture.credit)")
        labelPointingTask?.cancel()
        present(lesson, in: placement.frame)
        return true
    }

    @discardableResult
    func clear(reason: String) -> Bool {
        requestToken &+= 1
        questionHasLesson = false
        guard lesson != nil else { return false }
        AppLog.append("teacher: board cleared (\(reason))")
        labelPointingTask?.cancel()
        labelPointingTask = nil
        lesson = nil
        boardPanel?.orderOut(nil)
        closeButtonPanel?.orderOut(nil)
        return true
    }

    // MARK: - windows

    private func present(_ lesson: Lesson, in frame: CGRect) {
        self.lesson = lesson
        let panel = boardPanel ?? makeBoardPanel()
        boardPanel = panel
        panel.setFrame(frame, display: true)
        panel.orderFrontRegardless()

        let closePanel = closeButtonPanel ?? makeCloseButtonPanel()
        closeButtonPanel = closePanel
        let buttonSize: CGFloat = 26
        closePanel.setFrame(CGRect(x: frame.maxX - buttonSize / 2 - 4, y: frame.maxY - buttonSize / 2 - 4, width: buttonSize, height: buttonSize), display: true)
        closePanel.orderFrontRegardless()
    }

    private func makeBoardPanel() -> NSPanel {
        let panel = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        // Under the buddy's overlay (screen saver level), over ordinary windows.
        panel.level = .statusBar
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.contentView = NSHostingView(rootView: TeacherBoardView(board: self))
        return panel
    }

    private func makeCloseButtonPanel() -> NSPanel {
        let panel = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.contentView = NSHostingView(rootView: TeacherCloseButton { [weak self] in self?.clear(reason: "close button") })
        return panel
    }

    // MARK: - the buddy points at each label

    /// As each label is written the buddy flies to it, so "this is a, this is b" lands on the
    /// drawing. At most six labels, at least 1.2 s apart, so the pointer never darts about.
    private func pointAtLabels(of sketch: TeacherSketch, in frame: CGRect, style: TeacherBoardStyle, startedAt: Date) {
        labelPointingTask?.cancel()
        guard let onLabelWritten else { return }
        let padding: CGFloat = style == .paper ? 22 : 6
        let inner = frame.insetBy(dx: padding, dy: padding)
        // The same layout the view draws with; its points are from the inner area's top-left.
        let layout = TeacherSketchLayout(sketch: sketch, viewSize: inner.size)
        let displayFrame = NSScreen.screens.first(where: { $0.frame.intersects(frame) })?.frame ?? frame
        let timeline = TeacherSketchTimeline.entries(for: sketch)

        var stops: [(seconds: Double, location: CGPoint)] = []
        for (index, stroke) in sketch.strokes.enumerated() {
            guard case .text = stroke.kind, let anchor = stroke.points.first else { continue }
            let seconds = timeline[index].start
            if let last = stops.last, seconds - last.seconds < 1.2 { continue }
            // A little left of the word's centre and below it, so the triangle sits beside the
            // writing instead of over it.
            let inView = layout.viewPoint(anchor)
            let location = CGPoint(x: inner.minX + inView.x - 14, y: inner.maxY - inView.y - 12)
            stops.append((seconds, location))
            if stops.count == 6 { break }
        }
        labelPointingTask = Task { @MainActor [weak self] in
            for stop in stops {
                let wait = stop.seconds - Date().timeIntervalSince(startedAt)
                if wait > 0 { try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000)) }
                guard !Task.isCancelled, self?.lesson != nil else { return }
                self?.onLabelWritten?(stop.location, displayFrame)
            }
        }
    }
}

private struct TeacherCloseButton: View {
    let action: () -> Void
    @State private var isHovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark")
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 22, height: 22)
                .background(Circle().fill(Color.black.opacity(isHovering ? 0.8 : 0.55)))
        }
        .buttonStyle(.plain)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onHover { isHovering = $0 }
        .pointerCursor()
        .help("Clear the board (esc)")
        .accessibilityLabel("Clear the board")
    }
}
