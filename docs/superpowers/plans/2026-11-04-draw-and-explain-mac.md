# Draw and Explain on the Mac Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Mac ask lane answers questions a picture explains by drawing a narrated 2D/3D figure on screen through a stage page shared with Windows, which minimises to a bubble by the island when done.

**Architecture:** The stage page (HTML/JS/three.js, from Windows PR #39) moves to a repo-root `draw-stage/` folder bundled into the app. The Mac hosts it in a transparent click-through `NSPanel` + `WKWebView` served over a custom URL scheme, and talks to it with the Windows JSON protocol. A `DrawingAnswer` streams the model's program into the page and speaks each `say()` line through `SpeechQueue`, whose new per-piece hooks pace the drawing. `CompanionManager` tries a drawing before the ordinary ask-lane request whenever a narrow keyword gate passes, and falls through untouched when the model answers `NONE`.

**Tech Stack:** Swift 5 mode, AppKit (`NSPanel`), WebKit (`WKWebView`, `WKURLSchemeHandler`, `WKScriptMessageHandler`), SwiftUI (bubble), Swift Testing, the existing `SpeechQueue` / `ClaudeAPI` / `AnswerProvider` stack; backend TypeScript (one cap change).

**Spec:** `docs/superpowers/specs/2026-11-04-draw-and-explain-mac-design.md`

## Global Constraints

- Nothing under `windows/` or `linux/` changes.
- The shared folder is `draw-stage/` at the repo root; only the five changes the spec lists are made to the page (host bridge, worker sandbox, `sleep`/`wake`, `prompt/question.md`, Mac fonts). The message protocol gains only `sleep` and `wake`.
- The Mac never sends `remembered`, `recording`, `saving`, `piece`.
- A drawing is attempted only when `DrawStageLesson.wantsADrawing` passes; the model's `NONE` or prose falls through to the ordinary lane **before anything is spoken or shown**; the hold line ("One moment, let me draw that for you.") is spoken only after the reply has shown it is a program and 2 s have passed without a first line.
- Drawing requests: 8,192 output tokens, no tools, no pointer close-up, cursor screen only, thinking off (`between_tools` on Sonnet direct, as `TeacherSketchWriter` does), header `X-OpenClicky-Purpose: drawing`; the backend honours `max_tokens` up to 8,192 only with that header. A 402 is treated like `NONE`.
- Stage readiness timeout: 5 s after the first `feed`. Drawing limit: 3 minutes. Hold line after 2 s.
- Stage panel: borderless non-activating `NSPanel`, level `.statusBar`, `[.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]`, `ignoresMouseEvents = true` except over reported figure rects. Hidden means `sleep` sent and `orderOut`.
- Bubble: 96 pt wide, beside the island (outside its near edge for left/right placement, below-right under the notch, top-right of the screen when hidden), level `.popUpMenu`, hover shows ✕, click restores, only the last drawing kept.
- Setting: `DictationSettings.drawToExplain`, key `openclicky.ask.drawToExplain`, default true, row `clicky.drawToExplain` titled "Draw to explain" in the Clicky page's first group.
- Code style per `macos/OpenClicky/CLAUDE.md`: long clear names, comments say why, `@MainActor` UI, `.pointerCursor()` on buttons. Commit messages in imperative mood ending with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Tests: Swift Testing (`@Test`, `#expect`), never read `~/Documents`, run with the default DerivedData: `scripts` in this plan use `xcodebuild -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' build-for-testing` then `test-without-building -only-testing:OpenClickyTests/<Suite>` from `macos/OpenClicky`. Kill a running installed OpenClicky before a test run.
- Credit darkworks (PR #39) in `draw-stage/README.md` and in the commit that adds the folder.

## Review Focus

1. A question that passes the pre-filter but whose reply is cut off at `max_tokens` mid-program: the drawing must end cleanly (the page gets `feed … done`, `finishNow` is never needed) and the voice must finish the lines it has; pinned in Task 6 (`aCutOffProgramStillFinishes`).
2. Esc pressed between the request starting and the first line: the request task is cancelled, nothing is spoken, the stage is cleared, and no bubble appears; pinned in Task 6 (`cancellingBeforeTheFirstLineClearsTheStage`).
3. The page reports `error` (a statement failed) but goes on drawing: the answer still completes and the error is only logged; pinned in Task 6 (`aPageErrorIsLoggedNotFatal`).
4. A second question while a drawing is **playing** (not finished): the first drawing is interrupted (voice cancelled, `finishNow`), then the new question is handled as a follow-up of the still-open stage; pinned in Task 10 (`aQuestionWhilePlayingInterruptsThenFollowsUp`, a pure decision test).
5. The person switches displays or the screen configuration changes mid-drawing: the stage is cleared and hidden, the voice stops, no crash; pinned in Task 8 (`aDisplayChangeHidesTheStage`, through the controller's `displaysChanged()`).

---

### Task 1: The shared `draw-stage/` folder, bundled into the Mac app

**Files:**
- Create: `draw-stage/` (copied from `git show pr-39:windows/src/OpenClicky.Windows/Assets/DrawStage/…`, see the list in the spec), `draw-stage/README.md`, `draw-stage/prompt/drawing-language.md`, `draw-stage/prompt/follow-up.md`, `draw-stage/prompt/question.md`
- Create: `macos/OpenClicky/scripts/embed-draw-stage.sh`
- Modify: `macos/OpenClicky/OpenClicky.xcodeproj/project.pbxproj` (a third `PBXShellScriptBuildPhase` "Embed draw stage", id `AA00BB0C2F6500100039DA55`, listed after "Embed app skills" in the target's `buildPhases`)
- Test: `macos/OpenClicky/OpenClickyTests/DrawStage/DrawStageBundleTests.swift`

**Interfaces:**
- Produces: bundle folder `Contents/Resources/draw-stage/` with `index.html`, `stage.js`, `stage-worker.js`, `stage.css`, `figure3d.js`, `vendor/three/*`, `samples/*`, `prompt/*.md`. `window.drawStage.handle(message)` in the page; page → host via `window.webkit.messageHandlers.stage.postMessage(object)`. New page messages `sleep` and `wake`.
- The prompt files use these placeholders exactly: `{{W}}`, `{{H}}`, `{{beats}}`, `{{calls}}`, `{{parts}}`, `{{partsAtMost}}` (drawing-language.md); `{{asked}}`, `{{program}}`, `{{question}}`, `{{nextStep}}`, `{{marked}}` (follow-up.md); `{{question}}` (question.md).

- [ ] **Step 1: Extract the page from the PR branch**

Run from the repo root: `git archive pr-39 windows/src/OpenClicky.Windows/Assets/DrawStage docs/draw-stage.md | tar -x -C /tmp/draw-stage-src` then copy `/tmp/draw-stage-src/windows/src/OpenClicky.Windows/Assets/DrawStage/*` to `draw-stage/`. Verify `shasum -a 256 draw-stage/vendor/three/three.module.min.js` equals the npm three@0.172.0 file (it did on 2026-11-04).

- [ ] **Step 2: The host bridge (`draw-stage/stage.js`, the `send` function and the `host.addEventListener` line at the end)**

`send(message)`: if `window.chrome?.webview` post there; else if `window.webkit?.messageHandlers?.stage` call `.postMessage(message)`; else `console.log`. Keep `window.drawStage = { handle, reportHits, place }` as the app → page entry. No other change.

- [ ] **Step 3: The worker sandbox (`draw-stage/stage-worker.js`)**

Extend the nulled-globals list with `'Worker', 'SharedWorker', 'Blob'` and set `URL.createObjectURL = undefined` (guarded by try/catch like the others).

- [ ] **Step 4: `sleep` and `wake` (`draw-stage/stage.js`)**

Add `case 'sleep'` and `case 'wake'` to `handle`: a module-level `asleep` flag; `tick()` returns without scheduling the next frame while asleep; `wake` clears the flag and calls `requestAnimationFrame(tick)` once. Expose `window.drawStage.isAsleep` for the smoke check. In `figure3d.js`, the frame loop checks `window.drawStage?.isAsleep` and idles the same way.

- [ ] **Step 5: Fonts (`draw-stage/stage.css`)**

Every font stack that begins `"Ink Free"` becomes `"Ink Free", "Segoe Print", "Noteworthy", "Bradley Hand", "Comic Sans MS", cursive`.

- [ ] **Step 6: The prompt files**

From `/tmp/draw-stage-src/.../DrawStageLesson.cs`: `SystemPromptText(...)` → `prompt/drawing-language.md` with the six placeholders where the C# interpolates; `FollowUpPrompt` → `prompt/follow-up.md`; the pieces section left out. `prompt/question.md`:

```
They asked: "{{question}}"
If a drawing or a turning 3D model is genuinely needed to understand the answer, answer by drawing on their screen, as one ```js program.
If not, reply with only the word NONE, nothing else: not for a definition, a fact, a number, a yes or no, a short explanation words carry well, or anything about the person's own files, errors, settings or the app in front of them. NONE must be your first word.
```

- [ ] **Step 7: `draw-stage/README.md`**

From `/tmp/draw-stage-src/docs/draw-stage.md`: keep "How it works", "Messages" (add `sleep` / `wake` to the app → page table), "The drawing language", "3D figures", "Follow-up questions", "Safety" (mention the `Worker`/`Blob` fence), the browser-preview paragraph; drop the Windows window, saving, pieces, marker and `Trying it` (PowerShell) sections. Open with one paragraph: what the folder is, that both apps load it unchanged, and credit: "Written by darkworks for OpenClicky's Windows app (PR #39), MIT; three.js r172 under its own LICENSE."

- [ ] **Step 8: `macos/OpenClicky/scripts/embed-draw-stage.sh`**

The twin of `embed-app-skills.sh`: source `$project_dir/../../draw-stage`, destination `$TARGET_BUILD_DIR/$UNLOCALIZED_RESOURCES_FOLDER_PATH/draw-stage`, `rm -rf` then `cp -R` everything except `README.md`; print `embed-draw-stage: N files`. `chmod +x`.

- [ ] **Step 9: Add the build phase to `project.pbxproj`**

Copy the "Embed app skills" `PBXShellScriptBuildPhase` block with id `AA00BB0C2F6500100039DA55`, name `"Embed draw stage"`, script `"\"$PROJECT_DIR/scripts/embed-draw-stage.sh\"\n"`; add `AA00BB0C2F6500100039DA55 /* Embed draw stage */,` after the "Embed app skills" line in the target's `buildPhases`.

- [ ] **Step 10: Write the failing test**

`DrawStageBundleTests.swift`:

```swift
@Suite struct DrawStageBundleTests {
    @Test func theStagePageAndPromptsAreBundled() {
        let bundle = Bundle(for: CompanionManager.self)
        for path in ["index.html", "stage.js", "stage-worker.js", "figure3d.js", "vendor/three/three.module.min.js",
                     "prompt/drawing-language.md", "prompt/follow-up.md", "prompt/question.md", "samples/rocket.js"] {
            #expect(bundle.url(forResource: path, withExtension: nil, subdirectory: "draw-stage") != nil, "\(path)")
        }
    }
    @Test func theWorkerFencesNestedWorkersAndBlobs() throws {
        let url = try #require(Bundle(for: CompanionManager.self).url(forResource: "stage-worker.js", withExtension: nil, subdirectory: "draw-stage"))
        let source = try String(contentsOf: url, encoding: .utf8)
        for name in ["'Worker'", "'SharedWorker'", "'Blob'", "URL.createObjectURL"] { #expect(source.contains(name)) }
    }
}
```

- [ ] **Step 11: Run the test to verify it fails** (before the build phase lands the folder) — expected: `theStagePageAndPromptsAreBundled` fails with nil URLs.

- [ ] **Step 12: Build and run the test to verify it passes** — `build-for-testing` then `-only-testing:OpenClickyTests/DrawStageBundleTests`. Expected: 2 passed. Also check `ls "$(xcodebuild -showBuildSettings … | grep -m1 ' BUILT_PRODUCTS_DIR')"/OpenClicky.app/Contents/Resources/draw-stage` lists the files.

- [ ] **Step 13: Commit** — "Share the drawing stage: draw-stage/ from Windows PR #39, bundled into the Mac app" with the credit line "The stage page, drawing language and samples are by darkworks (PR #39)."

---

### Task 2: `DrawStageMessages.swift`, the protocol in Swift

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/DrawStageMessages.swift`
- Test: `macos/OpenClicky/OpenClickyTests/DrawStage/DrawStageMessagesTests.swift`

**Interfaces:**
- Produces:
  ```swift
  enum DrawStagePace: String { case estimate, speech }
  struct DrawStageBounds: Equatable { let x, y, width, height, pageWidth, pageHeight: Double; var isEmpty: Bool }
  struct DrawStagePhoto { let imageData: Data; let mediaType: String; let credit: String }
  /// A message from the page. `k` carries "k" or "lines"; `rects` carries "figures".
  struct DrawStageEvent: Equatable { let type: String; let k: Int?; let text: String?; let id: String?; let query: String?;
      let bounds: DrawStageBounds?; let rects: [CGRect]? }
  enum DrawStageMessages {
      static func beginStream(canvasWidth: Int, canvasHeight: Int, pace: DrawStagePace) -> String
      static func continueStream(pace: DrawStagePace) -> String
      static func feed(programSoFar: String, done: Bool) -> String
      static func line(_ type: String, k: Int) -> String        // "lineStarted" / "lineEnded"
      static func simple(_ type: String) -> String              // "finishNow", "clear", "measure", "sleep", "wake"
      static func photo(id: String, photo: DrawStagePhoto?) -> String
      static func parseEvent(_ json: String) -> DrawStageEvent?
  }
  ```
  Every builder returns compact JSON (`JSONSerialization`, sorted keys off). `parseEvent` returns nil for anything that is not a JSON object with a string `type`; numbers that are not finite become 0; `figures` rects are `[{x,y,w,h}]` in page pixels.

- [ ] **Step 1: Write the failing tests**

```swift
@Suite struct DrawStageMessagesTests {
    @Test func beginStreamCarriesTheCanvasAndPace() throws {
        let json = DrawStageMessages.beginStream(canvasWidth: 1280, canvasHeight: 720, pace: .speech)
        let object = try #require(try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
        #expect(object["type"] as? String == "beginStream"); #expect(object["w"] as? Int == 1280); #expect(object["pace"] as? String == "speech")
    }
    @Test func feedAndLineAndSimpleRoundTrip() { /* feed(program,done:true) has code+done; line("lineStarted", k: 3) has k 3; simple("sleep") has only type */ }
    @Test func photoWithoutAnImageSaysFailed() { /* photo(id:"p1", photo:nil) → {"type":"photo","id":"p1","failed":true}; with a PNG → data: "data:image/png;base64,…" and credit */ }
    @Test func parsesEveryPageEventShape() {
        #expect(DrawStageMessages.parseEvent(#"{"type":"line","k":2,"text":"hi"}"#) == DrawStageEvent(type: "line", k: 2, text: "hi", id: nil, query: nil, bounds: nil, rects: nil))
        #expect(DrawStageMessages.parseEvent(#"{"type":"programRan","lines":5}"#)?.k == 5)
        #expect(DrawStageMessages.parseEvent(#"{"type":"bounds","x":10,"y":20,"w":300,"h":200,"pageWidth":1440,"pageHeight":900}"#)?.bounds == DrawStageBounds(x: 10, y: 20, width: 300, height: 200, pageWidth: 1440, pageHeight: 900))
        #expect(DrawStageMessages.parseEvent(#"{"type":"figures","rects":[{"x":1,"y":2,"w":3,"h":4}]}"#)?.rects == [CGRect(x: 1, y: 2, width: 3, height: 4)])
        #expect(DrawStageMessages.parseEvent(#"{"type":"photoWanted","id":"p1","query":"heart","text":"a heart"}"#)?.query == "heart")
    }
    @Test func unreadablePageMessagesAreIgnored() {
        for json in ["", "[]", "{}", #"{"type":3}"#, "not json"] { #expect(DrawStageMessages.parseEvent(json) == nil) }
    }
}
```

- [ ] **Step 2: Run to verify they fail** — expected: compile errors (types missing).
- [ ] **Step 3: Implement `DrawStageMessages.swift`** per the Interfaces block.
- [ ] **Step 4: Run `-only-testing:OpenClickyTests/DrawStageMessagesTests`** — expected: 5 passed.
- [ ] **Step 5: Commit** — "Draw stage: the page protocol in Swift".

---

### Task 3: `DrawStageLesson.swift`, the gate, the prompts and the reply reader

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/DrawStageLesson.swift`
- Test: `macos/OpenClicky/OpenClickyTests/DrawStage/DrawStageLessonTests.swift`

**Interfaces:**
- Consumes: the bundled `draw-stage/prompt/*.md` (Task 1).
- Produces:
  ```swift
  enum DrawStageLesson {
      static let holdLine = "One moment, let me draw that for you."
      static let outputTokens = 8192
      static let notAFollowUp = "NONE"
      static func wantsADrawing(_ question: String) -> Bool
      static func mayBeAFollowUp(_ question: String) -> Bool
      /// nil when the bundled prompt is missing (the feature turns itself off and logs).
      static func systemPrompt(canvasWidth: Int, canvasHeight: Int) -> String?
      static func questionPrompt(_ question: String) -> String?
      static func followUpPrompt(question: String, drawing: DrawnAnswer) -> String?
      static func programIn(_ replySoFar: String) -> String
      /// true = starts with keep(), false = anything else, nil = can't tell yet (done == false).
      static func keepsTheDrawing(_ programSoFar: String, done: Bool) -> Bool?
      enum ReplyDecision: Equatable { case undecided, notADrawing, program }
      /// NONE or prose as the first non-blank line → .notADrawing; a line that starts like code or a ``` fence → .program.
      static func decide(replySoFar: String, done: Bool) -> ReplyDecision
  }
  struct DrawnAnswer: Equatable { let questions: [String]; let program: String; let canvasWidth: Int; let canvasHeight: Int; let spokenLines: [String] }
  ```
  Placeholder values for "detailed": beats "3 to 6", calls 70, parts "15-40 parts, with the small things that make it recognisable", partsAtMost 50. `followUpPrompt` fills `{{asked}}` with the questions joined by `, then ` each in quotes, `{{nextStep}}` with the highest `step(n)` + 1, `{{marked}}` with "".

- [ ] **Step 1: Write the failing tests**

```swift
@Suite struct DrawStageLessonTests {
    @Test(arguments: ["how does a jet engine work?", "why is the sky blue", "what happens when ice melts", "what's inside this?", "draw a heart for me",
                      "show me how a rocket flies", "teach me how pythagoras works", "explain how a transistor works", "how does this work"])
    func curiousQuestionsPassTheGate(question: String) { #expect(DrawStageLesson.wantsADrawing(question)) }

    @Test(arguments: ["why is my build failing?", "what time is it", "how do I export this", "how does this app handle login?", "explain this error",
                      "what happens if I click save?", "how are you", "why not", "open spotify", "how does my code reach the server", "why is this slow",
                      "what's the difference between tcp and udp", "python vs rust", "hi"])
    func simpleAndPersonalQuestionsStayOut(question: String) { #expect(!DrawStageLesson.wantsADrawing(question)) }

    @Test func followUpsAreWiderButNeverActions() {
        #expect(DrawStageLesson.mayBeAFollowUp("now show it in 3d")); #expect(DrawStageLesson.mayBeAFollowUp("what does the turbine do?"))
        #expect(!DrawStageLesson.mayBeAFollowUp("open spotify")); #expect(!DrawStageLesson.mayBeAFollowUp("thanks"))
    }
    @Test func programInReadsAFenceOrBareCode() {
        #expect(DrawStageLesson.programIn("```js\nstep(1); say(\"hi\")\n```") == "step(1); say(\"hi\")\n")
        #expect(DrawStageLesson.programIn("```js\nstep(1)\n``") == "step(1)\n")          // a closing fence arriving
        #expect(DrawStageLesson.programIn("step(1); say(\"hi\")") == "step(1); say(\"hi\")")
        #expect(DrawStageLesson.programIn("Sure! Here is") == "")
    }
    @Test func keepsTheDrawingWaitsForTheFirstStatement() {
        #expect(DrawStageLesson.keepsTheDrawing("", done: false) == nil); #expect(DrawStageLesson.keepsTheDrawing("ke", done: false) == nil)
        #expect(DrawStageLesson.keepsTheDrawing("keep()\nstep(4)", done: false) == true); #expect(DrawStageLesson.keepsTheDrawing("step(1)", done: false) == false)
        #expect(DrawStageLesson.keepsTheDrawing("// a comment\n", done: true) == false)
    }
    @Test func decidesNoneProseOrProgram() {
        #expect(DrawStageLesson.decide(replySoFar: "", done: false) == .undecided)
        #expect(DrawStageLesson.decide(replySoFar: "NO", done: false) == .undecided)
        #expect(DrawStageLesson.decide(replySoFar: "NONE", done: false) == .notADrawing)
        #expect(DrawStageLesson.decide(replySoFar: "It is 3 pm.\n", done: false) == .notADrawing)
        #expect(DrawStageLesson.decide(replySoFar: "```js\nstep(1)", done: false) == .program)
        #expect(DrawStageLesson.decide(replySoFar: "step(1); say(\"a\")", done: false) == .program)
        #expect(DrawStageLesson.decide(replySoFar: "Sure", done: true) == .notADrawing)
    }
    @Test func promptsComeFromTheBundleWithPlaceholdersFilled() throws {
        let system = try #require(DrawStageLesson.systemPrompt(canvasWidth: 1280, canvasHeight: 800))
        #expect(system.contains("1280")); #expect(!system.contains("{{"))
        #expect(try #require(DrawStageLesson.questionPrompt("how does a heart work?")).contains("NONE"))
        let drawing = DrawnAnswer(questions: ["how does a heart work?"], program: "step(1); say(\"a\")\nstep(2); say(\"b\")", canvasWidth: 1280, canvasHeight: 800, spokenLines: ["a", "b"])
        let followUp = try #require(DrawStageLesson.followUpPrompt(question: "what does the valve do?", drawing: drawing))
        #expect(followUp.contains("step(3)")); #expect(followUp.contains("\"how does a heart work?\"")); #expect(!followUp.contains("{{"))
    }
}
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement `DrawStageLesson.swift`**: port `Words`/`ContainsAny`/`StartsAny` word matching from the C#; the gate per the spec's three bullet lists (out-words, trouble/work words, first-person; in-phrases including the teaching phrases); prompts read once from `Bundle.main` (`draw-stage/prompt/<name>.md`) and cached; `decide` reads the first non-blank line: `NONE` → `.notADrawing`; a ``` fence or a line starting `step(`, `const `, `keep(`, `say(`, `//` → `.program`; another complete line (ends with `\n`, or `done`) → `.notADrawing`; else `.undecided`.
- [ ] **Step 4: Run `-only-testing:OpenClickyTests/DrawStageLessonTests`** — expected: all passed.
- [ ] **Step 5: Commit** — "Draw stage: which questions get a drawing, the prompts, and reading the reply".

---

### Task 4: `SpeechQueue` per-piece hooks

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/SpeechQueue.swift:47-98`
- Test: `macos/OpenClicky/OpenClickyTests/SpeechQueueTests.swift` (add to the existing suite)

**Interfaces:**
- Produces: `func enqueue(_ pieceText: String, tag: Int? = nil)` and `func run(onFirstAudio: () -> Void, onPieceStarted: ((Int) -> Void)? = nil, onPieceEnded: ((Int) -> Void)? = nil) async throws`. The hooks receive the piece's **tag** (its index when none was given). `onPieceStarted` fires just before `playAudio` for that piece (after `onFirstAudio` for the first), `onPieceEnded` right after `playAudio` returns normally. Existing callers are unchanged.

- [ ] **Step 1: Write the failing test** (in `SpeechQueueTests`):

```swift
@Test func pieceHooksFireInOrderWithTheirTags() async throws {
    var events: [String] = []
    let queue = SpeechQueue(fetchAudio: { Data($0.utf8) }, playAudio: { data in events.append("play \(String(decoding: data, as: UTF8.self))") })
    queue.enqueue("a", tag: 10); queue.enqueue("b", tag: 11); queue.finish()
    try await queue.run(onFirstAudio: { events.append("first") }, onPieceStarted: { events.append("start \($0)") }, onPieceEnded: { events.append("end \($0)") })
    #expect(events == ["first", "start 10", "play a", "end 10", "start 11", "play b", "end 11"])
}
@Test func untaggedPiecesReportTheirIndex() async throws { /* enqueue "a","b" without tags → start 0, end 0, start 1, end 1 */ }
```

- [ ] **Step 2: Run `-only-testing:OpenClickyTests/SpeechQueueTests`** — expected: the new tests fail to compile.
- [ ] **Step 3: Implement**: store `pieceTags: [Int]` beside `pieceTexts`; call the hooks around `playAudio` in `run`.
- [ ] **Step 4: Run the suite** — expected: all passed, including the old tests.
- [ ] **Step 5: Commit** — "SpeechQueue: tell the caller when each piece starts and ends".

---

### Task 5: `DrawStageController.swift`, the stage behind a surface

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/DrawStageController.swift`
- Test: `macos/OpenClicky/OpenClickyTests/DrawStage/DrawStageControllerTests.swift`

**Interfaces:**
- Consumes: Task 2.
- Produces:
  ```swift
  /// What the window gives the controller: the page, and showing/hiding it on a screen.
  @MainActor protocol DrawStageSurface: AnyObject {
      func post(_ json: String)                       // app → page
      func show(on displayFrame: CGRect)              // covers that screen, wakes the page
      func hide()                                     // sleep + orderOut
      func snapshot(of bounds: DrawStageBounds) async -> NSImage?
      var isShowing: Bool { get }
  }
  @MainActor final class DrawStageController {
      static let shared = DrawStageController()
      var surface: DrawStageSurface?                  // set by the window; nil in tests until a fake is given
      private(set) var isReady: Bool
      private(set) var isShowing: Bool
      // Events, set by DrawingAnswer for one answer and cleared after:
      var lineAdded: ((Int, String) -> Void)?
      var programRan: ((Int) -> Void)?
      var finished: (() -> Void)?
      var logged: ((String) -> Void)?
      var figurePartClicked: ((String) -> Void)?
      var figuresChanged: (([CGRect]) -> Void)?       // page pixels, for the window's hit test
      var findPhoto: ((String, String) async -> DrawStagePhoto?)?   // query, caption
      func prepare()                                  // asks the surface to load the page (no-op when loaded)
      func begin(displayFrame: CGRect, canvasWidth: Int, canvasHeight: Int)     // show + beginStream(speech)
      func continueDrawing()                                                      // continueStream(speech)
      func feed(_ programSoFar: String, done: Bool)
      func lineStarted(_ k: Int); func lineEnded(_ k: Int)
      func finishNow(); func clear(reason: String); func hide(reason: String)
      func measure() async -> DrawStageBounds?        // sends measure, awaits bounds (nil after 2 s)
      func receive(_ json: String)                    // the surface hands page messages here
      func displaysChanged()                          // clear + hide, logged
  }
  ```
  Messages sent before `ready` are queued and flushed on `ready` (the Windows `waiting` list). `receive` dispatches exactly the Windows `switch`: ready, line, programRan, figurePart, bounds, photoWanted (calls `findPhoto` in a Task and posts `photo`), finished, error (→ `logged("error: …")`), log, figures.

- [ ] **Step 1: Write the failing tests** with a `FakeSurface: DrawStageSurface` that records posted JSON and show/hide calls:

```swift
@Test func messagesWaitForReady() { controller.begin(…); #expect(fake.posted.isEmpty); controller.receive(#"{"type":"ready"}"#); #expect(fake.posted.first!.contains("beginStream")) }
@Test func pageEventsReachTheirHandlers() { /* line → lineAdded(2,"hi"); programRan lines 5 → programRan(5); finished; error → logged starts with "error: "; figures → figuresChanged([rect]) */ }
@Test func aPhotoRequestIsAnsweredWithTheFinderResult() async { /* findPhoto returns a photo → a "photo" message with "data:image/png;base64" is posted; nil → "failed":true */ }
@Test func measureAnswersWithBounds() async { /* post measure, then receive bounds → returns the bounds */ }
@Test func clearHidesAndForgetsReadyState() { /* after clear: fake.hidden == true, a later begin waits for ready again only if the surface reloads (isReady stays true) */ }
@Test func aDisplayChangeHidesTheStage() { controller.begin(…); controller.displaysChanged(); #expect(fake.hideCount == 1); #expect(fake.posted.last!.contains("clear")) }
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement `DrawStageController.swift`** per the Interfaces block.
- [ ] **Step 4: Run `-only-testing:OpenClickyTests/DrawStageControllerTests`** — expected: all passed.
- [ ] **Step 5: Commit** — "Draw stage: the controller that queues for the page and routes its events".

---

### Task 6: `DrawingAnswer.swift`, one answer drawn while it is spoken

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/DrawingAnswer.swift`
- Test: `macos/OpenClicky/OpenClickyTests/DrawStage/DrawingAnswerTests.swift`

**Interfaces:**
- Consumes: Tasks 3, 4, 5.
- Produces:
  ```swift
  struct DrawingAnswerResult: Equatable {
      let drew: Bool; let spokenLines: [String]; let unspokenAfterVoiceFailure: [String]?; let program: String; let keptTheDrawing: Bool
  }
  enum DrawingAnswer {
      static let limitSeconds: TimeInterval = 180
      static let holdLineAfterSeconds: TimeInterval = 2
      static let readinessTimeoutSeconds: TimeInterval = 5
      /// `writeReply` streams the whole reply so far to its closure and returns the whole reply (the ClaudeAPI call).
      static func run(stage: DrawStageController, displayFrame: CGRect, canvasWidth: Int, canvasHeight: Int,
                      writeReply: @escaping (@escaping @MainActor (String) -> Void) async throws -> String,
                      speech: SpeechQueue, onFirstAudio: @escaping () -> Void, log: @escaping (String) -> Void,
                      holdLine: String?, followUp: DrawnAnswer?) async throws -> DrawingAnswerResult
  }
  ```
  Behaviour (the Windows `RunAsync`, plus the spec's additions): decide with `DrawStageLesson.decide` on every chunk; `.notADrawing` → cancel the reply task, cancel speech, `stage.clear`, return `drew: false` with nothing spoken; `.program` (first time) → `begin` (or `continueDrawing` when `keepsTheDrawing` is true for a follow-up), start the hold-line timer, feed as it grows; `lineAdded` → `speech.enqueue(text, tag: k)`; `onPieceStarted(k)` → `stage.lineStarted(k)`, `onPieceEnded(k)` → `stage.lineEnded(k)`; `programRan` → `speech.finish()`; await `finished` or playback end or the 3-minute limit; a `SpeechQueue.Failure` → `stage.finishNow()` and return the unspoken lines; readiness: if no `ready` 5 s after the first feed → treat as not drawn; cancellation rethrown after `stage.clear`. A hold line is enqueued with tag `-1` (not a stage line) and only while `spokenLines.isEmpty`.

- [ ] **Step 1: Write the failing tests** with a `FakeSurface` (Task 5's fake, moved to a shared test helper `DrawStageFakes.swift`) that auto-replies `ready`, and on each `feed … done: true` emits `line` events for every `say("…")` in the program, then `programRan` and, once all `lineEnded` arrived, `finished`; a `SpeechQueue` with `fetchAudio: { Data($0.utf8) }` and a `playAudio` that records and sleeps 10 ms:

```swift
@Test func aProgramIsDrawnAndSpokenLineByLine() async throws { /* reply "```js\nstep(1); say(\"a\")\nstep(2); say(\"b\")\n```" → drew true, spokenLines ["a","b"], posted contains lineStarted k 0, lineEnded k 0, lineStarted k 1 in that order, begin called once with the canvas */ }
@Test func noneFallsThroughBeforeAnythingIsSpoken() async throws { /* reply "NONE" → drew false, played == [], posted has no beginStream, clear sent */ }
@Test func proseFallsThroughToo() async throws { /* "It is 3 pm.\n" → drew false */ }
@Test func theHoldLineIsSaidOnlyAfterTheProgramStartsAndTheFirstLineIsSlow() async throws { /* a reply whose first say() arrives after 2.5 s; holdLine given → played.first == holdLine; with a fast first line → holdLine never played */ }
@Test func aStageThatNeverReportsReadyFallsThroughAfterTheTimeout() async throws { /* fake never sends ready; DrawingAnswer.readinessTimeout overridable for the test via an internal static var set to 0.2 → drew false within 1 s */ }
@Test func aVoiceFailureFinishesTheDrawingAndHandsBackTheRest() async throws { /* playAudio throws on piece 1 → finishNow posted, unspokenAfterVoiceFailure == ["b"] */ }
@Test func aFollowUpThatKeepsContinuesTheDrawing() async throws { /* followUp given, reply "keep()\nstep(3); say(\"c\")" → continueStream posted, no beginStream, keptTheDrawing true */ }
@Test func aCutOffProgramStillFinishes() async throws { /* reply ends mid-statement "step(2); say(\"b\")\nrect(10," with done → drew true, spokenLines ["a","b"], feed done:true sent */ }
@Test func cancellingBeforeTheFirstLineClearsTheStage() async throws { /* writeReply sleeps; the Task running run() is cancelled after 50 ms → throws CancellationError, played == [], clear posted */ }
@Test func aPageErrorIsLoggedNotFatal() async throws { /* fake emits {"type":"error","text":"x is not defined"} mid-program → drew true, log received "page: error: x is not defined" */ }
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement `DrawingAnswer.swift`** per the behaviour above (one `@MainActor` function; events hooked on the controller for the run's duration and cleared in a `defer`).
- [ ] **Step 4: Run `-only-testing:OpenClickyTests/DrawingAnswerTests`** — expected: 10 passed.
- [ ] **Step 5: Commit** — "Draw stage: an answer drawn on the stage while its lines are spoken".

---

### Task 7: Bigger answers for drawings: `ClaudeAPI`, the providers, the backend

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/ClaudeAPI.swift:179-240, 312-336`
- Modify: `macos/OpenClicky/OpenClicky/ClaudeCodeAnswerProvider.swift:38-50` (timeout from the request)
- Modify: `macos/OpenClicky/OpenClicky/AnswerProvider.swift:107` (add `var purpose: AnswerPurpose = .answer`, `var timeoutSeconds: TimeInterval?`)
- Modify: `backend/src/anthropicGrant.ts:9,71-72` and its caller for `/chat` (pass the request header through)
- Test: `macos/OpenClicky/OpenClickyTests/ClaudeAPIRequestTests.swift` (new), `backend/src/anthropicGrant.test.ts` (add)

**Interfaces:**
- Produces:
  ```swift
  enum AnswerPurpose: String { case answer, drawing }
  struct AnswerRequestOptions { var maxTokens: Int = 1024; var purpose: AnswerPurpose = .answer; var thinkingOff: Bool = false }
  // ClaudeAPI.analyzeImageStreaming gains `options: AnswerRequestOptions = .init()` after `tools`.
  // Pure, for the test:
  static func anthropicRequestBody(model: String, systemPrompt: String, messages: [[String: Any]], tools: [[String: Any]], options: AnswerRequestOptions) -> [String: Any]
  static func extraHeaders(for options: AnswerRequestOptions) -> [String: String]   // ["X-OpenClicky-Purpose": "drawing"] for a drawing
  ```
  `thinkingOff` adds `"thinking": ["type": "between_tools"]` only when the model name starts with `claude-sonnet-5` or `claude-opus-5` (the models that think by default; Haiku rejects it). A drawing request on the Claude Code plan uses `timeoutSeconds: 180`. Backend: `MAX_TOKENS_CAP` stays 1024; a new `DRAWING_TOKENS_CAP = 8192` applies when `req.headers["x-openclicky-purpose"] === "drawing"`.

- [ ] **Step 1: Write the failing tests**

```swift
@Suite struct ClaudeAPIRequestTests {
    @Test func aDrawingRequestAsksForMoreTokensAndNoThinking() {
        let body = ClaudeAPI.anthropicRequestBody(model: "claude-sonnet-5-5", systemPrompt: "s", messages: [], tools: [], options: .init(maxTokens: 8192, purpose: .drawing, thinkingOff: true))
        #expect(body["max_tokens"] as? Int == 8192); #expect((body["thinking"] as? [String: String])?["type"] == "between_tools"); #expect(body["tools"] == nil)
        #expect(ClaudeAPI.extraHeaders(for: .init(purpose: .drawing)) == ["X-OpenClicky-Purpose": "drawing"])
    }
    @Test func anOrdinaryRequestIsUnchanged() { /* default options → max_tokens 1024, no thinking key, no extra headers */ }
    @Test func haikuNeverGetsTheThinkingKey() { /* model "claude-haiku-4-5", thinkingOff true → no "thinking" */ }
}
```
Backend (`anthropicGrant.test.ts`, matching its existing style): `prepareGrantRequest` with `max_tokens: 8192` and no header → 1024; with header `x-openclicky-purpose: drawing` → 8192; with the header and `max_tokens: 20000` → 8192.

- [ ] **Step 2: Run both to verify they fail.**
- [ ] **Step 3: Implement**: extract the body building in `ClaudeAPI` into `anthropicRequestBody`; thread `options` through `answerThroughProvider` into `AnswerRequest.maxTokens`/`purpose`/`timeoutSeconds`; `OpenAIChatAnswerProvider` and `GeminiAnswerProvider` already read `maxTokens`; `ClaudeCodeAnswerProvider` uses `answerRequest.timeoutSeconds ?? timeoutSeconds`; the backend reads the header in the `/chat` route and passes a `purpose` into `prepareGrantRequest` (or whatever the function at `anthropicGrant.ts:71` is named).
- [ ] **Step 4: Run** `-only-testing:OpenClickyTests/ClaudeAPIRequestTests`, `-only-testing:OpenClickyTests/AnswerProviderHTTPTests`, `-only-testing:OpenClickyTests/AnswerProviderPlanTests` and `npm test -w backend` — expected: all passed.
- [ ] **Step 5: Commit** — "Answers: a drawing may use 8,192 tokens, no thinking, and says so to the backend".

---

### Task 8: `DrawStageWindow.swift`, the WKWebView stage, and the smoke command

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/DrawStageWindow.swift`
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/DrawStageSmoke.swift`
- Modify: `macos/OpenClicky/OpenClicky/OpenClickyApp.swift:366` (register `--openclicky-drawstage-smoke` before the teacher smoke)
- Test: `macos/OpenClicky/OpenClickyTests/DrawStage/DrawStageWindowTests.swift`

**Interfaces:**
- Consumes: Task 5's `DrawStageSurface`.
- Produces:
  ```swift
  enum DrawStageScheme {
      static let scheme = "openclicky-stage"; static let host = "page"
      static let pageURL = URL(string: "openclicky-stage://page/index.html")!
      /// The bundled file for a request path, or nil when the path escapes the folder or the file is missing.
      static func fileURL(forRequestPath path: String, in folder: URL) -> URL?
      static func mediaType(forExtension ext: String) -> String   // html, js (text/javascript), css, json, png, jpg, svg, md (text/plain)
  }
  @MainActor final class DrawStageWindow: NSObject, DrawStageSurface, WKScriptMessageHandler, WKNavigationDelegate, WKURLSchemeHandler {
      static let shared = DrawStageWindow(controller: .shared)
      init(controller: DrawStageController)
      func load()                                   // creates the panel + web view once, navigates to pageURL
      /// Takes the mouse only over `rects` (page px → screen pt using the panel frame and the page size); called from figuresChanged.
      func takesMouse(over rects: [CGRect], pageSize: CGSize)
  }
  ```
  Panel per the Global Constraints; `webView.setValue(false, forKey: "drawsBackground")`, `underPageBackgroundColor = .clear`; `configuration.setURLSchemeHandler(self, forURLScheme: scheme)`; `userContentController.add(self, name: "stage")`; `decidePolicyFor navigationAction` cancels anything not on the scheme; `webView.isInspectable = ProcessInfo.processInfo.environment["OPENCLICKY_DRAWSTAGE_DEVTOOLS"] == "1"`. A global `NSEvent.addGlobalMonitorForEvents(matching: .mouseMoved)` installed only while rects are non-empty toggles `ignoresMouseEvents`. `hide()` posts `sleep` then `orderOut(nil)`; `show(on:)` sets the frame, `orderFrontRegardless()`, posts `wake`. `snapshot(of:)` uses `webView.takeSnapshot(with: WKSnapshotConfiguration(rect:))`. Display changes: observe `NSApplication.didChangeScreenParametersNotification` → `controller.displaysChanged()`.

- [ ] **Step 1: Spike (30 minutes, throwaway): does WebKit run `new Worker('stage-worker.js')` and `<script type="module" src="figure3d.js">` from the custom scheme?** Load the page in a plain window and read `window.drawStage` + a worker round trip from the console. If either fails, switch `pageURL` to `loadFileURL(index, allowingReadAccessTo: folder)` and note it in the file header; the navigation delegate stays the fence. Record the outcome in the commit message.

- [ ] **Step 2: Write the failing tests** (pure parts only):

```swift
@Suite struct DrawStageWindowTests {
    @Test func requestPathsStayInsideTheFolder() {
        let folder = URL(fileURLWithPath: "/tmp/stage")
        #expect(DrawStageScheme.fileURL(forRequestPath: "/index.html", in: folder)?.path == "/tmp/stage/index.html")
        #expect(DrawStageScheme.fileURL(forRequestPath: "/vendor/three/three.module.min.js", in: folder)?.path == "/tmp/stage/vendor/three/three.module.min.js")
        for escape in ["/../shell.json", "/vendor/../../x", "//etc/passwd", "/%2e%2e/x"] { #expect(DrawStageScheme.fileURL(forRequestPath: escape, in: folder) == nil, escape) }
    }
    @Test func mediaTypesForTheStageFiles() { #expect(DrawStageScheme.mediaType(forExtension: "js") == "text/javascript"); #expect(DrawStageScheme.mediaType(forExtension: "html") == "text/html") }
    @Test func pageRectsBecomeScreenPointsForTheHitTest() { /* DrawStageWindow.screenRects(from: [CGRect(x:100,y:50,width:200,height:100)], pageSize: CGSize(1440,900), panelFrame: CGRect(0,0,1440,900)) == [CGRect(x:100, y:750, width:200, height:100)] (AppKit y flipped) */ }
}
```
(`fileURL` resolves `..` and percent-escapes by standardising the joined URL and requiring the folder as a prefix; existence is not required in the test, so take a `requireExists: Bool = true` parameter the test passes as false.)

- [ ] **Step 3: Run to verify they fail.**
- [ ] **Step 4: Implement `DrawStageWindow.swift`** per the Interfaces block, and `DrawStageSmoke.swift` with `static func run(arguments: [String]) async` for `--openclicky-drawstage-smoke [program.js | reply:<file> | "lesson:<question>"] [--voice]`: no argument → plays `samples/rocket.js` then `samples/printer-3d.js` with pace `estimate` (post `beginStream` with pace estimate and `feed … done: true`, wait for `finished` + 4 s); `program.js` → that file the same way; `reply:` → `DrawingAnswer.run` with the file as the reply, a `SpeechQueue` on `MacVoice` when `--voice` else a silent queue that sleeps 0.4 s per piece; `lesson:` → the person's answer model through `ClaudeAPI.analyzeImageStreaming` with the drawing prompt on a cursor-screen capture. Print `smoke: …` lines and `exit(0)` on finish, `exit(1)` on a fall-through.
- [ ] **Step 5: Run `-only-testing:OpenClickyTests/DrawStageWindowTests`** — expected: 3 passed.
- [ ] **Step 6: Run the smoke** — build the app (`xcodebuild … build`), then `"<built app>/Contents/MacOS/OpenClicky" --openclicky-drawstage-smoke`. Expected: the rocket draws on the cursor screen over the desktop with a transparent background, then the 3D printer turns; `smoke: finished` printed; the process exits 0. Take a screenshot with `screencapture -x /tmp/drawstage-smoke.png` from a second shell 8 s in and view it. Then run `--openclicky-drawstage-smoke reply:draw-stage/samples/heart-reply.md --voice` and confirm the Mac voice speaks each line while its beat draws.
- [ ] **Step 7: Check sleep** — while the smoke's drawing is up, run `top -l 2 -pid <pid>` after `hide` (add a `--hide-after-finish` smoke flag that hides 2 s after finished and stays alive 10 s): CPU must be under 2 %.
- [ ] **Step 8: Commit** — "Draw stage: the transparent WKWebView stage window and its smoke command".

---

### Task 9: The bubble

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/DrawStageBubble.swift`
- Modify: `macos/OpenClicky/OpenClicky/NotchHUD.swift:729-800` (add `func islandScreenFrame() -> (frame: CGRect, placement: HUDPlacement, screen: NSScreen)?` on `NotchHUDManager`, from the primary instance's model)
- Test: `macos/OpenClicky/OpenClickyTests/DrawStage/DrawStageBubbleTests.swift`

**Interfaces:**
- Produces:
  ```swift
  enum DrawStageBubblePlacement {
      static let width: CGFloat = 96
      /// Where the bubble goes for the island's placement; screen is the one the drawing was on.
      static func frame(islandFrame: CGRect?, placement: HUDPlacement, screenVisibleFrame: CGRect, thumbnailAspect: CGFloat) -> CGRect
  }
  @MainActor final class DrawStageBubble {
      static let shared = DrawStageBubble()
      var onRestore: (() -> Void)?; var onDiscard: (() -> Void)?
      func show(thumbnail: NSImage, islandFrame: CGRect?, placement: HUDPlacement, screen: NSScreen)
      func dismiss()
      var isShowing: Bool { get }
  }
  ```
  Placement: height = width / aspect clamped to 54…120; `.left` → x = island.maxX + 12, y aligned with island.maxY; `.right` → x = island.minX − 12 − width; `.notch` → x = island.maxX + 12, y = island.minY − 8 − height; `.hidden` or no island → top-right of the visible frame inset 16. The panel: non-activating, level `.popUpMenu`, SwiftUI view with the thumbnail on paper, 14 pt corners, a shadow, ✕ on hover at the top-right (`.pointerCursor()`), a click anywhere else calls `onRestore`.

- [ ] **Step 1: Write the failing tests**

```swift
@Suite struct DrawStageBubbleTests {
    let screen = CGRect(x: 0, y: 0, width: 1440, height: 875)
    @Test func besideASideDrawer() {
        let island = CGRect(x: 16, y: 500, width: 360, height: 200)
        let frame = DrawStageBubblePlacement.frame(islandFrame: island, placement: .left, screenVisibleFrame: screen, thumbnailAspect: 1.5)
        #expect(frame.minX == 388); #expect(frame.maxY == 700); #expect(frame.width == 96); #expect(frame.height == 64)
    }
    @Test func underTheNotch() { /* placement .notch, island (540, 800, 360, 75) → x 912, maxY 792 */ }
    @Test func topRightWhenHidden() { /* .hidden, nil island → maxX 1424, maxY 859 */ }
    @Test func heightIsClamped() { /* aspect 0.3 → height 120; aspect 4 → 54 */ }
}
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** `DrawStageBubble.swift` and the `NotchHUDManager.islandScreenFrame()` accessor.
- [ ] **Step 4: Run `-only-testing:OpenClickyTests/DrawStageBubbleTests`** — expected: 4 passed.
- [ ] **Step 5: Commit** — "Draw stage: a finished drawing shrinks into a bubble beside the island".

---

### Task 10: The ask lane draws: `CompanionManager+DrawStage.swift`

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/CompanionManager+DrawStage.swift`
- Create: `macos/OpenClicky/OpenClicky/Plugins/DrawStage/DrawStageTurn.swift` (pure decisions)
- Modify: `macos/OpenClicky/OpenClicky/CompanionManager.swift:1826` (before `TeacherPlugin.startLesson`), `:663-675` (`interruptCurrentAnswer`), `:1421-1443` (esc), `:1026/1454` (prepare at press), `:375` (hold the bubble/stage wiring in `start`)
- Modify: `macos/OpenClicky/OpenClicky/AskLaneControl.swift:17-50` (`EscapeTarget.drawing`, `target(... drawingIsPlaying: Bool = false, drawingIsOpen: Bool = false)`)
- Modify: `macos/OpenClicky/OpenClicky/Dictation/DictationSettings.swift` (`drawToExplain`, key `openclicky.ask.drawToExplain`, default true)
- Test: `macos/OpenClicky/OpenClickyTests/DrawStage/DrawStageTurnTests.swift`, `AskLaneControlTests.swift` (add)

**Interfaces:**
- Consumes: Tasks 3, 5, 6, 7, 9.
- Produces:
  ```swift
  enum DrawStageTurn {
      enum StageState: Equatable { case none, playing, open, bubble }
      enum Decision: Equatable { case notADrawingQuestion, newDrawing, interruptThenFollowUp, followUp, toBubbleThenOrdinary }
      static func decide(question: String, stageState: StageState, drawToExplain: Bool, isGuidedStepContinuation: Bool) -> Decision
  }
  extension CompanionManager {
      /// True when the question was answered by a drawing (spoken and drawn); false when the caller must answer it the ordinary way.
      func tryDrawingAnswer(transcript: String, cursorCapture: CompanionScreenCapture, decision: DrawStageTurn.Decision) async throws -> Bool
      func minimiseDrawingToBubble(reason: String) async
      func discardDrawing(reason: String)
  }
  ```
  `decide`: `drawToExplain` false or a guided continuation → `.notADrawingQuestion`; state `.playing` → `.interruptThenFollowUp` if `mayBeAFollowUp` else `.toBubbleThenOrdinary`; state `.open` → `.followUp` if `mayBeAFollowUp` else `.toBubbleThenOrdinary`; otherwise `wantsADrawing` → `.newDrawing` else `.notADrawingQuestion`.
  In `sendTranscriptToClaudeWithScreenshot`, after `screenCaptures` and the agent gate, before `TeacherPlugin.startLesson`: compute the decision; for `.toBubbleThenOrdinary` call `minimiseDrawingToBubble`; for `.newDrawing`/`.followUp`/`.interruptThenFollowUp` call `tryDrawingAnswer`; if it returns true, `rememberExchange(transcript, spokenLines joined)`, `await finishAskTurn()`, return; false → carry on unchanged (and skip `TeacherPlugin.startLesson` only when a drawing was attempted). `tryDrawingAnswer` builds the request through `claudeAPI.analyzeImageStreaming(images: [labelled cursor capture], systemPrompt: withReplyLanguage(DrawStageLesson.systemPrompt), userPrompt: question or follow-up prompt, tools: [], options: .init(maxTokens: 8192, purpose: .drawing, thinkingOff: true))`, a `SpeechQueue` from the answer voice session (as `makeAnswerSpeaker` does), `voiceState = .responding` on first audio, then `DrawingAnswer.run`; on `drew` it keeps `DrawnAnswer`, waits for the voice, snapshots and shows the bubble (`minimiseDrawingToBubble`); a 402 `BackendHTTPError` or any error before the first line → false. `interruptCurrentAnswer` cancels the drawing task and posts `finishNow`; esc with a drawing playing → `.drawing` target: stop voice, minimise; esc with only a bubble → `discardDrawing`. The bubble's `onRestore` shows the stage (state `.open`), `onDiscard` clears.

- [ ] **Step 1: Write the failing tests**

```swift
@Suite struct DrawStageTurnTests {
    @Test func aCuriousQuestionWithNothingUpIsANewDrawing() { #expect(DrawStageTurn.decide(question: "how does a jet engine work?", stageState: .none, drawToExplain: true, isGuidedStepContinuation: false) == .newDrawing) }
    @Test func theSettingOffNeverDraws() { #expect(DrawStageTurn.decide(question: "how does a jet engine work?", stageState: .none, drawToExplain: false, isGuidedStepContinuation: false) == .notADrawingQuestion) }
    @Test func aQuestionWhilePlayingInterruptsThenFollowsUp() { #expect(DrawStageTurn.decide(question: "what does the turbine do?", stageState: .playing, drawToExplain: true, isGuidedStepContinuation: false) == .interruptThenFollowUp) }
    @Test func anUnrelatedQuestionSendsTheDrawingToItsBubble() { #expect(DrawStageTurn.decide(question: "open spotify", stageState: .open, drawToExplain: true, isGuidedStepContinuation: false) == .toBubbleThenOrdinary) }
    @Test func aBubbleNeverGetsFollowUps() { #expect(DrawStageTurn.decide(question: "what does the turbine do?", stageState: .bubble, drawToExplain: true, isGuidedStepContinuation: false) == .notADrawingQuestion) }
    @Test func aGuideStepNeverDraws() { #expect(DrawStageTurn.decide(question: "how does this work", stageState: .none, drawToExplain: true, isGuidedStepContinuation: true) == .notADrawingQuestion) }
}
// AskLaneControlTests:
@Test func escapeStopsADrawingBeforeTheAskLane() { #expect(AskLaneEscape.target(dictationTakeInProgress: false, askLaneCanBeStopped: true, drawingIsPlaying: true) == .drawing) }
@Test func escapeWithOnlyABubbleClearsIt() { #expect(AskLaneEscape.target(dictationTakeInProgress: false, askLaneCanBeStopped: false, drawingIsOpen: true) == .drawing) }
@Test func drawToExplainIsOnByDefault() { let settings = DictationSettings(defaults: ScratchDefaults.make()); #expect(settings.drawToExplain) }
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** the three files and the CompanionManager edits per the Interfaces block. Wire in `start()`: `DrawStageController.shared.surface = DrawStageWindow.shared`, `findPhoto = { query, caption in await TeacherPictureFinder.find(query:, caption:).map { DrawStagePhoto(imageData: $0.imageData, mediaType: DrawStageMessages.mediaType(of: $0.imageData), credit: $0.credit) } }` (add `mediaType(of:)` to `DrawStageMessages` by the Windows first-bytes rule), `figurePartClicked = { part in submitTypedRequest("what does the \(part) do?") }`, bubble callbacks. `prepare()` at the talk press beside the local rung prewarm.
- [ ] **Step 4: Run** `-only-testing:OpenClickyTests/DrawStageTurnTests` and `-only-testing:OpenClickyTests/AskLaneControlTests` — expected: all passed.
- [ ] **Step 5: Live check with the Debug build** (needs a key in shell.json): `"<built app>/Contents/MacOS/OpenClicky" --openclicky-ask "how does a jet engine work?"` → app.log shows `draw: pre-filter passed`, `draw: program (N chars)`, the stage draws and the voice narrates, then the bubble appears; `--openclicky-ask "what time is it"` → `draw: pre-filter out`, an ordinary answer; `--openclicky-ask "why is the sky blue"` with a prose-minded model → `draw: model said NONE`, an ordinary answer, nothing drawn.
- [ ] **Step 6: Commit** — "Ask lane: questions a picture explains are drawn and narrated, then kept in a bubble".

---

### Task 11: The Settings row, docs, changelog, full test run

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/Dictation/UI/SettingsClickyPage.swift:16-40` (a `DrawToExplainRow` and its `SettingsItem` id `clicky.drawToExplain`), `macos/OpenClicky/OpenClicky/Dictation/UI/SettingsLayout.swift:51` (append to the first Clicky group)
- Modify: `macos/OpenClicky/CLAUDE.md` (Key Files rows for the five new `Plugins/DrawStage/` files; a line in the Teacher row), `docs/architecture.md` (one paragraph: the shared `draw-stage/`), `CHANGELOG.md` (an Unreleased entry under the Mac heading), `AGENTS.md` at the repo root (one line pointing at `draw-stage/README.md`)
- Test: `macos/OpenClicky/OpenClickyTests/SettingsLayoutTests.swift` or equivalent (the existing "every row has a place" check must pass)

- [ ] **Step 1: Add the row**: `DrawToExplainRow` (`SettingsRow` + `SettingsToggle(settings:keyPath: \.drawToExplain)`), title "Draw to explain", detail "When a picture or a 3D model explains it better, Clicky draws on your screen while it talks.", keywords `["draw", "drawing", "3d", "diagram", "explain", "teach", "stage", "picture"]`; place its id in `SettingsLayout` first Clicky group after `clicky.answerSource`.
- [ ] **Step 2: Write the docs and the changelog entry** (CHANGELOG Mac section: "Draw to explain: ask how something works and Clicky draws a narrated diagram or turning 3D model on your screen, then keeps it in a bubble by the island; on by default, Settings › Clicky › Draw to explain").
- [ ] **Step 3: Run the whole Mac suite** — `build-for-testing` then `test-without-building -only-testing:OpenClickyTests -skip-testing:OpenClickyTests/RealtimeAudioEngineConcurrencyTests`. Expected: 0 failures (a single `PermissionPromptTests` timing flake re-runs green). Run `swiftlint lint --quiet` from `macos/OpenClicky` — expected: no errors. Run `npm test -w backend` — expected: pass.
- [ ] **Step 4: Hand checks** (recorded in the final report, each as done / not done): a live jet engine and heart over real web pages; "what time is it" and "why is my build failing" produce no drawing and no extra wait; esc mid-drawing; the bubble on each island placement; Activity Monitor after minimising; a second display.
- [ ] **Step 5: Commit** — "Settings: Draw to explain; docs and changelog for the drawing stage".
