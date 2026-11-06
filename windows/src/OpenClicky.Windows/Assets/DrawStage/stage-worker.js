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
//   card(x, y, w, h, o)                     a paper card to explain on ({title})
//   photo(x, y, w, h, {query, caption})     a real photo (Wikimedia Commons, found by the app) in a polaroid frame
//   figure(x, y, w, h, o)                   a 3D stage ({tilt, turn, zoom, spin, ground}); its parts: f.box/cyl/cone/sphere/
//                                           torus/capsule/tube/pipe/lathe/extrude/spring/gear/blob/arrow/piece({…}) (any of them
//                                           cut open, repeated or mirrored: cut, copies, around, mirror) → part.spin/bob/
//                                           stroke/pulse/orbit/move({…}) and part.label(words); f.label(part, words), f.view({…})
//   o: {color, width, fill, dashed, size, align: 'start' | 'middle' | 'end', bold}
//   W, H (the canvas: the screenshot's size), math (sin, cos, PI, ...) and point helpers (mid, along, polar, lerp)
//   mechanism(x, y, w, h, {parts, speed})  a working machine in 3D: gears, pulleys and sprockets with belts and chains,
//                                           a rack, a crank with a piston or a rocker → m.label(name, words), m.speed(name)
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
const MOTIONS = ['spin', 'bob', 'stroke', 'pulse', 'orbit', 'move', 'path'];
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

// Mechanisms: mechanism(x, y, w, h, {parts: [...], speed}) builds a working machine in a 3D figure: gears that mesh,
// pulleys and sprockets joined by a belt or a chain, a rack, a crank turning a connecting rod and a piston, a rocker.
// The program says what is joined to what; where each part sits, how fast and which way it turns, and the path a rod
// or a piston follows are worked out here, so teeth always meet, a gear twice the size turns half as fast the other
// way, and a piston slows at each end. Everything keeps one clock, so it all stays in step. The first part drives.
const TOOTH = 0.25;   // a tooth's size (the gears' module), world units: every gear meshes with every other
const SHAFT_Z = 0.5;  // how far apart along a shaft its parts sit (a gear in front of a gear)

// A gear's outline: `teeth` trapezoid teeth of one module round its pitch circle, the first centred at angle 0.
function gearOutline(teeth, module) {
  const rp = module * teeth / 2, ra = rp + module, rf = rp - 1.25 * module, s = 2 * Math.PI / teeth, pts = [];
  for (let i = 0; i < teeth; i++) {
    const a = i * s;
    for (const [r, d] of [[rf, -0.3], [ra, -0.13], [ra, 0.13], [rf, 0.3], [rf, 0.5]]) pts.push([r * Math.cos(a + d * s), r * Math.sin(a + d * s)]);
  }
  return pts;
}
const circleOutline = (r, n, cx = 0, cy = 0) => Array.from({ length: n }, (_, i) => [cx + r * Math.cos(2 * Math.PI * i / n), cy + r * Math.sin(2 * Math.PI * i / n)]);
const angleOf = d => Math.atan2(d[1], d[0]);
const plus = (p, q) => [p[0] + q[0], p[1] + q[1]];
const minus = (p, q) => [p[0] - q[0], p[1] - q[1]];
const times = (p, k) => [p[0] * k, p[1] * k];
const toward = a => [Math.cos(a), Math.sin(a)];
const span = (p, q) => Math.hypot(q[0] - p[0], q[1] - p[1]);

// The loop a belt or chain makes round two wheels (open, or crossed into a figure eight), as points every so often,
// and the way it travels at the first wheel: +1 if counter-clockwise there.
function beltLoop(c1, r1, c2, r2, crossed) {
  const d = span(c1, c2), u = times(minus(c2, c1), 1 / d), perp = [-u[1], u[0]];
  const cos = (crossed ? r1 + r2 : r1 - r2) / d;
  if (Math.abs(cos) >= 1) return null;
  const sin = Math.sqrt(1 - cos * cos);
  const normals = [1, -1].map(side => plus(times(u, cos), times(perp, side * sin)));
  const t1 = normals.map(n => plus(c1, times(n, r1))), t2 = normals.map(n => (crossed ? minus(c2, times(n, r2)) : plus(c2, times(n, r2))));
  // An arc round c from angle a0 to a1 the far way from the other wheel.
  const arc = (c, r, a0, a1, other) => {
    let sweep = ((a1 - a0) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
    const mid = s => plus(c, times(toward(a0 + s / 2), r));
    if (span(mid(sweep), other) < span(mid(sweep - 2 * Math.PI), other)) sweep -= 2 * Math.PI;
    const n = Math.max(4, Math.ceil(Math.abs(sweep) * r / 0.25));
    return Array.from({ length: n }, (_, i) => plus(c, times(toward(a0 + sweep * i / n), r)));
  };
  const line = (p, q) => { const n = Math.max(2, Math.ceil(span(p, q) / 0.3)); return Array.from({ length: n }, (_, i) => plus(p, times(minus(q, p), i / n))); };
  const pts = [...line(t1[0], t2[0]), ...arc(c2, r2, angleOf(minus(t2[0], c2)), angleOf(minus(t2[1], c2)), c1),
    ...line(t2[1], t1[1]), ...arc(c1, r1, angleOf(minus(t1[1], c1)), angleOf(minus(t1[0], c1)), c2)];
  // Which way the loop runs round the first wheel, at the middle of its arc there.
  const k = pts.length - 2, dir = minus(pts[k + 1], pts[k]), radial = minus(pts[k], c1);
  return { pts, ccw: radial[0] * dir[1] - radial[1] * dir[0] > 0 ? 1 : -1 };
}
// The point and heading s along a closed polyline of total length L, from its cumulative lengths.
function alongLoop(pts, cum, s) {
  const L = cum[cum.length - 1], t = ((s % L) + L) % L;
  let i = 0;
  while (i < pts.length - 1 && cum[i + 1] < t) i++;
  const a = pts[i], b = pts[(i + 1) % pts.length], k = (t - cum[i]) / Math.max(1e-9, cum[i + 1] - cum[i]);
  return { at: plus(a, times(minus(b, a), k)), heading: angleOf(minus(b, a)) };
}

function mechanism(x, y, w, h, o) {
  const opts = options(o);
  if (![x, y, w, h].every(finite) || w < 60 || h < 60) { warn('mechanism() needs x, y, width and height (at least 60 each); skipped'); return noMechanism(); }
  const specs = (Array.isArray(opts.parts) ? opts.parts : []).slice(0, 24).map(options);
  if (!specs.length) { warn('mechanism() needs parts: [{gear: …}, …]'); return noMechanism(); }
  // A piston or rocker names its crank (crank: 'K'), so those kinds are looked for first.
  const KINDS = ['piston', 'rocker', 'rack', 'gear', 'pulley', 'sprocket', 'crank'];
  const rpm = finite(opts.speed) && opts.speed > 0 ? Math.min(120, opts.speed) : 20, period = 60 / rpm; // the driver's turn
  const f = api.figure(x, y, w, h, { tilt: finite(opts.tilt) ? opts.tilt : 10, turn: finite(opts.turn) ? opts.turn : -18,
    zoom: opts.zoom, ground: false });

  // First the layout: each part where it goes, its ratio to the driver (turns per driver turn, + counter-clockwise) and
  // its angle when the driver is at 0.
  const placed = new Map(), list = [];
  const fail = (s, why) => warn('mechanism part ' + JSON.stringify(s).slice(0, 80) + ': ' + why);
  const sameLayer = (a, z) => Math.abs(a.z - z) < SHAFT_Z * 0.9;
  const free = (c, r, z, except) => list.every(a => a === except || !a.r || !sameLayer(a, z) || span(a.c, c) > a.r + r + 0.2);
  const AUTO = [0, 180, 90, 270, 45, 135, 315, 225, 30, 150, 330, 210];
  const aimed = (s, around, distance, r, z) => {
    const tries = finite(s.angle) ? [s.angle] : AUTO;
    for (const deg of tries) {
      const c = plus(around.c, times(toward(deg * Math.PI / 180), distance));
      if (finite(s.angle) || free(c, r, z, around)) return { c, psi: deg * Math.PI / 180 };
    }
    return { c: plus(around.c, times(toward(0), distance)), psi: 0 };
  };
  let rack = null;
  for (const s of specs) {
    const kind = KINDS.find(k => typeof s[k] === 'string' || s[k] === true);
    if (!kind) { fail(s, 'name its kind: gear, pulley, sprocket, rack, crank, piston or rocker'); continue; }
    const name = typeof s[kind] === 'string' ? s[kind] : kind + (list.length + 1);
    const ref = k => { const r = typeof s[k] === 'string' ? placed.get(s[k]) : null; if (typeof s[k] === 'string' && !r) fail(s, 'no part called ' + s[k] + ' before it'); return r; };
    const part = { name, kind, spec: s, z: 0, ratio: list.length ? null : 1, phase: 0 };
    const meshes = ref('meshes'), on = ref('on'), belt = ref('belt') || ref('chain'), crank = kind === 'piston' || kind === 'rocker' ? ref('crank') : null;
    if (kind === 'gear' || kind === 'sprocket' || kind === 'pulley') {
      part.teeth = kind === 'pulley' ? 0 : Math.max(8, Math.min(72, Math.round(finite(s.teeth) ? s.teeth : 16)));
      part.r = kind === 'pulley' ? Math.max(0.4, Math.min(8, finite(s.r) ? s.r : 1.5)) : TOOTH * part.teeth / 2; // pitch radius
      part.outer = part.r + (kind === 'pulley' ? 0.15 : TOOTH);
      if (on) { Object.assign(part, { c: on.c, z: on.z + SHAFT_Z, ratio: on.ratio, phase: 0 }); }
      else if (meshes && kind === 'gear' && meshes.teeth && meshes.kind === 'gear') {
        const { c, psi } = aimed(s, meshes, meshes.r + part.r, part.outer, meshes.z);
        const sA = 2 * Math.PI / meshes.teeth, sB = 2 * Math.PI / part.teeth;
        const delta = (((psi - meshes.phase) % sA) + sA) % sA; // turning the first gear this far puts a tooth at the contact
        Object.assign(part, { c, z: meshes.z, ratio: -meshes.ratio * meshes.teeth / part.teeth,
          phase: psi + Math.PI - 0.5 * sB + delta * meshes.teeth / part.teeth });
      } else if (belt && (belt.kind === 'pulley' || belt.kind === 'sprocket')) {
        const distance = finite(s.distance) && s.distance > belt.r + part.r ? s.distance : (belt.r + part.r) * 1.6 + 1.5;
        const { c } = aimed(s, belt, distance, part.outer, belt.z);
        const crossed = !!s.crossed && kind === 'pulley';
        // A chain runs on the sprockets' pitch circles, a belt on the pulleys' rims.
        const rim = kind === 'pulley' ? 0.09 : 0;
        const loop = beltLoop(belt.c, belt.r + rim, c, part.r + rim, crossed);
        if (!loop) { fail(s, 'the wheels are too close for a belt'); continue; }
        Object.assign(part, { c, z: belt.z, ratio: (crossed ? -1 : 1) * belt.ratio * (belt.r + rim) / (part.r + rim), phase: 0, loop, from: belt, chain: kind === 'sprocket' });
      } else if (meshes || belt) { fail(s, 'a gear meshes with a gear; a pulley or sprocket takes a belt or chain from another'); continue; }
      else if (list.length) {
        const at = Array.isArray(s.at) && point(s.at) ? s.at : [list.length * 4, 0];
        Object.assign(part, { c: at, ratio: 0 }); // not joined to anything: it stands still
        warn('mechanism part ' + name + ' is not joined to anything, so it stands still');
      } else part.c = [0, 0];
    } else if (kind === 'rack') {
      if (!meshes || meshes.kind !== 'gear') { fail(s, 'a rack meshes with a gear (its pinion)'); continue; }
      if (rack) { fail(s, 'one rack per machine'); continue; }
      const psi = (finite(s.angle) ? s.angle : 270) * Math.PI / 180;
      Object.assign(part, { pinion: meshes, psi, z: meshes.z, length: Math.max(4, Math.min(30, finite(s.length) ? s.length : meshes.r * 5)), c: plus(meshes.c, times(toward(psi), meshes.r)) });
      rack = part;
    } else if (kind === 'crank') {
      part.arm = Math.max(0.3, Math.min(6, finite(s.r) ? s.r : 1.2));
      if (on) Object.assign(part, { c: on.c, z: on.z + SHAFT_Z * 0.8, ratio: on.ratio, phase: on.phase, shaft: on });
      else if (!list.length) part.c = [0, 0];
      else { fail(s, 'a crank sits on a shaft: on: a gear or pulley'); continue; }
    } else if (kind === 'piston' || kind === 'rocker') {
      if (!crank || crank.kind !== 'crank') { fail(s, 'a ' + kind + ' is driven by a crank: crank: its name'); continue; }
      const rod = Math.max(crank.arm * 1.6, finite(s.rod) ? s.rod : crank.arm * 3);
      Object.assign(part, { crank, rodLength: rod, z: crank.z + 0.3, ratio: crank.ratio, phase: crank.phase });
      if (kind === 'piston') part.slide = (finite(s.angle) ? s.angle : 90) * Math.PI / 180;
      else {
        part.pivot = Array.isArray(s.pivot) && point(s.pivot) ? plus(crank.c, s.pivot) : plus(crank.c, [rod * 0.9, -crank.arm * 0.5]);
        part.length = Math.max(0.5, finite(s.length) ? s.length : rod * 0.8);
      }
    }
    placed.set(name, part);
    list.push(part);
  }
  if (!list.length) return noMechanism();

  // How the driver moves: round and round, or (with a rack, which can't go round) back and forth so the rack slides
  // about a third of its length. turn(part, u) is a part's angle u of the way through a cycle; cycle(part) the cycle's
  // seconds: one turn of the part (or of the swing).
  const swingOf = rack ? (rack.length * 0.33) / (rack.pinion.r * Math.abs(rack.pinion.ratio || 1)) : 0;
  const cycle = p => (rack || !p.ratio ? period : period / Math.abs(p.ratio));
  const turn = (p, u) => p.phase + (rack ? p.ratio * swingOf * Math.sin(2 * Math.PI * u) : Math.sign(p.ratio) * 2 * Math.PI * u);
  const FRAMES = 72, us = Array.from({ length: FRAMES }, (_, i) => i / FRAMES);

  // Then the parts. Wheels turn by spin when the driver goes round (exact at any speed), by a path when it swings.
  const handles = new Map(), driverMat = 'brass';
  // The ground a rocker's post and a driving crank's stand reach down to: below the lowest wheel or crank disc.
  const groundY = Math.min(...list.filter(p => p.c).map(p => p.c[1] - (p.outer || (p.arm ? p.arm * 1.3 : 0.5)))) - 0.6;
  const standTo = (top, z, halfWidth) => {
    const drop = top[1] - groundY;
    if (drop > 0.3) f.extrude({ points: [[-halfWidth - drop * 0.18, -drop], [halfWidth + drop * 0.18, -drop], [halfWidth, 0], [-halfWidth, 0]], depth: 0.3, at: [top[0], top[1], z], mat: 'iron' });
  };
  const turning = (handle, p) => {
    if (!p.ratio) return handle;
    if (rack) handle.path({ frames: us.map(u => [p.c[0], p.c[1], p.z, 0, 0, turn(p, u)]), period, sync: true });
    else handle.spin({ axis: 'z', period: period / Math.abs(p.ratio), reverse: p.ratio < 0, sync: true });
    return handle;
  };
  const matFor = (p, fallback) => (typeof p.spec.mat === 'string' ? p.spec.mat : p.spec.color ? 'paint:' + p.spec.color : list[0] === p ? driverMat : fallback);
  const degrees = r => r * 180 / Math.PI;
  for (const p of list) {
    const s = p.spec;
    let handle = null;
    if (p.kind === 'gear' || p.kind === 'sprocket') {
      const hole = circleOutline(TOOTH * 1.1, 20).reverse();
      handle = f.extrude({ points: gearOutline(p.teeth, TOOTH), holes: [hole], depth: 0.35, bevel: 0.03, at: [p.c[0], p.c[1], p.z],
        rot: [0, 0, degrees(p.phase)], mat: matFor(p, p.kind === 'sprocket' ? 'iron' : 'steel') });
    } else if (p.kind === 'pulley') {
      // A wheel with windows in it, so its turning shows, and a rim for the belt.
      const windows = p.r > 0.8 ? [0, 1, 2, 3, 4].map(k => circleOutline(p.r * 0.22, 16, Math.cos(k * 2 * Math.PI / 5) * p.r * 0.55, Math.sin(k * 2 * Math.PI / 5) * p.r * 0.55).reverse()) : [];
      handle = f.extrude({ points: circleOutline(p.r, 64), holes: [circleOutline(TOOTH * 1.1, 20).reverse(), ...windows], depth: 0.45, bevel: 0.05,
        at: [p.c[0], p.c[1], p.z], rot: [0, 0, degrees(p.phase)], mat: matFor(p, 'alu') });
    } else if (p.kind === 'crank') {
      // A crank disc on the shaft (or its own, when it drives), a window cut opposite its pin so its turning shows; the
      // pin is its child and turns with it.
      const web = p.arm * 1.3, window = circleOutline(p.arm * 0.4, 24, -p.arm * 0.55, 0).reverse();
      handle = f.extrude({ points: circleOutline(web, 64), holes: [circleOutline(TOOTH * 1.1, 20).reverse(), window], depth: 0.3, bevel: 0.04,
        at: [p.c[0], p.c[1], p.z], rot: [0, 0, degrees(p.phase)], mat: matFor(p, 'steel') });
      f.cyl({ r: 0.14, h: 0.6, axis: 'z', at: [p.arm, 0, 0.2], parent: handle, mat: 'chrome' });
      if (!p.shaft) standTo(p.c, p.z - 0.45, 0.3); // a driving crank stands on its own bearing
    } else if (p.kind === 'rack') {
      // The rack along the pinion's tangent, its teeth towards the pinion; it slides as far as the pinion's pitch circle turns.
      const pin = p.pinion, sP = 2 * Math.PI / pin.teeth, pitch = Math.PI * TOOTH, n = 2 * Math.max(2, Math.round(p.length / pitch / 2));
      const outline = [[-n * pitch / 2, -TOOTH * 2.6]];
      for (let i = 0; i < n; i++) {
        const a = -n * pitch / 2 + i * pitch;
        for (const [dx, dy] of [[0.15, -1.25], [0.4, 1], [0.6, 1], [0.85, -1.25]]) outline.push([a + dx * pitch, dy * TOOTH]);
      }
      outline.push([n * pitch / 2, -1.25 * TOOTH], [n * pitch / 2, -TOOTH * 2.6]);
      // Teeth (+y in its own frame) face the pinion; +x runs the way the pinion's edge moves when it turns counter-clockwise.
      const facing = p.psi + Math.PI, along = p.psi + Math.PI / 2, rotZ = facing - Math.PI / 2;
      const delta = (((p.psi - pin.phase) % sP) + sP) % sP; // turning the pinion this far puts a tooth at the contact
      // With an even count of teeth a gap sits at the rack's middle: there, at the contact, when the pinion has turned delta.
      const slideAt = u => (turn(pin, u) - pin.phase - delta) * pin.r;
      const frames = us.map(u => { const c = plus(p.c, times(toward(along), slideAt(u))); return [c[0], c[1], p.z, 0, 0, rotZ]; });
      handle = f.extrude({ points: outline, depth: 0.35, bevel: 0.02, at: frames[0].slice(0, 3), rot: [0, 0, degrees(rotZ)], mat: matFor(p, 'steel') });
      handle.path({ frames, period, sync: true });
    } else if (p.kind === 'piston' || p.kind === 'rocker') {
      const k = p.crank, e = toward(p.slide || 0), ePerp = [-e[1], e[0]];
      const pinAt = u => plus(k.c, times(toward(turn(k, u)), k.arm));
      let joints;
      if (p.kind === 'piston') {
        // The wrist pin slides on the line through the crank's centre: where the rod's length reaches it.
        joints = us.map(u => {
          const q = minus(pinAt(u), k.c), a = q[0] * e[0] + q[1] * e[1], b = q[0] * ePerp[0] + q[1] * ePerp[1];
          return plus(k.c, times(e, a + Math.sqrt(Math.max(0, p.rodLength * p.rodLength - b * b))));
        });
      } else {
        // The rod's far end on the rocker's circle round its pivot, the same of the two crossings every frame.
        let last = null, broken = false;
        joints = us.map(u => {
          const P = pinAt(u), O = p.pivot, d = span(P, O), L = p.rodLength, R = p.length;
          if (d > L + R || d < Math.abs(L - R)) { broken = true; return last || plus(O, [R, 0]); }
          const a = (L * L - R * R + d * d) / (2 * d), hh = Math.sqrt(Math.max(0, L * L - a * a));
          const m = plus(P, times(minus(O, P), a / d)), n = times([-(O[1] - P[1]), O[0] - P[0]], hh / d);
          const options = [plus(m, n), minus(m, n)];
          last = last ? options.sort((g, q) => span(g, last) - span(q, last))[0] : options[0];
          return last;
        });
        if (broken) warn('mechanism rocker ' + p.name + ': the crank cannot turn all the way round with that rod and rocker; give a longer rod or rocker');
      }
      // The connecting rod, from the crank pin to the joint.
      const rodFrames = us.map((u, i) => { const P = pinAt(u), Q = joints[i], c = times(plus(P, Q), 0.5); return [c[0], c[1], p.z, 0, 0, angleOf(minus(Q, P))]; });
      f.box({ w: p.rodLength, h: 0.28, d: 0.18, round: 0.06, at: rodFrames[0].slice(0, 3), mat: 'steel' }).path({ frames: rodFrames, period: cycle(k), sync: true });
      if (p.kind === 'piston') {
        const pr = Math.max(0.45, k.arm * 0.55), len = pr * 1.6, ends = joints.map(J => (J[0] - k.c[0]) * e[0] + (J[1] - k.c[1]) * e[1]);
        const lo = Math.min(...ends) - 0.2, hi = Math.max(...ends) + len + 0.4, mid = plus(k.c, times(e, (lo + hi) / 2));
        f.cyl({ r: pr + 0.06, h: hi - lo, axis: [e[0], e[1], 0], open: true, at: [mid[0], mid[1], p.z], mat: 'glass' }); // the cylinder
        handle = f.cyl({ r: pr, h: len, axis: [e[0], e[1], 0], at: [joints[0][0], joints[0][1], p.z], mat: matFor(p, 'alu') });
        handle.path({ frames: joints.map(J => { const c = plus(J, times(e, len / 2 - 0.15)); return [c[0], c[1], p.z]; }), period: cycle(k), sync: true });
      } else {
        const O = p.pivot;
        f.cyl({ r: 0.2, h: 0.9, axis: 'z', at: [O[0], O[1], p.z], mat: 'iron' });
        standTo(O, p.z - 0.3, 0.25); // its post, down to the ground
        // reach carries the arm on past its pivot (a pump jack's walking beam out to its horse head).
        const reach = finite(s.reach) && s.reach > 0 ? Math.min(12, s.reach) : 0;
        handle = f.box({ w: p.length + reach, h: 0.3, d: 0.2, round: 0.06, at: [O[0], O[1], p.z], mat: matFor(p, 'paint:#3A6FB0') });
        handle.path({ frames: joints.map(J => {
          const d = times(minus(J, O), 1 / Math.max(1e-6, span(O, J))), c = plus(O, times(d, (p.length - reach) / 2));
          return [c[0], c[1], p.z, 0, 0, angleOf(d)];
        }), period: cycle(k), sync: true });
        // head: a pump jack's horse head on the far end of the reach, nodding with the beam.
        if (s.head && reach) {
          f.box({ w: 0.6, h: 1.8, d: 0.5, round: 0.25, at: [O[0], O[1], p.z], mat: matFor(p, 'paint:#3A6FB0') }).path({ frames: joints.map(J => {
            const d = times(minus(J, O), 1 / Math.max(1e-6, span(O, J))), c = minus(O, times(d, reach));
            return [c[0], c[1], p.z, 0, 0, angleOf(d)];
          }), period: cycle(k), sync: true });
        }
      }
    }
    if (handle && (p.kind === 'gear' || p.kind === 'sprocket' || p.kind === 'pulley' || p.kind === 'crank')) {
      turning(handle, p);
      if (!p.spec.on || p.kind === 'crank' && !p.shaft) f.cyl({ r: TOOTH * 0.9, h: 1.2, axis: 'z', at: [p.c[0], p.c[1], p.z - 0.2], mat: 'chrome' }); // its axle
    }
    // A belt or chain round this wheel and the one it comes from, and links or marks that travel round it.
    if (p.loop) {
      const { pts, ccw } = p.loop, from = p.from;
      f.tube({ points: pts.map(q => [q[0], q[1], p.z]), r: p.chain ? 0.1 : 0.09, closed: true, mat: p.chain ? 'iron' : 'rubber' });
      const cum = [0];
      for (let i = 1; i <= pts.length; i++) cum.push(cum[i - 1] + span(pts[i - 1], pts[i % pts.length]));
      const L = cum[cum.length - 1], count = p.chain ? Math.min(36, Math.max(8, Math.round(L / 0.6))) : 8;
      // The belt moves as far as the first wheel's rim turns: s grows the way the loop runs.
      const rim = from.r + (p.chain ? 0 : 0.09);
      const sAt = u => ccw * (rack ? rim * (turn(from, u) - from.phase) : Math.sign(from.ratio) * L * u);
      const beltPeriod = rack ? period : L * cycle(from) / (2 * Math.PI * rim);
      for (let k = 0; k < count && from.ratio; k++) {
        const frames = us.map(u => { const g = alongLoop(pts, cum, k * L / count + sAt(u)); return [g.at[0], g.at[1], p.z, 0, 0, g.heading]; });
        // A chain's link plates ride on both faces of the teeth, so they are wider than the sprocket.
        f.box({ w: p.chain ? 0.42 : 0.3, h: p.chain ? 0.26 : 0.2, d: p.chain ? 0.5 : 0.24, round: 0.04, at: frames[0].slice(0, 3), mat: p.chain ? 'steel' : 'paint:#E8B04A' })
          .path({ frames, period: beltPeriod, sync: true });
      }
    }
    if (handle && typeof s.label === 'string') handle.label(s.label, s.labelColor ? { color: s.labelColor } : undefined);
    if (handle) handles.set(p.name, handle);
  }
  const rpmOf = p => Math.round(rpm * Math.abs(p.ratio || 0) * 10) / 10;
  return {
    figure: f,
    part: name => handles.get(name) || null,
    label: (name, words, o2) => (handles.get(name) ? handles.get(name).label(words, o2) : (warn('mechanism label: no part ' + name), null)),
    // How fast a part turns (turns a minute) and which way: for the spoken words, worked out not guessed.
    speed: name => (placed.get(name) ? rpmOf(placed.get(name)) : null),
    direction: name => (placed.get(name) && placed.get(name).ratio ? (placed.get(name).ratio > 0 ? 'anticlockwise' : 'clockwise') : null),
    view: o2 => f.view(o2),
  };
}
// What mechanism() hands back when it couldn't build one: calls on it do nothing.
const noMechanism = () => new Proxy({}, { get: () => () => null });
api.mechanism = mechanism;

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
  const body = new Function('__api', '__scope', 'with (__api) { with (__scope) {\n' + rewrite(statement) + '\n} }');
  body(api, scope);
  // const liftFan = f.cyl(…): the part is named after its variable ("lift fan"), so a click on it asks about that.
  const named = /^\s*(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*[A-Za-z_$][\w$]*\s*\.\s*[a-z]+\s*\(/.exec(statement);
  const part = named && named[1].length > 2 && marks.find(m => m.do === 'part3d' && !m.params.name);
  if (part) part.params.name = named[1].replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/[_$]+/g, ' ').trim().toLowerCase();
}

// Nothing the program writes can reach the network, load more code or start a worker of its own: a nested worker (from
// a Blob URL) would have a fresh global with fetch in it. Each name is fixed to undefined for good; if any can't be, no
// program runs at all. The app also serves this script with its own CSP (no network, no scripts, no workers), which
// covers what no name can: import() of an address.
const FENCED = ['fetch', 'XMLHttpRequest', 'WebSocket', 'WebTransport', 'EventSource', 'importScripts', 'indexedDB', 'caches',
  'BroadcastChannel', 'Worker', 'SharedWorker', 'Blob', 'File', 'FileReader', 'Request', 'Response'];
for (const name of FENCED) {
  try { Object.defineProperty(self, name, { value: undefined, writable: false, configurable: false }); } catch (_) { /* checked below */ }
}
try { Object.defineProperty(URL, 'createObjectURL', { value: undefined, writable: false, configurable: false }); } catch (_) { /* checked below */ }
const fenced = FENCED.every(name => self[name] === undefined) && URL.createObjectURL === undefined;

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
    if (!fenced) throw new Error('the stage could not shut the program off from the network, so it runs nothing');
    run(String(message.code));
  } catch (e) {
    error = (e && e.message) || String(e);
  }
  self.postMessage({ type: 'ran', seq: message.seq, marks, says, warnings, error });
};
