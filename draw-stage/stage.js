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

  const WORDS_PER_SECOND = 2.6;
  const LINE_GAP = 0.25;          // between two estimated lines
  const LEAD = 0.08, TAIL = 0.12; // a line's strokes start just after its words and end just before they do
  const FASTEST = 0.35, SLOWEST = 2, OPEN_LINE_SLOWEST = 1.3;
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
  let lines = [], items = [], released = 0, penFreeAt = 0, finishing = false, finishedSent = false, ranSent = false;
  let markCount = 0, errors = 0, uid = 0;
  const drawn = new Map(); // a 2D mark's number in the program → what was drawn for it (erase / change)
  let remembered = false, recording = false;
  const quizButton = document.querySelector('#tools [data-act="quiz"]');

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
    penFreeAt = 0;
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
    svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
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
      line.end = line.start + Math.max(0.8, words / WORDS_PER_SECOND);
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
    items.push({ mark, line: lines.length - 1, seconds: naturalSeconds(mark) });
  }

  const lineEnd = line => line.end != null ? line.end : line.start + Math.max(0.8, line.words / WORDS_PER_SECOND);
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

  function tick() {
    const t = now();
    askForLines(t);
    while (released < items.length) {
      const item = items[released];
      const line = item.line >= 0 ? lines[item.line] : null;
      let startAt = penFreeAt;
      if (line && !finishing) {
        if (line.start == null) break;
        startAt = Math.max(startAt, line.start + LEAD);
      }
      if (t < startAt) break;
      let factor = 1;
      if (finishing) factor = FASTEST;
      else if (line) {
        let rest = 0;
        for (let i = released; i < items.length && items[i].line === item.line; i++) rest += items[i].seconds;
        const available = lineEnd(line) - TAIL - t;
        factor = available <= 0.05 ? FASTEST : Math.max(FASTEST, Math.min(lineIsOpen(line) ? OPEN_LINE_SLOWEST : SLOWEST, available / rest));
      }
      const seconds = item.seconds * factor;
      paint(item.mark, seconds, released);
      penFreeAt = t + seconds + 0.04;
      released++;
    }
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
  function sleep() { asleep = true; window.drawStage.isAsleep = true; }
  function wake() { if (!asleep) return; asleep = false; window.drawStage.isAsleep = false; requestAnimationFrame(tick); }

  function checkFinished(t) {
    if (finishedSent || !feedDone || pending.length || running || released < items.length || t < penFreeAt) return;
    const last = lines[lines.length - 1];
    if (last && !finishing && (last.start == null || t < lineEnd(last))) return;
    finishedSent = true;
    showCaption(null);
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
    mark.pts[i] = [mark.pts[i][0] + label.dx, mark.pts[i][1] + label.dy];
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

  // a little randomness that is the same for the same mark every time
  function random(seed) {
    let a = (seed * 2654435761) >>> 0;
    return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
  }

  const length = pts => pts.reduce((sum, p, i) => i ? sum + Math.hypot(p[0] - pts[i - 1][0], p[1] - pts[i - 1][1]) : 0, 0);

  function naturalSeconds(mark) {
    switch (mark.do) {
      case 'line': case 'arrow': return clamp(length(mark.pts) / 700, 0.25, 1.2) + (mark.do === 'arrow' ? 0.15 : 0);
      case 'poly': return clamp(length(mark.pts.concat([mark.pts[0]])) / 800, 0.35, 1.4);
      case 'ellipse': return clamp((Math.PI * (mark.rx + mark.ry)) / 800, 0.3, 1.2);
      case 'rect': return clamp((2 * (mark.w + mark.h)) / 900, 0.35, 1.3);
      case 'text': return clamp(String(mark.text).length * 0.045, 0.3, 1.6);
      case 'highlight': return 0.45;
      case 'card': return 0.4;
      case 'photo': return 0.5;
      case 'figure': return 0.5;
      case 'part3d': return mark.params && mark.params.hidden ? 0.02 : mark.params && mark.params.quick ? 0.1 : 0.6; // quick: one of many small parts (a DNA's nucleotides)
      case 'label3d': return 0.45;
      case 'view3d': return 0.5;
      case 'motion3d': return 0.05;
      case 'change3d': case 'tempo3d': case 'labels3d': return 0.15;
      case 'change2d': return 0.4;
      case 'quiz': return 0.6;
      default: return 0.2;
    }
  }
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

  // A hand's line: the points with a slight wobble, through a smooth curve.
  function handPoints(pts, rnd, closed) {
    const out = [];
    const all = closed ? pts.concat([pts[0]]) : pts;
    for (let i = 0; i < all.length - 1; i++) {
      const [x0, y0] = all[i], [x1, y1] = all[i + 1];
      const d = Math.hypot(x1 - x0, y1 - y0), steps = Math.max(1, Math.round(d / 40));
      const nx = -(y1 - y0) / (d || 1), ny = (x1 - x0) / (d || 1), wobble = Math.min(1.6, d * 0.006);
      for (let s = 0; s < steps; s++) {
        const t = s / steps, j = s === 0 ? 0 : (rnd() - 0.5) * 2 * wobble;
        out.push([x0 + (x1 - x0) * t + nx * j, y0 + (y1 - y0) * t + ny * j]);
      }
    }
    out.push(all[all.length - 1]);
    return out;
  }

  function smoothPath(pts) {
    if (pts.length < 3) return 'M' + pts.map(p => p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join(' L');
    let d = `M${pts[0][0].toFixed(1)} ${pts[0][1].toFixed(1)}`;
    for (let i = 0; i < pts.length - 1; i++) {
      const p0 = pts[i - 1] || pts[i], p1 = pts[i], p2 = pts[i + 1], p3 = pts[i + 2] || p2;
      const c1 = [p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6];
      const c2 = [p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6];
      d += ` C${c1[0].toFixed(1)} ${c1[1].toFixed(1)} ${c2[0].toFixed(1)} ${c2[1].toFixed(1)} ${p2[0].toFixed(1)} ${p2[1].toFixed(1)}`;
    }
    return d;
  }

  function ellipsePoints(m, rnd) {
    const out = [], n = 40, start = rnd() * Math.PI * 2, overshoot = 0.06 + rnd() * 0.05;
    for (let i = 0; i <= n * (1 + overshoot); i++) {
      const a = start + (i / n) * Math.PI * 2, r = 1 + (rnd() - 0.5) * 0.03;
      out.push([m.at[0] + Math.cos(a) * m.rx * r, m.at[1] + Math.sin(a) * m.ry * r]);
    }
    return out;
  }

  // One pen stroke: a white halo under the coloured line, both revealed from start to end.
  function stroke(group, d, m, seconds, delay) {
    const width = m.width > 0 ? m.width : 3.2;
    const halo = el('path', { d, class: 'halo', 'stroke-width': width + 5 });
    const pen = el('path', { d, class: 'pen', stroke: tone(m.color, TONES.blue), 'stroke-width': width });
    if (m.dashed) pen.setAttribute('stroke-dasharray', `${width * 3} ${width * 2.5}`);
    group.appendChild(halo);
    group.appendChild(pen);
    for (const path of m.dashed ? [halo] : [halo, pen]) {
      const total = path.getTotalLength() + 2;
      path.style.strokeDasharray = `${total} ${total}`;
      path.animate([{ strokeDashoffset: total }, { strokeDashoffset: 0 }],
        { duration: seconds * 1000, delay: (delay || 0) * 1000, easing: 'cubic-bezier(.35,.2,.55,.95)', fill: 'both' });
    }
    if (m.dashed) pen.animate([{ opacity: 0 }, { opacity: 1 }], { duration: seconds * 1000, delay: (delay || 0) * 1000, fill: 'both' });
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

  function reveal(group, node, box, seconds) {
    const id = 'clip' + (++uid);
    const clip = el('clipPath', { id });
    const rect = el('rect', { x: box.x - 6, y: box.y - 6, width: 0, height: box.height + 12 });
    clip.appendChild(rect);
    group.appendChild(clip);
    node.setAttribute('clip-path', `url(#${id})`);
    rect.animate([{ width: '0px' }, { width: `${box.width + 12}px` }], { duration: seconds * 1000, easing: 'linear', fill: 'forwards' });
  }

  function paint(m, seconds, index) {
    if (m.do === 'change2d') { change2d(m, seconds); return; }
    if (m.do === 'quiz') { drawQuiz(m, seconds); return; }
    const group = el('g');
    if (m.do === 'text') labelLayer.appendChild(group); else svg.insertBefore(group, labelLayer);
    if (Number.isInteger(m.n)) drawn.set(m.n, group);
    const rnd = random(index + 1);
    switch (m.do) {
      case 'line': {
        const pts = m.smooth ? m.pts : handPoints(m.pts, rnd, false);
        stroke(group, smoothPath(pts), m, seconds);
        break;
      }
      case 'arrow': {
        const shaft = Math.max(0.1, seconds - 0.15);
        stroke(group, smoothPath(m.smooth ? m.pts : handPoints(m.pts, rnd, false)), m, shaft);
        const tip = m.pts[m.pts.length - 1], back = m.pts[m.pts.length - 2];
        const angle = Math.atan2(tip[1] - back[1], tip[0] - back[0]);
        const size = Math.min(22, Math.max(10, length(m.pts) * 0.25));
        for (const side of [-1, 1]) {
          const a = angle + Math.PI + side * 0.5;
          stroke(group, smoothPath([tip, [tip[0] + Math.cos(a) * size, tip[1] + Math.sin(a) * size]]), m, 0.12, shaft);
        }
        break;
      }
      case 'poly': {
        const d = smoothPath(handPoints(m.pts, rnd, true));
        stroke(group, d, m, seconds);
        fillShape(group, d, m, seconds);
        break;
      }
      case 'rect': {
        const [x, y] = m.at;
        const d = smoothPath(handPoints([[x, y], [x + m.w, y], [x + m.w, y + m.h], [x, y + m.h]], rnd, true));
        stroke(group, d, m, seconds);
        fillShape(group, d, m, seconds);
        break;
      }
      case 'ellipse': {
        const d = smoothPath(ellipsePoints(m, rnd));
        stroke(group, d, m, seconds);
        fillShape(group, d, m, seconds);
        break;
      }
      case 'dot': {
        const dot = el('circle', { cx: m.at[0], cy: m.at[1], r: m.r, fill: tone(m.color, TONES.blue), stroke: '#fff', 'stroke-width': 2 });
        group.appendChild(dot);
        dot.style.transformBox = 'fill-box';
        dot.style.transformOrigin = 'center';
        dot.animate([{ transform: 'scale(0)' }, { transform: 'scale(1.25)' }, { transform: 'scale(1)' }], { duration: seconds * 1000, fill: 'both' });
        break;
      }
      case 'text': {
        const attributes = labelAttributes(m);
        const text = el('text', Object.assign(attributes, {
          fill: tone(m.color, TONES.ink), stroke: '#fff', 'stroke-width': Math.max(3, attributes['font-size'] * 0.2)
        }));
        text.textContent = m.text;
        group.appendChild(text);
        reveal(group, text, text.getBBox(), seconds);
        break;
      }
      case 'highlight': {
        const [x, y] = m.at;
        const mark = el('rect', { x, y, width: m.w, height: m.h, rx: Math.min(6, m.h / 4), fill: tone(m.color, TONES.yellow), opacity: 0.38 });
        group.appendChild(mark);
        reveal(group, mark, { x, y, width: m.w, height: m.h }, seconds);
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
        frame.animate([{ opacity: 0, transform: 'rotate(-2deg) translateY(10px)' }, { opacity: 1, transform: 'rotate(0) translateY(0)' }],
          { duration: seconds * 1000, easing: 'ease-out', fill: 'both' });
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
        card.animate([{ opacity: 0, transform: 'translateY(12px)' }, { opacity: 1, transform: 'translateY(0)' }],
          { duration: seconds * 1000, easing: 'ease-out', fill: 'both' });
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
      case 'remembered': remembered = true; quizButton.hidden = !message.quiz; showTools(); break;
      case 'recording': {
        recording = !!message.on;
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
        if (line) { if (line.start == null) line.start = now(); line.end = now(); }
        break;
      }
      case 'finishNow': finishing = true; break;
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
