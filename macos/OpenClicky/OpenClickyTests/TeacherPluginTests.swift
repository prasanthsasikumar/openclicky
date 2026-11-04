//
//  TeacherPluginTests.swift
//  OpenClickyTests
//
//  The Teacher plugin: the drawing language, its timing, where the board lands on a screen, and
//  the picture search replies.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct TeacherPluginTests {
    /// The Teacher is a built-in: always on the island, so its switch is there to flip. One
    /// switch, two places (the island row and Settings), off by default; nothing installs it.
    @MainActor @Test func theTeacherIsAlwaysOnTheIslandAndOneSwitchTurnsLessonsOn() {
        let defaults = ScratchDefaults.make()
        #expect(!TeacherPlugin.lessonsAreOn(defaults: defaults), "off by default")
        #expect(!TeacherPlugin.isEnabled(defaults: defaults))
        TeacherPlugin.setLessonsOn(true, defaults: defaults)
        #expect(TeacherPlugin.isEnabled(defaults: defaults), "the prompt line, the tools and the lessons follow the one switch")
        TeacherPlugin.setLessonsOn(false, defaults: defaults)
        #expect(!TeacherPlugin.isEnabled(defaults: defaults))
        let island = IslandComponentSettings(defaults: defaults)
        #expect(island.isEnabled(TeacherPlugin.id, in: IslandComponentRegistry.all), "on the island whatever the switch says")
        #expect(IslandComponentRegistry.builtIns.contains { $0.id == TeacherPlugin.id })
        #expect(!IslandPluginRegistry.all.contains { $0.id == TeacherPlugin.id })
    }

    // MARK: - the drawing language

    @Test func theToolsOwnExampleParsesWithoutASkippedLine() throws {
        let example = TeacherPlugin.sketchGrammar.components(separatedBy: "Example, Pythagoras:\n").last ?? ""
        let result = TeacherSketchParser.parse(example)
        let sketch = try #require(result.sketch)
        #expect(result.skippedLines.isEmpty)
        #expect(sketch.strokes.count == 8)
        #expect(sketch.boardWidth == 400 && sketch.boardHeight == 300)
        #expect(sketch.strokes[0].kind == .shape && sketch.strokes[0].points.count == 3)
        #expect(sketch.strokes[1].kind == .rightAngle)
        #expect(sketch.strokes[2].kind == .text("b") && sketch.strokes[2].ink == .blue && sketch.strokes[2].weight > 1)
        #expect(sketch.strokes[7].kind == .text("a² + b² = c²"))
    }

    @Test func commandsOnOneLineSplitOnSemicolonsButNotInsideQuotes() throws {
        let sketch = try #require(TeacherSketchParser.parse(#"board 200 100; line 0 0 100 50 red dashed; text 50 50 "a; b" small"#).sketch)
        #expect(sketch.boardWidth == 200 && sketch.boardHeight == 100)
        #expect(sketch.strokes.count == 2)
        #expect(sketch.strokes[0].ink == .red && sketch.strokes[0].isDashed)
        #expect(sketch.strokes[1].kind == .text("a; b") && sketch.strokes[1].weight < 1)
    }

    @Test func everyCommandReadsItsNumbers() throws {
        let source = """
        arrow 10 10 50 50
        curve 0 0 10 10 20 0
        circle 100 100 40 fill
        arc 100 100 30 0 90
        box 10 20 30 40
        angle 50 50 90 50 50 10
        dot 5 5
        pause 1.5
        label 20 20 hypotenuse orange
        """
        let sketch = try #require(TeacherSketchParser.parse(source).sketch)
        #expect(sketch.strokes.map(\.kind) == [
            .arrow, .curve, .circle(radius: 40), .arc(radius: 30, startDegrees: 0, endDegrees: 90), .box, .angle, .dot,
            .pause(seconds: 1.5), .text("hypotenuse"),
        ])
        #expect(sketch.strokes[2].isFilled)
        #expect(sketch.strokes[4].points == [CGPoint(x: 10, y: 20), CGPoint(x: 30, y: 40)])
        #expect(sketch.strokes[8].ink == .orange)
    }

    @Test func unreadableLinesAreSkippedAndNothingDrawableIsNil() {
        let result = TeacherSketchParser.parse("triangle 1 2\nscribble 1 2 3\nline 0 0 10 10")
        #expect(result.sketch?.strokes.count == 1)
        #expect(result.skippedLines == ["triangle 1 2", "scribble 1 2 3"])
        #expect(TeacherSketchParser.parse("").sketch == nil)
        #expect(TeacherSketchParser.parse("pause 2").sketch == nil)
        #expect(TeacherSketchParser.parse("circle 10 10 -5").sketch == nil)
    }

    @Test func pointsFarOffTheBoardArePulledBackOntoIt() throws {
        let sketch = try #require(TeacherSketchParser.parse("line -900 50 5000 50").sketch)
        #expect(sketch.strokes[0].points == [CGPoint(x: -40, y: 50), CGPoint(x: 440, y: 50)])
    }

    @Test func aSketchIsCappedAtItsMaximumStrokes() throws {
        let source = (0..<120).map { "dot \($0) 10" }.joined(separator: "\n")
        let result = TeacherSketchParser.parse(source)
        #expect(result.sketch?.strokes.count == TeacherSketch.maximumStrokes)
        #expect(result.skippedLines.count == 120 - TeacherSketch.maximumStrokes)
    }

    // MARK: - timing

    @Test func strokesFollowEachOtherAtAHandsPace() throws {
        let sketch = try #require(TeacherSketchParser.parse("line 0 0 400 0\ntext 10 10 \"hypotenuse\"\npause 2\ndot 1 1").sketch)
        let entries = TeacherSketchTimeline.entries(for: sketch)
        #expect(entries.count == 4)
        #expect(entries[0].start == 0)
        #expect(entries[0].duration >= 0.35 && entries[0].duration <= 1.6)
        #expect(abs(entries[1].duration - 0.7) < 0.001)
        #expect(entries[2].duration == 2)
        #expect(abs(entries[1].start - (entries[0].duration + TeacherSketchTimeline.gapBetweenStrokes)) < 0.0001)
        #expect(TeacherSketchTimeline.totalSeconds(for: sketch) == entries[3].start + entries[3].duration)
    }

    // MARK: - fitting the drawing

    @Test func theLayoutFitsWhatWasDrawnNotTheDeclaredBoard() throws {
        // Drawn in the board's middle, with a formula written past its right edge.
        let sketch = try #require(TeacherSketchParser.parse("line 140 200 220 200\nline 140 140 140 200\ntext 380 220 \"a² + b² = c²\" big").sketch)
        let bounds = TeacherSketchLayout.drawingBounds(of: sketch)
        #expect(bounds.minX < 140 && bounds.minX > 100)
        #expect(bounds.maxX > 400)
        let layout = TeacherSketchLayout(sketch: sketch, viewSize: CGSize(width: 400, height: 300))
        // The drawing is centred and fills the view's width.
        let left = layout.viewPoint(CGPoint(x: bounds.minX, y: bounds.minY))
        let right = layout.viewPoint(CGPoint(x: bounds.maxX, y: bounds.maxY))
        #expect(abs(left.x) < 0.5 || abs(left.y) < 0.5)
        #expect(abs(right.x - 400) < 0.5 || abs(right.y - 300) < 0.5)
        #expect(abs((left.x + right.x) / 2 - 200) < 0.5 && abs((left.y + right.y) / 2 - 150) < 0.5)
    }

    @Test func aTinyDrawingStillGetsABoardOfSomeSize() throws {
        let sketch = try #require(TeacherSketchParser.parse("dot 200 150").sketch)
        let bounds = TeacherSketchLayout.drawingBounds(of: sketch)
        #expect(bounds.width >= 100 && bounds.height >= 100)
    }

    // MARK: - where the board lands

    /// A grid that is empty except where `busy` says, all of one brightness.
    func grid(columns: Int, rows: Int, brightness: Double, busy: (Int, Int) -> Bool) -> TeacherScreenGrid {
        var isEmpty: [Bool] = []
        for row in 0..<rows { for column in 0..<columns { isEmpty.append(!busy(column, row)) } }
        return TeacherScreenGrid(columns: columns, rows: rows, isEmpty: isEmpty, brightness: Array(repeating: brightness, count: columns * rows))
    }

    @Test func aGoogleLikePageGetsInkInItsWideRightMargin() {
        // 1440 × 900 pt: 90 × 56 cells. Results text fills columns 10–50 from row 8 down.
        let page = grid(columns: 90, rows: 56, brightness: 0.98) { column, row in column >= 10 && column <= 50 && row >= 8 }
        let display = CGRect(x: 0, y: 0, width: 1440, height: 900)
        let placement = TeacherBoardPlacement.placement(aspectRatio: 4.0 / 3.0, grid: page, displayFrame: display, mouseLocation: CGPoint(x: 400, y: 400))
        #expect(placement.style == .inkOnLightPage)
        #expect(placement.frame.minX > 51 * 16)
        #expect(placement.frame.maxX < 1440)
        #expect(placement.frame.width >= TeacherBoardPlacement.minimumBoardWidthInPoints)
        #expect(abs(placement.frame.width / placement.frame.height - 4.0 / 3.0) < 0.02)
        // Never in the menu bar and island band.
        #expect(placement.frame.maxY <= 900 - TeacherBoardPlacement.topBandInPoints)
    }

    @Test func aDarkEmptyAreaGetsChalk() {
        let editor = grid(columns: 90, rows: 56, brightness: 0.1) { column, _ in column < 40 }
        let placement = TeacherBoardPlacement.placement(aspectRatio: 1.5, grid: editor, displayFrame: CGRect(x: 0, y: 0, width: 1440, height: 900), mouseLocation: .zero)
        #expect(placement.style == .chalkOnDarkPage)
    }

    @Test func aBusyScreenGetsPaperAwayFromTheMouse() {
        let busy = grid(columns: 90, rows: 56, brightness: 0.5) { _, _ in true }
        let display = CGRect(x: 0, y: 0, width: 1440, height: 900)
        let mouseOnTheRight = TeacherBoardPlacement.placement(aspectRatio: 4.0 / 3.0, grid: busy, displayFrame: display, mouseLocation: CGPoint(x: 1300, y: 400))
        #expect(mouseOnTheRight.style == .paper)
        #expect(mouseOnTheRight.frame.maxX < 720)
        let mouseOnTheLeft = TeacherBoardPlacement.placement(aspectRatio: 4.0 / 3.0, grid: busy, displayFrame: display, mouseLocation: CGPoint(x: 100, y: 400))
        #expect(mouseOnTheLeft.frame.minX > 720)
    }

    @Test func greyPixelsBecomeEmptyAndBusyCells() {
        // 2 × 1 cells of 8 px: the left one flat white, the right one with a black stroke.
        var pixels = [UInt8](repeating: 250, count: 16 * 8)
        for row in 0..<8 { pixels[row * 16 + 12] = 0 }
        let cells = TeacherBoardPlacement.grid(fromGreyPixels: pixels, width: 16, height: 8, pixelsPerCell: 8)
        #expect(cells.columns == 2 && cells.rows == 1)
        #expect(cells.isEmpty == [true, false])
        #expect(cells.brightness[0] > 0.97)
    }

    @Test func theLargestRectangleIsFoundAroundAnObstacle() throws {
        // 20 × 10 with a busy column at 6: the empty block on the right (13 wide) wins.
        let cells = grid(columns: 20, rows: 10, brightness: 1) { column, _ in column == 6 }
        let best = try #require(TeacherBoardPlacement.largestEmptyRectangle(in: cells, aspectRatio: 1))
        #expect(best == TeacherCellRect(column: 7, row: 0, width: 13, height: 10))
    }

    // MARK: - pictures

    @Test func commonsRepliesGiveStillImageThumbnailsInSearchOrder() {
        let reply = """
        {"query":{"pages":{
          "11":{"index":2,"title":"File:B.png","imageinfo":[{"mime":"image/png","thumburl":"https://upload.wikimedia.org/b.png"}]},
          "22":{"index":1,"title":"File:A.svg","imageinfo":[{"mime":"image/svg+xml","thumburl":"https://upload.wikimedia.org/a.svg.png"}]},
          "33":{"index":3,"title":"File:C.ogv","imageinfo":[{"mime":"video/ogg","thumburl":"https://upload.wikimedia.org/c.jpg"}]}
        }}}
        """
        let urls = TeacherPictureFinder.commonsThumbnailURLs(from: Data(reply.utf8))
        #expect(urls.map(\.absoluteString) == ["https://upload.wikimedia.org/a.svg.png", "https://upload.wikimedia.org/b.png"])
        #expect(TeacherPictureFinder.commonsThumbnailURLs(from: Data("{}".utf8)).isEmpty)
    }

    @Test func wikipediaRepliesGiveLeadImages() {
        let reply = #"{"query":{"pages":{"5":{"index":1,"thumbnail":{"source":"https://upload.wikimedia.org/neuron.jpg"}},"6":{"index":2}}}}"#
        #expect(TeacherPictureFinder.wikipediaThumbnailURLs(from: Data(reply.utf8)).map(\.absoluteString) == ["https://upload.wikimedia.org/neuron.jpg"])
    }

    @Test func theSearchSendsOnlyTheWords() throws {
        let url = try #require(TeacherPictureFinder.commonsSearchURL(for: "neuron diagram"))
        #expect(url.host == "commons.wikimedia.org")
        let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(query.first(where: { $0.name == "gsrsearch" })?.value == "neuron diagram filetype:bitmap|drawing")
    }

    // MARK: - tools

    @Test func bothLanesGetTheSameTwoTools() {
        let realtimeNames = TeacherPlugin.realtimeToolDefinitions().compactMap { $0["name"] as? String }
        let anthropicNames = TeacherPlugin.anthropicToolDefinitions().compactMap { $0["name"] as? String }
        #expect(Set(realtimeNames) == TeacherPlugin.toolNames)
        #expect(Set(anthropicNames) == TeacherPlugin.toolNames)
        #expect(TeacherPlugin.anthropicToolDefinitions().allSatisfy { $0["input_schema"] != nil })
    }

    @Test func theVoiceLaneDescribesTheDrawingAndTheAskLaneWritesIt() {
        let realtimeDraw = TeacherPlugin.realtimeToolDefinitions().first { $0["name"] as? String == TeacherPlugin.drawToolName }
        let realtimeRequired = (realtimeDraw?["parameters"] as? [String: Any])?["required"] as? [String]
        #expect(realtimeRequired == ["title", "what_to_draw"])
        let anthropicDraw = TeacherPlugin.anthropicToolDefinitions().first { $0["name"] as? String == TeacherPlugin.drawToolName }
        let anthropicRequired = (anthropicDraw?["input_schema"] as? [String: Any])?["required"] as? [String]
        #expect(anthropicRequired == ["title", "sketch"])
    }

    @Test func theSketchWritersReplyIsReadAndUnfenced() throws {
        let reply = #"{"choices":[{"message":{"role":"assistant","content":"```\nline 0 0 10 10\n```"}}]}"#
        let text = try #require(TeacherSketchWriter.chatCompletionText(from: Data(reply.utf8)))
        #expect(TeacherSketchWriter.withoutCodeFences(text) == "line 0 0 10 10")
        #expect(TeacherSketchWriter.chatCompletionText(from: Data(#"{"choices":[{"message":{"content":"  "}}]}"#.utf8)) == nil)
        #expect(TeacherSketchWriter.userPrompt(title: "Refraction", whatToDraw: "a ray").contains("Draw: a ray"))
    }

    @Test func teachingQuestionsAreToldApartFromScreenAndHowToQuestions() {
        let lessons = [
            "how does a rotatry engine work compared to a normal engine",
            "Teach me how the Pythagoras theorem works.",
            "teach me refraction",
            "how do vaccines work?",
            "explain how photosynthesis happens",
            "what's the difference between a series and a parallel circuit",
            "AC vs DC",
        ]
        let notLessons = [
            "how do I export this video",
            "how does this button work",
            "where is the save menu",
            "open safari",
            "what time is it in Tokyo",
            "how to make a folder on the desktop",
            "hi",
        ]
        for question in lessons { #expect(TeacherQuestion.isTeachingQuestion(question), "\(question)") }
        for question in notLessons { #expect(!TeacherQuestion.isTeachingQuestion(question), "\(question)") }
    }

    @Test func solvingWhatIsOnTheScreenIsALessonThatNeedsTheScreenshot() {
        for question in ["please solve this equaltion in the screen", "can you help me solve this", "work this out step by step"] {
            #expect(TeacherQuestion.isTeachingQuestion(question), "\(question)")
            #expect(TeacherQuestion.needsTheScreen(question), "\(question)")
        }
        #expect(!TeacherQuestion.needsTheScreen("how does a rotary engine work"))
        #expect(!TeacherQuestion.isProblemToSolve("how does a rotary engine work"))
    }

    @Test func answersAreWrittenWhenTheyCannotHear() {
        let loud = TeacherListening.Output(volume: 0.8, isMuted: false)
        #expect(!TeacherListening.answersShouldBeWritten(questionWasTyped: false, output: loud))
        #expect(TeacherListening.answersShouldBeWritten(questionWasTyped: true, output: loud))
        #expect(TeacherListening.answersShouldBeWritten(questionWasTyped: false, output: .init(volume: 0.8, isMuted: true)))
        #expect(TeacherListening.answersShouldBeWritten(questionWasTyped: false, output: .init(volume: 0.31, isMuted: false)))
        #expect(!TeacherListening.answersShouldBeWritten(questionWasTyped: false, output: .init(volume: nil, isMuted: false)))
    }

    @Test func aWrittenAnswerWrapsIntoLeftAlignedLines() throws {
        let answer = "Add the two equations: (x + y) + (2x - y) = 24 + (-6). That gives 3x = 18, so x = 6. Then 6 + y = 24, so y = 18."
        let lines = TeacherNote.wrappedLines(answer)
        #expect(lines.count >= 3)
        #expect(lines.allSatisfy { $0.count <= TeacherNote.charactersPerLine || !$0.contains(" ") })
        #expect(lines.joined(separator: " ") == answer)
        let note = try #require(TeacherNote.sketch(for: answer))
        #expect(note.strokes.allSatisfy { $0.isLeftAligned })
        let long = TeacherNote.wrappedLines(String(repeating: "word ", count: 400))
        #expect(long.count == TeacherNote.maximumLines && long.last?.hasSuffix("…") == true)
        #expect(TeacherNote.sketch(for: "   ") == nil)
    }

    @Test func leftIsAnOptionNotAWord() throws {
        let sketch = try #require(TeacherSketchParser.parse("text 10 20 \"x = 6\" left red").sketch)
        #expect(sketch.strokes[0].isLeftAligned && sketch.strokes[0].ink == .red && sketch.strokes[0].kind == .text("x = 6"))
    }

    @Test func theScreenshotRidesAlongAsAnImage() {
        #expect(TeacherSketchWriter.userContent(userPrompt: "hi", screenshotJPEG: nil) as? String == "hi")
        let parts = TeacherSketchWriter.userContent(userPrompt: "hi", screenshotJPEG: Data([1, 2])) as? [[String: Any]]
        #expect(parts?.count == 2)
        #expect(((parts?[1]["image_url"] as? [String: Any])?["url"] as? String)?.hasPrefix("data:image/jpeg;base64,") == true)
    }

    func credentials(openAI: String? = nil, anthropic: String? = nil, account: String? = nil, gemini: String? = nil, claudeCodePlan: Bool = false) -> ProviderCredentials {
        ProviderCredentials(openAIKey: openAI, anthropicKey: anthropic, accountToken: account, geminiKey: gemini, claudeCodePlanOn: claudeCodePlan)
    }

    @Test func lessonsAreWrittenWithoutOpenAITooButNeverOnAPlanAlone() {
        #expect(TeacherSketchWriter.route(for: credentials(openAI: "sk-o", anthropic: "sk-a", account: "t")) == .openAI(key: "sk-o"))
        #expect(TeacherSketchWriter.route(for: credentials(anthropic: "sk-a", account: "t")) == .anthropic(key: "sk-a"))
        #expect(TeacherSketchWriter.route(for: credentials(account: "t", claudeCodePlan: true)) == .account)
        #expect(TeacherSketchWriter.route(for: credentials(gemini: "g")) == .answerSource(.gemini))
        #expect(TeacherSketchWriter.route(for: credentials(claudeCodePlan: true)) == nil)
        #expect(TeacherSketchWriter.route(for: credentials()) == nil)
    }

    @Test func claudeWritesWithThinkingOffAndRoomForADrawing() throws {
        let body = TeacherSketchWriter.claudeBody(userPrompt: "draw", screenshotJPEG: Data([1]))
        #expect(body["model"] as? String == "claude-sonnet-5-5")
        #expect(body["max_tokens"] as? Int == 1024)
        #expect((body["thinking"] as? [String: String])?["type"] == "between_tools")
        let content = try #require(((body["messages"] as? [[String: Any]])?.first?["content"]) as? [[String: Any]])
        #expect(content.map { $0["type"] as? String } == ["image", "text"])
        let plain = TeacherSketchWriter.claudeBody(userPrompt: "draw", screenshotJPEG: nil)
        #expect((((plain["messages"] as? [[String: Any]])?.first?["content"]) as? [[String: Any]])?.count == 1)
    }

    @Test func theWritersReplyIsADrawingOrAPicture() {
        #expect(TeacherSketchWriter.Reply.parse("```\nline 0 0 1 1\n```") == .sketch("line 0 0 1 1"))
        #expect(TeacherSketchWriter.Reply.parse("PICTURE: human heart diagram") == .picture(query: "human heart diagram"))
        #expect(TeacherSketchWriter.Reply.parse("picture: neuron") == .picture(query: "neuron"))
    }

    @MainActor @Test func anotherToolIsNotTheTeachersBusiness() async {
        #expect(await TeacherPlugin.perform(toolName: "open_app", arguments: [:], capture: nil) == nil)
    }
}
