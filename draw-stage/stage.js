// OpenClicky drawing stage: the page.
//
// The app streams the model's answer here as a program (DrawStageMessages.cs: beginStream, feed, lineStarted,
// lineEnded, finishNow, clear). Each statement runs in stage-worker.js once it is complete, and what it draws is
// painted with a pen, in order, each beat's strokes spread over the time its line is spoken: by the app's voice
// (pace "speech": the page asks for each line with lineDue) or by an estimate of 2.6 words a second.
// The page answers ready, line (each say() as it runs), programRan (all of it ran), lineDue, finished, log and error.
//
// continueStream runs a follow-up's program after the one drawn (the drawing and its names stay). Once the app has
// remembered a finished drawing, Picture and Video buttons sit in its corner (they say save) with a resize grip in the
// other; measure answers with the drawing's box (bounds), recording hides the caption and the buttons while the app
// records the drawing, and saving shows what the app is doing just outside it. A drawing on a card can be dragged
// about by the card (and the wheel over it resizes it): the page then takes the mouse over the card (reportHits).
//
// The pen (stage-pen.js holds its arithmetic, unit-tested): a stroke is ink with a tapered outline, revealed along its
// length; it takes a time that goes with its length, the next mark starts before it has quite ended, and a pen cursor
// flies to each mark and draws it. A spoken line's marks are spread to end with the line. With Reduce Motion (the
// system's setting, or beginStream {reduceMotion}) marks appear in place: no pen, no travel. beginStream {sound} turns
// on the pen's sound (stage-sound.js); without it the stage is silent.
'use strict';

(function () {
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const svg = document.getElementById('stage');
  const caption = document.getElementById('caption');
  const tools = document.getElementById('tools');
  const paper = document.getElementById('paper');
  const board = document.getElementById('board');
  const grip = document.getElementById('grip');
  const saving = document.getElementById('saving');

  const Pen = window.StagePen;     // the pen's arithmetic (stage-pen.js)
  const Sound = window.StageSound; // the pen's sound (stage-sound.js); silent unless the app asks for it
  const LINE_GAP = 0.25;          // between two estimated lines
  const LEAD = 0.08, TAIL = 0.12; // a line's strokes start just after its words and end just before they do
  const STATEMENT_SECONDS = 2;    // the longest one statement may run before the program is stopped
  const MAX_ERRORS = 5;

  const TONES = {
    ink: '#1E2230', blue: '#2F6BFF', red: '#E5484D', green: '#25A55F', orange: '#F28C28', purple: '#7C5CD6',
    pink: '#E85D9C', yellow: '#F2C200', teal: '#14A3A3', brown: '#9A6235', grey: '#7A808C', white: '#FFFFFF'
  };
  const tone = (name, fallback) => TONES[name] || (typeof name === 'string' && /^#[0-9a-f]{3,8}$/i.test(name) ? name : fallback);

  // ------------------------------------------------------------------ talking to the app
  // WebView2 on Windows posts through window.chrome.webview; WKWebView on the Mac through a "stage" message
  // handler (and the app calls window.drawStage.handle directly); a plain browser preview logs.
  const host = window.chrome && window.chrome.webview;
  const webkitHost = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.stage;
  function send(message) {
    if (host) host.postMessage(message);
    else if (webkitHost) webkitHost.postMessage(message);
    else console.log('[stage →]', message);
  }
  const log = text => send({ type: 'log', text: String(text) });

  // ------------------------------------------------------------------ state of one program
  let W = 1280, H = 800, pace = 'estimate';
  let worker = null, seq = 0, running = null, runTimer = 0;
  let source = '', scanned = 0, feedDone = false, stopped = false;
  let pending = [];              // complete statements not run yet
  let lines = [], items = [], released = 0, finishing = false, finishedSent = false, ranSent = false;
  let lastMark = null;           // the mark released last: when its ink started, its natural times, how they were paced
  let inkDoneAt = 0;             // when the ink of everything released has dried
  let spoken = [];               // the lines heard so far ({words, seconds}): the voice's own speed (pace "speech")
  let markCount = 0, errors = 0, uid = 0;
  const drawn = new Map(); // a 2D mark's number in the program → what was drawn for it (erase / change)
  let remembered = false, recording = false;
  const quizButton = document.getElementById('tools').querySelector('[data-act="quiz"]');

  // What the app (or a preview's address: ?sound=1, ?motion=reduce) asks of a drawing. Nothing asked: no sound, and
  // the system's Reduce Motion setting.
  const query = new URLSearchParams(window.location.search);
  const motionQuery = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
  let still = false;             // marks appear in place: no pen, no travel
  function takeOptions(message) {
    const asked = typeof message.reduceMotion === 'boolean' ? message.reduceMotion : query.has('motion') ? query.get('motion') === 'reduce' : null;
    still = asked != null ? asked : !!(motionQuery && motionQuery.matches);
    const sound = 'sound' in message ? message.sound : query.has('sound') ? Number(query.get('sound')) || query.get('sound') === 'true' : false;
    if (Sound) Sound.set(sound === true ? 1 : typeof sound === 'number' ? sound : 0);
  }

  const now = () => performance.now() / 1000;

  function reset() {
    if (worker) worker.terminate();
    worker = null;
    restartStream();
    markCount = 0;
    labels = [];
    drawn.clear();
    clearQuizzes();
    cards = [];
    strokes = [];
    while (svg.lastChild) svg.removeChild(svg.lastChild);
    if (window.Figure3D) window.Figure3D.clear();
    if (typeof photos !== 'undefined') photos.clear();
    svg.appendChild(defs());
    labelLayer = el('g', { class: 'labels' });
    svg.appendChild(labelLayer);
    hidePen(true);
    if (Sound) { Sound.hush(); Sound.rest(); }
  }

  // A new stream of statements: nothing pending, no lines, no button (what is drawn stays).
  function restartStream() {
    clearTimeout(runTimer);
    running = null;
    source = '';
    scanned = 0;
    feedDone = false;
    stopped = false;
    pending = [];
    lines = [];
    items = [];
    released = 0;
    lastMark = null;
    inkDoneAt = 0;
    finishing = false;
    finishedSent = false;
    ranSent = false;
    errors = 0;
    remembered = false;
    showCaption(null);
    showTools();
  }

  function begin(message) {
    reset();
    // A new drawing goes where it is drawn; the app's replay of one for a video keeps it where it was moved to.
    if (!message.keepPlace) { resetPlace(); hideSaving(); }
    W = message.w > 0 ? message.w : W;
    H = message.h > 0 ? message.h : H;
    pace = message.pace === 'speech' ? 'speech' : 'estimate';
    spoken = [];
    takeOptions(message);
    svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
    penLayer.setAttribute('viewBox', `0 0 ${W} ${H}`);
    worker = new Worker('stage-worker.js');
    worker.onmessage = event => ran(event.data);
    worker.onerror = event => { event.preventDefault(); stop('the program worker failed: ' + (event.message || 'unknown error')); };
    worker.postMessage({ type: 'begin', w: W, h: H });
  }

  // A follow-up that keeps the drawing: its statements run after the ones drawn, in the same worker, so the names the
  // program made (its card, its figure and parts) are still there.
  function continueStream(message) {
    restartStream();
    pace = message.pace === 'speech' ? 'speech' : 'estimate';
    takeOptions(message);
    if (!worker) {
      log('continue: the program worker had stopped; the follow-up runs without the earlier names');
      worker = new Worker('stage-worker.js');
      worker.onmessage = event => ran(event.data);
      worker.onerror = event => { event.preventDefault(); stop('the program worker failed: ' + (event.message || 'unknown error')); };
      worker.postMessage({ type: 'begin', w: W, h: H });
    }
  }

  // ------------------------------------------------------------------ finding complete statements in a stream
  // A statement ends at a ';', a line break or a '}' outside brackets, strings and comments, when what follows can't
  // continue it (".then", "+ 1", "else") and it doesn't end mid-expression ("=", ",", "("). Only an end with code after
  // it is taken: the text at the end of the stream can still grow until the stream is done.
  const CONTINUES_NEXT = '.?:+-*/%&|^,)]=<>';
  const CONTINUES_PREVIOUS = '=+-*/%&|^,([{:?<>!';
  function statementEnds(code, start) {
    const ends = [];
    let depth = 0, i = start;
    const n = code.length;
    while (i < n) {
      const c = code[i];
      if (c === '/' && code[i + 1] === '/') { const j = code.indexOf('\n', i); i = j < 0 ? n : j; continue; }
      if (c === '/' && code[i + 1] === '*') { const j = code.indexOf('*/', i + 2); if (j < 0) break; i = j + 2; continue; }
      if (c === '"' || c === "'" || c === '`') {
        let j = i + 1;
        while (j < n && code[j] !== c) j += code[j] === '\\' ? 2 : 1;
        if (j >= n) break;
        i = j + 1;
        continue;
      }
      if (c === '(' || c === '[' || c === '{') depth++;
      else if (c === ')' || c === ']' || c === '}') depth = Math.max(0, depth - 1);
      if (depth === 0 && (c === ';' || c === '\n' || c === '}')) {
        let j = i + 1;
        while (j < n && /\s/.test(code[j])) j++;
        if (j >= n) break; // nothing after it yet
        let k = i - (c === ';' ? 1 : 0);
        while (k >= start && /\s/.test(code[k])) k--;
        const last = k >= start ? code[k] : '';
        const statementIsEmpty = code.slice(start, i + 1).trim() === '' || code.slice(start, i + 1).trim() === ';';
        const next = code[j];
        const continues = CONTINUES_NEXT.includes(next) || (c === '}' && /^else\b|^catch\b|^finally\b/.test(code.slice(j))) ||
          (c !== ';' && CONTINUES_PREVIOUS.includes(last));
        if (!continues && !statementIsEmpty) { ends.push(i + 1); start = i + 1; }
        else if (statementIsEmpty) start = i + 1;
      }
      i++;
    }
    return ends;
  }

  function feed(message) {
    if (!worker || stopped) return;
    const code = String(message.code || '');
    if (!code.startsWith(source)) log('feed: the program changed under the stream; continuing from the new text');
    source = code;
    for (const end of statementEnds(source, scanned)) {
      const statement = source.slice(scanned, end).trim();
      if (statement && statement !== ';') pending.push(statement);
      scanned = end;
    }
    if (message.done) {
      const rest = source.slice(scanned).trim();
      if (rest) pending.push(rest);
      scanned = source.length;
      feedDone = true;
    }
    runNext();
  }

  // ------------------------------------------------------------------ running statements in the worker
  function runNext() {
    if (!running && !pending.length && feedDone && !ranSent) {
      // Every statement has run: every line the program says is known (a voice can be told there are no more).
      ranSent = true;
      send({ type: 'programRan', lines: lines.length });
    }
    if (running || stopped || !worker || !pending.length) return;
    running = { seq: ++seq, code: pending.shift() };
    worker.postMessage({ type: 'run', seq: running.seq, code: running.code });
    runTimer = setTimeout(() => stop('a statement ran longer than ' + STATEMENT_SECONDS + ' s: ' + running.code.slice(0, 80)), STATEMENT_SECONDS * 1000);
  }

  function ran(result) {
    if (!running || result.seq !== running.seq) return;
    clearTimeout(runTimer);
    const code = running.code;
    running = null;
    for (const warning of result.warnings || []) log('warning: ' + warning);
    if (result.error && errors++ < MAX_ERRORS) send({ type: 'error', text: result.error + ' in: ' + code.slice(0, 120) });
    // Marks and lines in the order the statement made them: a mark belongs to the latest say() before it. say().at
    // counts the program's marks before it, as markCount does here.
    const says = (result.says || []).slice();
    for (const mark of result.marks || []) {
      while (says.length && says[0].at <= markCount) addLine(says.shift());
      addItem(mark);
    }
    while (says.length) addLine(says.shift());
    runNext();
  }

  function stop(reason) {
    stopped = true;
    pending = [];
    clearTimeout(runTimer);
    running = null;
    if (worker) worker.terminate();
    worker = null;
    feedDone = true;
    send({ type: 'error', text: reason });
    runNext(); // nothing more will run: says programRan
  }

  // ------------------------------------------------------------------ lines and pacing
  function addLine(say) {
    const k = lines.length;
    const words = String(say.text).trim().split(/\s+/).filter(Boolean).length;
    const line = { k, text: say.text, words, arrive: now(), start: null, end: null, asked: false };
    lines.push(line);
    // The app's voice can start fetching this line's audio now, long before it is due.
    send({ type: 'line', k, text: line.text });
    if (pace === 'estimate') {
      const before = lines[k - 1];
      line.start = Math.max(line.arrive, before ? before.end + LINE_GAP : line.arrive);
      line.end = line.start + Pen.lineSeconds(words);
    }
  }

  function addItem(mark) {
    markCount++;
    if (mark.do === 'text') placeLabel(mark);
    else if (mark.do === 'card') addCard(mark);
    else { followLabels(mark); addStroke(mark); }
    // A 3D piece the app makes: asked for as soon as its statement runs, so it is being made while the voice talks.
    if (mark.do === 'part3d' && mark.kind === 'piece') {
      const p = mark.params || {};
      send({ type: 'pieceWanted', id: mark.id, text: String(p.make || p.name || 'a part'), of: p.of ? String(p.of) : '', size: Array.isArray(p.size) ? p.size.slice(0, 3) : [1, 1, 1] });
    }
    items.push({ mark, line: lines.length - 1, timing: Pen.naturalTiming(mark), index: items.length, path: undefined });
  }

  // A line still being said ends when the voice has got through its words, at the speed it has spoken so far.
  const lineEnd = line => line.end != null ? line.end : line.start + Pen.lineSeconds(line.words, Pen.speechRate(spoken));
  const lineIsOpen = line => !feedDone || pending.length > 0 || running !== null ? line.k === lines.length - 1 : false;

  function askForLines(t) {
    if (pace !== 'speech') return;
    for (const line of lines) {
      if (line.asked) continue;
      const before = lines[line.k - 1];
      if (before && !(before.end != null && before.end <= t)) break;
      line.asked = true;
      send({ type: 'lineDue', k: line.k, text: line.text });
      break;
    }
  }

  // The line the pen follows over a stroke mark (null for any other mark), the same every time for the same mark:
  // seeded by the mark's place in the program. Worked out once (an arrow moved with its label is worked out again).
  function pathOf(item) {
    if (item.path === undefined || item.pathFrom !== item.mark.pts) {
      item.path = Pen.centreline(item.mark, item.index + 1);
      item.pathFrom = item.mark.pts;
    }
    return item.path;
  }

  // Where the pen starts and ends a mark: null for a mark the pen does not draw.
  function endsOf(item) {
    if (!item.timing.pen) return null;
    const m = item.mark;
    if (m.do === 'text') return Pen.textEnds(m);
    if (m.do === 'highlight') return { from: [m.at[0], m.at[1] + m.h / 2], to: [m.at[0] + m.w, m.at[1] + m.h / 2] };
    const path = pathOf(item);
    return path ? { from: path[0], to: path[path.length - 1] } : null;
  }

  // The cursor's flight to a mark from where the pen is (or will be): none with Reduce Motion or when hurrying.
  function flightTo(item, from) {
    if (still || finishing || recording) return 0;
    const ends = endsOf(item);
    return ends ? Pen.flightSeconds(from, ends.from) : 0;
  }

  // The marks of the line still to draw, from item `first` on, as the plan wants them (StagePen.planLine).
  function stepsFrom(first, t) {
    const steps = [];
    let from = penPlace(t);
    for (let i = first; i < items.length && items[i].line === items[first].line; i++) {
      const item = items[i], ends = endsOf(item);
      steps.push({ seconds: item.timing.seconds, advance: item.timing.advance, lead: flightTo(item, from) });
      if (ends) from = ends.to;
    }
    return steps;
  }

  function tick() {
    const t = now();
    askForLines(t);
    while (released < items.length) {
      const item = items[released];
      const line = item.line >= 0 ? lines[item.line] : null;
      const lead = flightTo(item, penPlace(t));
      // After the mark before it: at 0.7 of a stroke the pen need not leave, at its end when the cursor must fly.
      let startAt = lastMark ? lastMark.inkAt + (lead > 0 ? lastMark.seconds : lastMark.advance) * lastMark.spread : 0;
      if (line && !finishing) {
        if (line.start == null) break;
        startAt = Math.max(startAt, line.start + LEAD);
      }
      if (t < startAt) break;
      let plan = { pace: 1, spread: 1, lead: 1 };
      if (finishing) plan = Pen.planLine([item.timing], 0, { finishing: true });
      else if (line) plan = Pen.planLine(stepsFrom(released, t), lineEnd(line) - TAIL - t, { open: lineIsOpen(line) });
      const seconds = item.timing.seconds * plan.pace, wait = lead * plan.lead;
      paint(item, seconds, wait, t);
      lastMark = { inkAt: t + wait, seconds: item.timing.seconds, advance: item.timing.advance, spread: plan.spread };
      inkDoneAt = Math.max(inkDoneAt, t + wait + seconds + 0.04);
      released++;
    }
    restPen(t);
    updateCaption(t);
    checkFinished(t);
    // While it is drawn the card grows: where the page takes the mouse follows it.
    if (t - hitsCheckedAt > 0.3 && !gesture) { hitsCheckedAt = t; showTools(); }
    // A hidden stage (sleep) stops here and costs nothing until wake schedules the next frame.
    if (!asleep) requestAnimationFrame(tick);
  }

  // sleep / wake: the app hides the stage window between drawings; a page still animating under a
  // hidden window kept the GPU busy for the rest of the session.
  let asleep = false;
  function sleep() { asleep = true; window.drawStage.isAsleep = true; if (Sound) Sound.rest(); }
  function wake() { if (!asleep) return; asleep = false; window.drawStage.isAsleep = false; requestAnimationFrame(tick); }

  function checkFinished(t) {
    if (finishedSent || !feedDone || pending.length || running || released < items.length || t < inkDoneAt) return;
    const last = lines[lines.length - 1];
    if (last && !finishing && (last.start == null || t < lineEnd(last))) return;
    finishedSent = true;
    showCaption(null);
    // Gone before the app measures the drawing or takes its picture.
    hidePen(true);
    if (Sound) Sound.rest(1);
    send({ type: 'finished' });
    for (const quiz of quizzes) if (quiz.unspoken) speakQuestion(quiz);
    showTools();
  }

  function updateCaption(t) {
    let shown = null;
    for (const line of lines) if (line.start != null && line.start <= t && t <= lineEnd(line) + 0.4) shown = line;
    showCaption(finishing ? null : shown);
  }

  function showCaption(line) {
    if (!line || recording) { caption.hidden = true; return; }
    if (caption.textContent !== line.text) caption.textContent = line.text;
    caption.hidden = false;
  }

  // ------------------------------------------------------------------ labels kept apart
  // A model places its labels by guesswork, and two of them often land on each other ("cloud" and "condensation" a
  // few units apart). Each label is measured before it is drawn: one that would cover a label already there (or a
  // card's title) moves the least it can, up or down before sideways, to a free spot inside its card (or the canvas),
  // off the strokes drawn so far when it can be, and an arrow with an end at it moves that end along with it. Labels
  // are drawn in a layer of their own over every stroke, so a stroke drawn after one never covers it.
  const LABEL_PAD = 3;    // around a label's letters and their white edge
  const LABEL_REACH = 30; // an arrow's end this close to a label's box (where the model put it) is at the label
  let labels = [];        // labels placed so far: {from: the box the model gave it, box: where it is, dx, dy}
  let cards = [];         // the cards' boxes: a label put on a card stays on it
  let strokes = [];       // the pen strokes so far, as straight pieces: {box, pieces: [[x1, y1, x2, y2], …]}
  let labelLayer = null;  // the <g> the labels are drawn in, last in the SVG

  const labelAttributes = m => ({
    x: m.at[0], y: m.at[1], 'font-size': m.size > 0 ? m.size : 26, 'dominant-baseline': 'middle',
    'text-anchor': m.align === 'start' || m.align === 'end' ? m.align : 'middle', 'font-weight': m.bold ? 700 : 400
  });
  const titleAttributes = (x, y) => ({ x: x + 22, y: y + 34, 'font-size': 26, 'font-weight': 700 });
  const overlaps = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
  const inside = (a, frame) => a.x >= frame.x && a.y >= frame.y && a.x + a.w <= frame.x + frame.w && a.y + a.h <= frame.y + frame.h;
  const distanceTo = (p, b) => Math.hypot(Math.max(b.x - p[0], 0, p[0] - b.x - b.w), Math.max(b.y - p[1], 0, p[1] - b.y - b.h));

  // The box words take on the canvas (the SVG's viewBox units), from a hidden copy of them.
  function measure(attributes, words) {
    const probe = el('text', Object.assign({ visibility: 'hidden' }, attributes));
    probe.textContent = words;
    svg.appendChild(probe);
    const b = probe.getBBox();
    probe.remove();
    return { x: b.x - LABEL_PAD, y: b.y - LABEL_PAD, w: b.width + 2 * LABEL_PAD, h: b.height + 2 * LABEL_PAD };
  }

  // A stroke's straight pieces (an ellipse goes round in 24 of them), so a moved label can keep off it.
  const range24 = Array.from({ length: 24 }, (_, k) => k);
  function addStroke(m) {
    let pts = null, closed = false;
    if (m.do === 'line' || m.do === 'arrow') pts = m.pts;
    else if (m.do === 'poly') { pts = m.pts; closed = true; }
    else if (m.do === 'rect') { const [x, y] = m.at; pts = [[x, y], [x + m.w, y], [x + m.w, y + m.h], [x, y + m.h]]; closed = true; }
    else if (m.do === 'ellipse') { pts = range24.map(k => [m.at[0] + m.rx * Math.cos(k * Math.PI / 12), m.at[1] + m.ry * Math.sin(k * Math.PI / 12)]); closed = true; }
    if (!pts || pts.length < 2) return;
    const pieces = [];
    for (let i = 1; i < pts.length + (closed ? 1 : 0); i++) {
      const a = pts[i - 1], b = pts[i % pts.length];
      pieces.push([a[0], a[1], b[0], b[1]]);
    }
    const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
    const x = Math.min(...xs), y = Math.min(...ys);
    strokes.push({ box: { x, y, w: Math.max(...xs) - x, h: Math.max(...ys) - y }, pieces });
  }

  // Whether a straight piece crosses a box (Liang-Barsky clipping).
  function crosses(piece, box) {
    const [x1, y1, x2, y2] = piece, dx = x2 - x1, dy = y2 - y1;
    let t0 = 0, t1 = 1;
    for (const [p, q] of [[-dx, x1 - box.x], [dx, box.x + box.w - x1], [-dy, y1 - box.y], [dy, box.y + box.h - y1]]) {
      if (p === 0) { if (q < 0) return false; continue; }
      const t = q / p;
      if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; } else { if (t < t0) return false; if (t < t1) t1 = t; }
    }
    return true;
  }
  const strokesUnder = box => strokes.filter(s => overlaps(s.box, box) && s.pieces.some(piece => crosses(piece, box))).length;

  function addCard(m) {
    cards.push({ x: m.at[0], y: m.at[1], w: m.w, h: m.h });
    if (!m.title) return;
    const title = measure(titleAttributes(m.at[0], m.at[1]), m.title);
    labels.push({ from: title, box: title, dx: 0, dy: 0 });
  }

  function placeLabel(m) {
    const from = measure(labelAttributes(m), m.text);
    if (!(from.w > 0 && from.h > 0)) return;
    const centre = [from.x + from.w / 2, from.y + from.h / 2];
    const frame = cards.slice().reverse().find(card => distanceTo(centre, card) === 0) || { x: 0, y: 0, w: W, h: H };
    const fits = box => inside(box, frame) && !labels.some(other => overlaps(box, other.box));
    let dx = 0, dy = 0;
    if (!fits(from)) {
      // Spots on a grid of half a line around it, nearest first: a step sideways costs more than one up or down, one
      // towards the labels it covers more than one away from them, and a spot on a stroke more than a short way on.
      // A label below (or above) one it covers stays below (or above) it: "runs back" / "to the sea" still read in order.
      const covered = labels.filter(other => overlaps(from, other.box));
      const theirMiddle = covered.reduce((sum, other) => sum + other.box.y + other.box.h / 2, 0) / (covered.length || 1);
      const away = covered.length && centre[1] < theirMiddle ? -1 : 1;
      const inOrder = spot => covered.every(other => {
        const side = Math.sign(centre[1] - (other.box.y + other.box.h / 2));
        return !side || Math.sign(spot.y + spot.h / 2 - (other.box.y + other.box.h / 2)) === side;
      });
      const step = Math.max(6, from.h / 2), steps = Math.ceil(Math.max(120, from.h * 4) / step);
      let best = Infinity;
      for (let j = -steps; j <= steps; j++) {
        for (let i = -steps; i <= steps; i++) {
          let cost = Math.hypot(i * step * 1.6, j * step) + (j && Math.sign(j) !== away ? step / 2 : 0);
          if (cost >= best) continue;
          const spot = { x: from.x + i * step, y: from.y + j * step, w: from.w, h: from.h };
          if (!fits(spot) || !inOrder(spot)) continue;
          cost += strokesUnder(spot) * step * 4;
          if (cost >= best) continue;
          best = cost; dx = i * step; dy = j * step;
        }
      }
    }
    labels.push({ from, box: { x: from.x + dx, y: from.y + dy, w: from.w, h: from.h }, dx, dy });
    if (!dx && !dy) return;
    log(`label "${m.text}" moved by ${Math.round(dx)}, ${Math.round(dy)} to keep off another`);
    m.at = [m.at[0] + dx, m.at[1] + dy];
    // Arrows not drawn yet that end at it go with it.
    for (let k = released; k < items.length; k++) followLabels(items[k].mark);
  }

  // An arrow with one end at a moved label (the nearest label, where the model put it) moves that end with it. Only
  // arrows: the prompt points a label at its thing with one, and a plain line by a label is more often a shape (rain).
  function followLabels(mark) {
    if (mark.followed || mark.do !== 'arrow') return;
    const ends = [0, mark.pts.length - 1].map(i => {
      let nearest = null, gap = Infinity;
      for (const label of labels) {
        const d = distanceTo(mark.pts[i], label.from);
        if (d < gap) { gap = d; nearest = label; }
      }
      return gap <= LABEL_REACH && (nearest.dx || nearest.dy) ? { i, label: nearest } : null;
    }).filter(Boolean);
    if (ends.length !== 1) return; // neither end, or both (an arrow between two moved labels): it stays as drawn
    const { i, label } = ends[0];
    // A new list, so the pen's line through the arrow is worked out again (pathOf).
    mark.pts = mark.pts.map((p, k) => k === i ? [p[0] + label.dx, p[1] + label.dy] : p);
    mark.followed = true;
  }

  // ------------------------------------------------------------------ painting
  function defs() {
    const d = el('defs');
    const shadow = el('filter', { id: 'card-shadow', x: '-10%', y: '-10%', width: '130%', height: '140%' });
    shadow.appendChild(el('feDropShadow', { dx: 0, dy: 4, stdDeviation: 6, 'flood-color': '#000', 'flood-opacity': 0.22 }));
    d.appendChild(shadow);
    return d;
  }

  function el(name, attributes) {
    const node = document.createElementNS(SVG_NS, name);
    for (const key in attributes || {}) node.setAttribute(key, attributes[key]);
    return node;
  }

  const clamp = Pen.clamp;

  // Marks appear in place with Reduce Motion: a short fade, nothing travelling.
  const APPEAR = 0.18;
  function appear(node, delay, opacity) {
    node.animate([{ opacity: 0 }, { opacity: opacity == null ? 1 : opacity }], { duration: APPEAR * 1000, delay: (delay || 0) * 1000, easing: 'ease-out', fill: 'both' });
  }

  // One pen stroke along `points`: a white halo under ink with a tapered outline (StagePen.outline), both revealed
  // from start to end by a mask the pen's line is drawn in. The mask goes once the stroke is drawn, so a finished
  // drawing is plain shapes. `taper` 1 is a full stroke; a dashed stroke is one reveal over many short dashes.
  function ink(group, points, m, seconds, delay, seed, taper) {
    const width = m.width > 0 ? m.width : 3.2, phase = Pen.rng(seed)() * Math.PI * 2;
    const centre = Pen.pathData(points, false);
    const body = el('g');
    body.appendChild(el('path', { d: centre, class: 'halo', 'stroke-width': width + 5 }));
    const pieces = m.dashed ? Pen.dashes(points, Math.max(8, width * 3), width * 2.5) : [points];
    const outline = pieces.map(piece => Pen.pathData(Pen.outline(piece, width, { taper: m.dashed ? 0.3 : taper, phase }), true)).join('');
    body.appendChild(el('path', { d: outline, class: 'ink', fill: tone(m.color, TONES.blue) }));
    group.appendChild(body);
    if (still) { appear(body, delay); return; }
    const xs = points.map(p => p[0]), ys = points.map(p => p[1]), pad = width + 12;
    const x = Math.min(...xs) - pad, y = Math.min(...ys) - pad;
    const id = 'ink' + (++uid);
    const mask = el('mask', { id, maskUnits: 'userSpaceOnUse', x, y, width: Math.max(...xs) + pad - x, height: Math.max(...ys) + pad - y });
    const front = el('path', { d: centre, class: 'reveal', 'stroke-width': width + 10 });
    const total = Pen.polylineLength(points) + 2;
    front.style.strokeDasharray = `${total} ${total}`;
    mask.appendChild(front);
    group.appendChild(mask);
    body.setAttribute('mask', `url(#${id})`);
    const drawn = front.animate([{ strokeDashoffset: total }, { strokeDashoffset: 0 }],
      { duration: seconds * 1000, delay: (delay || 0) * 1000, easing: Pen.PEN_EASE_CSS, fill: 'both' });
    drawn.onfinish = () => { body.removeAttribute('mask'); mask.remove(); };
  }

  // ------------------------------------------------------------------ the pen's cursor
  // A small pen that flies to each mark and draws it, in a layer of its own over the drawing (so it is never in the
  // drawing's box, its picture or its video). It shows while the pen is at work and goes when it rests.
  const penLayer = el('svg', { id: 'pen', preserveAspectRatio: 'xMidYMid meet', viewBox: `0 0 ${W} ${H}` });
  const penNode = el('g');
  const penShape = el('g');
  const penTip = el('path', { d: 'M0 0L8.5 -3.2L3.2 -8.5Z', fill: TONES.blue });
  penShape.appendChild(el('path', { d: 'M8.5 -3.2L3.2 -8.5L23 -28.3A3.7 3.7 0 0 1 28.3 -23Z', fill: '#FFFFFF', stroke: '#1E2230', 'stroke-width': 1.3, 'stroke-linejoin': 'round' }));
  penShape.appendChild(el('path', { d: 'M19.6 -24.9L24.9 -19.6', fill: 'none', stroke: '#1E2230', 'stroke-width': 1.3 }));
  penShape.appendChild(penTip);
  penNode.appendChild(penShape);
  penLayer.appendChild(penNode);
  penNode.style.opacity = 0;
  board.appendChild(penLayer);

  let penShown = false;
  let penTrack = null;           // where the pen is going: {points, from: when it sets off, seconds, ease}
  let penBusyUntil = 0;
  const penMoves = [];           // the cursor's running animations, so the pen can be stopped where it is

  // Where the pen's tip is at time t; null while it is off the page.
  function penPlace(t) {
    if (!penShown || !penTrack) return null;
    const progress = penTrack.seconds > 0 ? clamp((t - penTrack.from) / penTrack.seconds, 0, 1) : 1;
    return Pen.pointAt(penTrack.points, penTrack.ease(progress));
  }

  const translate = p => ({ transform: `translate(${p[0].toFixed(1)}px, ${p[1].toFixed(1)}px)` });

  // Sends the pen over a mark: `flight` seconds to its start (from where the pen is, or a touch-down beside it when
  // the pen was off the page), then along `points` for `seconds` on the ink's own curve (linear for words).
  function movePen(points, seconds, flight, t, colour, linear) {
    if (still || finishing || recording || !points || points.length < 2) return;
    const here = penPlace(t), start = points[0];
    for (const move of penMoves.splice(0)) move.cancel();
    // The pen's size on the screen stays the same whatever the canvas is scaled by.
    const k = Math.min(window.innerWidth / W, window.innerHeight / H) || 1;
    penShape.setAttribute('transform', `scale(${(1.15 / k).toFixed(3)})`);
    penTip.setAttribute('fill', colour);
    const easing = linear ? 'linear' : Pen.PEN_EASE_CSS;
    let follow = Pen.sampleAlong(points, 24).map(translate);
    if (flight > 0) {
      const from = here || [start[0] + 26 / k, start[1] - 30 / k];
      penMoves.push(penNode.animate(Pen.flightArc(from, start, 8).map(translate), { duration: flight * 1000, easing: 'ease-in-out', fill: 'both' }));
    } else if (here) {
      // Close by: the pen hops from where it is and meets the ink just after it starts.
      const hop = 0.06;
      follow = [Object.assign(translate(here), { offset: 0 })].concat(
        Pen.sampleAlong(points, 22, hop).map((p, i) => Object.assign(translate(p), { offset: hop + (1 - hop) * i / 22 })));
    }
    penMoves.push(penNode.animate(follow, { duration: seconds * 1000, delay: flight * 1000, easing, fill: 'both' }));
    if (!penShown) {
      penShown = true;
      penNode.style.opacity = 1;
      penNode.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 160, easing: 'ease-out' });
    }
    penTrack = { points, from: t + flight, seconds, ease: linear ? (v => v) : Pen.penEase };
    penBusyUntil = t + flight + seconds;
  }

  // The pen waits where its last stroke ended while more is coming, and leaves the page when nothing has been.
  const PEN_WAITS = 1.2;
  function restPen(t) {
    if (penShown && t > penBusyUntil + PEN_WAITS) hidePen(false);
  }

  function hidePen(atOnce) {
    if (!penShown && !atOnce) return;
    penShown = false;
    penTrack = null;
    penNode.style.opacity = 0;
    if (atOnce) for (const move of penMoves.splice(0)) move.cancel();
    else penNode.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 200, easing: 'ease-in' });
  }

  // The pen's sound over a stroke (when the app asked for sound, and never while hurrying to finish).
  const canSound = () => Sound && Sound.isOn() && !finishing && !recording;
  function soundStroke(length, seconds, delay) {
    if (canSound() && !still) Sound.stroke(Pen.speedCurve(length, seconds, 24), seconds, delay, 'pen');
  }
  function soundTick(delay, strength) {
    if (canSound()) Sound.tick(delay, strength);
  }

  // ------------------------------------------------------------------ a quiz on the drawing (quiz())
  // One question at a time beside the drawing, its choices to tap. The app reads each question out (say) once the
  // drawing has finished speaking, then whether the answer was right; the part it is about swells for a moment; the
  // score last. The panel sits in the screen's room beside the drawing (placeQuizzes), not on it, and follows it.
  const quizzes = [];
  function drawQuiz(m, seconds) {
    const panel = document.createElement('div');
    panel.className = 'quiz';
    document.body.appendChild(panel);
    panel.animate([{ opacity: 0 }, { opacity: 1 }], { duration: Math.max(150, seconds * 1000), fill: 'both' });
    const quiz = { panel, questions: m.questions, i: 0, score: 0, unspoken: true, gone: false, answered: false, timers: [], at: null };
    quizzes.push(quiz);
    showQuestion(quiz);
    placeQuizzes();
    if (finishedSent) speakQuestion(quiz);
  }

  // Each quiz panel goes where there is most room beside the drawing's box: left or right of it, else below or above it,
  // else over its bottom edge. It only moves when its place has clearly changed (a figure's tags sway the box a little).
  function placeQuizzes() {
    if (!quizzes.length) return;
    const box = contentBounds();
    if (!box) return;
    const gap = 14, edge = 12, vw = window.innerWidth, vh = window.innerHeight;
    const room = { left: box.x - gap - edge, right: vw - box.x - box.w - gap - edge, below: vh - box.y - box.h - gap - edge, above: box.y - gap - edge };
    for (const quiz of quizzes) {
      const panel = quiz.panel;
      let width, x, y;
      const side = room.right >= room.left ? 'right' : 'left';
      if (room[side] >= 300) {
        width = Math.min(520, room[side]);
        panel.style.width = width + 'px';
        const h = panel.offsetHeight;
        x = side === 'right' ? box.x + box.w + gap : box.x - gap - width;
        y = clamp(box.y + box.h / 2 - h / 2, edge, Math.max(edge, vh - h - edge));
      } else {
        width = Math.min(560, Math.max(280, box.w - 2 * gap));
        panel.style.width = width + 'px';
        const h = panel.offsetHeight;
        x = clamp(box.x + box.w / 2 - width / 2, edge, Math.max(edge, vw - width - edge));
        y = room.below >= h ? box.y + box.h + gap : room.above >= h ? box.y - gap - h : box.y + box.h - h - gap;
      }
      if (!quiz.at || Math.abs(quiz.at.x - x) > 12 || Math.abs(quiz.at.y - y) > 12) {
        quiz.at = { x, y };
        panel.style.left = Math.round(x) + 'px';
        panel.style.top = Math.round(y) + 'px';
      }
    }
  }

  const spokenChoices = choices => choices.length > 1 ? choices.slice(0, -1).join(', ') + ' or ' + choices[choices.length - 1] + '?' : choices[0];
  function speakQuestion(quiz) {
    if (quiz.gone || quiz.i >= quiz.questions.length) return;
    quiz.unspoken = false;
    const q = quiz.questions[quiz.i];
    send({ type: 'say', text: q.q + ' ' + spokenChoices(q.choices) });
  }

  function showQuestion(quiz) {
    const q = quiz.questions[quiz.i], n = quiz.questions.length;
    quiz.panel.textContent = '';
    const add = (tag, className, text) => { const node = document.createElement(tag); node.className = className; if (text != null) node.textContent = text; return node; };
    quiz.panel.appendChild(add('div', 'count', n > 1 ? 'Question ' + (quiz.i + 1) + ' of ' + n : 'Question'));
    quiz.panel.appendChild(add('div', 'question', q.q));
    const choices = add('div', 'choices');
    // Two columns when the choices are short and there's room; one otherwise.
    const narrow = quiz.panel.getBoundingClientRect().width < 420 || q.choices.some(c => c.length > 24);
    choices.style.setProperty('--columns', narrow ? 1 : 2);
    q.choices.forEach((choice, j) => {
      const button = add('button', '', choice);
      button.type = 'button';
      button.addEventListener('click', () => answer(quiz, j));
      choices.appendChild(button);
    });
    quiz.panel.appendChild(choices);
    quiz.panel.appendChild(add('div', 'feedback'));
    quiz.at = null;
    placeQuizzes();
  }

  function answer(quiz, j) {
    if (quiz.gone || quiz.answered) return;
    quiz.answered = true;
    const q = quiz.questions[quiz.i], right = j === q.answer;
    if (right) quiz.score++;
    quiz.panel.querySelectorAll('.choices button').forEach((button, b) => {
      button.disabled = true;
      if (b === q.answer) button.classList.add('right');
      else if (b === j) button.classList.add('wrong');
    });
    const said = right ? (q.right || "Yes, that's right!") : (q.wrong || "Not quite: it's " + q.choices[q.answer] + '.');
    const feedback = quiz.panel.querySelector('.feedback');
    feedback.textContent = said;
    feedback.className = 'feedback ' + (right ? 'right' : 'wrong');
    send({ type: 'say', text: said });
    log('quiz: question ' + (quiz.i + 1) + (right ? ' right' : ' wrong'));
    // The part it is about swells for a moment.
    if (window.Figure3D) for (const id of q.parts || []) {
      window.Figure3D.apply({ do: 'change3d', part: id, params: { size: 1.3, secs: 0.35 } }, 0.35, null);
      quiz.timers.push(setTimeout(() => window.Figure3D.apply({ do: 'change3d', part: id, params: { size: 1 / 1.3, secs: 0.5 } }, 0.5, null), 1600));
    }
    // On to the next question once that has been said, or the score.
    const words = said.split(/\s+/).length;
    quiz.timers.push(setTimeout(() => {
      if (quiz.gone) return;
      quiz.i++;
      quiz.answered = false;
      if (quiz.i < quiz.questions.length) { showQuestion(quiz); speakQuestion(quiz); return; }
      const n = quiz.questions.length, all = quiz.score === n;
      const score = 'You got ' + quiz.score + ' out of ' + n + (all ? '. Brilliant!' : quiz.score >= n / 2 ? '. Well done!' : '. Good try!');
      quiz.panel.textContent = '';
      const done = document.createElement('div');
      done.className = 'question done';
      done.textContent = score;
      quiz.panel.appendChild(done);
      send({ type: 'say', text: score });
      log('quiz: ' + quiz.score + ' of ' + n);
    }, Math.max(2200, (words / WORDS_PER_SECOND + 1.1) * 1000)));
  }

  function clearQuizzes() {
    for (const quiz of quizzes) { quiz.gone = true; quiz.timers.forEach(clearTimeout); quiz.panel.remove(); }
    quizzes.length = 0;
  }

  // A follow-up's change to a 2D mark already drawn: hidden (true fades it out, false brings it back), color (its pen
  // strokes, text and fill).
  function change2d(m, seconds) {
    const group = drawn.get(m.target);
    if (!group) { log('change: no mark ' + m.target + ' on the stage'); return; }
    const ms = Math.max(150, seconds * 1000);
    if (m.hidden === true) group.animate([{ opacity: 1 }, { opacity: 0 }], { duration: ms, fill: 'forwards' });
    if (m.hidden === false) group.animate([{ opacity: 0 }, { opacity: 1 }], { duration: ms, fill: 'forwards' });
    const color = m.color ? tone(m.color, null) : null;
    if (color) {
      for (const node of group.querySelectorAll('.pen')) node.setAttribute('stroke', color);
      for (const node of group.querySelectorAll('path.ink')) node.setAttribute('fill', color);
      for (const node of group.querySelectorAll('text')) node.setAttribute('fill', color);
      for (const node of group.querySelectorAll('path.fade')) node.setAttribute('fill', color);
    }
  }

  function fillShape(group, d, m, delay) {
    if (!m.fill) return;
    const fill = el('path', { d, fill: tone(m.fill, TONES.yellow), stroke: 'none', class: 'fade' });
    group.insertBefore(fill, group.firstChild);
    fill.animate([{ opacity: 0 }, { opacity: m.fillOpacity > 0 ? m.fillOpacity : 0.3 }], { duration: 350, delay: delay * 1000, fill: 'forwards' });
  }

  // Words and the marker's sweep are uncovered from the left (in place with Reduce Motion).
  function reveal(group, node, box, seconds, delay) {
    if (still) { appear(node, delay, Number(node.getAttribute('opacity')) || 1); return; }
    const id = 'clip' + (++uid);
    const clip = el('clipPath', { id });
    const rect = el('rect', { x: box.x - 6, y: box.y - 6, width: 0, height: box.height + 12 });
    clip.appendChild(rect);
    group.appendChild(clip);
    node.setAttribute('clip-path', `url(#${id})`);
    rect.animate([{ width: '0px' }, { width: `${box.width + 12}px` }], { duration: seconds * 1000, delay: (delay || 0) * 1000, easing: 'linear', fill: 'both' });
  }

  // Paints a mark over `seconds`, its ink starting `wait` seconds from now (t), once the pen's cursor has flown to it.
  function paint(item, seconds, wait, t) {
    const m = item.mark, seed = item.index + 1;
    if (m.do === 'change2d') { change2d(m, seconds); return; }
    if (m.do === 'quiz') { drawQuiz(m, seconds); return; }
    if (m.do === 'quiz') { drawQuiz(m, seconds); return; }
    const group = el('g');
    if (m.do === 'text') labelLayer.appendChild(group); else svg.insertBefore(group, labelLayer);
    if (Number.isInteger(m.n)) drawn.set(m.n, group);
    const colour = tone(m.color, TONES.blue);
    switch (m.do) {
      case 'line': case 'poly': case 'rect': case 'ellipse': {
        const points = pathOf(item);
        if (!points) { log('a ' + m.do + ' without usable points was skipped'); break; }
        ink(group, points, m, seconds, wait, seed, 1);
        if (m.do !== 'line') fillShape(group, Pen.pathData(points, false), m, wait + (still ? 0 : seconds));
        movePen(points, seconds, wait, t, colour);
        soundStroke(Pen.polylineLength(points), seconds, wait);
        break;
      }
      case 'arrow': {
        const points = pathOf(item);
        if (!points) { log('an arrow without usable points was skipped'); break; }
        // The shaft, then the head's two flicks.
        const head = Math.min(Pen.PEN.head, seconds * 0.4), shaft = Math.max(0.1, seconds - head);
        ink(group, points, m, shaft, wait, seed, 1);
        Pen.arrowBarbs(m.pts).forEach((barb, side) => ink(group, barb, m, head * 0.75, wait + (still ? 0 : shaft), seed + 7919 * (side + 1), 0.5));
        movePen(points, shaft, wait, t, colour);
        soundStroke(Pen.polylineLength(points), shaft, wait);
        if (!still) soundTick(wait + shaft, 0.6);
        break;
      }
      case 'dot': {
        const dot = el('circle', { cx: m.at[0], cy: m.at[1], r: m.r, fill: colour, stroke: '#fff', 'stroke-width': 2 });
        group.appendChild(dot);
        if (still) { appear(dot, wait); soundTick(wait, 0.7); break; }
        dot.style.transformBox = 'fill-box';
        dot.style.transformOrigin = 'center';
        dot.animate([{ transform: 'scale(0)' }, { transform: 'scale(1.25)' }, { transform: 'scale(1)' }], { duration: seconds * 1000, delay: wait * 1000, fill: 'both' });
        soundTick(wait + seconds * 0.4, 0.7);
        break;
      }
      case 'text': {
        const attributes = labelAttributes(m);
        const text = el('text', Object.assign(attributes, {
          fill: tone(m.color, TONES.ink), stroke: '#fff', 'stroke-width': Math.max(3, attributes['font-size'] * 0.2)
        }));
        text.textContent = m.text;
        group.appendChild(text);
        const box = text.getBBox();
        reveal(group, text, box, seconds, wait);
        // The pen writes along under the words.
        const y = box.y + box.height * 0.62;
        movePen([[box.x, y], [box.x + box.width, y]], seconds, wait, t, tone(m.color, TONES.ink), true);
        if (canSound() && !still) Sound.stroke(Pen.writingCurve(String(m.text).length, 24), seconds, wait, 'writing');
        break;
      }
      case 'highlight': {
        const [x, y] = m.at;
        const mark = el('rect', { x, y, width: m.w, height: m.h, rx: Math.min(6, m.h / 4), fill: tone(m.color, TONES.yellow), opacity: 0.38 });
        group.appendChild(mark);
        reveal(group, mark, { x, y, width: m.w, height: m.h }, seconds, wait);
        movePen([[x, y + m.h / 2], [x + m.w, y + m.h / 2]], seconds, wait, t, tone(m.color, TONES.yellow), true);
        if (canSound() && !still) Sound.stroke(Array.from({ length: 13 }, () => 0.6), seconds, wait, 'marker');
        break;
      }
      case 'figure': case 'part3d': case 'label3d': case 'motion3d': case 'view3d': case 'change3d': case 'tempo3d': case 'labels3d': {
        if (!window.Figure3D) { log('3D is not loaded; skipped ' + m.do); break; }
        let rect = null;
        if (m.do === 'figure') {
          // The figure's box on the board: the canvas units through the SVG's viewBox (xMidYMid meet). Not the SVG's
          // screen mapping, which has the board's move and size in too.
          const k = Math.min(window.innerWidth / W, window.innerHeight / H);
          const ox = (window.innerWidth - W * k) / 2, oy = (window.innerHeight - H * k) / 2;
          rect = { x: ox + k * m.at[0], y: oy + k * m.at[1], w: k * m.w, h: k * m.h };
        }
        window.Figure3D.apply(m, seconds, rect);
        if (m.do === 'figure' || (m.do === 'part3d' && !(m.params && (m.params.hidden || m.params.quick)))) soundTick(seconds * 0.5, 0.5);
        break;
      }
      case 'photo': {
        // A polaroid: the frame and caption now, the photo when the app has found it (photoWanted → photo).
        const [x, y] = m.at, pad = 10, captionRoom = m.caption ? 34 : 12;
        group.setAttribute('data-kind', 'photo');
        const frame = el('g', { filter: 'url(#card-shadow)' });
        frame.appendChild(el('rect', { x, y, width: m.w, height: m.h, rx: 6, fill: '#FFFFFF' }));
        const well = el('rect', { x: x + pad, y: y + pad, width: m.w - 2 * pad, height: m.h - pad - captionRoom, fill: '#ECEEF2' });
        frame.appendChild(well);
        if (m.caption) {
          const caption = el('text', { x: x + m.w / 2, y: y + m.h - 16, 'font-size': 22, 'text-anchor': 'middle', 'dominant-baseline': 'middle', fill: TONES.ink, stroke: 'none' });
          caption.textContent = m.caption;
          frame.appendChild(caption);
        }
        group.appendChild(frame);
        if (still) appear(frame, 0);
        else frame.animate([{ opacity: 0, transform: 'rotate(-2deg) translateY(10px)' }, { opacity: 1, transform: 'rotate(0) translateY(0)' }],
          { duration: seconds * 1000, easing: 'ease-out', fill: 'both' });
        soundTick(still ? 0 : seconds * 0.7, 0.9);
        const id = 'photo' + (++uid);
        photos.set(id, { group: frame, box: { x: x + pad, y: y + pad, w: m.w - 2 * pad, h: m.h - pad - captionRoom } });
        send({ type: 'photoWanted', id, query: String(m.query), text: String(m.caption || '') });
        break;
      }
      case 'card': {
        const [x, y] = m.at;
        group.setAttribute('data-kind', 'card');
        const card = el('g', { filter: 'url(#card-shadow)' });
        card.appendChild(el('rect', { x, y, width: m.w, height: m.h, rx: 14, fill: tone(m.fill, '#FFF8E7'), stroke: '#00000014' }));
        if (m.title) {
          const title = el('text', Object.assign(titleAttributes(x, y), { fill: TONES.ink, stroke: 'none' }));
          title.textContent = m.title;
          card.appendChild(title);
        }
        group.appendChild(card);
        if (still) appear(card, 0);
        else card.animate([{ opacity: 0, transform: 'translateY(12px)' }, { opacity: 1, transform: 'translateY(0)' }],
          { duration: seconds * 1000, easing: 'ease-out', fill: 'both' });
        soundTick(still ? 0 : seconds * 0.7, 0.6);
        break;
      }
      default:
        log('unknown mark: ' + m.do);
    }
  }

  // ------------------------------------------------------------------ photos the app found
  const photos = new Map();
  function showPhoto(message) {
    const photo = photos.get(message.id);
    if (!photo) return;
    photos.delete(message.id);
    const { box, group } = photo;
    if (message.failed || !message.data) {
      const note = el('text', { x: box.x + box.w / 2, y: box.y + box.h / 2, 'font-size': 18, 'text-anchor': 'middle', fill: TONES.grey, stroke: 'none' });
      note.textContent = 'no photo found';
      group.appendChild(note);
      return;
    }
    const clip = el('clipPath', { id: message.id + 'clip' });
    clip.appendChild(el('rect', { x: box.x, y: box.y, width: box.w, height: box.h }));
    group.appendChild(clip);
    const image = el('image', { x: box.x, y: box.y, width: box.w, height: box.h, preserveAspectRatio: 'xMidYMid slice', 'clip-path': `url(#${message.id}clip)` });
    image.setAttribute('href', message.data);
    group.appendChild(image);
    image.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 600, fill: 'both' });
    if (message.credit) {
      const credit = el('text', { x: box.x + box.w - 6, y: box.y + box.h - 6, 'font-size': 11, 'text-anchor': 'end', fill: '#FFFFFF', stroke: '#000000', 'stroke-width': 2, 'paint-order': 'stroke', 'font-family': 'Segoe UI, sans-serif' });
      credit.textContent = String(message.credit).slice(0, 80);
      group.appendChild(credit);
    }
  }

  // ------------------------------------------------------------------ the drawing's box, and its buttons
  // What is drawn, in page pixels: its cards and photos with the 3D figures and their tags (marks pointing at the real
  // screen outside them are left out), or every mark when there is no card; BOX_PAD round it.
  const BOX_PAD = 14;
  function contentBounds() {
    let l = Infinity, t = Infinity, r = -Infinity, b = -Infinity;
    const add = box => {
      if (!box || !(box.width > 1) || !(box.height > 1)) return;
      l = Math.min(l, box.left); t = Math.min(t, box.top); r = Math.max(r, box.left + box.width); b = Math.max(b, box.top + box.height);
    };
    const framed = svg.querySelectorAll('[data-kind="card"], [data-kind="photo"]');
    if (framed.length) framed.forEach(node => add(node.getBoundingClientRect()));
    else for (const node of svg.children) if (node.tagName !== 'defs') add(node.getBoundingClientRect());
    if (window.Figure3D) for (const f of window.Figure3D.rects()) add({ left: f.x, top: f.y, width: f.w, height: f.h });
    for (const node of document.getElementById('labels3d').children) add(node.getBoundingClientRect());
    if (!isFinite(l)) return null;
    l = Math.max(0, l - BOX_PAD); t = Math.max(0, t - BOX_PAD);
    r = Math.min(window.innerWidth, r + BOX_PAD); b = Math.min(window.innerHeight, b + BOX_PAD);
    return { x: l, y: t, w: r - l, h: b - t };
  }

  // A drawing on a card (or a photo) can be moved and resized: not marks drawn straight over the screen, which point
  // at what is under them.
  function movable() {
    return !recording && !!svg.querySelector('[data-kind="card"], [data-kind="photo"]');
  }

  // The buttons show in the drawing's top-right corner, and the grip in its bottom-right one, once the drawing has
  // finished and the app remembers it.
  let dragArea = null, hitsCheckedAt = 0, lastHits = '';
  function showTools() {
    const show = remembered && finishedSent && !recording;
    const canMove = movable();
    const box = show || canMove ? contentBounds() : null;
    tools.hidden = grip.hidden = !(show && box);
    if (show && box) {
      tools.style.left = Math.max(0, box.x + box.w - tools.offsetWidth - 16) + 'px';
      tools.style.top = (box.y + 16) + 'px';
      // In the card's corner (the box has BOX_PAD round it).
      grip.style.left = (box.x + box.w - BOX_PAD - grip.offsetWidth) + 'px';
      grip.style.top = (box.y + box.h - BOX_PAD - grip.offsetHeight) + 'px';
    }
    dragArea = canMove ? box : null;
    placeQuizzes();
    svg.style.cursor = canMove ? 'move' : '';
    if (!gesture) reportHits();
  }

  // Where the stage window takes the mouse: over a card it can be dragged by, the 3D figures, the buttons and the grip
  // (nowhere while recording, so a pointer resting over the drawing doesn't light up its parts in the video).
  function reportHits() {
    const rects = window.Figure3D && !recording ? window.Figure3D.rects() : [];
    if (dragArea && !recording) rects.push(dragArea);
    if (!recording) for (const quiz of quizzes) { const box = quiz.panel.getBoundingClientRect(); rects.push({ x: box.left, y: box.top, w: box.width, h: box.height }); }
    for (const node of [tools, grip]) {
      if (node.hidden || recording) continue;
      const box = node.getBoundingClientRect();
      rects.push({ x: box.left, y: box.top, w: box.width, h: box.height });
    }
    const rounded = rects.map(r => ({ x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.w), h: Math.round(r.h) }));
    const key = JSON.stringify(rounded);
    if (key === lastHits) return;
    lastHits = key;
    send({ type: 'figures', rects: rounded });
  }

  for (const button of tools.querySelectorAll('button')) {
    button.addEventListener('click', () => {
      if (button.disabled) return;
      if (button.dataset.act === 'quiz') send({ type: 'quiz' }); else send({ type: 'save', text: button.dataset.save });
    });
  }

  // ------------------------------------------------------------------ moving and resizing the drawing
  // The board (the drawing, its figures and their tags) is placed on the page at place.s × its own size, moved by
  // (place.x, place.y): dragged by its card, resized by the grip or the wheel.
  const place = { x: 0, y: 0, s: 1 };
  const SMALLEST = 0.35, LARGEST = 3, KEEP_SHOWING = 80;
  let gesture = null;

  function applyPlace(settled) {
    board.style.transform = place.x || place.y || place.s !== 1 ? `translate(${place.x}px, ${place.y}px) scale(${place.s})` : '';
    // The 3D figures are drawn again at the new size once it settles, so they stay sharp.
    if (settled && window.Figure3D) window.Figure3D.setScale(place.s);
    showTools();
  }

  function resetPlace() {
    place.x = place.y = 0;
    place.s = 1;
    gesture = null;
    applyPlace(true);
  }

  // Scales the board to s keeping the page point (ax, ay) where it is.
  function scaleAbout(ax, ay, from, s) {
    s = clamp(s, SMALLEST, LARGEST);
    const ux = (ax - from.x) / from.s, uy = (ay - from.y) / from.s;
    place.s = s;
    place.x = ax - s * ux;
    place.y = ay - s * uy;
  }

  function startGesture(event, kind) {
    const box = contentBounds();
    if (!box || event.button !== 0) return;
    gesture = { kind, box, x0: event.clientX, y0: event.clientY, from: { ...place }, id: event.pointerId };
    event.currentTarget.setPointerCapture(event.pointerId);
    event.preventDefault();
  }

  function moveGesture(event) {
    if (!gesture || event.pointerId !== gesture.id) return;
    const dx = event.clientX - gesture.x0, dy = event.clientY - gesture.y0, b = gesture.box, from = gesture.from;
    if (gesture.kind === 'move') {
      // A part of it always stays on the screen.
      place.x = from.x + clamp(dx, KEEP_SHOWING - b.x - b.w, window.innerWidth - KEEP_SHOWING - b.x);
      place.y = from.y + clamp(dy, KEEP_SHOWING - b.y - b.h, window.innerHeight - KEEP_SHOWING - b.y);
    } else {
      // The grip follows the pointer along the diagonal; the drawing's top-left corner stays put.
      scaleAbout(b.x, b.y, from, from.s * Math.max(0.05, (b.w + dx + b.h + dy) / (b.w + b.h)));
    }
    applyPlace(false);
  }

  function endGesture(event) {
    if (!gesture || event.pointerId !== gesture.id) return;
    gesture = null;
    applyPlace(true);
  }

  svg.addEventListener('pointerdown', event => { if (movable()) startGesture(event, 'move'); });
  grip.addEventListener('pointerdown', event => startGesture(event, 'size'));
  for (const node of [svg, grip]) {
    node.addEventListener('pointermove', moveGesture);
    node.addEventListener('pointerup', endGesture);
    node.addEventListener('pointercancel', endGesture);
  }
  svg.addEventListener('wheel', event => {
    if (!movable() || gesture) return;
    event.preventDefault();
    scaleAbout(event.clientX, event.clientY, { ...place }, place.s * Math.exp(-event.deltaY * 0.0012));
    applyPlace(true);
  }, { passive: false });

  // ------------------------------------------------------------------ saving: what the app is doing
  // A pill with a bar (or a spinner while it can't say how far it is), just outside the drawing's box: below it, above
  // it or beside it, so the video's frames (cut to the box) never have it in.
  let savingBox = null, savingTimer = 0;
  function showSaving(message) {
    clearTimeout(savingTimer);
    const busy = message.state !== 'done' && message.state !== 'failed';
    for (const button of tools.querySelectorAll('button')) button.disabled = busy;
    if (saving.hidden || !savingBox) savingBox = contentBounds();
    const known = typeof message.fraction === 'number' && isFinite(message.fraction);
    saving.className = busy ? (known ? '' : 'indeterminate') : message.state;
    saving.querySelector('.text').textContent = String(message.text || '').slice(0, 140);
    saving.querySelector('.bar span').style.width = (known ? Math.round(clamp(message.fraction, 0, 1) * 100) : 0) + '%';
    saving.hidden = false;
    placeSaving();
    if (!busy) savingTimer = setTimeout(hideSaving, message.state === 'failed' ? 6000 : 4000);
  }

  function hideSaving() {
    clearTimeout(savingTimer);
    saving.hidden = true;
    savingBox = null;
    for (const button of tools.querySelectorAll('button')) button.disabled = false;
  }

  function placeSaving() {
    const box = savingBox || { x: window.innerWidth / 2, y: window.innerHeight / 2, w: 0, h: 0 };
    const w = saving.offsetWidth, h = saving.offsetHeight, gap = 10, edge = 6;
    const pageW = window.innerWidth, pageH = window.innerHeight;
    const middleX = clamp(box.x + box.w / 2 - w / 2, edge, pageW - w - edge);
    const middleY = clamp(box.y + box.h / 2 - h / 2, edge, pageH - h - edge);
    const spots = [
      [middleX, box.y + box.h + gap], [middleX, box.y - h - gap],
      [box.x + box.w + gap, middleY], [box.x - w - gap, middleY],
    ];
    const fits = ([x, y]) => x >= edge && y >= edge && x + w <= pageW - edge && y + h <= pageH - edge;
    // No room outside: low inside it (it may show in the video then).
    const [left, top] = spots.find(fits) || [middleX, clamp(box.y + box.h - h - 2 * gap, edge, pageH - h - edge)];
    Object.assign(saving.style, { left: left + 'px', top: top + 'px' });
  }

  // ------------------------------------------------------------------ messages from the app
  function handle(message) {
    if (!message || typeof message !== 'object') return;
    switch (message.type) {
      case 'beginStream': begin(message); break;
      case 'continueStream': continueStream(message); break;
      case 'remembered': remembered = true; if (quizButton) quizButton.hidden = !message.quiz; showTools(); break;
      case 'recording': {
        recording = !!message.on;
        if (recording) hidePen(true);
        document.body.classList.toggle('recording', recording);
        // Paper behind the drawing's box, so its frames never show the screen under the stage.
        const box = recording ? contentBounds() : null;
        paper.hidden = !box;
        if (box) Object.assign(paper.style, { left: box.x + 'px', top: box.y + 'px', width: box.w + 'px', height: box.h + 'px' });
        showCaption(null);
        showTools();
        break;
      }
      case 'measure': {
        const box = contentBounds() || { x: 0, y: 0, w: 0, h: 0 };
        send({ type: 'bounds', x: box.x, y: box.y, w: box.w, h: box.h, pageWidth: window.innerWidth, pageHeight: window.innerHeight });
        break;
      }
      case 'feed': feed(message); break;
      case 'lineStarted': {
        const line = lines[message.k];
        if (line && line.start == null) line.start = now();
        break;
      }
      case 'lineEnded': {
        const line = lines[message.k];
        if (line) {
          if (line.start == null) line.start = now();
          // How fast the voice speaks, for the lines to come.
          if (line.end == null && pace === 'speech') spoken.push({ words: line.words, seconds: now() - line.start });
          line.end = now();
        }
        break;
      }
      case 'finishNow': finishing = true; hidePen(true); if (Sound) Sound.hush(); break;
      case 'photo': showPhoto(message); break;
      case 'piece': if (window.Figure3D) window.Figure3D.pieceArrived(message); break;
      case 'saving': showSaving(message); break;
      case 'clear': reset(); resetPlace(); hideSaving(); break;
      case 'sleep': sleep(); break;
      case 'wake': wake(); break;
      default: log('unknown message: ' + message.type);
    }
  }

  if (host) host.addEventListener('message', event => handle(event.data));
  // Any script error on the page (the 3D module included) reaches the app's log instead of failing silently.
  window.addEventListener('error', event => send({ type: 'error', text: 'page: ' + event.message + ' (' + (event.filename || '').split('/').pop() + ':' + event.lineno + ')' }));
  window.addEventListener('unhandledrejection', event => send({ type: 'error', text: 'page: ' + (event.reason && event.reason.message || event.reason) }));
  // For a preview in an ordinary browser: window.drawStage.handle({type: 'beginStream', ...}).
  window.drawStage = { handle, reportHits, place, isAsleep: false };
  reset();
  requestAnimationFrame(tick);
  send({ type: 'ready' });
})();
