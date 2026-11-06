// OpenClicky drawing stage: the program worker.
//
// Runs the model's program one complete statement at a time (stage.js finds the statements as the answer streams)
// and sends back what each one drew, as plain data ("marks"). The page never runs model code itself; this worker has
// no network, no DOM and no host bridge, and the page ends it if a statement runs too long.
//
// The drawing language (keep docs/draw-stage.md in sync):
//   step(n) / say(text)                     a beat and its spoken line; marks after a say() are drawn while it is said
//   keep()                                  a follow-up's first statement: it draws after the drawing on the stage
//   line(points, o) / curve(points, o)      a pen stroke through [[x, y], ...]; curve() is smooth
//   arrow(points, o)                        a stroke with a head at its last point
//   circle(x, y, r, o) / ellipse(x, y, rx, ry, o) / rect(x, y, w, h, o) / poly(points, o)
//   dot(x, y, r, o) / text(x, y, words, o) / highlight(x, y, w, h, o)
//   cell(x, y, w, h, {type, show})          a cut-open 3D animal, plant or bacterial cell, its parts sized and packed →
//                                           c.label(part, words), c.point(part), c.parts
//   card(x, y, w, h, o)                     a paper card to explain on ({title})
//   photo(x, y, w, h, {query, caption})     a real photo (Wikimedia Commons, found by the app) in a polaroid frame
//   figure(x, y, w, h, o)                   a 3D stage ({tilt, turn, zoom, spin, ground}); its parts: f.box/cyl/cone/sphere/
//                                           torus/capsule/tube/pipe/lathe/extrude/spring/gear/blob/arrow/piece({…}) (any of them
//                                           cut open, repeated or mirrored: cut, copies, around, mirror) → part.spin/bob/
//                                           stroke/pulse/orbit/move({…}) and part.label(words); f.label(part, words), f.view({…})
//   o: {color, width, fill, dashed, size, align: 'start' | 'middle' | 'end', bold}
//   W, H (the canvas: the screenshot's size), math (sin, cos, PI, ...) and point helpers (mid, along, polar, lerp)
'use strict';

const LIMIT_MARKS = 2000;
let marks = [], says = [], warnings = [];
let currentStep = 1, total = 0, W = 1280, H = 800;
// Declared names (const piston = ...) live here between statements.
const scope = Object.create(null);

const finite = v => typeof v === 'number' && isFinite(v);
const point = p => Array.isArray(p) && finite(p[0]) && finite(p[1]);
const points = list => Array.isArray(list) ? list.filter(point).map(p => [p[0], p[1]]) : [];
const options = o => (o && typeof o === 'object' && !Array.isArray(o) ? o : {});
const warn = message => { if (warnings.length < 20) warnings.push(message); };

function push(mark, o) {
  if (total >= LIMIT_MARKS) throw new Error('too many marks (the limit is ' + LIMIT_MARKS + ')');
  total++;
  const merged = Object.assign({}, options(o), mark, { step: currentStep });
  marks.push(merged);
  return { id: total - 1 };
}

function needs(name, ok) {
  if (!ok) warn(name + '() was called with missing or non-numeric arguments; skipped');
  return ok;
}

const api = {
  PI: Math.PI, sin: Math.sin, cos: Math.cos, tan: Math.tan, atan2: Math.atan2, abs: Math.abs, min: Math.min, max: Math.max,
  sqrt: Math.sqrt, pow: Math.pow, round: Math.round, floor: Math.floor, ceil: Math.ceil, hypot: Math.hypot,
  random: () => 0.5, // a program draws the same every time
  range: n => Array.from({ length: Math.max(0, Math.min(1000, n | 0)) }, (_, i) => i),
  lerp: (a, b, t) => a + (b - a) * t,
  mid: (a, b) => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2],
  along: (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t],
  polar: (cx, cy, r, angle) => [cx + Math.cos(angle) * r, cy + Math.sin(angle) * r],

  keep: () => {}, // the app reads it: this program is drawn after the one already on the stage
  step: n => { if (finite(n)) currentStep = n; },
  say: words => { says.push({ text: String(words), step: currentStep, at: total }); },

  line: (pts, o) => { const p = points(pts); return needs('line', p.length >= 2) ? push({ do: 'line', pts: p }, o) : null; },
  curve: (pts, o) => { const p = points(pts); return needs('curve', p.length >= 2) ? push({ do: 'line', pts: p, smooth: true }, o) : null; },
  arrow: (pts, o) => { const p = points(pts); return needs('arrow', p.length >= 2) ? push({ do: 'arrow', pts: p }, o) : null; },
  poly: (pts, o) => { const p = points(pts); return needs('poly', p.length >= 3) ? push({ do: 'poly', pts: p }, o) : null; },
  circle: (x, y, r, o) => needs('circle', [x, y, r].every(finite)) ? push({ do: 'ellipse', at: [x, y], rx: r, ry: r }, o) : null,
  ellipse: (x, y, rx, ry, o) => needs('ellipse', [x, y, rx, ry].every(finite)) ? push({ do: 'ellipse', at: [x, y], rx, ry }, o) : null,
  rect: (x, y, w, h, o) => needs('rect', [x, y, w, h].every(finite)) ? push({ do: 'rect', at: [x, y], w, h }, o) : null,
  dot: (x, y, r, o) => needs('dot', [x, y].every(finite)) ? push({ do: 'dot', at: [x, y], r: finite(r) ? r : 6 }, o) : null,
  text: (x, y, words, o) => needs('text', [x, y].every(finite)) ? push({ do: 'text', at: [x, y], text: String(words) }, o) : null,
  highlight: (x, y, w, h, o) => needs('highlight', [x, y, w, h].every(finite)) ? push({ do: 'highlight', at: [x, y], w, h }, o) : null,
  card: (x, y, w, h, o) => needs('card', [x, y, w, h].every(finite)) ? push({ do: 'card', at: [x, y], w, h }, o) : null,
  photo: (x, y, w, h, o) => needs('photo', [x, y, w, h].every(finite) && options(o).query) ? push({ do: 'photo', at: [x, y], w, h }, o) : null,

  figure: (x, y, w, h, o) => {
    if (![x, y, w, h].every(finite) || w < 40 || h < 40) {
      warn('figure() needs x, y, width and height (at least 40 each); skipped');
      return new Proxy({}, { get: () => () => new Proxy({}, { get: () => () => null }) });
    }
    const id = 'fig' + (++figureCount);
    push({ do: 'figure', id, at: [x, y], w, h }, o);
    const figure = { id };
    for (const kind of PART_KINDS) figure[kind] = opts => part3d(id, kind, opts);
    figure.label = (part, words, opts) => { push({ do: 'label3d', fig: id, part: part && part.id, text: String(words) }, opts); return figure; };
    figure.view = opts => { push({ do: 'view3d', fig: id }, opts); return figure; };
    return figure;
  },
};

// 3D parts of a figure (figure3d.js builds them). Options are plain data: functions and the like are dropped.
const PART_KINDS = ['box', 'cyl', 'cone', 'sphere', 'torus', 'capsule', 'tube', 'pipe', 'lathe', 'extrude', 'spring', 'gear', 'blob', 'arrow', 'piece'];
const MOTIONS = ['spin', 'bob', 'stroke', 'pulse', 'orbit', 'move'];
let figureCount = 0, partCount = 0;
const plain = o => { try { return JSON.parse(JSON.stringify(options(o))); } catch (_) { return {}; } };

function part3d(fig, kind, o) {
  const params = plain(o);
  if (o && o.parent && o.parent.id) params.parent = o.parent.id;
  const id = 'part' + (++partCount);
  push({ do: 'part3d', fig, id, kind, params });
  const handle = { id };
  for (const motion of MOTIONS) handle[motion] = opts => { push({ do: 'motion3d', fig, part: id, kind: motion, params: plain(opts) }); return handle; };
  handle.label = (words, opts) => { push({ do: 'label3d', fig, part: id, text: String(words) }, opts); return handle; };
  return handle;
}

// The parts of `text` between commas outside brackets and strings.
function splitTopLevelCommas(text) {
  const parts = [];
  let depth = 0, from = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"' || c === "'" || c === '`') { let j = i + 1; while (j < text.length && text[j] !== c) j += text[j] === '\\' ? 2 : 1; i = j; continue; }
    if (c === '(' || c === '[' || c === '{') depth++;
    else if (c === ')' || c === ']' || c === '}') depth--;
    else if (c === ',' && depth === 0) { parts.push(text.slice(from, i)); from = i + 1; }
  }
  parts.push(text.slice(from));
  return parts;
}

// const x = ..., let x = ..., var x = ... (each name of `const a = 1, b = 2`) and function f() {...} at the start of a
// statement are kept in `scope`, so later statements see them; everything else runs as written.
function rewrite(statement) {
  const declaration = /^\s*(?:const|let|var)\s+(?=[A-Za-z_$])/.exec(statement);
  if (declaration) {
    const declarators = splitTopLevelCommas(statement.slice(declaration[0].length).trim().replace(/;$/, ''));
    if (declarators.every(d => /^\s*[A-Za-z_$][\w$]*\s*=[^=]/.test(d))) {
      return declarators.map(d => '__scope.' + d.trim()).join(';\n');
    }
  }
  const fn = /^\s*(?:async\s+)?function\s*\*?\s*([A-Za-z_$][\w$]*)\s*\(/.exec(statement);
  if (fn) return '__scope.' + fn[1] + ' = ' + statement.trim().replace(/;$/, '');
  if (/^\s*(?:const|let|var)\s*[[{]/.test(statement)) warn('destructuring declarations are not kept between statements');
  return statement;
}

function run(statement) {
  // The program sees the drawing language and its own names; `with` keeps the language out of the global scope.
  // eslint-disable-next-line no-new-func
  const body = new Function('__api', '__scope', 'with (__api) { with (__scope) {\n' + rewrite(statement) + '\n} }');
  body(api, scope);
  // const liftFan = f.cyl(…): the part is named after its variable ("lift fan"), so a click on it asks about that.
  const named = /^\s*(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*[A-Za-z_$][\w$]*\s*\.\s*[a-z]+\s*\(/.exec(statement);
  const part = named && named[1].length > 2 && marks.find(m => m.do === 'part3d' && !m.params.name);
  if (part) part.params.name = named[1].replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/[_$]+/g, ' ').trim().toLowerCase();
}

// Cells: cell(x, y, w, h, {type: 'animal' | 'plant' | 'bacterium', show, labels}) builds a cut-open 3D cell with its
// parts the right sizes for each other and in sensible places: the nucleus with its nucleolus, the ER wrapped round
// it, the Golgi beside it, mitochondria spread through the cytoplasm, and for a plant a cell wall, a big vacuole and
// chloroplasts round the edge; a bacterium has no nucleus, its DNA loose in a nucleoid, plasmids and a flagellum.
// The front half is cut away (the cell, the nucleus and the vacuole sliced open) and the parts are packed behind the
// cut without overlapping. Each kind of part is named once.
const CELL_PARTS = {
  animal: ['membrane', 'nucleus', 'nucleolus', 'rough ER', 'smooth ER', 'Golgi body', 'mitochondrion', 'lysosome', 'vesicle', 'ribosomes', 'centrioles'],
  plant: ['cell wall', 'membrane', 'vacuole', 'nucleus', 'nucleolus', 'chloroplast', 'mitochondrion', 'Golgi body', 'rough ER', 'ribosomes'],
  bacterium: ['cell wall', 'nucleoid', 'plasmid', 'ribosomes', 'flagellum', 'pili'],
};
const CELL_LOOK = {
  membrane: 'tissue:#F2B8B0', 'cell wall': 'tissue:#8DB255', nucleus: 'tissue:#8E6CC4', nucleolus: 'tissue:#5B3F99',
  'rough ER': 'tissue:#5BA3D9', 'smooth ER': 'tissue:#7CC9C4', 'Golgi body': 'tissue:#F2C14E', mitochondrion: 'tissue:#E8833A',
  lysosome: 'tissue:#C0392B', vesicle: 'tissue:#F6D98B', ribosomes: 'tissue:#3A3A48', centrioles: 'tissue:#4CAF7A',
  vacuole: 'water:#9FD3E8', chloroplast: 'leaf', nucleoid: 'tissue:#5B6C8F', plasmid: 'tissue:#C2185B', flagellum: 'tissue:#A0703C',
  pili: 'tissue:#A0703C',
};
// Names a model might use for a part, to its own name.
const CELL_NAMES = {
  'cell membrane': 'membrane', 'plasma membrane': 'membrane', mitochondria: 'mitochondrion', er: 'rough ER',
  'endoplasmic reticulum': 'rough ER', 'rough er': 'rough ER', 'smooth er': 'smooth ER', golgi: 'Golgi body', 'golgi apparatus': 'Golgi body',
  'golgi body': 'Golgi body', lysosomes: 'lysosome', vesicles: 'vesicle', ribosome: 'ribosomes', centriole: 'centrioles',
  chloroplasts: 'chloroplast', plasmids: 'plasmid', pilus: 'pili', dna: 'nucleoid', 'cell wall': 'cell wall', wall: 'cell wall',
};
const cellName = n => { const k = String(n).trim().toLowerCase(); return CELL_NAMES[k] || Object.keys(CELL_LOOK).find(p => p.toLowerCase() === k) || null; };

// The i-th point of a Halton sequence in base b: spread evenly, the same every time.
function halton(i, b) { let f = 1, r = 0; for (let n = i; n > 0; n = Math.floor(n / b)) { f /= b; r += f * (n % b); } return r; }

function cell(x, y, w, h, o) {
  const opts = options(o);
  if (![x, y, w, h].every(finite) || w < 60 || h < 60) { warn('cell() needs x, y, width and height (at least 60 each); skipped'); return noCell(); }
  const type = ['animal', 'plant', 'bacterium'].includes(String(opts.type).toLowerCase()) ? String(opts.type).toLowerCase() : 'animal';
  if (opts.type && type !== String(opts.type).toLowerCase()) warn('cell(): type is animal, plant or bacterium; drawing an animal cell');
  const wanted = Array.isArray(opts.show) ? new Set(opts.show.map(cellName).filter(Boolean)) : null;
  const shows = name => CELL_PARTS[type].includes(name) && (!wanted || wanted.has(name) || ['membrane', 'cell wall'].includes(name));
  const f = api.figure(x, y, w, h, { tilt: finite(opts.tilt) ? opts.tilt : 16, turn: finite(opts.turn) ? opts.turn : -24, zoom: opts.zoom, ground: false });
  const handles = new Map(); // a part's name → its parts
  const keep = (name, handle) => { if (!handles.has(name)) handles.set(name, []); handles.get(name).push(handle); return handle; };
  const look = name => (typeof (opts.colors || {})[name] === 'string' ? 'tissue:' + opts.colors[name] : CELL_LOOK[name]);

  // The cell's inside, as a test for a point with a margin, and the box it fits in.
  const R = 3;
  const shape = type === 'plant'
    ? { box: [3.3, 2.3, 1.7], inside: (p, m) => Math.abs(p[0]) <= 3.05 - m && Math.abs(p[1]) <= 2.05 - m && Math.abs(p[2]) <= 1.45 - m }
    : type === 'bacterium'
      ? { box: [3.1, 1.2, 1.2], inside: (p, m) => Math.hypot(Math.max(0, Math.abs(p[0]) - 1.9), p[1], p[2]) <= 1.05 - m }
      : { box: [R, 0.82 * R, 0.78 * R], inside: (p, m) => (p[0] / (R - m)) ** 2 + (p[1] / (0.82 * R - m)) ** 2 + (p[2] / (0.78 * R - m)) ** 2 <= 1 };
  // Room taken so far, as spheres: [x, y, z, r].
  const taken = [];
  // …and boxes, for the flat things (a plant's vacuole): [centre, half sizes].
  const boxes = [];
  const clear = (p, r) => taken.every(t => Math.hypot(p[0] - t[0], p[1] - t[1], p[2] - t[2]) > r + t[3] + 0.08)
    && boxes.every(([c, half]) => [0, 1, 2].some(k => Math.abs(p[k] - c[k]) > half[k] + r + 0.08));
  let seed = 1;
  // A free spot for something r across, behind the cut (z ≤ -r): spread evenly, or nearest `near`, or out by the edge.
  function spot(r, { near = null, edge = false, tries = 1500 } = {}) {
    let best = null, score = Infinity;
    for (let k = 0; k < tries; k++) {
      const i = seed++;
      const p = [(halton(i, 2) * 2 - 1) * shape.box[0], (halton(i, 3) * 2 - 1) * shape.box[1], -halton(i, 5) * shape.box[2]];
      if (p[2] > -r || !shape.inside(p, r + 0.05) || !clear(p, r)) continue;
      if (!near && !edge) { best = p; break; }
      const s = near ? Math.hypot(p[0] - near[0], p[1] - near[1], p[2] - near[2]) : -Math.max(Math.abs(p[0]) / shape.box[0], Math.abs(p[1]) / shape.box[1]);
      if (s < score) { score = s; best = p; }
    }
    if (best) taken.push([...best, r]);
    return best;
  }
  const turnOf = i => [halton(i + 7, 2) * 360, halton(i + 7, 3) * 360, halton(i + 7, 5) * 360];

  // The outside, cut open.
  if (type === 'animal') {
    keep('membrane', f.blob({ balls: [[0, 0, 0, R, 0.82 * R, 0.78 * R], [0.9, 0.9, -0.2, 1.3], [-1.1, -0.8, 0.1, 1.2]], smooth: 0.6, cut: 'z', mat: look('membrane') }));
  } else if (type === 'plant') {
    keep('cell wall', f.box({ w: 6.8, h: 4.8, d: 3.6, round: 0.45, cut: 'z', mat: look('cell wall') }));
    if (shows('membrane')) keep('membrane', f.box({ w: 6.35, h: 4.35, d: 3.15, round: 0.4, cut: 'z', mat: look('membrane'), opacity: 0.55 }));
  } else {
    keep('cell wall', f.capsule({ r: 1.2, h: 3.8, axis: 'x', cut: 'z', mat: look('cell wall') }));
  }

  // A plant's vacuole fills the middle: its room is kept before anything else is placed.
  const VACUOLE = [0.75, -0.35, -0.4];
  if (type === 'plant' && shows('vacuole')) boxes.push([VACUOLE, [1.5, 1.05, 1.0]]);

  // The nucleus (sliced open, its nucleolus inside) and what wraps round it.
  let nucleus = null;
  if (shows('nucleus')) {
    nucleus = type === 'plant' ? [-1.9, 0.9, -0.2] : [-0.35, 0.15, -0.25];
    const rn = type === 'plant' ? 0.85 : 1.0;
    taken.push([...nucleus, rn]);
    keep('nucleus', f.sphere({ r: rn, at: nucleus, cut: 'z', mat: look('nucleus') }));
    if (shows('nucleolus')) keep('nucleolus', f.sphere({ r: rn * 0.35, at: [nucleus[0] + rn * 0.2, nucleus[1] - rn * 0.1, nucleus[2] - rn * 0.25], cut: 'z', mat: look('nucleolus') }));
    // Rough ER: folded sheets round the nucleus on one side, seen as curving tubes; smooth ER further out.
    for (const [name, gap, count] of [['rough ER', 0.32, 3], ['smooth ER', 0.85, 2]]) {
      if (!shows(name)) continue;
      for (let k = 0; k < count; k++) {
        const radius = rn + gap + k * 0.17, from = -0.3 + k * 0.25, sweep = 1.9;
        const points = Array.from({ length: 14 }, (_, i) => {
          const a = from + (sweep * i) / 13, wave = 0.07 * Math.sin(i * 1.7 + k);
          return [nucleus[0] + Math.cos(a) * (radius + wave), nucleus[1] + Math.sin(a) * (radius + wave), nucleus[2] - 0.35 - 0.12 * k];
        });
        keep(name, f.tube({ points, r: name === 'rough ER' ? 0.075 : 0.06, mat: look(name) }));
      }
      taken.push([nucleus[0] + rn + gap, nucleus[1] + 0.5, nucleus[2] - 0.4, 0.45]);
    }
  }
  // The Golgi: a stack of flattened, curved sacs, beside the nucleus (or anywhere, without one).
  if (shows('Golgi body')) {
    const at = spot(0.6, { near: nucleus ? [nucleus[0] + 1.9, nucleus[1] - 1.1, -0.7] : null });
    if (at) {
      const crescent = [...Array.from({ length: 9 }, (_, i) => { const a = Math.PI * (0.15 + 0.7 * i / 8); return [Math.cos(a) * 0.6, Math.sin(a) * 0.6 - 0.35]; }),
        ...Array.from({ length: 9 }, (_, i) => { const a = Math.PI * (0.85 - 0.7 * i / 8); return [Math.cos(a) * 0.48, Math.sin(a) * 0.48 - 0.35]; })];
      keep('Golgi body', f.extrude({ points: crescent, depth: 0.09, bevel: 0.03, at, rot: [70, 0, 25], copies: { n: 4, step: [0, 0, 0.14] }, mat: look('Golgi body') }));
    }
  }
  // The rest, packed into the space left.
  const recipe = {
    animal: [['mitochondrion', 6, 0.36], ['lysosome', 3, 0.17], ['vesicle', 3, 0.13], ['ribosomes', 10, 0.06], ['centrioles', 1, 0.25]],
    plant: [['vacuole', 1, 0], ['chloroplast', 6, 0.32], ['mitochondrion', 3, 0.32], ['ribosomes', 8, 0.06]],
    bacterium: [['nucleoid', 1, 0], ['plasmid', 2, 0.22], ['ribosomes', 12, 0.06], ['flagellum', 1, 0], ['pili', 4, 0]],
  }[type];
  let n = 0;
  for (const [name, count, r] of recipe) {
    if (!shows(name)) continue;
    for (let k = 0; k < count; k++, n++) {
      if (name === 'vacuole') { // the big central sac, sliced open like the cell
        keep(name, f.box({ w: 3.0, h: 2.1, d: 2.0, round: 0.85, at: VACUOLE, cut: 'z', mat: look(name), opacity: 0.6 }));
        continue;
      }
      if (name === 'nucleoid') { // the bacterium's DNA, one long tangled loop in the middle
        const points = Array.from({ length: 40 }, (_, i) => { const a = i / 40 * Math.PI * 2;
          return [Math.cos(a) * 1.1 + 0.3 * Math.sin(a * 5), Math.sin(a) * 0.45 + 0.15 * Math.cos(a * 7), -0.45 + 0.15 * Math.sin(a * 3)]; });
        keep(name, f.tube({ points, r: 0.05, closed: true, mat: look(name) }));
        taken.push([0, 0, -0.45, 1.2]);
        continue;
      }
      if (name === 'flagellum') { // a long whip from one end, turning
        const points = Array.from({ length: 24 }, (_, i) => [3.0 + i * 0.22, Math.sin(i * 0.8) * 0.25, Math.cos(i * 0.8) * 0.25 - 0.1]);
        keep(name, f.tube({ points, r: 0.05, mat: look(name) })).spin({ axis: 'x', period: 0.6 });
        continue;
      }
      if (name === 'pili') { // short hairs growing out of the wall, top and bottom
        const along = [-1.3, 0.7][k % 2], side = k < count / 2 ? 1 : -1, base = [along, side * 1.1, -0.45];
        keep(name, f.tube({ points: [base, [along * 1.08, side * 1.55, -0.5], [along * 1.15, side * 1.85, -0.45]], r: 0.03, mat: look(name) }));
        continue;
      }
      const at = spot(r, { edge: name === 'chloroplast' });
      if (!at) { warn('cell(): no room left for another ' + name); break; }
      const rot = turnOf(n);
      if (name === 'mitochondrion') keep(name, f.capsule({ r: r * 0.42, h: r * 1.3, at, rot, mat: look(name) }));
      else if (name === 'chloroplast') keep(name, f.sphere({ r: r * 0.62, at, rot: [rot[0] * 0.15, rot[1] * 0.15, rot[2]], scale: [1.6, 0.75, 0.9], mat: look(name) }));
      else if (name === 'centrioles') {
        keep(name, f.cyl({ r: 0.07, h: 0.32, at, mat: look(name) }));
        keep(name, f.cyl({ r: 0.07, h: 0.32, axis: 'x', at: [at[0] + 0.12, at[1] + 0.14, at[2]], mat: look(name) }));
      } else if (name === 'plasmid') keep(name, f.torus({ r: r, tube: 0.045, at, rot: [75 + rot[0] * 0.05, 0, rot[2]], mat: look(name) }));
      else keep(name, f.sphere({ r, at, mat: look(name) }));
    }
  }

  // Each kind of part is named once, on its first piece; an outside's name points at its edge, not at its middle.
  const edgeOf = {
    animal: { membrane: [2.75, -0.9, -0.4] },
    plant: { 'cell wall': [3.35, 1.4, -0.6], membrane: [3.12, -1.3, -0.6] },
    bacterium: { 'cell wall': [0.6, -1.15, -0.3] },
  }[type];
  const tag = (name, words, o2) => handles.get(name)[0].label(words, Object.assign(edgeOf[name] ? { at: edgeOf[name] } : {}, options(o2)));
  if (opts.labels !== false) for (const name of handles.keys()) tag(name, name, { color: look(name).startsWith('tissue:') ? look(name).slice(7) : undefined });
  const find = which => handles.get(cellName(which)) || null;
  return {
    figure: f, type,
    parts: [...handles.keys()],
    label: (which, words, o2) => (find(which) ? tag(cellName(which), String(words), o2) : (warn('cell label: no ' + which + ' in this cell'), null)),
    // Points one part out while it is talked about: every piece of it swells gently.
    point: which => { const list = find(which); if (!list) { warn('cell point: no ' + which + ' in this cell'); return null; } list.forEach(p => p.pulse({ amount: 0.12, period: 1.2 })); return list[0]; },
    view: o2 => f.view(o2),
  };
}
// What cell() hands back when it couldn't build one: calls on it do nothing.
const noCell = () => new Proxy({}, { get: () => () => null });
api.cell = cell;

// Nothing the program writes can reach the network or load more code.
// A nested worker made from a blob would get a fresh global with fetch in it, so Worker, SharedWorker, Blob and
// URL.createObjectURL go too.
for (const name of ['fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource', 'importScripts', 'indexedDB', 'caches', 'BroadcastChannel',
                    'Worker', 'SharedWorker', 'Blob']) {
  try { self[name] = undefined; } catch (_) { /* read-only in some builds */ }
}
try { URL.createObjectURL = undefined; } catch (_) { /* read-only in some builds */ }

self.onmessage = event => {
  const message = event.data || {};
  if (message.type === 'begin') {
    W = finite(message.w) ? message.w : W;
    H = finite(message.h) ? message.h : H;
    api.W = W;
    api.H = H;
    return;
  }
  if (message.type !== 'run') return;
  marks = [];
  says = [];
  warnings = [];
  let error = null;
  try {
    run(String(message.code));
  } catch (e) {
    error = (e && e.message) || String(e);
  }
  self.postMessage({ type: 'ran', seq: message.seq, marks, says, warnings, error });
};
