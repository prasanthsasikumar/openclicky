# The drawing stage (Windows)

Ask "how does this work?" about a rocket, a 3D printer, a heart, and OpenClicky answers by **drawing on your
screen while it talks**: a hand-drawn diagram that appears stroke by stroke, each beat in step with the sentence
being spoken. Later phases add turning 3D models you can click to ask about a part.

This is the same kind of feature HeyClicky ships as "draw and explain". It is written from scratch for OpenClicky:
no HeyClicky code, prompts or assets are used.

## How it works

```
 the model's answer (a small program, streamed)
        │  DrawStageController.Feed(programSoFar, done)          OpenClicky.Core/Plugins/DrawStage
        ▼
 DrawStageWindow  ── PostWebMessageAsJson ──►  Assets/DrawStage/stage.js       OpenClicky.Windows/Plugins/DrawStage
 (Win32 popup, no redirection surface,          │  finds each complete statement as the text streams in
  Windows.UI.Composition visual tree,           ▼
  WebView2 in visual-hosting mode,           stage-worker.js  runs one statement at a time → marks (plain data)
  transparent background, click-through,        │
  excluded from screenshots)                    ▼
                                             stage.js paints the marks as SVG pen strokes, each beat's strokes
                                             spread over the time its say() line is spoken
```

**The model writes a program, not a picture.** An answer is a list of beats:

```js
step(1); say("A rocket is a tall tube full of fuel, with an engine at the bottom.")
const x = W * 0.62, top = H * 0.16
rect(x, top, 90, 360, {color: 'ink', fill: 'white'})
step(2); say("The engine blasts hot gas down, and the gas pushes the rocket up.")
arrow([[x - 70, top + 270], [x - 70, top + 70]], {color: 'green'})
text(x - 85, top + 50, 'thrust', {color: 'green', align: 'end'})
```

Coordinates are in **canvas units**: the screenshot the model saw (`W` × `H`, usually 1280 wide). The page scales
the canvas onto the monitor.

**Drawing while it streams.** The page runs a statement as soon as it is complete, without waiting for the rest of
the answer. A statement ends at `;`, a line break or `}` outside brackets, strings and comments, unless the next
text continues it (`.then`, `+ 1`, `else`) or it ends mid-expression (`=`, `,`, `(`).

**Pacing.** Every mark belongs to the `say()` line before it. The pen draws a line's marks within that line's time,
running up to about 3× faster or 2× slower than its natural speed so they finish as the line does. There are two clocks:

- `estimate` (default): each line lasts its word count / 2.6 seconds.
- `speech`: the page sends `lineDue(k)` when line *k* is next. The app speaks it and answers `lineStarted(k)` /
  `lineEnded(k)`.

## Messages

| App → page | |
|---|---|
| `beginStream {w, h, pace}` | a new program on a `w`×`h` canvas; wipes the old drawing |
| `feed {code, done}` | the whole program so far; `done` on the last |
| `lineStarted {k}` / `lineEnded {k}` | the voice's timing for line `k` (pace `speech`) |
| `finishNow` | draw everything still waiting, quickly (the voice was cut short) |
| `clear` | wipe the drawing |

| Page → app | |
|---|---|
| `ready` | the page loaded (earlier messages wait for this) |
| `line {k, text}` | a `say()` ran: the voice can fetch its audio now, well before it is due |
| `programRan {lines}` | every statement has run, so every line is known (the voice may finish after the last) |
| `lineDue {k, text}` | speak line `k` now (pace `speech`) |
| `finished` | the program is done and everything is drawn |
| `log {text}` / `error {text}` | warnings, statement errors, a stopped program |

## Answering a question by drawing (phase 1)

`CompanionManager.DrawStage` runs in the ask lane, before the Teacher's own lesson would start. A question gets a
drawing (`DrawStageLesson.WantsADrawing`) when it is one of the Teacher's teaching questions ("how does a jet engine
work"). It also gets one when it asks about the thing on the screen in a way a picture answers ("how does this
work?", "what's inside this?", "draw …"). Asking how to *do* something on screen ("how do I export this") stays
with pointing.

1. The cursor screen's screenshot is the canvas. It goes to the **answer model the person already chose** (Claude,
   OpenAI, Gemini, OpenRouter, Groq, Mistral, xAI, a plan, or the free account) with `DrawStageLesson.SystemPrompt`.
   That prompt teaches the drawing language and how a good answer is built, and allows up to 4096 output tokens.
2. `DrawingAnswer` feeds the program in the streamed reply (`DrawStageLesson.ProgramIn`: the ```js block) to the
   stage as it grows.
3. Each `line` from the page goes straight into a `SpeechQueue` in the person's answer voice. Its
   `PieceStarted` / `PieceEnded` events become `lineStarted` / `lineEnded` (pace `speech`), so each beat is drawn
   while its sentence is heard. `programRan` finishes the queue after the last line.
4. If the reply holds no program, or the stage cannot start, the question goes back to the normal ask lane
   before anything is said. If the voice fails partway, the stage draws the rest at once and the system voice
   reads what was left.
5. Esc and the next question clear the stage (`TeacherPlugin.ClearBoard`). The stage's browser starts as soon as
   the talk key goes down (`DrawStageController.Prepare`), so the first drawing does not wait for it.

It is on whenever the Teacher plugin is on. To turn it off, set the preference `teacher.drawingStage` to `false`;
teaching questions then go back to the Teacher's board. The fast (Realtime) voice lane still uses the Teacher's
board.

## The drawing language (phase 1 subset)

| Call | Draws |
|---|---|
| `step(n)`, `say(text)` | a beat and its spoken line |
| `line(points, o)`, `curve(points, o)` | a pen stroke through `[[x, y], …]`; `curve` is smooth |
| `arrow(points, o)` | a stroke with a head at its last point |
| `circle(x, y, r, o)`, `ellipse(x, y, rx, ry, o)`, `rect(x, y, w, h, o)`, `poly(points, o)` | outlines, optionally filled |
| `dot(x, y, r, o)`, `text(x, y, words, o)`, `highlight(x, y, w, h, o)` | a point, handwriting, a marker sweep |
| `card(x, y, w, h, {title})` | a paper card to explain on |
| `W`, `H`, `sin`, `cos`, `PI`, `mid`, `along`, `polar`, `lerp`, `range` | the canvas and helpers |

Options `o`: `color` (`ink, blue, red, green, orange, purple, pink, yellow, teal, brown, grey, white` or `#hex`),
`width`, `fill`, `fillOpacity`, `dashed`, `size`, `align` (`start`, `middle`, `end`), `bold`.

## Safety

The program is model-written code, so:

- It runs in a **Web Worker**, never in the page. The worker has no DOM, and `fetch`, `XMLHttpRequest`,
  `WebSocket`, `importScripts` and similar are removed before any program runs.
- The page's Content-Security-Policy allows no network at all (`connect-src 'none'`). The page is served from
  the app folder through a WebView2 virtual host and cannot navigate anywhere else or open windows.
- Each statement has 2 seconds to run, or the program is stopped. A program may draw at most 2000 marks.
  `random()` is fixed, so a program draws the same picture every time.
- The stage is click-through and kept out of screenshots, so the next question's screenshot sees the screen,
  not the drawing.

## Why WebView2 + SVG (and three.js for 3D)

- **The window:** a WebView2 control inside a transparent WPF window draws nothing, and in an opaque one it covers
  the screen. So the stage is a plain Win32 popup (`WS_EX_NOREDIRECTIONBITMAP`, layered + transparent for
  click-through) whose content is a Windows.UI.Composition visual. WebView2 renders into that visual in
  visual-hosting mode, so the page's alpha reaches the desktop.
- **3D (phase 2): three.js over Filament.** Both are excellent. three.js runs in the same page as the 2D drawing,
  and the same engine page can later run unchanged in the Android/iOS WebViews and on Linux. It also has the
  procedural geometry a model needs to build a machine from parts (lathe, tube, extrude, gears made from
  shapes). Filament is a native C++ PBR renderer built for glTF assets. It would need its own bindings on every
  platform and a mesh pipeline for model-built parts.

## Phases

| Phase | |
|---|---|
| 0 ✓ | the stage window, the message bridge, the page with streaming execution, the worker sandbox, the pen, captions and both pacing clocks; `--openclicky-drawstage-smoke` |
| **1 (this)** | questions answered by drawing in the ask lane, with any answer model; the drawing prompt; the screenshot as the canvas; speech pacing from `SpeechQueue`; falling back to words |
| 1.x | the Realtime voice lane; a Settings switch; a model-callable tool for questions the heuristic misses |
| 2 | `figure()` 3D parts with three.js: materials, labels, motion, cutaways, drag to turn, click a part → ask about it |
| 3 | photo cards, molecules, graphs and timelines, live edits for follow-up questions |
| 4 | lessons: save, replay, export as video, quiz cards |

## Trying it

```powershell
cd windows
dotnet build OpenClicky.Windows.sln
.\src\OpenClicky.Windows\bin\Debug\net8.0-windows10.0.19041.0\OpenClicky.exe --openclicky-drawstage-smoke
.\src\OpenClicky.Windows\bin\Debug\net8.0-windows10.0.19041.0\OpenClicky.exe --openclicky-drawstage-smoke my-program.js --speech --shot stage.png
# a whole drawing answer on a screenshot of your screen: a written reply, or your answer model's own (needs a key)
.\src\OpenClicky.Windows\bin\Debug\net8.0-windows10.0.19041.0\OpenClicky.exe --openclicky-drawstage-smoke reply:src\OpenClicky.Windows\Assets\DrawStage\samples\heart-reply.md --voice
.\src\OpenClicky.Windows\bin\Debug\net8.0-windows10.0.19041.0\OpenClicky.exe --openclicky-drawstage-smoke "lesson:how does a jet engine work?" --voice
```

Set `OPENCLICKY_DRAWSTAGE_DEVTOOLS=1` to allow the page's dev tools and context menu. To preview the page in an
ordinary browser, serve `Assets/DrawStage` over http and call
`drawStage.handle({type: 'beginStream', w: 1280, h: 720})`, then `drawStage.handle({type: 'feed', code, done: true})`.
