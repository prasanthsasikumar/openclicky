# The drawing stage

Ask "how does this work?" about a rocket, a 3D printer, a heart, and OpenClicky answers by **drawing on your
screen while it talks**: a hand-drawn diagram that appears stroke by stroke, or a turning 3D model built part
by part, each beat in step with the sentence being spoken.

This folder is the stage itself, shared by every OpenClicky app: the page (`index.html`, `stage.js`,
`stage.css`), the sandboxed worker that runs the model's program (`stage-worker.js`), the 3D figure engine
(`figure3d.js`, on three.js r172 in `vendor/three`, MIT, see its LICENSE), the sample programs
(`samples/`) and the prompts that teach a model the drawing language (`prompt/`). An app loads the page in a
web view, feeds it the model's reply as it streams, speaks each `say()` line, and tells the page when each
line starts and ends. The drawing language and the messages below are the contract between the apps and the
page: nothing here may change in a way one app would not understand.

Written by darkworks for OpenClicky's Windows app (PR #39), MIT, from scratch: no HeyClicky code, prompts or
assets are used. The Mac app loads this folder from its bundle (`Contents/Resources/draw-stage`); the
Windows app keeps its own copy under `windows/src/OpenClicky.Windows/Assets/DrawStage` until it moves here.

## How it works

```
 the model's answer (a small program, streamed)
        │  the app's controller feeds the program so far
        ▼
 the app's stage window  ── postMessage ──►  stage.js      finds each complete statement as the text streams in
 (transparent, click-through,                   │
  kept out of screenshots)                      ▼
                                             stage-worker.js  runs one statement at a time → marks (plain data)
                                                │
                                                ▼
                                             stage.js paints the marks as SVG pen strokes, each beat's strokes
                                             spread over the time its say() line is spoken; figure3d.js renders
                                             the 3D figures
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

Coordinates are in **canvas units**: the screenshot the model saw (`W` × `H`). The page scales the canvas
onto the monitor.

**Drawing while it streams.** The page runs a statement as soon as it is complete, without waiting for the
rest of the answer. A statement ends at `;`, a line break or `}` outside brackets, strings and comments,
unless the next text continues it (`.then`, `+ 1`, `else`) or it ends mid-expression (`=`, `,`, `(`).

**Labels keep apart.** A `text()` label that would land on another label (or a card's title) moves the
least it can, up or down before sideways, to a free spot inside its card or the canvas, off the strokes drawn
so far when it can be. It keeps its place above or below the label it met, so a two-line label still reads in
order, and an `arrow()` with an end at it moves that end too. Labels draw over every stroke, so a later stroke
never covers one. `samples/labels-crowded.js` is a drawing whose labels crowd each other.

**Pacing.** Every mark belongs to the `say()` line before it. The pen draws a line's marks within that line's
time, running up to about 3× faster or 2× slower than its natural speed so they finish as the line does. Two
clocks:

- `estimate` (default): each line lasts its word count / 2.6 seconds.
- `speech`: the page sends `lineDue(k)` when line *k* is next. The app speaks it and answers
  `lineStarted(k)` / `lineEnded(k)`. An app that plays the lines in order from its own queue can ignore
  `lineDue` and send `lineStarted` / `lineEnded` as each line's audio starts and ends (the Mac does).

## Messages

| App → page | |
|---|---|
| `beginStream {w, h, pace, keepPlace?}` | a new program on a `w`×`h` canvas; wipes the old drawing (and where it was moved, unless `keepPlace`) |
| `continueStream {pace}` | a follow-up's program, run after the one drawn: the drawing and the program's names stay |
| `feed {code, done}` | the whole program so far; `done` on the last |
| `lineStarted {k}` / `lineEnded {k}` | the voice's timing for line `k` (pace `speech`) |
| `finishNow` | draw everything still waiting, quickly (the voice was cut short) |
| `clear` | wipe the drawing |
| `sleep` / `wake` | stop the page's animation loops while its window is hidden, and start them again |
| `remembered` | the app keeps the finished drawing: its Picture and Video buttons and its resize grip show (Windows) |
| `measure` | answer with the drawing's box (`bounds`) |
| `recording {on}` / `saving {text, state, fraction?}` | while a drawing is recorded or saved (Windows) |
| `photo {id, data, credit}` / `photo {id, failed}` | the photo the page asked for, as a data URL, or that none was found |
| `piece {id, kind, …}` | a 3D piece the page asked for (Windows) |

| Page → app | |
|---|---|
| `ready` | the page loaded (earlier messages wait for this) |
| `line {k, text}` | a `say()` ran: the voice can fetch its audio now, well before it is due |
| `programRan {lines}` | every statement has run, so every line is known (the voice may finish after the last) |
| `lineDue {k, text}` | speak line `k` now (pace `speech`) |
| `finished` | the program is done and everything is drawn |
| `figures {rects}` | where the window should take the mouse (page pixels): a card it can be dragged by, the 3D figures, the buttons and the grip |
| `figurePart {text}` / `save {text}` | a part of a figure was clicked / the Picture or Video button was |
| `bounds {x, y, w, h, pageWidth, pageHeight}` | the drawing's box: its cards, photos, figures and their tags |
| `photoWanted {id, query, text}` / `pieceWanted {…}` | a `photo()` or `f.piece()` the app should find or make |
| `log {text}` / `error {text}` | warnings, statement errors, a stopped program |

## The drawing language

| Call | Draws |
|---|---|
| `step(n)`, `say(text)` | a beat and its spoken line |
| `line(points, o)`, `curve(points, o)` | a pen stroke through `[[x, y], …]`; `curve` is smooth |
| `arrow(points, o)` | a stroke with a head at its last point |
| `circle(x, y, r, o)`, `ellipse(x, y, rx, ry, o)`, `rect(x, y, w, h, o)`, `poly(points, o)` | outlines, optionally filled |
| `dot(x, y, r, o)`, `text(x, y, words, o)`, `highlight(x, y, w, h, o)` | a point, handwriting, a marker sweep |
| `card(x, y, w, h, {title})` | a paper card to explain on |
| `photo(x, y, w, h, {query, caption})` | a real photo from Wikimedia Commons (found by the app) in a polaroid frame |
| `figure(x, y, w, h, {tilt, turn, zoom, spin, ground})` | a turning 3D stage; its parts are below |
| `keep()` | first statement of a follow-up: run after the drawing on the stage, which stays |
| `chart(x, y, w, h, o)`, `timeline(x, y, w, o)` | a graph or a timeline placed by value; below |
| `W`, `H`, `sin`, `cos`, `exp`, `log`, `PI`, `mid`, `along`, `polar`, `lerp`, `range` | the canvas and helpers |

Options `o`: `color` (`ink, blue, red, green, orange, purple, pink, yellow, teal, brown, grey, white` or `#hex`),
`width`, `fill`, `fillOpacity`, `dashed`, `size`, `align` (`start`, `middle`, `end`), `bold`.

### Graphs and timelines

`chart()` and `timeline()` are drawn with the marks above, but take values instead of canvas units, so the scale is
right without the model working out every coordinate (`stage-worker.js`, nothing new on the page).

| Call | Draws |
|---|---|
| `chart(x, y, w, h, {x, y, grid, color})` | axes with numbered ticks in that box. Each axis is `{from, to, step, label, format}` (`step` is optional: about one number per 50-80 units), or `{labels: [...]}` for named columns. They cross at zero when zero is on them |
| `g.line(data, o)`, `g.area(data, o)` | a line (or the shaded space under it) through `[[x, y], …]`, numbers (one per column) or a function of x; cut where it leaves the axes. `{dots: true}` marks each point |
| `g.bars(values, o)` | a bar per value (`{colors, values: true, barWidth}`) |
| `g.dot(x, y, {label})`, `g.label(x, y, words, o)`, `g.hline(y, words, o)`, `g.vline(x, words, o)` | a point, a note, a dashed line across at a value |
| `g.at(x, y)`, `g.x(v)`, `g.y(v)` | canvas units of a value, for the model's own marks |
| `timeline(x, y, w, {from, to, step, label, format})` | a line of years across at height `y` |
| `t.event(when, words, o)`, `t.span(from, to, words, o)`, `t.at(when)` | a dot with its label on a stem, a band for a stretch of time, a value's point |

Each axis is one stroke, its tick nubs on it. A chart's own labels (a dot's, a dashed line's) take the spot around
their point that crosses the fewest of its strokes and labels. A timeline's labels go on stems: the shortest stem,
above or below, where the label touches no other and no stem runs through it, centred on its stem or ending at it.
Samples: `chart-falling-ball.js`, `chart-bars-water.js`, `timeline-flight.js`.

### 3D figures

| Part | Shape |
|---|---|
| `f.box({w, h, d, round})` | a box; `round` rounds its edges and corners |
| `f.cyl({r, h, r2, open})`, `f.cone({r, h})`, `f.sphere({r})`, `f.torus({r, tube})`, `f.capsule({r, h})` | the round solids |
| `f.lathe({points: [[radius, y], …]})` | a turned profile: casings, nozzles, bottles |
| `f.extrude({points, depth, holes, bevel})` | a flat outline made thick: wings, fins, brackets |
| `f.tube({points, r})`, `f.pipe({points, r, bend})` | a smooth tube; a pipe with straight runs and bends |
| `f.spring({r, h, coils, wire})`, `f.gear({r, teeth, depth})`, `f.arrow({from, to, color})` | |
| `f.blob({balls, sticks, paths, holes, smooth})` | one smooth organic surface over balls, sticks and paths, blended where they meet: organs, animals, leaves |

Every part takes `at`, `rot`, `axis`, `scale`, `parent`, `color`, `opacity` and a material (`steel, alu,
titanium, chrome, iron, gold, copper, brass, plastic:<color>, paint:<color>, rubber, wood, stone, ceramic,
glass, water, glow:<color>`, and the organic `tissue, muscle, skin, bone, leaf, fabric, pcb`). `cut: 'z'`
takes the front half away, `copies`, `around` and `mirror` repeat a part. Motion: `.spin .stroke .bob .pulse
.orbit .move`; labels: `part.label(words, {color})`; camera: `f.view({tilt, turn, zoom})`. A part named by
its const (`const liftFan = f.cyl(…)`) is asked about as "lift fan" when clicked. Dragging a figure turns it;
the wheel zooms; a card is dragged by its paper and resized by the wheel over it.

### Molecules

`molecule(x, y, w, h, o)` builds a ball-and-stick model in a 3D figure (`stage-worker.js`, from `f.sphere` and
`f.cyl` parts, so nothing new on the page). The program says only which atoms there are and how they are bonded; the
shape is worked out in the worker:

| Input | |
|---|---|
| `{name: 'water'}` | a known molecule, by name or formula: water, carbon dioxide, carbon monoxide, oxygen, ozone, nitrogen, hydrogen, methane, ammonia, ethanol, ethylene, acetic acid, hydrogen peroxide, sulfuric acid, sodium chloride, benzene, glucose, caffeine, aspirin |
| `{smiles: 'CC(=O)Oc1ccccc1C(=O)O'}` | any other, as SMILES: atoms (lowercase in an aromatic ring, `[Na+]` `[NH4+]` `[nH]` in brackets), bonds `- = # :`, branches, ring closures (`1`, `%12`) |
| `{atoms: ['C', 'C', 'O'], bonds: [[0, 1], [1, 2]]}` | or as lists, `[a, b, 2]` for a double bond |

Hydrogens left out are added up to each atom's usual valence. Options: `style: 'space'` (space-filling), `labels`
(`false`, or `'all'` on a small molecule; by default each element's name is tagged once, on its outermost atom),
`spin`, `tilt`, `turn`. It hands back `m.label(atom, words)`, `m.atom(atom)` and `m.view({...})`; an atom is a number
(its place in the SMILES) or a name: `'N'`, `'O#2'`, `'ring'`. Models count a SMILES string's atoms badly and name
them well.

The shape: bond lengths from covalent radii (shorter for double and triple bonds, halfway for aromatic ones); the angle
at each atom from its bonds and lone pairs (VSEPR: 180°, 120°, 117°, 109.5°, 107°, 104.5°), or a small ring's own angle
(108° in a five-ring); atoms around a double bond kept in one plane; a push between atoms not bonded. It starts from
the flattest ring laid out as a ring and chains zigzagging out of it, relaxes for up to 1500 rounds (aspirin takes
about 0.15 s; a statement may run 2 s), then turns the molecule so its longest axis lies across and a flat one faces
the viewer. Samples: `molecule-water-co2.js`, `molecule-glucose.js`.

## Prompts

`prompt/drawing-language.md` is the system prompt that teaches the language. An app fills `{{W}}`, `{{H}}`,
`{{captionTop}}` (0.86 × H, where the captions sit), `{{beats}}`, `{{calls}}`, `{{parts}}` and
`{{partsAtMost}}` for the level of detail it wants (the Mac's "detailed": "3 to 6", 70, "15-40 parts, with
the small things that make it recognisable", 50). `prompt/question.md` is the user prompt for a first
question, with a `NONE` way out when a drawing is not needed; `prompt/follow-up.md` is the user prompt for a
question asked while a drawing is up (`{{asked}}`, `{{program}}`, `{{question}}`, `{{nextStep}}`,
`{{marked}}`), where the model adds to the drawing (`keep()`), redraws it, or answers `NONE`.

### Mechanisms

`mechanism(x, y, w, h, {parts, speed})` builds a working machine in a 3D figure (`stage-worker.js`). The program says
what joins what; the worker works out where each part sits, how fast and which way it turns, and the path a rod or a
piston follows. The first part is driven at `speed` turns a minute (20 by default).

| Part | |
|---|---|
| `{gear: 'A', teeth: 12}`, `{gear: 'B', teeth: 36, meshes: 'A', angle}`, `{gear: 'C', teeth: 10, on: 'B'}` | gears of one tooth size (module 0.25), placed so their teeth meet (a tooth of one at a gap of the other), turning the other way at the teeth's ratio; `on` shares a shaft |
| `{pulley: 'P', r}`, `{pulley: 'Q', r, belt: 'P', distance, angle, crossed}` | pulleys and a belt round them (open or crossed), marks travelling on it |
| `{sprocket: 'S', teeth}`, `{sprocket: 'T', teeth, chain: 'S', distance, angle}` | sprockets and a chain whose links travel round |
| `{rack: 'R', meshes: 'A', length, angle}` | a rack: the machine then swings back and forth, the rack sliding a third of its length |
| `{crank: 'K', r, on}`, `{piston: 'P', crank: 'K', rod, angle}`, `{rocker: 'L', crank: 'K', rod, length, pivot, reach, head}` | a crank disc, a connecting rod and a piston in its cylinder (slider-crank), or a rocker on a pivot (four-bar), `reach` carrying it on past the pivot and `head` putting a pump jack's horse head on its far end |

Any part takes `label`, `color`, `mat`. The machine's still body (a frame, a tower, a base) goes round it on `m.figure`, in the same units: the first part at the origin, the moving parts in the z = 0 plane. It hands back `m.speed(name)` (turns a minute) and `m.direction(name)`
(clockwise or anticlockwise) for the spoken words, `m.label(name, words)`, `m.part(name)` and `m.view({...})`.

Gears are drawn by the worker (`f.extrude` of a toothed outline), since `f.gear`'s teeth grow with its size and two
sizes would not mesh. Wheels turn by `spin`; rods, pistons, rockers, racks and chain links follow a `path`: frames the
worker computes for one cycle (the slider-crank's wrist pin from the rod's length, the four-bar's joint as the crossing
of two circles, kept on one branch), which `figure3d.js` plays, moving straight and turning the short way between
frames. Every motion of a mechanism has `sync`, so it keeps the figure's clock: parts added a beat apart stay in step.
A rocker's post and a driving crank's stand are drawn down to one ground. Samples: `mechanism-gears.js`,
`mechanism-engine.js`, `mechanism-bike.js`, `mechanism-pumpjack.js`.

## Safety

The program is model-written code, so:

- It runs in a **Web Worker**, never in the page. The worker has no DOM, and `fetch`, `XMLHttpRequest`,
  `WebSocket`, `EventSource`, `importScripts`, `indexedDB`, `caches`, `BroadcastChannel`, `Worker`,
  `SharedWorker`, `Blob` and `URL.createObjectURL` are removed before any program runs, so it cannot reach
  the network or start a worker that could.
- The page's Content-Security-Policy allows no network at all (`connect-src 'none'`). Each app serves the
  page from its own folder and cancels any navigation elsewhere.
- Each statement has 2 seconds to run, or the program is stopped. A program may draw at most 2000 marks.
  `random()` is fixed, so a program draws the same picture every time.

## Previewing in a browser

Serve this folder over http (`python3 -m http.server`) and open `index.html`, then in the console:
`drawStage.handle({type: 'beginStream', w: 1280, h: 720})` and
`drawStage.handle({type: 'feed', code: <a sample's text>, done: true})`. Without an app the page logs its
messages instead of posting them.
