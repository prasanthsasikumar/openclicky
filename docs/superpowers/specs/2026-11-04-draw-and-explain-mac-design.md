# Draw and explain on the Mac

*2026-11-04. Decided with Prasanth in conversation; the "my call" notes are the author's choices, not his.*

## What it is

Hold ⌃⌥ and ask something a picture explains ("how does a jet engine work?", "what's inside this?" about a
thing on screen) and OpenClicky answers by **drawing while it talks**. A cream paper card appears over a calm
part of the screen; on it a figure of the real thing builds up part by part, each part growing in while the
sentence about it is spoken, with hand-written tags on leader lines and a slow turn so you see its shape.
When the explanation is done the drawing **shrinks into a bubble** beside the island; click the bubble and it
comes back. Ask again while it is up and the answer **builds on it**.

It is the Mac counterpart of the drawing stage in Windows PR #39 by darkworks, built so the two apps share
one stage page and one drawing language. The three sample drawings in that PR (a jet engine cut open, a
camera plugged into a board, the inside of a heart) are the look to hit.

## Why

OpenClicky answers in words and by pointing. Some things, a machine or an organ or how a thing fits
together, are far better shown than said, and no assistant on the Mac does this. HeyClicky ships it as
"draw and explain"; ours is written from scratch (the Windows author did the page; no HeyClicky code, prompts
or assets are used).

## Who it is for

Everyone. It is on by default on the ask lane, not tied to the Teacher plugin (which is off by default).
With Teacher on, lessons lean on it harder.

## Goals

- A question that genuinely needs a picture or a 3D view gets one, narrated in the voice the person already
  uses, with each beat drawn while its sentence is spoken.
- A question that doesn't, gets the ordinary spoken answer with **no added delay and nothing drawn**. This
  is the rule Prasanth cares most about.
- When the drawing and the voice are done, the drawing minimises to a bubble by the island; a click brings it
  back; a new drawing replaces it.
- A follow-up asked while the drawing is open adds to it, redraws it another way, or (if it isn't about it)
  is answered normally while the drawing goes to its bubble.
- The stage page, its drawing language, the vendored three.js and the samples live in **one folder shared by
  every platform**, like the skills folders, so Windows and Linux can pick them up unchanged.
- If anything fails (no program in the reply, the page doesn't start, the voice fails), the person gets the
  normal answer and never notices.

## Non-goals (v1)

- Windows and Linux: nothing in `windows/` or `linux/` changes. The Windows app keeps its own copy of the page
  until its maintainers switch it to the shared folder.
- Marking part of the screen with a marker, saving the drawing as a picture or video, 3D "pieces" from
  LLaMA-Mesh or Hunyuan3D, your-own-models (Ollama / LM Studio), the voice-key actions, the mouse-button
  talk, the assistant's name. These are separate Windows features, not part of drawing.
- The fast-voice (OpenAI Realtime) lane. v1 is the classic ask lane only; Realtime keeps its Teacher board.
  Known cost (the reviewer raised it): a person with an own OpenAI key and fast voice on uses the Realtime
  lane for ⌃⌥, so they see no drawings until the Realtime follow-up lands. It is the first follow-up.
- A "drawings come from" model picker and a detail-level setting. v1 draws with the person's answer model
  at the "detailed" level.

## Anti-goals (what would make this a failure even if it works)

- A drawing popping up for a simple question, or a card covering the thing the person asked about.
- A pause with nothing happening while the app decides, or a hold line spoken and then no drawing.
- The hidden stage quietly eating CPU or GPU for the rest of the session (the October idle-drain bug).
- Any way for a model-written program to reach the network or the file system.

## Constraints

- The Mac app has no web view today. This adds WebKit; the page runs in a `WKWebView`.
- `SpeechQueue` reports only the first audio start. Drawing needs to know when each line starts and ends.
- `ClaudeAPI.analyzeImageStreaming` and the backend's `/chat` cap answers at 1,024 tokens. A detailed
  drawing program needs up to 8,192.
- The app's own windows are already left out of screenshots (`CompanionScreenCaptureUtility` excludes every
  window of the bundle id), so the stage needs no special handling to stay out of the next question's canvas.
- Preserve the existing lanes: pointing, guided steps, quick actions, write-in-place, the local Apple-model
  rung, the Teacher board for Realtime.
- Esc must stop a drawing the way it stops anything else (the v0.9.2 rule: esc stops anything).

## The shared folder: `draw-stage/`

A new repo-root folder, a sibling of `skills/` and `app-skills/`:

```
draw-stage/
  README.md                 what it is, the message protocol, how to preview in a browser; credits darkworks
  index.html  stage.css  stage.js  stage-worker.js  figure3d.js
  vendor/three/             three.js r172 as published on npm (byte-identical), its LICENSE, RoomEnvironment.js
  samples/                  rocket.js, printer-3d.js, jet-engine-cutaway-3d.js, heart-blob-3d.js, board-3d.js,
                            rocket-engine-3d.js, heart-photo-3d.js, heart-reply.md, jet-engine-followup-reply.md
  prompt/
    drawing-language.md     the system prompt that teaches the language (from DrawStageLesson.SystemPromptText),
                            with {{W}} {{H}} {{beats}} {{calls}} {{parts}} {{partsAtMost}} placeholders
    follow-up.md            the follow-up prompt (from DrawStageLesson.FollowUpPrompt), with placeholders
```

These files are copied from PR #39 (branch `pr-39`, commit `23afa99`), with credit to darkworks in the
README and in the commit. Five changes are made in the shared copy, all platform-neutral:

1. **The host bridge.** `stage.js` talks to its host through `window.chrome.webview` (WebView2). Add a
   second branch: when `window.webkit.messageHandlers.stage` exists, `send()` posts there, and the host calls
   `window.drawStage.handle(message)` directly (it is already exported for browser previews). No other page
   code changes.
2. **The worker sandbox hole.** `stage-worker.js` nulls `fetch`, `XMLHttpRequest`, `WebSocket`,
   `EventSource`, `importScripts`, `indexedDB`, `caches` and `BroadcastChannel`, but a program can still do
   `new Worker(URL.createObjectURL(new Blob([...])))` and get a fresh realm with `fetch`. Null `Worker`,
   `SharedWorker`, `Blob` and `URL.createObjectURL` too. (The same fix was asked of the Windows PR.)
3. **The page stops its loops when told to.** Add two messages, `sleep` and `wake`: on `sleep` the page
   cancels its `requestAnimationFrame` loops (`stage.js` `tick`, the 3D `frame`) and on `wake` restarts them.
   The Mac sends `sleep` whenever the stage window is hidden. (Windows can use it for the same bug.)
4. **A first-question prompt with a way out.** The Windows user prompt demands a program ("Answer it by
   drawing … as one ```js program"); only its follow-up prompt offers `NONE`. A new shared file,
   `prompt/question.md`, is the first-question user prompt with the `NONE` rule (below). It is a new file,
   so Windows is untouched until its maintainers choose it. (Reviewer's question; my call.)
5. **A Mac handwriting face.** `stage.css` names Ink Free, Segoe Print and Comic Sans MS for labels and
   tags; on a Mac that falls to Comic Sans. Noteworthy and Bradley Hand are added to the stack, before
   Comic Sans. Windows is unaffected. (Reviewer's question; my call.)

The **drawing language and the message protocol are the contract** between platforms. They are documented in
`draw-stage/README.md` (moved from the PR's `docs/draw-stage.md`, trimmed of Windows specifics) and must not
change without both platforms agreeing. The Mac does not add messages beyond `sleep`/`wake`.

The prompt text in `prompt/` is the shared version of the Windows prompt. The Mac fills the placeholders
for the "detailed" level: 3 to 6 beats, 70 calls, 15-40 parts, 50 parts at most, 8,192 output tokens. The
"pieces" section of the Windows prompt is left out (no piece makers on the Mac).

Not sent by the Mac: `remembered` (it shows the page's Picture and Video buttons, which do nothing without
saving), `recording`, `saving`, `piece`. The wheel still resizes a card without the grip.

**Bundling.** A new build phase "Embed draw stage" (`macos/OpenClicky/scripts/embed-draw-stage.sh`, the twin
of `embed-app-skills.sh`) copies `draw-stage/` minus `README.md` and `prompt/*.md` kept as-is into
`Contents/Resources/draw-stage`. The prompt files are read from there at runtime (a missing file is a
programming error: the feature turns itself off and logs).

## How it works on the Mac

```
⌃⌥ question ─► local rung (unchanged) ─► pause ─► screenshot ─► DrawStageLesson.wantsADrawing?
                                                                     │ no: the ask lane as today
                                                                     ▼ yes
                                        DrawingAnswer: the drawing request streams from the answer model
                                                                     │
                     first non-blank line is NONE, or prose ─────────┼──► the ask lane as today (nothing spoken yet)
                                                                     ▼ a program
                                        DrawStageController.feed(program so far)  ─►  DrawStageWindow (WKWebView)
                                        page `line {k,text}`  ─►  SpeechQueue piece k (account voice first)
                                        piece k starts/ends   ─►  page `lineStarted/lineEnded {k}` (pace "speech")
                                        page `finished` + queue drained  ─►  the bubble
```

### Deciding to draw (the rule that matters)

Two gates, both cheap, and the model has the last word:

1. **A keyword pre-filter**, `DrawStageLesson.wantsADrawing(question)`, narrower than the Windows
   `WantsADrawing`, erring on the side of no drawing (the reviewer asked which way to err; my call):
   - Out, whatever else it says: doing something on screen ("how do I…", "how can I…", "where is…",
     "click", "open"), fewer than two words, "how do you/we…", "how are you", "why not".
   - Out: **trouble and work words**, because those are the person's own situation, which words answer and
     a drawing cannot: error, errors, fail, failing, failed, crash, crashes, broken, bug, warning, stuck,
     slow, won't, doesn't work, not working, issue, problem; and app, file, files, code, build, setting,
     settings, window, tab, button, menu, folder, email, message, page (the thing on screen the person is
     working in); and first-person ownership: my, mine, our, I'm, I've. "How does my heart beat" is lost to
     this rule; it is accepted so that "why is my build failing" never pays a round trip.
   - In, otherwise: curious how-and-why questions ("how does…", "how do…", "why…", "what happens when…",
     "what makes…", "what's inside…", "what are the parts…"), explicit asks ("draw", "sketch", "diagram",
     "show me how", "how does this/that/it work", "what's inside this", "how is this made"), and the
     teaching phrases ("teach me…", "explain how…", "explain … works"), **always, Teacher on or off**
     (reviewer's question; my call: the feature is for everyone). "X vs Y" and "difference between" stay
     out of the pre-filter: words answer those well.
2. **The model decides, with a high bar.** The drawing request's user prompt (shared `prompt/question.md`) says: answer
   with only the word `NONE` unless a drawing or a 3D model is genuinely needed to understand the answer; never
   for a definition, a fact, a number, a yes or no, a short explanation words carry well, or anything about the
   person's own files, errors, settings or the app in front of them. `DrawingAnswer` waits for the first
   non-blank line of the reply: `NONE` or prose means **fall through to the ordinary ask lane before anything
   is spoken or shown**; a program means draw.

Cost when the model says `NONE`: one short extra round-trip (a few tokens). Cost when it draws: none, the
drawing request *is* the answer request. The pre-filter keeps the extra round-trip rare.

The hold line ("One moment, let me draw that for you.") is spoken **only after the reply has shown it is a
program** and the first `say()` line is still more than 2 s away. Never before the decision. My call.

### The drawing request

Same route as the ordinary answer: the person's answer source via `ClaudeAPI.analyzeImageStreaming`
(Anthropic direct, any `AnswerProvider`, a plan CLI, or the account's backend). Changes:

- `analyzeImageStreaming` gains an optional `maxTokens` (default unchanged at 1,024). Drawings pass 8,192.
  `OpenAIChatAnswerProvider`, `GeminiAnswerProvider` and the plan providers honour it; the request to the
  backend sends it as `max_tokens`.
- **Backend:** `/chat` accepts `max_tokens` up to 8,192 when the request carries the header
  `X-OpenClicky-Purpose: drawing` (today `MAX_TOKENS_CAP` clamps everything to 1,024). This is the only backend change and is small. Without it, account users get cut-off drawings; with
  it, a drawing costs an account roughly eight ordinary answers of budget, which is acceptable because the
  pre-filter and `NONE` keep drawings rare. (A per-day drawing cap for the free account is a follow-up if
  the budget shows it is needed.)
- System prompt: the shared drawing-language prompt with the canvas size filled in, plus the reply-language
  wrapper the ask lane already applies (`withReplyLanguage`). No tools are offered on a drawing request.
- User prompt: the question, and (when a drawing is open) the follow-up prompt with its program.
- Images: the cursor screen's screenshot only (that is the canvas; `W`×`H` are its pixel size as labelled by
  `imageLabeledWithItsPixelSize`). No pointer close-up.
- Thinking: the request asks for no thinking the way `TeacherSketchWriter` does (`thinking: between_tools`
  for Sonnet 5.5 direct; the backend maps it per model), or the model spends the budget before a stroke.
- Plans: Codex returns the whole reply at the end (no streaming) and the Claude Code provider's 90 s
  timeout can be short for 8k tokens; a drawing request on a plan gets 180 s and is logged.
- A 402 from the account (the 8,192-token hold is about eight ordinary answers of budget) is treated like
  `NONE`: fall through to the ordinary lane, which carries the budget messages.

The reply's program is `DrawStageLesson.programIn(replySoFar)`, ported as-is (the ```js fence, or a bare
program that starts like code).

### The stage window

`Plugins/DrawStage/DrawStageWindow.swift`: a borderless, non-activating `NSPanel` covering the cursor
screen, clear background, level `.statusBar` (the Teacher board's level: under the pointer overlay and the
island, over everything else), `canJoinAllSpaces` + `fullScreenAuxiliary` + `stationary`,
`ignoresMouseEvents = true` by default. It hosts a `WKWebView` with `drawsBackground` off and a clear
`underPageBackgroundColor`, so the page's transparency reaches the desktop.

- **Serving the page.** A `WKURLSchemeHandler` for `openclicky-stage://page/…` serves files from the bundled
  `draw-stage` folder only (path-normalised, no `..`). `index.html` loads at
  `openclicky-stage://page/index.html`, so the page's `Content-Security-Policy` (`'self'`, `connect-src 'none'`)
  means what it says. The navigation delegate cancels any navigation off that origin and any new window.
  JavaScript is on; no other WebKit features. Dev tools (`isInspectable`) only with
  `OPENCLICKY_DRAWSTAGE_DEVTOOLS=1`.
- **Bridge.** App → page: `evaluateJavaScript("window.drawStage.handle(<json>)")`. Page → app: a
  `WKScriptMessageHandler` named `stage`, parsed with `Codable` into `DrawStageMessage` (unknown types
  ignored, as in the Windows `UnreadablePageMessagesAreIgnored` test).
- **The mouse.** The page reports `figures {rects}` (the card, the 3D figures, the resize grip) in page
  pixels. The window converts them to screen points and, on a global mouse-moved monitor, sets
  `ignoresMouseEvents` false while the pointer is inside one, true otherwise. That gives dragging the card,
  turning a figure, zooming with the wheel and clicking a part, all of which the page already does.
  `figurePart {text}` becomes a typed question "what does the <part> do?" through `submitTypedRequest`.
- **Placement.** Covers the cursor screen's full frame; the page is told the canvas size and scales the
  drawing itself. On a display change the stage is cleared and hidden (no rebuild; the next question makes a
  new one). Fullscreen apps: `fullScreenAuxiliary` lets it show over them, same as the Teacher board.
- **Prepared early.** `prepare()` creates the window and loads the page when the talk key goes down (the
  Windows `Prepare`), so the first drawing doesn't wait for WebKit. If it hasn't reported `ready` within 5 s
  of the first `feed`, the drawing is abandoned and the question falls through to the ordinary lane. (This
  is the readiness timeout the Windows PR lacks.)
- **Hidden means asleep.** Hiding the window sends `sleep` to the page and `orderOut`s the panel; showing
  sends `wake`. Verified by Activity Monitor before the feature is called done.

### Voice in step with the drawing

`SpeechQueue` gains per-piece hooks: `enqueue(_ text:, tag:)` and `run(onFirstAudio:onPieceStarted:onPieceEnded:)`
where the hooks receive the piece's tag. Existing callers pass no tag and no hooks; nothing changes for them.

`DrawingAnswer` bypasses `SpokenChunker`: each page `line {k, text}` is enqueued at once as piece `k`
(so its audio is fetched well before it is due, the queue already fetches two ahead), and the queue plays
the pieces in order. Piece `k` starting sends `lineStarted {k}`, ending sends `lineEnded {k}`; the page draws
beat `k` between them (pace `speech`). The page's `lineDue` message is not needed under this scheme and is
ignored (the queue's order *is* the due order). `programRan` finishes the queue after the last line.

Audio comes from the same `AnswerVoiceSession` the ask lane uses (account voice first, slow-voice fallback,
`AccountVoiceRest`), through the closures `makeAnswerSpeaker` passes today. If the voice fails partway
(`SpeechQueue.Failure`), send `finishNow` so the page draws the rest at once, and hand the unsaid lines to
the system voice as `waitForAnswerVoice` does.

The island shows the usual "answering" state while a drawing runs, with the stop button working.

### Finishing: the bubble

When the page reports `finished` and the speech queue has drained, `DrawStageBubble` shows:

- The stage asks the page to `measure`; its `bounds` give the drawing's box. `webView.takeSnapshot` of that
  box becomes a thumbnail.
- A small non-activating panel, 96 pt wide, rounded like the island's tiles, with the thumbnail on paper
  and a faint ring, sits **beside the island**: just outside its near edge for a side drawer (left or
  right placement), and just below-right of it under the notch; for `hidden` placement, the top-right
  corner of the screen the drawing was on. Level `.popUpMenu` like the island so it is never under it.
- Then the stage window hides (`sleep`). The page still holds the drawing, so **click** on the bubble
  wakes and shows the stage again with the same drawing, and the bubble goes; the drawing is then open for
  follow-ups.
- Hover shows a small ✕ on the bubble; clicking it discards the drawing (`clear`) and the bubble.
- A new drawing replaces the bubble. Only the last drawing is kept.
- Esc while a drawing is playing stops the voice (as esc stops anything), sends `finishNow`, and sends the
  drawing to its bubble; esc again, with nothing else running, removes the bubble. My call.

### Follow-ups

While the stage is **open** (never from the bubble), a new question is first checked with the ported
`mayBeAFollowUp` pre-filter. If it passes, the request uses the shared follow-up prompt with the drawing's
program (its earlier questions, its step count) and the model answers one of three ways, exactly as on
Windows: `keep()` first (the stage gets `continueStream`, the new beats run after the old ones), a fresh
program (`beginStream`), or `NONE` (the drawing goes to its bubble and the question is answered normally).
A question that can't be about the drawing sends it to the bubble before anything else happens.
`DrawnAnswer` (questions, program, screen, lines) is kept for this; it is cleared when the bubble is.

### Teacher

- Teacher on: its teaching questions go to the stage instead of the Teacher board (the Windows rule).
  `TeacherPlugin.startLesson` is not called for a question the stage takes. The board stays for Realtime and
  for `draw_on_screen` tool calls on ordinary answers.
- Teacher off: the stage still answers explain-questions. No Teacher dependency.
- The pointer flying to labels (the Teacher board's `onLabelWritten`) is not wired to the stage in v1.

### Settings

One row, Settings › Clicky, in the main group (not under More): **"Draw to explain"**, a toggle, on by
default, detail: "When a picture or a 3D model explains it better, Clicky draws on your screen while it
talks." Stored in `DictationSettings` as `openclicky.ask.drawToExplain`. Nothing else in v1.

### Escape and interruption

`interruptCurrentAnswer` also cancels the running `DrawingAnswer` (its request task and its queue) and tells
the stage `finishNow`. `AskLaneEscape.target` gets a `.drawing` case ahead of `.askLane` so esc during a
drawing stops the voice and minimises (above). Starting a new question while a drawing is *open* runs the
follow-up path; while it is *playing*, it interrupts it first (as any new question interrupts an answer).

### Smoke commands and tests

- `--openclicky-drawstage-smoke [program.js | reply:<file> | "lesson:<question>"] [--voice]`, the twin of
  `--openclicky-teacher-smoke`: with no argument it plays the bundled rocket and the 3D printer samples with
  the estimate clock; `reply:` plays a written reply through the whole `DrawingAnswer` path with the Mac
  voice; `lesson:` asks the person's answer model for real. Exits when done.
- Swift Testing, in `OpenClickyTests/DrawStage/`:
  - `DrawStageLessonTests`: the pre-filters (a table of questions in and out, including the anti-goal
    cases: "why is my build failing", "what time is it", "how do I export this" must all be out),
    `programIn`, `keepsTheDrawing`, the prompt placeholders filled.
  - `DrawStageMessagesTests`: every message both ways round-trips through `Codable`; unknown types ignored.
  - `DrawingAnswerTests`: end to end against a fake stage surface and a fake speech queue: a program draws
    and speaks in order with start/end per line; `NONE` and prose fall through before any speech; a page that
    never says `ready` falls through after the timeout; a voice failure sends `finishNow` and hands the rest
    to the system voice; a follow-up with `keep()` sends `continueStream`.
  - `SpeechQueueTests`: the new per-piece hooks fire in order with the right tags, and existing behaviour is
    unchanged.
  - `DrawStageBubbleTests`: placement for each island placement and screen.
- Checked by hand before it ships: a live jet engine and heart over real web pages; "what time is it" and
  "why is my build failing" produce no drawing and no extra wait; esc mid-drawing; the bubble and its click
  on each island placement; Activity Monitor shows no stage CPU after minimising; a second display.

## Safety

- The program runs in the page's Web Worker with no DOM and (after the fix above) no way to make a network
  request or load code; the page's CSP allows no connections; the scheme handler serves only the bundled
  folder; navigation elsewhere is cancelled; the only native surface is the `stage` message handler and the
  typed-question path for a clicked part.
- The page keeps its own limits (2 s per statement, 2,000 marks, fixed `random()`).
- The stage is the app's own window, so it is already excluded from the next question's screenshot.
- The drawing request sends the same screenshot the ordinary ask lane sends; no new data leaves the Mac.

## Left to the builder

- The exact Swift shapes of `DrawStageController`, `DrawStageMessage` and `DrawingAnswer` (port the Windows
  `DrawStageController.cs`, `DrawStageMessages.cs`, `DrawingAnswer.cs` for behaviour; don't copy structure
  the Mac doesn't need, e.g. photos, pieces, saving).
- Whether `photo()` cards are supported in v1: the Mac has `TeacherPictureFinder` (Wikimedia), so wiring
  `photoWanted` → `photo` is small; if skipped, the page must get a `photo` with no image so the frame
  resolves. Builder's choice; say which in the plan.
- How the stage panel and bubble host their views (`NSHostingView` for the bubble, a plain `WKWebView`
  for the stage).
- The hold line's voice (the answer voice through the same session is preferred; the system voice is fine).
- The exact bubble geometry and its show/hide animation.
- Log lines (`AppLog`) for: decision (pre-filter pass, model `NONE`, program), readiness timeout, voice
  fallback, time to first beat.
- Spike first: a `Worker('stage-worker.js')` and the ES-module `figure3d.js` import over the custom
  `WKURLSchemeHandler` scheme. If WebKit refuses either, fall back to `loadFileURL(_:allowingReadAccessTo:)`
  on the bundled folder and keep the navigation delegate as the fence.
- The island's frame is private model state; the bubble needs a small accessor on `NotchHUDManager`.

## Credits

The stage page, the drawing language, the 3D figure engine and the samples were written by darkworks for
Windows PR #39 and are reused here under the repository's MIT licence with their credit kept in
`draw-stage/README.md` and the vendored three.js LICENSE.
