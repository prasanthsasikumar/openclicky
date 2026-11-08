// OpenClicky drawing stage: the program worker.
//
// Runs the model's program one complete statement at a time (stage.js finds the statements as the answer streams)
// and sends back what each one drew, as plain data ("marks"). The page never runs model code itself; this worker has
// no network, no DOM and no host bridge, and the page ends it if a statement runs too long.
//
// The drawing language (keep docs/draw-stage.md in sync):
//   step(n) / say(text)                     a beat and its spoken line; marks after a say() are drawn while it is said
//   keep()                                  a follow-up's first statement: it draws after the drawing on the stage
//   change(mark, {color, hidden}) / erase(mark)   a follow-up changes what is drawn in place: a 2D mark, a 3D part
//                                           (part.change({size, color, opacity, hidden}), part.hide(), part.show()) or a
//                                           builder's part (cl.part('nucleus')); f.tempo(0.5) slows a figure's motions,
//                                           f.labels(false) hides its labels
//   line(points, o) / curve(points, o)      a pen stroke through [[x, y], ...]; curve() is smooth
//   arrow(points, o)                        a stroke with a head at its last point
//   circle(x, y, r, o) / ellipse(x, y, rx, ry, o) / rect(x, y, w, h, o) / poly(points, o)
//   dot(x, y, r, o) / text(x, y, words, o) / highlight(x, y, w, h, o)
//   cell(x, y, w, h, {type, show})          a cut-open 3D animal, plant or bacterial cell, its parts sized and packed →
//                                           c.label(part, words), c.point(part), c.parts
//   card(x, y, w, h, o)                     a paper card to explain on ({title})
//   photo(x, y, w, h, {query, caption})     a real photo (Wikimedia Commons, found by the app) in a polaroid frame
//   chart(x, y, w, h, {x, y, grid})         axes with numbered ticks ({from, to, step, label, format} or {labels}) → g.line/
//                                           area/bars/dot/label/hline/vline(…) placed by value, g.at(x, y)
//   timeline(x, y, w, {from, to, step})     a line of years → t.event(when, words), t.span(from, to, words), t.at(when)
//   figure(x, y, w, h, o)                   a 3D stage ({tilt, turn, zoom, spin, ground}); its parts: f.box/cyl/cone/sphere/
//                                           torus/capsule/tube/pipe/lathe/extrude/spring/gear/blob/arrow/piece({…}) (any of them
//                                           cut open, repeated or mirrored: cut, copies, around, mirror) → part.spin/bob/
//                                           stroke/pulse/orbit/move({…}) and part.label(words); f.label(part, words), f.view({…})
//   molecule(x, y, w, h, {name} | {atoms, bonds})  a 3D ball-and-stick model, its shape from chemistry (VSEPR) →
//                                           m.label(i, words), m.atom(i), m.view({…})
//   o: {color, width, fill, dashed, size, align: 'start' | 'middle' | 'end', bold}
//   W, H (the canvas: the screenshot's size), math (sin, cos, PI, ...) and point helpers (mid, along, polar, lerp)
//   mechanism(x, y, w, h, {parts, speed})  a working machine in 3D: gears, pulleys and sprockets with belts and chains,
//                                           a rack, a crank with a piston or a rocker → m.label(name, words), m.speed(name)
//   dna(x, y, w, h, {sequence, form})       a double helix (or ladder) from one strand's letters → d.unzip(), d.copy(),
//                                           d.transcribe({from, to}), d.label(which, words), d.complement, d.mrna()
//   circulation(x, y, w, h, {beat, lap})    the heart, lungs and body with blood going round, blue to red →
//                                           b.point(part), b.label(part, words)
//   mitosis(x, y, w, h, {pairs})            a cell dividing, phase by phase: m.next() or m.to('anaphase') plays it on →
//                                           m.phase, m.point(part), m.label(part, words)
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
  const merged = Object.assign({}, options(o), mark, { step: currentStep, n: total - 1 });
  marks.push(merged);
  return { id: total - 1 };
}

function needs(name, ok) {
  if (!ok) warn(name + '() was called with missing or non-numeric arguments; skipped');
  return ok;
}

const api = {
  PI: Math.PI, sin: Math.sin, cos: Math.cos, tan: Math.tan, atan2: Math.atan2, abs: Math.abs, min: Math.min, max: Math.max,
  sqrt: Math.sqrt, pow: Math.pow, exp: Math.exp, log: Math.log, round: Math.round, floor: Math.floor, ceil: Math.ceil, hypot: Math.hypot,
  random: () => 0.5, // a program draws the same every time
  range: n => Array.from({ length: Math.max(0, Math.min(1000, n | 0)) }, (_, i) => i),
  lerp: (a, b, t) => a + (b - a) * t,
  mid: (a, b) => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2],
  along: (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t],
  polar: (cx, cy, r, angle) => [cx + Math.cos(angle) * r, cy + Math.sin(angle) * r],

  keep: () => {}, // the app reads it: this program is drawn after the one already on the stage
  // A follow-up's changes to what is already drawn, in place.
  change: (target, o) => changeDrawn(target, options(o)),
  erase: (...targets) => { targets.forEach(target => changeDrawn(target, { hidden: true })); return null; },
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
  chart, timeline,

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
    // Every motion in the figure at this speed (0.5 half, 2 twice as fast), and its labels shown or not.
    figure.tempo = (k, opts) => { if (finite(k) && k > 0) push({ do: 'tempo3d', fig: id, tempo: k }, opts); else warn('tempo() needs a speed above 0, like 0.5'); return figure; };
    figure.labels = shown => { push({ do: 'labels3d', fig: id, shown: shown !== false }); return figure; };
    return figure;
  },
};

// Graphs and timelines, drawn with the marks above. They place things by value, not by canvas units, so a model gets
// the scale right without working out every coordinate, and they put their own labels where nothing else is.
const TICK = 8, TICK_SIZE = 18; // a tick's nub and its number's size
// About how wide words are in the stage's handwriting at a size (no fonts in here to measure with): capitals,
// digits and wide letters take more room than the rest, a space less.
const textWidth = (words, size) => Array.from(String(words)).reduce((sum, ch) =>
  sum + (ch === ' ' ? 0.3 : /[A-Zmw@%]/.test(ch) ? 0.64 : /[0-9]/.test(ch) ? 0.58 : /[il.,:;'|!]/.test(ch) ? 0.28 : 0.47), 0) * size;

function niceStep(span, most) {
  const raw = span / Math.max(1, most), power = Math.pow(10, Math.floor(Math.log10(raw)));
  for (const m of [1, 2, 2.5, 5]) if (m * power >= raw - 1e-9) return m * power;
  return 10 * power;
}

// 0.30000000000000004 → "0.3", 25000 → "25k", 3200000 → "3.2M"; years stay as they are (1969).
function numberText(v) {
  const a = Math.abs(v), short = n => String(+n.toFixed(2));
  if (a >= 1e9) return short(v / 1e9) + 'B';
  if (a >= 1e6) return short(v / 1e6) + 'M';
  if (a >= 1e4) return short(v / 1e3) + 'k';
  return String(+v.toFixed(4));
}

// An axis from its spec: {from, to, step, label, format} or named columns {labels: [...]}.
function scale(spec, name) {
  const s = options(spec);
  const format = typeof s.format === 'function' ? s.format : numberText;
  const write = v => { try { return String(format(v)); } catch (_) { return numberText(v); } };
  if (Array.isArray(s.labels) && s.labels.length) {
    const names = s.labels.slice(0, 40).map(String);
    return { from: -0.5, to: names.length - 0.5, names, label: s.label, write: i => names[i] || '', ticks: () => names.map((_, i) => i) };
  }
  let from = finite(s.from) ? s.from : 0, to = finite(s.to) ? s.to : 10;
  if (!finite(s.from) || !finite(s.to)) warn(name + ' needs {from, to}; using ' + from + ' to ' + to);
  if (to < from) [from, to] = [to, from];
  if (to === from) to = from + 1;
  const ticks = room => {
    let step = finite(s.step) && s.step > 0 ? s.step : niceStep(to - from, room);
    if ((to - from) / step > 40) step = niceStep(to - from, 40);
    const out = [];
    for (let v = Math.ceil(from / step - 1e-9) * step; v <= to + step * 1e-9; v += step) out.push(+v.toPrecision(12));
    return out;
  };
  return { from, to, label: s.label, write, ticks };
}

// A nub on an axis at p, out to q. Each corner point is there twice, so the smooth stroke turns sharply instead of looping.
const nub = (list, p, q) => list.push(p, p, q, q, p, p);
const boxesMeet = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
// Whether a straight piece [x1, y1, x2, y2] crosses a box (Liang-Barsky clipping).
function crossesBox(piece, box) {
  const [x1, y1, x2, y2] = piece, dx = x2 - x1, dy = y2 - y1;
  let t0 = 0, t1 = 1;
  for (const [p, q] of [[-dx, x1 - box.x], [dx, box.x + box.w - x1], [-dy, y1 - box.y], [dy, box.y + box.h - y1]]) {
    if (p === 0) { if (q < 0) return false; continue; }
    const t = q / p;
    if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; } else { if (t < t0) return false; if (t < t1) t1 = t; }
  }
  return true;
}

// The canvas points of data inside a box, cut where the data leaves it or isn't a number: a list of runs.
function runs(values, toPoint, inside) {
  const out = [];
  let run = [];
  for (const v of values) {
    const p = v && toPoint(v);
    if (p && finite(p[0]) && finite(p[1]) && inside(v)) run.push(p);
    else { if (run.length) out.push(run); run = []; }
  }
  if (run.length) out.push(run);
  return out;
}

// What a chart or timeline hands back when it couldn't be drawn: calls on it do nothing.
const nothing = () => new Proxy({}, { get: () => () => null });

function chart(x, y, w, h, o) {
  if (![x, y, w, h].every(finite) || w < 120 || h < 100) { warn('chart() needs x, y, width and height (at least 120 by 100); skipped'); return nothing(); }
  const opts = options(o), sx = scale(opts.x, 'chart x'), sy = scale(opts.y, 'chart y');
  const ink = opts.color || 'ink';
  const top = y + (sy.label ? 40 : 14), bottom = y + h - TICK - TICK_SIZE - 14 - (sx.label ? 32 : 0);
  const yTicks = sy.ticks(Math.max(2, Math.floor((bottom - top) / 50)));
  const yTexts = yTicks.map(sy.write);
  const left = x + Math.max(10, ...yTexts.map(t => textWidth(t, TICK_SIZE))) + TICK + 14, right = x + w - 18;
  const widest = Math.max(...(sx.names || [sx.write(sx.from), sx.write(sx.to)]).map(t => textWidth(t, TICK_SIZE)));
  const xTicks = sx.ticks(Math.max(2, Math.floor((right - left) / Math.max(56, widest + 18))));
  const px = v => left + (v - sx.from) / (sx.to - sx.from) * (right - left);
  const py = v => bottom - (v - sy.from) / (sy.to - sy.from) * (bottom - top);
  // The axes cross at zero when zero is on them, else at the bottom left.
  const zeroY = sy.from < 0 && sy.to > 0 ? 0 : sy.from, zeroX = !sx.names && sx.from < 0 && sx.to > 0 ? 0 : sx.from;
  const baseY = py(zeroY), baseX = px(zeroX);

  // What the chart has drawn, so its labels keep off it: straight pieces of its strokes, and its labels' boxes.
  const pieces = [], labelBoxes = [];
  const keep = pts => { for (let i = 1; i < pts.length; i++) pieces.push([pts[i - 1][0], pts[i - 1][1], pts[i][0], pts[i][1]]); };
  const boxOf = (at, words, size, align) => {
    const wide = Math.max(size, textWidth(words, size)), high = size * 1.05; // letters, not the line's full height
    return { x: align === 'end' ? at[0] - wide : align === 'start' ? at[0] : at[0] - wide / 2, y: at[1] - high / 2, w: wide, h: high };
  };
  const write = (at, words, o2, align) => {
    labelBoxes.push(boxOf(at, words, finite(o2.size) ? o2.size : 26, align));
    return push({ do: 'text', at, text: String(words) }, Object.assign({}, o2, { align }));
  };
  // Of the spots offered ([x, y, align], best first), the one whose words cross the fewest strokes and labels.
  const writeClear = (spots, words, o2) => {
    const size = finite(o2.size) ? o2.size : 22;
    let best = spots[0], least = Infinity;
    spots.forEach((spot, i) => {
      const box = boxOf(spot, words, size, spot[2]);
      const cost = i * 0.04 + pieces.filter(piece => crossesBox(piece, box)).length + 3 * labelBoxes.filter(b => boxesMeet(b, box)).length
        + (box.x < x || box.x + box.w > x + w || box.y < y || box.y + box.h > y + h ? 2 : 0);
      if (cost < least) { least = cost; best = spot; }
    });
    return write([best[0], best[1]], words, Object.assign({}, o2, { size }), o2.align || best[2]);
  };

  // Grid lines count as strokes for the labels too: one shows through the gaps between a label's words.
  if (opts.grid) {
    for (const v of yTicks) {
      if (v === zeroY) continue;
      const pts = [[left, py(v)], [right, py(v)]];
      keep(pts);
      push({ do: 'line', pts, smooth: true }, { color: 'grey', width: 1 });
    }
  }
  const xAxis = [[left, baseY]];
  if (!sx.names) for (const v of xTicks) nub(xAxis, [px(v), baseY], [px(v), baseY + TICK]);
  xAxis.push([right + 8, baseY]);
  push({ do: sx.names ? 'line' : 'arrow', pts: xAxis, smooth: true }, { color: ink, width: 3 });
  const yAxis = [[baseX, bottom]];
  for (const v of yTicks) nub(yAxis, [baseX, py(v)], [baseX - TICK, py(v)]);
  yAxis.push([baseX, top - 10]);
  push({ do: 'arrow', pts: yAxis, smooth: true }, { color: ink, width: 3 });
  keep([[left, baseY], [right + 8, baseY]]);
  keep([[baseX, bottom], [baseX, top - 10]]);
  const tickText = { size: TICK_SIZE, color: 'grey' };
  xTicks.forEach(v => { if (sx.names || v !== zeroX || zeroY === sy.from) write([px(v), baseY + TICK + 14], sx.write(v), tickText, 'middle'); });
  yTicks.forEach((v, i) => { if (v !== zeroY || zeroX === sx.from) write([baseX - TICK - 6, py(v)], yTexts[i], tickText, 'end'); });
  if (sx.label) write([right + 8, baseY + TICK + 46], sx.label, { size: 22, color: ink }, 'end');
  if (sy.label) write([baseX - 10, top - 28], sy.label, { size: 22, color: ink }, 'start');

  const inside = ([vx, vy]) => vx >= sx.from - 1e-9 && vx <= sx.to + 1e-9 && vy >= sy.from - 1e-9 && vy <= sy.to + 1e-9;
  const at = (vx, vy) => [px(vx), py(vy)];
  // Data as [[x, y], …], as numbers (y over the columns or 0, 1, 2 …), or as a function of x (sampled across the axis).
  const values = (data, o2) => {
    if (typeof data === 'function') {
      const o3 = options(o2), from = Math.max(sx.from, finite(o3.from) ? o3.from : sx.from), to = Math.min(sx.to, finite(o3.to) ? o3.to : sx.to);
      return Array.from({ length: 121 }, (_, i) => { const vx = from + (to - from) * i / 120; let vy; try { vy = +data(vx); } catch (_e) { vy = NaN; } return [vx, vy]; });
    }
    if (!Array.isArray(data)) return [];
    return data.map((d, i) => (finite(d) ? [i, d] : point(d) ? [d[0], d[1]] : null));
  };
  const series = (name, data, o2) => {
    const list = runs(values(data, o2), ([vx, vy]) => [px(vx), py(vy)], inside).filter(p => p.length >= 2 || name === 'line');
    if (!list.length) warn('chart ' + name + '() had no data inside its axes; skipped');
    return list;
  };
  // Around a point, nearest first: up-left, up-right, down-right, down-left, right above, beside it; then the same
  // further out, for a point in a crowded corner.
  const ring = (p, d) => [[p[0] - d, p[1] - d - 6, 'end'], [p[0] + d, p[1] - d - 6, 'start'], [p[0] + d, p[1] + d + 6, 'start'],
    [p[0] - d, p[1] + d + 6, 'end'], [p[0], p[1] - d - 14, 'middle'], [p[0] + d + 6, p[1], 'start'], [p[0] - d - 6, p[1], 'end']];
  const around = (p, d) => ring(p, d).concat(ring(p, d * 1.8), ring(p, d * 2.6));

  return {
    at, x: px, y: py,
    line: (data, o2) => {
      const o3 = Object.assign({ color: 'blue', width: 4 }, options(o2)), list = series('line', data, o2);
      const marks = list.filter(p => p.length >= 2).map(p => { keep(p); return push({ do: 'line', pts: p, smooth: typeof data === 'function' || !!o3.smooth }, o3); });
      if (o3.dots && typeof data !== 'function') list.flat().forEach(p => push({ do: 'dot', at: p, r: 6 }, o3));
      return marks[0] || null;
    },
    area: (data, o2) => {
      const color = options(o2).color || 'blue';
      const marks = series('area', data, o2).map(p => {
        keep(p);
        return push({ do: 'poly', pts: [[p[0][0], baseY]].concat(p, [[p[p.length - 1][0], baseY]]) },
          Object.assign({ width: 2, fill: color, fillOpacity: 0.3 }, options(o2), { color }));
      });
      return marks[0] || null;
    },
    bars: (data, o2) => {
      const o3 = options(o2), colors = Array.isArray(o3.colors) ? o3.colors : null;
      const unit = sx.names ? 1 : xTicks.length > 1 ? xTicks[1] - xTicks[0] : (sx.to - sx.from) / 5;
      const across = Math.max(4, Math.abs(px(unit) - px(0)) * (finite(o3.barWidth) ? o3.barWidth : 0.6));
      let first = null;
      values(data).forEach((d, i) => {
        if (!d || !finite(d[1]) || d[0] < sx.from || d[0] > sx.to) return;
        const value = Math.max(sy.from, Math.min(sy.to, d[1])), cx = px(d[0]), top2 = Math.min(py(value), baseY), tall = Math.abs(py(value) - baseY);
        const color = (colors && colors[i % colors.length]) || o3.color || 'blue';
        const mark = push({ do: 'rect', at: [cx - across / 2, top2], w: across, h: Math.max(1, tall) },
          Object.assign({ width: 2.5, fillOpacity: 0.55 }, o3, { color, fill: o3.fill || color }));
        keep([[cx - across / 2, top2 + tall], [cx - across / 2, top2], [cx + across / 2, top2], [cx + across / 2, top2 + tall]]);
        first = first || mark;
        if (o3.values) write([cx, d[1] >= zeroY ? top2 - 16 : top2 + tall + 16], sy.write(d[1]), { size: 20, color, bold: true }, 'middle');
      });
      if (!first) warn('chart bars() had no values inside its axes; skipped');
      return first;
    },
    dot: (vx, vy, o2) => {
      if (!needs('chart dot', [vx, vy].every(finite))) return null;
      const o3 = Object.assign({ color: 'red' }, options(o2)), p = at(vx, vy);
      const mark = push({ do: 'dot', at: p, r: finite(o3.r) ? o3.r : 7 }, o3);
      if (o3.label) writeClear(around(p, 12), o3.label, { color: o3.color, size: o3.size });
      return mark;
    },
    label: (vx, vy, words, o2) => needs('chart label', [vx, vy].every(finite))
      ? writeClear([[px(vx), py(vy) - 22, 'middle']].concat(around(at(vx, vy), 10)), words, options(o2)) : null,
    hline: (vy, words, o2) => {
      if (!needs('chart hline', finite(vy))) return null;
      const o3 = Object.assign({ color: 'red', width: 2.5, dashed: true }, options(o2)), pts = [[left, py(vy)], [right, py(vy)]];
      const mark = push({ do: 'line', pts, smooth: true }, o3);
      keep(pts);
      if (words) writeClear([[right, py(vy) - 16, 'end'], [left + 10, py(vy) - 16, 'start'], [right, py(vy) + 16, 'end'], [left + 10, py(vy) + 16, 'start']], words, { color: o3.color, size: 20 });
      return mark;
    },
    vline: (vx, words, o2) => {
      if (!needs('chart vline', finite(vx))) return null;
      const o3 = Object.assign({ color: 'red', width: 2.5, dashed: true }, options(o2)), pts = [[px(vx), bottom], [px(vx), top]];
      const mark = push({ do: 'line', pts, smooth: true }, o3);
      keep(pts);
      if (words) writeClear([[px(vx) + 8, top + 6, 'start'], [px(vx) - 8, top + 6, 'end'], [px(vx) + 8, bottom - 16, 'start'], [px(vx) - 8, bottom - 16, 'end']], words, { color: o3.color, size: 20 });
      return mark;
    },
  };
}

// A timeline across at height y. Each label goes on a stem, above or below: the shortest stem where it touches no other
// label and no stem runs through it, centred on its stem or starting or ending by it, straight up or leaning a little.
function timeline(x, y, w, o) {
  if (![x, y, w].every(finite) || w < 120) { warn('timeline() needs x, y and a width of at least 120; skipped'); return nothing(); }
  const opts = options(o), s = scale(opts, 'timeline'), ink = opts.color || 'ink';
  const left = x + 16, right = x + w - 24;
  const ticks = s.ticks(Math.max(2, Math.floor((right - left) / 80)));
  const px = v => left + (v - s.from) / (s.to - s.from) * (right - left);
  const axis = [[left - 12, y]];
  for (const v of ticks) nub(axis, [px(v), y], [px(v), y + TICK]);
  axis.push([right + 14, y]);
  push({ do: 'arrow', pts: axis, smooth: true }, { color: ink, width: 4 });
  for (const v of ticks) push({ do: 'text', at: [px(v), y + TICK + 14], text: s.write(v) }, { size: TICK_SIZE, color: 'grey' });
  const placed = []; // labels so far: {side, level, x0, x1, foot, head} (a stem runs from its foot on the line to its head)
  const axisLabel = opts.label ? String(opts.label) : '';
  if (axisLabel) {
    const x1 = right + 14, x0 = x1 - textWidth(axisLabel, 20) - 6;
    push({ do: 'text', at: [x1, y - 22], text: axisLabel }, { size: 20, color: ink, align: 'end' });
    placed.push({ side: -1, level: 0, x0, x1, foot: NaN, head: NaN });
  }
  // How far from the line a stem's foot and its head (at `level`) are, and where a stem is at the middle of row `row`.
  const depth = (side, level) => (side < 0 ? 30 : 56) + level * 34;
  const footDepth = side => (side < 0 ? 6 : TICK + 26); // a stem below starts under the tick numbers, never through one
  const stemAt = (st, row) => {
    const t = (depth(st.side, row) + 14 - footDepth(st.side)) / (depth(st.side, st.level) - footDepth(st.side));
    return st.foot + (st.head - st.foot) * Math.min(1, Math.max(0, t));
  };
  // A label that has to lean leans back (ending at its stem): events usually come in order, so that leaves room ahead.
  const ALIGN_COST = { middle: 0, end: 0.6, start: 1 };
  const SHIFTS = [0, -40, 40, -80, 80]; // a label may sit a little to the side, on a slanted stem, when nothing straight fits
  let turn = -1;
  function tag(foot, words, o2) {
    const o3 = options(o2), size = finite(o3.size) ? o3.size : 22, wide = Math.max(size, textWidth(words, size)) + 6;
    const sides = o3.side === 'above' ? [-1] : o3.side === 'below' ? [1] : [turn, -turn];
    let best = null;
    for (let level = 0; level < 5; level++) {
      for (const side of sides) {
        for (const dx of SHIFTS) {
          const head = foot + dx;
          for (const [align, shift, x0] of [['middle', 0, head - wide / 2], ['start', -10, head - 10], ['end', 10, head + 10 - wide]]) {
            const x1 = x0 + wide, me = { side, level, foot, head };
            let cost = level * 2 + ALIGN_COST[align] + Math.abs(dx) / 40 * 2.2 + (side === sides[0] ? 0 : 0.5) + (x0 < x || x1 > x + w ? 40 : 0);
            for (const b of placed) {
              if (b.side !== side) continue;
              if (b.level === level && b.x0 < x1 + 4 && x0 < b.x1 + 4) cost += 100;                     // on the same row
              if (b.level < level) { const at = stemAt(me, b.level); if (at > b.x0 - 3 && at < b.x1 + 3) cost += 20; } // through a nearer label
              if (b.level > level) { const at = stemAt(b, level); if (at > x0 - 3 && at < x1 + 3) cost += 20; }     // a longer stem through it
              if ((b.foot - foot) * (b.head - head) < 0) cost += 8;                                     // two stems cross
            }
            if (!best || cost < best.cost) best = { cost, side, level, head, align, shift, x0, x1 };
          }
        }
      }
    }
    if (sides.length > 1) turn = -best.side;
    placed.push({ side: best.side, level: best.level, x0: best.x0, x1: best.x1, foot, head: best.head });
    const tip = y + best.side * depth(best.side, best.level);
    push({ do: 'line', pts: [[foot, y + best.side * footDepth(best.side)], [best.head, tip]], smooth: true }, { color: 'grey', width: 2 });
    return push({ do: 'text', at: [best.head + best.shift, tip + best.side * 14], text: String(words) }, Object.assign({ size }, o3, { align: best.align }));
  }
  return {
    at: v => [px(v), y], x: px,
    event: (when, words, o2) => {
      if (!needs('timeline event', finite(when))) return null;
      if (when < s.from || when > s.to) warn('timeline event at ' + when + ' is off the line (' + s.from + ' to ' + s.to + ')');
      const color = options(o2).color || 'red';
      const mark = tag(px(when), words, Object.assign({ color }, options(o2)));
      push({ do: 'dot', at: [px(when), y], r: 7 }, { color });
      return mark;
    },
    span: (from, to, words, o2) => {
      if (!needs('timeline span', [from, to].every(finite))) return null;
      const a = px(Math.max(s.from, Math.min(from, to))), b = px(Math.min(s.to, Math.max(from, to))), color = options(o2).color || 'blue';
      const band = push({ do: 'rect', at: [a, y - 9], w: Math.max(4, b - a), h: 18 }, { color, fill: color, fillOpacity: 0.4, width: 2 });
      if (words) tag((a + b) / 2, words, Object.assign({ color }, options(o2)));
      return band;
    },
  };
}

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
  // Changed in place: size (times as big as it is now), color, opacity, hidden; secs (0.8) for the change to take.
  handle.change = opts => { push({ do: 'change3d', fig, part: id, params: plain(opts) }); return handle; };
  handle.hide = opts => handle.change(Object.assign({}, options(opts), { hidden: true }));
  handle.show = opts => handle.change(Object.assign({}, options(opts), { hidden: false }));
  return handle;
}

// A builder's figure switches on the builder too: cl.labels(false), b.tempo(0.5) (models write it that way).
function withFigure(build) {
  return (...args) => {
    const built = build(...args);
    if (built && typeof built === 'object' && built.figure && !built.labels) {
      built.labels = shown => { built.figure.labels(shown); return built; };
      built.tempo = (k, o) => { built.figure.tempo(k, o); return built; };
    }
    return built;
  };
}

// Several 3D parts that go by one name (a cell's mitochondria, the lungs): changed, shown, hidden and pointed out
// together, labelled on the first.
function partGroup(list) {
  const group = {
    change: o => { list.forEach(h => h.change(o)); return group; },
    hide: o => { list.forEach(h => h.hide(o)); return group; },
    show: o => { list.forEach(h => h.show(o)); return group; },
    pulse: o => { list.forEach(h => h.pulse(o)); return group; },
    label: (words, o) => (list[0] ? list[0].label(words, o) : null),
  };
  return group;
}
// What part() hands back for a name it doesn't know: calls on it do nothing.
const noPart = (who, which) => { warn(who + ': no part ' + JSON.stringify(which)); return new Proxy({}, { get: () => () => null }); };

// change(target, o) / erase(target): a 2D mark (what line(), text(), rect()… returned), a 3D part or a builder's part,
// or a list of them.
function changeDrawn(target, o) {
  if (Array.isArray(target)) { target.forEach(t => changeDrawn(t, o)); return target; }
  if (target && typeof target.change === 'function') return target.change(o);
  if (target && Number.isInteger(target.id)) {
    push({ do: 'change2d', target: target.id }, { color: typeof o.color === 'string' ? o.color : undefined, hidden: typeof o.hidden === 'boolean' ? o.hidden : undefined });
    return target;
  }
  warn("change() and erase() take something drawn: a mark, a 3D part, or a part of a builder (cl.part('nucleus'))");
  return null;
}

// Molecules: molecule(x, y, w, h, o) builds a ball-and-stick (or space-filling) model in a 3D figure. The model only
// says which atoms there are and how they are bonded (by name, as SMILES, or as lists); the shape is worked out here, the way chemistry has it: bond
// lengths from the atoms' sizes, the angles from the electron pairs around each atom (VSEPR: water bent, methane a
// tetrahedron, carbon dioxide straight, benzene a flat ring), then relaxed so nothing crowds. Hydrogens it leaves out
// are added where each atom has room for them.
// symbol: [covalent radius Å, van der Waals radius Å, bonds it usually makes, outer electrons, colour (CPK), name]
const ELEMENTS = {
  H: [0.31, 1.10, 1, 1, '#F4F4F4', 'hydrogen'], He: [0.28, 1.40, 0, 2, '#D9FFFF', 'helium'],
  Li: [1.28, 1.82, 1, 1, '#CC80FF', 'lithium'], B: [0.84, 1.92, 3, 3, '#FFB5B5', 'boron'],
  C: [0.76, 1.70, 4, 4, '#404040', 'carbon'], N: [0.71, 1.55, 3, 5, '#3050F8', 'nitrogen'],
  O: [0.66, 1.52, 2, 6, '#E8302A', 'oxygen'], F: [0.57, 1.47, 1, 7, '#90E050', 'fluorine'],
  Ne: [0.58, 1.54, 0, 8, '#B3E3F5', 'neon'], Na: [1.66, 2.27, 1, 1, '#AB5CF2', 'sodium'],
  Mg: [1.41, 1.73, 2, 2, '#8AFF00', 'magnesium'], Al: [1.21, 1.84, 3, 3, '#BFA6A6', 'aluminium'],
  Si: [1.11, 2.10, 4, 4, '#F0C8A0', 'silicon'], P: [1.07, 1.80, 3, 5, '#FF8000', 'phosphorus'],
  S: [1.05, 1.80, 2, 6, '#F0C419', 'sulfur'], Cl: [1.02, 1.75, 1, 7, '#1FC01F', 'chlorine'],
  Ar: [1.06, 1.88, 0, 8, '#80D1E3', 'argon'], K: [2.03, 2.75, 1, 1, '#8F40D4', 'potassium'],
  Ca: [1.76, 2.31, 2, 2, '#3DFF00', 'calcium'], Fe: [1.32, 2.00, 2, 2, '#E06633', 'iron'],
  Cu: [1.32, 1.40, 2, 2, '#C88033', 'copper'], Zn: [1.22, 1.39, 2, 2, '#7D80B0', 'zinc'],
  Br: [1.20, 1.85, 1, 7, '#A62929', 'bromine'], I: [1.39, 1.98, 1, 7, '#940094', 'iodine'],
};
// Common molecules by name or formula: heavy atoms and bonds [a, b, order] (or SMILES); hydrogens are added.
const MOLECULES = {
  water: { atoms: ['O'], bonds: [] },
  'carbon dioxide': { atoms: ['C', 'O', 'O'], bonds: [[0, 1, 2], [0, 2, 2]] },
  'carbon monoxide': { atoms: ['C', 'O'], bonds: [[0, 1, 3]], hydrogens: false },
  oxygen: { atoms: ['O', 'O'], bonds: [[0, 1, 2]] },
  ozone: { atoms: ['O', 'O', 'O'], bonds: [[0, 1, 2], [1, 2, 1]], hydrogens: false },
  nitrogen: { atoms: ['N', 'N'], bonds: [[0, 1, 3]] },
  hydrogen: { atoms: ['H', 'H'], bonds: [[0, 1, 1]] },
  methane: { atoms: ['C'], bonds: [] },
  ammonia: { atoms: ['N'], bonds: [] },
  ethanol: { atoms: ['C', 'C', 'O'], bonds: [[0, 1], [1, 2]] },
  ethylene: { atoms: ['C', 'C'], bonds: [[0, 1, 2]] },
  'acetic acid': { atoms: ['C', 'C', 'O', 'O'], bonds: [[0, 1], [1, 2, 2], [1, 3]] },
  'hydrogen peroxide': { atoms: ['O', 'O'], bonds: [[0, 1]] },
  'sulfuric acid': { atoms: ['S', 'O', 'O', 'O', 'O'], bonds: [[0, 1, 2], [0, 2, 2], [0, 3], [0, 4]] },
  'sodium chloride': { atoms: ['Na', 'Cl'], bonds: [[0, 1]] },
  benzene: { atoms: ['C', 'C', 'C', 'C', 'C', 'C'], bonds: [[0, 1, 2], [1, 2], [2, 3, 2], [3, 4], [4, 5, 2], [5, 0]] },
  glucose: { // the ring form: five carbons and an oxygen in a ring, a CH2OH on carbon 5
    atoms: ['C', 'C', 'C', 'C', 'C', 'O', 'C', 'O', 'O', 'O', 'O', 'O'],
    bonds: [[0, 1], [1, 2], [2, 3], [3, 4], [4, 5], [5, 0], [4, 6], [0, 7], [1, 8], [2, 9], [3, 10], [6, 11]],
  },
  caffeine: { smiles: 'CN1C=NC2=C1C(=O)N(C(=O)N2C)C' },
  aspirin: { smiles: 'CC(=O)Oc1ccccc1C(=O)O' },
};
const ALIASES = {
  h2o: 'water', co2: 'carbon dioxide', co: 'carbon monoxide', o2: 'oxygen', o3: 'ozone', n2: 'nitrogen', h2: 'hydrogen',
  ch4: 'methane', nh3: 'ammonia', c2h5oh: 'ethanol', 'alcohol': 'ethanol', c2h4: 'ethylene', ch3cooh: 'acetic acid',
  vinegar: 'acetic acid', c9h8o4: 'aspirin', h2o2: 'hydrogen peroxide', h2so4: 'sulfuric acid', nacl: 'sodium chloride', salt: 'sodium chloride',
  'table salt': 'sodium chloride', c6h6: 'benzene', c6h12o6: 'glucose', sugar: 'glucose', c8h10n4o2: 'caffeine',
};

const v3 = {
  add: (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]], sub: (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]],
  mul: (a, k) => [a[0] * k, a[1] * k, a[2] * k], dot: (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2],
  cross: (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]],
  len: a => Math.hypot(a[0], a[1], a[2]), unit: a => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; },
};
// Any unit vector square to a.
const across = a => v3.unit(v3.cross(a, Math.abs(a[0]) < 0.9 ? [1, 0, 0] : [0, 1, 0]));

// A SMILES string, the one line chemists write a molecule in ('CC(=O)Oc1ccccc1C(=O)O' is aspirin): atoms (C, N, O, Cl …,
// lowercase for one in an aromatic ring, [Na+] [NH4+] [O-] in square brackets), bonds (- = # :, single unless written),
// branches in brackets, ring closures as digits (%12 for two). → {atoms, bonds, hydrogens: count per atom} or null.
const SMILES_ATOMS = ['Cl', 'Br', 'B', 'C', 'N', 'O', 'P', 'S', 'F', 'I', 'b', 'c', 'n', 'o', 'p', 's'];
const SMILES_VALENCES = { B: [3], C: [4], N: [3, 5], O: [2], P: [3, 5], S: [2, 4, 6], F: [1], Cl: [1], Br: [1], I: [1] };
function parseSmiles(text) {
  const s = String(text).trim();
  const atoms = [], aromatic = [], hydrogens = [], bonds = [], branches = [], open = {};
  let previous = -1, bond = null;
  const order = (a, b) => (bond !== null ? bond : aromatic[a] && aromatic[b] ? 1.5 : 1);
  const add = (symbol, isAromatic, h) => {
    const k = atoms.length;
    atoms.push(symbol); aromatic.push(isAromatic); hydrogens.push(h);
    if (previous >= 0) bonds.push([previous, k, order(previous, k)]);
    previous = k; bond = null;
  };
  for (let i = 0; i < s.length && atoms.length <= 120;) {
    const ch = s[i];
    if (ch === '(') { branches.push(previous); i++; continue; }
    if (ch === ')') { previous = branches.length ? branches.pop() : previous; i++; continue; }
    if ('-=#:'.includes(ch)) { bond = { '-': 1, '=': 2, '#': 3, ':': 1.5 }[ch]; i++; continue; }
    if (ch === '/' || ch === '\\' || ch === '@') { i++; continue; } // which way round: the shape is worked out anyway
    if (ch === '.') { previous = -1; i++; continue; }
    if (/[0-9%]/.test(ch)) {
      const label = ch === '%' ? s.slice(i + 1, i + 3) : ch;
      i += ch === '%' ? 3 : 1;
      if (previous < 0) return null;
      if (open[label]) {
        const [a, written] = open[label];
        bonds.push([a, previous, bond !== null ? bond : written !== null ? written : order(a, previous)]);
        delete open[label];
      } else open[label] = [previous, bond];
      bond = null;
      continue;
    }
    if (ch === '[') {
      const end = s.indexOf(']', i), m = end > i && /^\d*([A-Z][a-z]?|[a-z][a-z]?)@*(?:H(\d*))?/.exec(s.slice(i + 1, end));
      if (!m) return null;
      const lower = m[1] === m[1].toLowerCase();
      add(lower ? m[1][0].toUpperCase() + m[1].slice(1) : m[1], lower, m[2] === undefined ? 0 : +(m[2] || 1));
      i = end + 1;
      continue;
    }
    const symbol = SMILES_ATOMS.includes(s.slice(i, i + 2)) ? s.slice(i, i + 2) : SMILES_ATOMS.includes(ch) ? ch : null;
    if (!symbol) return null;
    const lower = symbol === symbol.toLowerCase();
    add(lower ? symbol.toUpperCase() : symbol, lower, null);
    i += symbol.length;
  }
  if (!atoms.length || Object.keys(open).length) return null;
  // Hydrogens on atoms written without brackets: up to the atom's usual number of bonds (an aromatic atom has one in its
  // ring's shared electrons).
  hydrogens.forEach((h, k) => {
    if (h !== null) return;
    const used = bonds.reduce((sum, [a, b, o]) => sum + (a === k || b === k ? (o === 1.5 ? 1 : o) : 0), 0) + (aromatic[k] ? 1 : 0);
    const valence = (SMILES_VALENCES[atoms[k]] || [0]).find(v => v >= used);
    hydrogens[k] = valence === undefined ? 0 : valence - used;
  });
  return { atoms, bonds, hydrogens };
}

// The atoms and bonds a program gave (as SMILES, as lists, or a known molecule's), checked, with hydrogens added:
// {atoms, bonds} or null.
function moleculeFrom(o) {
  const key = String(o.name || '').trim().toLowerCase();
  const known = MOLECULES[key] || MOLECULES[ALIASES[key.replace(/\s+/g, '')]] || MOLECULES[ALIASES[key]];
  const smiles = o.smiles || (!o.atoms && known && known.smiles);
  if (smiles) {
    const read = parseSmiles(smiles);
    if (!read) { warn('molecule() could not read the SMILES "' + String(smiles).slice(0, 60) + '"'); return null; }
    const { atoms, bonds, hydrogens } = read;
    if (o.hydrogens !== false) hydrogens.forEach((h, i) => { for (let k = 0; k < h && atoms.length < 160; k++) { bonds.push([i, atoms.length, 1]); atoms.push('H'); } });
    for (const a of new Set(atoms)) if (!ELEMENTS[a]) warn('molecule(): "' + a + '" is not an element it knows; drawn grey');
    return { atoms, bonds };
  }
  if (key && !known && !Array.isArray(o.atoms)) { warn('molecule() does not know "' + o.name + '"; give it its smiles'); return null; }
  const spec = Array.isArray(o.atoms) ? o : known;
  if (!spec) { warn('molecule() needs {name}, {smiles} or {atoms, bonds}'); return null; }
  const atoms = spec.atoms.slice(0, 80).map(a => {
    const symbol = String(a).trim().replace(/^([a-z])/, c => c.toUpperCase());
    if (!ELEMENTS[symbol]) warn('molecule(): "' + a + '" is not an element it knows; drawn grey');
    return symbol;
  });
  const bonds = [], seen = new Set();
  for (const b of Array.isArray(spec.bonds) ? spec.bonds : []) {
    const [i, j] = Array.isArray(b) ? b : [], order = Array.isArray(b) && [1, 2, 3].includes(b[2]) ? b[2] : 1;
    const k = Math.min(i, j) + ':' + Math.max(i, j);
    if (!Number.isInteger(i) || !Number.isInteger(j) || i === j || !atoms[i] || !atoms[j] || seen.has(k)) { warn('molecule(): bond ' + JSON.stringify(b) + ' skipped'); continue; }
    seen.add(k);
    bonds.push([i, j, order]);
  }
  const hydrogens = (o.hydrogens !== undefined ? o.hydrogens : spec.hydrogens) !== false && !atoms.includes('H');
  if (hydrogens) {
    const heavy = atoms.length;
    for (let i = 0; i < heavy; i++) {
      const used = bonds.reduce((sum, b) => sum + (b[0] === i || b[1] === i ? b[2] : 0), 0);
      const room = ELEMENTS[atoms[i]] ? ELEMENTS[atoms[i]][2] - used : 0;
      for (let k = 0; k < room && atoms.length < 160; k++) { bonds.push([i, atoms.length, 1]); atoms.push('H'); }
    }
  }
  return atoms.length ? { atoms, bonds } : null;
}

// The smallest rings (up to 8 atoms), each as its atoms in order around it.
function moleculeRings(n, bonds, neighbours) {
  const found = new Map();
  for (const [a, b] of bonds) {
    const prev = new Array(n).fill(-1), queue = [a];
    prev[a] = a;
    while (queue.length && prev[b] === -1) {
      const x = queue.shift();
      for (const { j } of neighbours[x]) {
        if (prev[j] !== -1 || (x === a && j === b)) continue;
        prev[j] = x;
        queue.push(j);
      }
    }
    if (prev[b] === -1) continue;
    const ring = [b];
    for (let x = b; x !== a;) { x = prev[x]; ring.push(x); }
    const key = ring.slice().sort((p, q) => p - q).join(',');
    if (ring.length <= 8 && !found.has(key)) found.set(key, ring);
  }
  return [...found.values()].sort((p, q) => p.length - q.length);
}

// A set of points' main axes, longest first (Jacobi on their spread): {mid, axes: [[x, y, z] ×3]}.
function mainAxes(points) {
  const mid = v3.mul(points.reduce(v3.add, [0, 0, 0]), 1 / Math.max(1, points.length));
  const m = [[0, 0, 0], [0, 0, 0], [0, 0, 0]];
  for (const p0 of points) { const p = v3.sub(p0, mid); for (let r = 0; r < 3; r++) for (let c = 0; c < 3; c++) m[r][c] += p[r] * p[c]; }
  const vecs = [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
  for (let sweep = 0; sweep < 12; sweep++) {
    for (const [p, q] of [[0, 1], [0, 2], [1, 2]]) {
      if (Math.abs(m[p][q]) < 1e-12) continue;
      const phi = 0.5 * Math.atan2(2 * m[p][q], m[q][q] - m[p][p]), c = Math.cos(phi), s = Math.sin(phi);
      for (let k = 0; k < 3; k++) { const a = m[k][p], b = m[k][q]; m[k][p] = c * a - s * b; m[k][q] = s * a + c * b; }
      for (let k = 0; k < 3; k++) { const a = m[p][k], b = m[q][k]; m[p][k] = c * a - s * b; m[q][k] = s * a + c * b; }
      for (let k = 0; k < 3; k++) { const a = vecs[k][p], b = vecs[k][q]; vecs[k][p] = c * a - s * b; vecs[k][q] = s * a + c * b; }
    }
  }
  const order = [0, 1, 2].sort((a, b) => m[b][b] - m[a][a]);
  return { mid, axes: order.map(k => [vecs[0][k], vecs[1][k], vecs[2][k]]) };
}

// Where each atom sits, in ångströms: a start (the first ring as a ring, chains zigzagging out from it), then relaxed
// against the bond lengths, the angles at each atom, flatness around double bonds and a push between atoms that aren't
// bonded. Centred, and turned so the molecule lies widest across the screen and a flat one faces the viewer.
function moleculeShape(atoms, bonds) {
  const n = atoms.length, el = i => ELEMENTS[atoms[i]] || [0.8, 1.7, 0, 0, '#A0A0A0', atoms[i]];
  const neighbours = atoms.map(() => []);
  for (const [i, j, order] of bonds) { neighbours[i].push({ j, order }); neighbours[j].push({ j: i, order }); }
  // The angle between two bonds at an atom, from its bonds and lone pairs.
  const angle = atoms.map((_, i) => {
    const used = neighbours[i].reduce((s, b) => s + b.order, 0), [, , valence, outer] = el(i);
    const lone = valence > 0 && outer >= 4 ? Math.max(0, Math.floor((outer - used) / 2)) : 0;
    const steric = neighbours[i].length + lone;
    if (steric <= 2) return 180;
    if (steric === 3) return lone ? 117 : 120;
    if (steric === 4) return lone === 2 ? 104.5 : lone === 1 ? 107 : 109.5;
    return 0; // five or six around it: left to the push between its neighbours
  });
  const rings = moleculeRings(n, bonds, neighbours);
  // A benzene-like ring (six atoms, three double bonds in it) has every bond halfway between single and double.
  const halfway = new Set(bonds.filter(b => b[2] === 1.5).flatMap(([a, b]) => [a + ':' + b, b + ':' + a]));
  for (const ring of rings) {
    const edges = ring.map((a, k) => [a, ring[(k + 1) % ring.length]]);
    const doubles = edges.filter(([a, b]) => neighbours[a].some(x => x.j === b && x.order === 2)).length;
    if (ring.length === 6 && doubles === 3) for (const [a, b] of edges) { halfway.add(a + ':' + b); halfway.add(b + ':' + a); }
  }
  const length = (i, j, order) => (el(i)[0] + el(j)[0]) * (halfway.has(i + ':' + j) ? 0.93 : order === 3 ? 0.78 : order === 2 ? 0.87 : 1);

  // The start. One ring is laid out as a ring (puckered a little where its atoms aren't flat ones); everything
  // else sets out from the atom it is bonded to, at that atom's angle, turned away from the atom before it so chains
  // zigzag instead of curling.
  const pos = atoms.map(() => null), parent = atoms.map(() => -1), inward = atoms.map(() => null);
  const queue = [];
  if (rings.length) {
    // The flattest ring first (most double or aromatic bonds, then the bigger): it is the one a chain closes worst.
    const flatness = r => r.filter((a, k) => halfway.has(a + ':' + r[(k + 1) % r.length]) || neighbours[a].some(b => b.j === r[(k + 1) % r.length] && b.order === 2)).length;
    const ring = rings.slice().sort((p, q) => flatness(q) - flatness(p) || q.length - p.length)[0], side = ring.reduce((s, a, k) => s + length(a, ring[(k + 1) % ring.length], 1), 0) / ring.length;
    const radius = side / (2 * Math.sin(Math.PI / ring.length));
    ring.forEach((a, k) => {
      const t = 2 * Math.PI * k / ring.length;
      pos[a] = [radius * Math.cos(t), radius * Math.sin(t), angle[a] === 120 ? 0 : (k % 2 ? 0.25 : -0.25)];
      inward[a] = [-Math.cos(t), -Math.sin(t), 0];
      queue.push(a);
    });
  }
  const grow = () => {
    while (queue.length) {
      const a = queue.shift(), fresh = neighbours[a].filter(b => !pos[b.j]);
      if (!fresh.length) continue;
      const p = parent[a], offRing = p < 0 && inward[a];
      let back = p >= 0 ? v3.unit(v3.sub(pos[p], pos[a])) : inward[a];
      let side = back ? (p >= 0 && parent[p] >= 0 ? v3.sub(pos[parent[p]], pos[p]) : offRing ? [0, 0, 1] : across(back)) : null;
      // Off a ring, one neighbour points straight out; two lean out to either side of the ring.
      const theta = (offRing ? (fresh.length === 1 ? 180 : 125) : angle[a] || 109.5) * Math.PI / 180;
      fresh.forEach((b, k) => {
        let dir;
        if (!back) { dir = [1, 0, 0]; back = dir; side = [0, 1, 0]; } else {
          const u = back, s0 = v3.unit(v3.sub(side, v3.mul(u, v3.dot(side, u)))), s1 = v3.cross(u, s0);
          const placed = offRing ? fresh.length : neighbours[a].length - 1;
          const phi = (offRing ? 0 : Math.PI) + 2 * Math.PI * k / Math.max(1, placed);
          const around = v3.add(v3.mul(s0, Math.cos(phi)), v3.mul(s1, Math.sin(phi)));
          dir = v3.add(v3.mul(u, Math.cos(theta)), v3.mul(around, Math.sin(theta)));
        }
        pos[b.j] = v3.add(pos[a], v3.mul(v3.unit(dir), length(a, b.j, b.order)));
        parent[b.j] = a;
        queue.push(b.j);
      });
    }
  };
  grow();
  for (let start = 0; start < n; start++) {
    if (pos[start]) continue;
    pos[start] = [start * 3, 0, 0];
    queue.push(start);
    grow();
  }
  // A small nudge out of the flat start, so an atom that should be a pyramid (ammonia's nitrogen) can become one.
  pos.forEach((p, i) => { p[2] += 0.12 * Math.sin(i * 2.39 + 1); });

  // An angle inside a small ring is the ring's own (a pentagon's 108°, a square's 90°), not the atom's usual one.
  const ringAngle = new Map();
  for (const ring of rings) {
    if (ring.length > 5) continue;
    ring.forEach((c, k) => {
      const i = ring[(k + ring.length - 1) % ring.length], j = ring[(k + 1) % ring.length];
      ringAngle.set(c + ':' + Math.min(i, j) + ':' + Math.max(i, j), 180 * (ring.length - 2) / ring.length);
    });
  }

  // Relaxing: bonds, the angles (as the distance between two atoms bonded to the same one), flatness, and the push.
  const pairs = [], close = new Set();
  for (const [i, j, order] of bonds) { pairs.push([i, j, length(i, j, order), 0.6]); close.add(i + ':' + j); close.add(j + ':' + i); }
  for (let c = 0; c < n; c++) {
    const nb = neighbours[c];
    for (let p = 0; p < nb.length; p++) {
      for (let q = p + 1; q < nb.length; q++) {
        const i = nb[p].j, j = nb[q].j;
        close.add(i + ':' + j); close.add(j + ':' + i);
        if (!angle[c]) continue;
        const a = length(c, i, nb[p].order), b = length(c, j, nb[q].order);
        const t = (ringAngle.get(c + ':' + Math.min(i, j) + ':' + Math.max(i, j)) || angle[c]) * Math.PI / 180;
        pairs.push([i, j, Math.sqrt(a * a + b * b - 2 * a * b * Math.cos(t)), 0.4]);
      }
    }
  }
  // Atoms around a double bond lie in one plane (ethylene, benzene, C=O): its two atoms and everything bonded to
  // whichever of them is a flat one (three around it). Sulfuric acid's sulfur, with four, stays a tetrahedron.
  const flat = i => angle[i] === 120 || angle[i] === 117;
  const flats = [];
  for (const [i, j, order] of bonds) {
    if (order !== 2 && !halfway.has(i + ':' + j)) continue;
    const group = [...new Set([i, j, ...(flat(i) ? neighbours[i].map(b => b.j) : []), ...(flat(j) ? neighbours[j].map(b => b.j) : [])])];
    if (group.length > 3) flats.push(group);
  }
  const apart = [];
  for (let i = 0; i < n; i++) for (let j = i + 1; j < n; j++) if (!close.has(i + ':' + j)) apart.push([i, j, 0.62 * (el(i)[1] + el(j)[1])]);
  const rounds = n > 60 ? 600 : 1500;
  for (let round = 0; round < rounds; round++) {
    for (const [i, j, want, k] of pairs) {
      const d = v3.sub(pos[j], pos[i]), l = v3.len(d) || 1e-6, fix = v3.mul(d, k * (l - want) / l / 2);
      pos[i] = v3.add(pos[i], fix); pos[j] = v3.sub(pos[j], fix);
    }
    if (round % 4 === 0) {
      for (const group of flats) {
        const { mid, axes } = mainAxes(group.map(i => pos[i]));
        for (const i of group) pos[i] = v3.sub(pos[i], v3.mul(axes[2], 0.5 * v3.dot(v3.sub(pos[i], mid), axes[2])));
      }
    }
    for (const [i, j, least] of apart) {
      const d = v3.sub(pos[j], pos[i]), l = v3.len(d) || 1e-6;
      if (l >= least) continue;
      const fix = v3.mul(d, 0.1 * (l - least) / l / 2);
      pos[i] = v3.add(pos[i], fix); pos[j] = v3.sub(pos[j], fix);
    }
  }

  // Centred and turned to its main axes: longest across, flattest towards the viewer.
  const { mid, axes } = mainAxes(pos);
  axes[2] = v3.cross(axes[0], axes[1]);
  return { pos: pos.map(p0 => { const p = v3.sub(p0, mid); return [v3.dot(p, axes[0]), v3.dot(p, axes[1]), v3.dot(p, axes[2])]; }), neighbours, rings };
}

// What molecule() hands back when it couldn't build one: calls on it do nothing.
const noMolecule = () => new Proxy({}, { get: (_, key) => key === 'figure' ? nothing() : key === 'part' || key === 'atom' ? () => nothing() : () => null });

function molecule(x, y, w, h, o) {
  if (![x, y, w, h].every(finite) || w < 40 || h < 40) { warn('molecule() needs x, y, width and height (at least 40 each); skipped'); return noMolecule(); }
  const opts = options(o), made = moleculeFrom(opts);
  if (!made) return noMolecule();
  const { atoms, bonds } = made, { pos, neighbours, rings } = moleculeShape(atoms, bonds);
  const space = opts.style === 'space';
  const f = api.figure(x, y, w, h, { tilt: finite(opts.tilt) ? opts.tilt : 12, turn: finite(opts.turn) ? opts.turn : -20,
    spin: finite(opts.spin) ? opts.spin : 0, zoom: opts.zoom, ground: false }); // it sways unless asked to spin
  const el = i => ELEMENTS[atoms[i]] || [0.8, 1.7, 0, 0, '#A0A0A0', atoms[i]];
  const parts = atoms.map(() => null), drawn = atoms.map(() => false);
  const stick = (from, to, r) => {
    const d = v3.sub(to, from);
    return f.cyl({ r, h: v3.len(d), at: v3.mul(v3.add(from, to), 0.5), axis: v3.unit(d), mat: 'plastic:#C9CDD3' });
  };
  // A double or triple bond's sticks side by side, in the plane of a neighbour's bond where there is one.
  const sideOf = (i, j) => {
    const d = v3.unit(v3.sub(pos[j], pos[i])), other = neighbours[i].find(b => b.j !== j) || neighbours[j].find(b => b.j !== i);
    if (!other) return across(d);
    const t = v3.sub(pos[other.j], pos[i]);
    const s = v3.sub(t, v3.mul(d, v3.dot(t, d)));
    return v3.len(s) > 1e-3 ? v3.unit(s) : across(d);
  };
  const bond = (i, j, order) => {
    if (space) return;
    if (order === 1) { stick(pos[i], pos[j], 0.11); return; }
    const s = sideOf(i, j), gap = order === 2 ? 0.1 : 0.14;
    // An aromatic ring's bond (between single and double): a full stick and a thin one beside it.
    if (order === 1.5) { stick(pos[i], pos[j], 0.1); stick(v3.add(pos[i], v3.mul(s, 0.17)), v3.add(pos[j], v3.mul(s, 0.17)), 0.045); return; }
    const offsets = order === 2 ? [-gap, gap] : [-gap, 0, gap];
    for (const k of offsets) stick(v3.add(pos[i], v3.mul(s, k)), v3.add(pos[j], v3.mul(s, k)), order === 2 ? 0.075 : 0.065);
  };
  // Built out from the first atom: an atom, then its bonds to the atoms already there, so it grows like a model kit.
  const queue = [], label = opts.labels === undefined ? atoms.length <= 40 : opts.labels === 'all' && atoms.length > 12 ? true : opts.labels;
  // Which atoms carry their element's name: for each element the atom furthest out that isn't beside one already named,
  // so the tags spread round the molecule instead of piling up where it starts ('all': every atom, on a small one).
  const named = new Map();
  if (label) {
    const outward = atoms.map((_, i) => i).sort((a, b) => v3.len(pos[b]) - v3.len(pos[a]));
    const has = name => [...named.values()].includes(name);
    for (const i of outward) {
      if (label === 'all') { named.set(i, el(i)[5]); continue; }
      if (has(el(i)[5]) || [...named.keys()].some(k => v3.len(v3.sub(pos[k], pos[i])) < 2.2)) continue;
      named.set(i, el(i)[5]);
    }
    for (const i of outward) if (!has(el(i)[5])) named.set(i, el(i)[5]);
  }
  for (let start = 0; start < atoms.length; start++) {
    if (drawn[start]) continue;
    queue.push(start);
    drawn[start] = true;
    while (queue.length) {
      const i = queue.shift(), [, vdw, , , colour] = el(i);
      parts[i] = f.sphere({ r: space ? vdw * 0.95 : 0.16 + vdw * 0.17, at: pos[i], mat: 'plastic:' + colour });
      for (const b of neighbours[i]) if (parts[b.j]) bond(b.j, i, b.order);
      if (named.has(i)) parts[i].label(named.get(i));
      for (const b of neighbours[i]) if (!drawn[b.j]) { drawn[b.j] = true; queue.push(b.j); }
    }
  }
  // An atom by its place in the list (0 first), or by name: 'N' the first nitrogen, 'O#2' the second oxygen, 'ring' the
  // outermost atom of the first ring. A model counts a SMILES string's atoms badly; it names them well.
  const pick = which => {
    if (Number.isInteger(which)) return which;
    const w = String(which).trim();
    if (/^ring$/i.test(w)) return rings.length ? rings[0].reduce((best, a) => (v3.len(pos[a]) > v3.len(pos[best]) ? a : best), rings[0][0]) : -1;
    const m = /^([A-Z][a-z]?)(?:#(\d+))?$/.exec(w);
    if (!m) return -1;
    const all = atoms.map((a, i) => (a === m[1] ? i : -1)).filter(i => i >= 0);
    return all[(m[2] ? +m[2] : 1) - 1] ?? -1;
  };
  return {
    figure: f, atoms: atoms.length,
    atom: which => parts[pick(which)] || null,
    part: which => parts[pick(which)] || noPart('molecule', which),
    label: (which, words, o2) => {
      const i = pick(which);
      return parts[i] ? parts[i].label(words, o2) : (warn('molecule label: no atom ' + JSON.stringify(which)), null);
    },
    view: o2 => f.view(o2),
  };
}

api.molecule = withFigure(molecule);

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
    // on: is a shaft to share, so only a part that turns about a centre can carry another (not a piston, rocker or rack).
    if (on && !(on.c && ['gear', 'pulley', 'sprocket', 'crank'].includes(on.kind))) { fail(s, 'on: names a gear, pulley, sprocket or crank to share its shaft'); continue; }
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
    part: name => handles.get(name) || noPart('mechanism', name),
    label: (name, words, o2) => (handles.get(name) ? handles.get(name).label(words, o2) : (warn('mechanism label: no part ' + name), null)),
    // How fast a part turns (turns a minute) and which way: for the spoken words, worked out not guessed.
    speed: name => (placed.get(name) ? rpmOf(placed.get(name)) : null),
    direction: name => (placed.get(name) && placed.get(name).ratio ? (placed.get(name).ratio > 0 ? 'anticlockwise' : 'clockwise') : null),
    view: o2 => f.view(o2),
  };
}
// What mechanism() hands back when it couldn't build one: calls on it do nothing.
const noMechanism = () => new Proxy({}, { get: (_, key) => key === 'figure' ? nothing() : key === 'part' || key === 'atom' ? () => nothing() : () => null });
api.mechanism = withFigure(mechanism);

// DNA: dna(x, y, w, h, {sequence, form}) builds a double helix (or a flat ladder) in a 3D figure from the letters of
// one strand. The other strand is worked out here (A pairs with T, G with C), the helix has its real shape (ten base
// pairs a turn, 0.34 nm apart, the two backbones offset so the grooves show), and d.unzip(), d.copy() and
// d.transcribe() animate what a cell does with it: it untwists, the strands part, new nucleotides pair up base by
// base (two copies, each half old and half new), or an mRNA copy forms against one strand and leaves.
const DNA_R = 1, DNA_RISE = 0.34, DNA_TWIST = Math.PI / 5, DNA_OFFSET = 150 * Math.PI / 180; // nm, radians
const DNA_BASE_COLORS = { A: '#E5484D', T: '#F2B33D', G: '#3DAE6B', C: '#3B82D6', U: '#9B59D0' };
const DNA_BASE_NAMES = { A: 'adenine', T: 'thymine', G: 'guanine', C: 'cytosine', U: 'uracil' };
const DNA_PAIR = { A: 'T', T: 'A', G: 'C', C: 'G' };
const DNA_STRANDS = { one: '#5B6C8F', two: '#A0607F', newOne: '#AFC0E3', newTwo: '#E3B0CB', mrna: '#9B59D0' };

const v3add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const v3sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const v3mul = (a, k) => [a[0] * k, a[1] * k, a[2] * k];
const v3len = a => Math.hypot(a[0], a[1], a[2]);
const v3unit = a => v3mul(a, 1 / (v3len(a) || 1));
const v3lerp = (a, b, k) => v3add(a, v3mul(v3sub(b, a), k));
// The rotation (Euler XYZ, radians, as three.js keeps it) that turns a part built along y to point along d.
function eulerFromY(d0) {
  const d = v3unit(d0);
  let x, y, z, w;
  if (d[1] < -0.999999) { x = 1; y = 0; z = 0; w = 0; } else {
    // From y to d: w = 1 + y·d, (x, y, z) = y × d, normalised.
    w = 1 + d[1]; x = d[2]; y = 0; z = -d[0];
    const n = Math.hypot(x, y, z, w); x /= n; y /= n; z /= n; w /= n;
  }
  const m11 = 1 - 2 * (y * y + z * z), m12 = 2 * (x * y - w * z), m13 = 2 * (x * z + w * y);
  const m22 = 1 - 2 * (x * x + z * z), m23 = 2 * (y * z - w * x), m32 = 2 * (y * z + w * x), m33 = 1 - 2 * (x * x + y * y);
  const ry = Math.asin(Math.max(-1, Math.min(1, m13)));
  return Math.abs(m13) < 0.9999999 ? [Math.atan2(-m23, m33), ry, Math.atan2(-m12, m11)] : [Math.atan2(m32, m22), ry, 0];
}

function dna(x, y, w, h, o) {
  const opts = options(o);
  if (![x, y, w, h].every(finite) || w < 60 || h < 60) { warn('dna() needs x, y, width and height (at least 60 each); skipped'); return noDna(); }
  let letters = String(opts.sequence || 'ATGCGTACCTGAATGC').toUpperCase().replace(/U/g, 'T').replace(/[^ACGT]/g, '');
  if (letters.length < 4) { warn('dna() needs a sequence of at least 4 letters (A, T, G, C); using a short one'); letters = 'ATGCGTACCTGA'; }
  if (letters.length > 30) { warn('dna() shows up to 30 base pairs; the sequence was cut there'); letters = letters.slice(0, 30); }
  const n = letters.length, partner = Array.from(letters, b => DNA_PAIR[b]).join('');
  const f = api.figure(x, y, w, h, { tilt: finite(opts.tilt) ? opts.tilt : 12, turn: finite(opts.turn) ? opts.turn : -22, zoom: opts.zoom, ground: false });

  // Where things sit. In the helix, strand one's i-th backbone point turns 36° a step, strand two's 150° further on;
  // each base reaches from its backbone to the middle of the pair. In the ladder, the same pieces lie flat in rows,
  // as far apart as in the helix, so nothing changes length when it untwists: only where it sits.
  const bp = (s, i) => { const t = i * DNA_TWIST + (s === 2 ? DNA_OFFSET : 0); return [(i - (n - 1) / 2) * DNA_RISE, DNA_R * Math.cos(t), DNA_R * Math.sin(t)]; };
  const chord = v3len(v3sub(bp(1, 1), bp(1, 0))), baseHalf = v3len(v3sub(bp(1, 0), v3mul(v3add(bp(1, 0), bp(2, 0)), 0.5)));
  const baseLen = baseHalf - 0.06;
  const helixPose = (kind, s, i) => {
    if (kind === 'seg') { const a = bp(s, i), b = bp(s, i + 1); return { c: v3lerp(a, b, 0.5), d: v3sub(b, a) }; }
    const p = bp(s, i), m = v3mul(v3add(bp(1, i), bp(2, i)), 0.5), d = v3sub(m, p);
    return { c: v3add(p, v3mul(v3unit(d), baseLen / 2)), d };
  };
  // A strand in the ladder: its backbone at height yb (z deep), its bases pointing up (dir 1) or down (-1).
  const ladderX = i => (i - (n - 1) / 2) * chord;
  const ladderPose = (kind, i, yb, dir, z = 0) => (kind === 'seg'
    ? { c: [ladderX(i) + chord / 2, yb, z], d: [1, 0, 0] }
    : { c: [ladderX(i), yb + dir * baseLen / 2, z], d: [0, dir, 0] });
  const OPEN = 2 * baseHalf + 0.7; // an opened strand's backbone, from the middle: room for a new strand by each

  const parts = []; // {kind: 'base' | 'seg', s (1, 2, or a new strand's name), i, handle, pose}
  const make = (kind, s, i, pose, letter, backbone) => {
    const rot = eulerFromY(pose.d).map(r => r * 180 / Math.PI);
    const handle = kind === 'base'
      ? f.cyl({ r: 0.13, h: baseLen, at: pose.c, rot, mat: 'plastic:' + DNA_BASE_COLORS[letter], quick: true })
      : f.cyl({ r: 0.12, h: chord, at: pose.c, rot, mat: 'plastic:' + backbone, quick: true });
    const part = { kind, s, i, handle, pose, letter };
    parts.push(part);
    return part;
  };
  let form = opts.form === 'ladder' ? 'ladder' : 'helix', state = 'closed';
  for (let i = 0; i < n; i++) {
    for (const s of [1, 2]) {
      const letter = s === 1 ? letters[i] : partner[i], color = s === 1 ? DNA_STRANDS.one : DNA_STRANDS.two;
      const pose = k => (form === 'helix' ? helixPose(k, s, i) : ladderPose(k, i, s === 1 ? -baseHalf : baseHalf, s === 1 ? 1 : -1));
      make('base', s, i, pose('base'), letter);
      if (i < n - 1) make('seg', s, i, pose('seg'), null, color);
    }
  }

  // Moving parts: each part plays its own steps once, [[seconds, pose(part) or null to stay], …], arriving at each
  // pose by its time (eased), every time shifted by lag(part) seconds so a strand moves like a zip. One path per part
  // per call: a later path takes over a part, so a call plans the whole of its part's moves.
  const FRAMES = 72;
  const ease = k => k * k * (3 - 2 * k);
  const blend = (a, b, k) => ({ c: v3lerp(a.c, b.c, k), d: v3lerp(v3unit(a.d), v3unit(b.d), k) });
  function play(list, stepsOf, lag = () => 0) {
    for (const part of list) {
      const keys = [[0, part.pose]];
      for (const [t, to] of stepsOf(part)) keys.push([t + lag(part), to ? to(part) : keys[keys.length - 1][1]]);
      if (keys.length < 2) continue;
      const total = Math.max(0.2, keys[keys.length - 1][0]);
      const frames = Array.from({ length: FRAMES }, (_, k) => {
        const t = (k / (FRAMES - 1)) * total;
        let j = 0;
        while (j < keys.length - 2 && keys[j + 1][0] < t) j++;
        const [t0, a] = keys[j], [t1, b] = keys[j + 1];
        const pose = blend(a, b, ease(Math.min(1, Math.max(0, (t - t0) / Math.max(1e-6, t1 - t0)))));
        return [...pose.c, ...eulerFromY(pose.d)];
      });
      part.handle.path({ frames, period: total, once: true });
      part.pose = keys[keys.length - 1][1];
    }
  }
  const strandOf = s => parts.filter(p => p.s === s);
  const flatClosed = p => ladderPose(p.kind, p.i, p.s === 1 ? -baseHalf : baseHalf, p.s === 1 ? 1 : -1);
  const ZIP = 1.5; // seconds from the first nucleotide to the last: they move like a zip, left to right
  const zip = p => (p.i / Math.max(1, n - 1)) * ZIP;
  // Untwisting first, when it's still a helix: the seconds that takes (the zip included).
  const untwist = () => (form === 'helix' ? 1.6 + ZIP : 0);

  function unzip() {
    if (state !== 'closed') return handle;
    const open = p => ladderPose(p.kind, p.i, p.s === 1 ? -OPEN : OPEN, p.s === 1 ? 1 : -1);
    const twisted = form === 'helix';
    play([...strandOf(1), ...strandOf(2)], () => (twisted ? [[1.6, flatClosed], [3, open]] : [[1.4, open]]), zip);
    form = 'ladder';
    state = 'open';
    return handle;
  }

  // Each old strand gets a new partner: free nucleotides float in from the front and pair up, base by base. Two DNAs,
  // each one old strand and one new.
  function copy() {
    if (state === 'copied') return handle;
    let wait = 0.3;
    if (state !== 'open') { wait = (form === 'helix' ? 3 : 1.4) + ZIP; unzip(); }
    const gap = OPEN - 2 * baseHalf; // the new strands' backbones, either side of the middle
    const fresh = [];
    for (let i = 0; i < n; i++) {
      for (const [s, yb, dir, letter, color] of [['newOne', -gap, -1, partner[i], DNA_STRANDS.newOne], ['newTwo', gap, 1, letters[i], DNA_STRANDS.newTwo]]) {
        for (const kind of i < n - 1 ? ['base', 'seg'] : ['base']) {
          const target = ladderPose(kind, i, yb, dir);
          const part = make(kind, s, i, { c: v3add(target.c, [0, dir * -0.4, 2.6 + (i % 3) * 0.5]), d: target.d }, kind === 'base' ? letter : null, color);
          part.target = target;
          fresh.push(part);
        }
      }
    }
    play(fresh, () => [[wait, null], [wait + 1.3, q => q.target]], zip);
    state = 'copied';
    return handle;
  }

  // RNA polymerase reads a stretch: strand one steps forward out of the way, an mRNA (strand one's letters, U for T)
  // floats in and pairs with strand two, holds, then drops away below, and strand one steps back.
  function transcribe(o2) {
    if (state !== 'closed') { warn('dna transcribe(): only a closed DNA can be read; call it before unzip() or copy()'); return handle; }
    const oo = options(o2), from = Math.max(0, Math.min(n - 2, finite(oo.from) ? Math.round(oo.from) : Math.floor(n * 0.25)));
    const to = Math.max(from + 1, Math.min(n - 1, finite(oo.to) ? Math.round(oo.to) : Math.ceil(n * 0.75)));
    const inStretch = p => p.i >= from && p.i <= to;
    const t1 = untwist(), formed = t1 + 1.2 + 1.4 + ZIP, gone = formed + 1.6 + 1.4;
    const forward = p => ladderPose(p.kind, p.i, -baseHalf, 1, 1.9);
    const flatFirst = form === 'helix' ? [[1.6, flatClosed]] : [];
    play(strandOf(2), () => flatFirst, zip);
    play(strandOf(1), p => (inStretch(p) ? [...flatFirst, [t1, null], [t1 + 1.2, forward], [gone + ZIP + 0.1, null], [gone + ZIP + 1.3, flatClosed]] : flatFirst), p => (inStretch(p) ? 0 : zip(p)));
    form = 'ladder';
    const mrna = [];
    for (let i = from; i <= to; i++) {
      const letter = letters[i] === 'T' ? 'U' : letters[i];
      for (const kind of i < to ? ['base', 'seg'] : ['base']) {
        const target = ladderPose(kind, i, -baseHalf, 1);
        const part = make(kind, 'mrna', i, { c: v3add(target.c, [0, -2.2, 2.4]), d: target.d }, kind === 'base' ? letter : null, DNA_STRANDS.mrna);
        part.target = target;
        mrna.push(part);
      }
    }
    const away = p => ladderPose(p.kind, p.i, -baseHalf - 3.4, 1);
    play(mrna, () => [[t1 + 1.2, null], [t1 + 2.6, p => p.target], [formed + 1.6, null], [gone, away]], p => ((p.i - from) / Math.max(1, to - from)) * ZIP);
    return handle;
  }

  // A label on a part: a base pair's number (strand one's base), a letter (its first such base), 'backbone', 'new'
  // (a new strand, after copy) or 'mrna'.
  const find = which => {
    if (Number.isInteger(which)) return parts.find(p => p.kind === 'base' && p.s === 1 && p.i === which);
    const key = String(which).trim();
    if (/^backbone$/i.test(key)) return parts.find(p => p.kind === 'seg' && p.s === 1 && p.i === Math.floor((n - 1) / 2));
    if (/^new$/i.test(key)) return parts.find(p => p.kind === 'seg' && p.s === 'newOne' && p.i === Math.floor((n - 1) / 2));
    if (/^mrna$/i.test(key)) return parts.find(p => p.kind === 'seg' && p.s === 'mrna') || parts.find(p => p.s === 'mrna');
    const letter = key.toUpperCase();
    return parts.find(p => p.kind === 'base' && p.letter === letter && (p.s === 1 || p.s === 'mrna')) || parts.find(p => p.kind === 'base' && p.letter === letter);
  };
  const label = (which, words, o2) => { const p = find(which); return p ? p.handle.label(String(words), o2) : (warn('dna label: nothing called ' + JSON.stringify(which)), null); };
  // By default each base's name is tagged once, and the backbone.
  if (opts.labels !== false) {
    for (const b of ['A', 'T', 'G', 'C']) { const p = find(b); if (p) p.handle.label(b + ' ' + DNA_BASE_NAMES[b], { color: DNA_BASE_COLORS[b] }); }
    label('backbone', 'backbone');
  }
  const handle = {
    figure: f, sequence: letters, complement: partner, pairs: n,
    // The mRNA a stretch (or the whole) makes: strand one's letters with U for T.
    mrna: (a, b) => letters.slice(finite(a) ? a : 0, finite(b) ? b + 1 : n).replace(/T/g, 'U'),
    unzip, copy, transcribe, label, view: o2 => f.view(o2),
  };
  return handle;
}
// What dna() hands back when it couldn't build one: calls on it do nothing.
const noDna = () => new Proxy({}, { get: (_, key) => key === 'figure' ? nothing() : key === 'part' || key === 'atom' ? () => nothing() : () => null });
api.dna = withFigure(dna);

// Blood circulation: circulation(x, y, w, h, {speed, labels}) draws the heart, the lungs and the body as a flat
// diagram in a 3D figure, the way a textbook does (the person's right on the viewer's left), with see-through vessels
// and blood cells that really go round: body → vena cava → right atrium → right ventricle → pulmonary artery →
// lungs → pulmonary veins → left atrium → left ventricle → aorta → body. A cell is blue (little oxygen) from the body's
// capillaries to the lungs' and red (full of oxygen) from the lungs' to the body's, changing colour in the
// capillaries. The atria squeeze, then the ventricles, every beat.
const CIRC = {
  oxygenPoor: 'tissue:#4A6FB5', oxygenRich: 'tissue:#C9302C', lung: 'tissue:#F2A7A7', body: 'tissue:#E8C4A0', capillary: 'tissue:#9B6FA8',
};
// The route, as named stretches of [x, y, z] (z: arteries in front, veins behind, so they cross cleanly).
const CIRC_HEART = { ra: [-1.0, 0.8, 0.35], rv: [-0.8, -0.75, 0.35], la: [1.0, 0.8, 0.35], lv: [0.85, -0.85, 0.35] };
const CIRC_ROUTE = {
  bodyCapillaries: [[2.2, -4.4, 0.2], [1.3, -4.95, 0.2], [0.4, -4.4, 0.2], [-0.5, -4.95, 0.2], [-1.4, -4.4, 0.2], [-2.2, -4.7, 0.2]],
  venaCava: [[-2.6, -3.7, 0.2], [-2.65, -1.6, 0.2], [-2.35, 0.4, 0.25], [-1.55, 0.85, 0.3]],
  rightHeart: [CIRC_HEART.ra, [-0.95, 0.05, 0.35], CIRC_HEART.rv],
  pulmonaryTrunk: [[-0.45, -0.45, 0.45], [-0.15, 0.6, 0.6], [0, 1.9, 0.55], [0, 2.6, 0.45]],
  arteryTo: { left: [[-1.2, 3.05, 0.35], [-2.3, 3.55, 0.25]], right: [[1.2, 3.05, 0.35], [2.3, 3.55, 0.25]] },
  lungCapillaries: {
    left: [[-2.8, 4.3, 0.2], [-3.55, 4.15, 0.2], [-3.7, 3.5, 0.2], [-3.1, 3.2, 0.2]],
    right: [[2.8, 4.3, 0.2], [3.55, 4.15, 0.2], [3.7, 3.5, 0.2], [3.1, 3.2, 0.2]],
  },
  veinFrom: { left: [[-2.4, 2.9, -0.3], [-1.0, 2.1, -0.35], [0.4, 1.35, -0.2]], right: [[2.4, 2.9, -0.3], [1.8, 2.0, -0.35], [1.2, 1.35, -0.2]] },
  leftHeart: [CIRC_HEART.la, [0.95, 0.0, 0.35], CIRC_HEART.lv],
  aorta: [[0.55, -0.3, 0.5], [0.35, 1.2, 0.65], [0.55, 2.85, 0.65], [1.55, 3.15, 0.55], [2.4, 2.3, 0.35], [2.65, 0.4, 0.2], [2.65, -2.0, 0.2], [2.6, -3.6, 0.2]],
};

// One whole lap through the given lung: its points, and where in it the cell is blue (between the two capillary beds).
function circulationLap(side) {
  const R = CIRC_ROUTE, stretches = [
    ['body', R.bodyCapillaries], ['poor', R.venaCava], ['poor', R.rightHeart], ['poor', R.pulmonaryTrunk], ['poor', R.arteryTo[side]],
    ['lung', R.lungCapillaries[side]], ['rich', R.veinFrom[side]], ['rich', R.leftHeart], ['rich', R.aorta],
  ];
  const pts = [], kinds = [];
  for (const [kind, list] of stretches) for (const p of list) { pts.push(p); kinds.push(kind); }
  return { pts, kinds };
}

function circulation(x, y, w, h, o) {
  const opts = options(o);
  if (![x, y, w, h].every(finite) || w < 60 || h < 60) { warn('circulation() needs x, y, width and height (at least 60 each); skipped'); return noCirculation(); }
  const bpm = finite(opts.beat) ? Math.max(30, Math.min(150, opts.beat)) : 60, beat = 60 / bpm;
  const lapSeconds = finite(opts.lap) ? Math.max(4, Math.min(40, opts.lap)) : 10; // a blood cell's lap (really about a minute)
  const f = api.figure(x, y, w, h, { tilt: finite(opts.tilt) ? opts.tilt : 6, turn: finite(opts.turn) ? opts.turn : -12, zoom: opts.zoom, ground: false });
  const handles = new Map();
  const keep = (name, handle) => { if (!handles.has(name)) handles.set(name, []); handles.get(name).push(handle); return handle; };
  const R = CIRC_ROUTE, H = CIRC_HEART;

  // The lungs and the body's organs, see-through so the blood shows inside.
  for (const side of [-1, 1]) {
    keep('lungs', f.blob({ balls: [[side * 3.15, 3.8, -0.2, 1.05, 1.25, 0.7], [side * 2.75, 3.1, -0.2, 0.75, 0.8, 0.6]], smooth: 0.5, mat: CIRC.lung, opacity: 0.45, quick: true }));
  }
  keep('body', f.box({ w: 5.6, h: 1.6, d: 1.2, round: 0.5, at: [0, -4.65, -0.1], mat: CIRC.body, opacity: 0.4, quick: true }));

  // The heart: four chambers, the person's right side (oxygen-poor, blue) on the viewer's left, and the wall between.
  const chamber = (name, at, size, mat) => keep(name, f.blob({ balls: [[0, 0, 0, size[0], size[1], size[2]]], at, mat, opacity: 0.6, quick: true }));
  const ra = chamber('right atrium', H.ra, [0.62, 0.55, 0.5], CIRC.oxygenPoor), rv = chamber('right ventricle', H.rv, [0.72, 0.85, 0.55], CIRC.oxygenPoor);
  const la = chamber('left atrium', H.la, [0.62, 0.55, 0.5], CIRC.oxygenRich), lv = chamber('left ventricle', H.lv, [0.8, 0.95, 0.6], CIRC.oxygenRich);
  keep('septum', f.box({ w: 0.12, h: 2.6, d: 0.9, round: 0.05, at: [0.05, 0.0, 0.35], mat: 'tissue:#B05A60', quick: true }));
  // Each beat the atria squeeze first, then the ventricles: sizes over one beat, as path frames that stay put.
  const squeeze = (handle, at, from, to) => handle.path({ frames: Array.from({ length: 24 }, (_, i) => {
    const u = i / 24, k = u >= from && u <= to ? Math.sin(Math.PI * (u - from) / (to - from)) : 0;
    return [at[0], at[1], at[2], 0, 0, 0, 1 - 0.13 * k];
  }), period: beat, sync: true });
  squeeze(ra, H.ra, 0, 0.18); squeeze(la, H.la, 0, 0.18); squeeze(rv, H.rv, 0.22, 0.55); squeeze(lv, H.lv, 0.22, 0.55);

  // The vessels, see-through, and the capillary beds as fine tubes.
  const vessel = (name, points, mat, r = 0.17) => keep(name, f.tube({ points, r, mat, opacity: 0.4, quick: true }));
  vessel('vena cava', [R.bodyCapillaries[R.bodyCapillaries.length - 1], ...R.venaCava, H.ra], CIRC.oxygenPoor);
  vessel('pulmonary artery', [H.rv, ...R.pulmonaryTrunk], CIRC.oxygenPoor);
  for (const side of ['left', 'right']) {
    vessel('pulmonary artery', [R.pulmonaryTrunk[R.pulmonaryTrunk.length - 1], ...R.arteryTo[side], R.lungCapillaries[side][0]], CIRC.oxygenPoor, 0.14);
    vessel('lung capillaries', R.lungCapillaries[side], CIRC.capillary, 0.07);
    vessel('pulmonary veins', [R.lungCapillaries[side][R.lungCapillaries[side].length - 1], ...R.veinFrom[side], H.la], CIRC.oxygenRich, 0.14);
  }
  vessel('aorta', [H.lv, ...R.aorta, R.bodyCapillaries[0]], CIRC.oxygenRich, 0.19);
  vessel('body capillaries', R.bodyCapillaries, CIRC.capillary, 0.07);

  // Blood cells: each a red and a blue disc on the same lap, one of them sized 0 where the cell is the other colour,
  // trading over a few frames in the capillaries. Cells take turns through the left and the right lung.
  const count = finite(opts.cells) ? Math.max(4, Math.min(24, Math.round(opts.cells))) : 12, FRAMES = 120;
  for (let c = 0; c < count; c++) {
    const lap = circulationLap(c % 2 ? 'right' : 'left');
    // Spread evenly along the lap by distance: the points' running lengths, then FRAMES samples.
    const cum = [0];
    for (let i = 1; i < lap.pts.length; i++) cum.push(cum[i - 1] + Math.hypot(...lap.pts[i].map((v, k) => v - lap.pts[i - 1][k])));
    const L = cum[cum.length - 1] + Math.hypot(...lap.pts[0].map((v, k) => v - lap.pts[lap.pts.length - 1][k]));
    const sample = s => {
      const t = ((s % L) + L) % L;
      let i = 0;
      while (i < cum.length - 1 && cum[i + 1] < t) i++;
      const a = lap.pts[i], b = lap.pts[(i + 1) % lap.pts.length], seg = (i + 1 < cum.length ? cum[i + 1] : L) - cum[i];
      const k = seg > 0 ? (t - cum[i]) / seg : 0;
      // How blue it is: 1 on the oxygen-poor stretches, 0 on the rich ones, easing across the capillary beds.
      const blueAt = j => ({ poor: 1, rich: 0 }[lap.kinds[j]] ?? null);
      let blue = blueAt(i);
      if (blue === null) { // in a capillary bed: from what it was to what it becomes, across the bed
        const bed = lap.kinds[i];
        const first = lap.kinds.indexOf(bed), last = lap.kinds.lastIndexOf(bed);
        const along = Math.min(1, Math.max(0, (t - cum[first]) / Math.max(1e-6, cum[last] - cum[first])));
        blue = bed === 'lung' ? 1 - along : along;
      }
      return { at: a.map((v, j) => v + (b[j] - v) * k), blue };
    };
    const start = (c / count) * L;
    for (const colour of ['blue', 'red']) {
      const frames = Array.from({ length: FRAMES }, (_, i) => {
        const { at, blue } = sample(start + (i / FRAMES) * L);
        const size = colour === 'blue' ? blue : 1 - blue;
        return [at[0], at[1], at[2] + 0.02, Math.PI / 2, 0, 0, Math.max(0, Math.min(1, size))];
      });
      keep(colour === 'blue' ? 'oxygen-poor blood' : 'oxygen-rich blood',
        f.torus({ r: 0.1, tube: 0.06, at: frames[0].slice(0, 3), mat: colour === 'blue' ? 'tissue:#3D5FA8' : 'tissue:#D7262B', quick: true }))
        .path({ frames, period: lapSeconds, sync: true });
    }
  }

  const NAMES = ['right atrium', 'right ventricle', 'left atrium', 'left ventricle', 'lungs', 'body', 'aorta', 'vena cava', 'pulmonary artery', 'pulmonary veins'];
  const nameOf = which => {
    const k = String(which).trim().toLowerCase().replace(/s$/, '');
    return [...handles.keys()].find(n => n.toLowerCase().replace(/s$/, '') === k) || null;
  };
  // A vessel's name points at a spot on it (its box's middle is empty space for a curve); the rest at their middles.
  const ON = { 'vena cava': R.venaCava[1], 'pulmonary artery': R.pulmonaryTrunk[2], 'pulmonary veins': R.veinFrom.left[1],
    aorta: R.aorta[6], lungs: [-3.15, 3.8, -0.2] };
  const tag = (name, words, o2) => handles.get(name)[0].label(words, Object.assign(ON[name] ? { at: ON[name] } : {}, options(o2)));
  if (opts.labels !== false) for (const name of NAMES) if (handles.has(name)) tag(name, name);
  return {
    figure: f,
    label: (which, words, o2) => (nameOf(which) ? tag(nameOf(which), String(words), o2) : (warn('circulation label: no ' + which), null)),
    // Points a part out while it is talked about: it swells gently (the chambers already beat, so a vessel or organ).
    point: which => { const n = nameOf(which); if (!n) { warn('circulation point: no ' + which); return null; } handles.get(n).forEach(p => p.pulse({ amount: 0.06, period: 1.2 })); return handles.get(n)[0]; },
    part: which => (nameOf(which) ? partGroup(handles.get(nameOf(which))) : noPart('circulation', which)),
    parts: NAMES.filter(n => handles.has(n)),
    view: o2 => f.view(o2),
  };
}
// What circulation() hands back when it couldn't draw: calls on it do nothing.
const noCirculation = () => new Proxy({}, { get: (_, key) => key === 'figure' ? nothing() : key === 'part' || key === 'atom' ? () => nothing() : () => null });
api.circulation = withFigure(circulation);

// Cell division: mitosis(x, y, w, h, {pairs, labels}) builds an animal cell about to divide, in a 3D figure, and
// m.next() / m.to(phase) play what happens, phase by phase: interphase (the DNA loose in the nucleus) → prophase (each
// chromosome has been copied and coils up into two sister chromatids joined at a centromere, the nucleus breaks down,
// the centrosomes go to the poles) → metaphase (the chromosomes line up across the middle, spindle fibres from both
// poles holding each one) → anaphase (the sisters are pulled apart, one to each pole, and the cell stretches) →
// telophase (a new nucleus forms round each set, the chromosomes uncoil, the cell starts to pinch) → cytokinesis (it
// pinches in two: two cells, each with the same chromosomes as the first). Chromosomes come in pairs, one from each
// parent (warm and cool colours), each pair its own length. Every part is made at the start, the ones that come later
// hidden; a call plans every moving part's whole move and sends it as one 'paths' mark, so they all start together.
const MITOSIS_PHASES = ['interphase', 'prophase', 'metaphase', 'anaphase', 'telophase', 'cytokinesis'];
const MITOSIS_LOOK = {
  membrane: 'tissue:#F2B8B0', nucleus: 'tissue:#8E6CC4', nucleolus: 'tissue:#5B3F99', centrosome: 'tissue:#2E9E6B',
  spindle: 'tissue:#8A9AA8', centromere: 'tissue:#33333F',
};
const MITOSIS_MUM = ['#D64545', '#E07B39', '#C2185B', '#C9A227'], MITOSIS_DAD = ['#3366CC', '#2E9BB5', '#6A4FC4', '#2E8B57'];
const MITOSIS_LENGTHS = [1.0, 0.82, 0.66, 0.52];
// The cell's outline from round to two: two balls a apart each side of the middle, radius r, blended by k (a flip-book:
// one is shown at a time, the next taking over).
const MITOSIS_SHAPES = [
  { a: 0, r: 2.6, k: 0.5 }, { a: 0.4, r: 2.45, k: 1.2 }, { a: 0.75, r: 2.35, k: 1.2 }, { a: 1.05, r: 2.25, k: 1.1 },
  { a: 1.35, r: 2.1, k: 0.8 }, { a: 1.6, r: 2.0, k: 0.6 }, { a: 1.95, r: 1.95, k: 0.45 }, { a: 2.25, r: 1.92, k: 0.3 },
  { a: 2.6, r: 1.9, k: 0.15 },
];
// The i-th point of a Halton sequence in base b: spread evenly, the same every time.
function mitosisSpread(i, b) { let f = 1, r = 0; for (let n = i; n > 0; n = Math.floor(n / b)) { f /= b; r += f * (n % b); } return r; }

function mitosis(x, y, w, h, o) {
  const opts = options(o);
  if (![x, y, w, h].every(finite) || w < 60 || h < 60) { warn('mitosis() needs x, y, width and height (at least 60 each); skipped'); return noMitosis(); }
  const pairs = finite(opts.pairs) ? Math.max(1, Math.min(4, Math.round(opts.pairs))) : 2, n = pairs * 2;
  const turn = finite(opts.turn) ? opts.turn : -22, tilt = finite(opts.tilt) ? opts.tilt : 14;
  const f = api.figure(x, y, w, h, { tilt, turn, zoom: opts.zoom, ground: false });
  const handles = new Map();
  const keep = (name, item) => { if (!handles.has(name)) handles.set(name, []); handles.get(name).push(item); return item; };
  const deg = r => r.map(v => v * 180 / Math.PI);
  // A thing that moves: its part and where it is now ({c: centre, r: Euler radians, s: size, 0 hidden}).
  const thing = (name, kind, params, pose) => keep(name, {
    handle: f[kind](Object.assign({}, params, { at: pose.c, rot: deg(pose.r), quick: true, hidden: pose.s === 0 || undefined })), pose });
  const P = (c, r = [0, 0, 0], s = 1) => ({ c, r, s });
  const POLE = 2.2;

  // The outline, as a flip-book of shapes; the first is shown.
  const shapes = MITOSIS_SHAPES.map((sh, i) => thing('cell membrane', 'blob', {
    balls: sh.a ? [[-sh.a, 0, 0, sh.r], [sh.a, 0, 0, sh.r]] : [[0, 0, 0, sh.r]], smooth: sh.k, mat: MITOSIS_LOOK.membrane, opacity: 0.28,
  }, P([0, 0, 0], [0, 0, 0], i ? 0 : 1)));
  let shown = 0;

  // The nucleus, and the two that form at the end (hidden till telophase).
  const nucleus = thing('nucleus', 'sphere', { r: 1.3, mat: MITOSIS_LOOK.nucleus, opacity: 0.4 }, P([0, 0, 0]));
  const nucleolus = thing('nucleolus', 'sphere', { r: 0.32, mat: MITOSIS_LOOK.nucleolus }, P([0.45, 0.35, -0.55]));
  const daughters = [-1, 1].map(side => ({
    side,
    nucleus: thing('new nuclei', 'sphere', { r: 0.95, mat: MITOSIS_LOOK.nucleus, opacity: 0.4 }, P([side * 1.9, 0, 0], [0, 0, 0], 0)),
    nucleolus: thing('nucleolus', 'sphere', { r: 0.24, mat: MITOSIS_LOOK.nucleolus }, P([side * 1.9 + 0.3, 0.3, -0.45], [0, 0, 0], 0)),
  }));

  // Two centrosomes (each two centrioles at right angles), side by side above the nucleus until prophase.
  const centrosomes = [-1, 1].map((side, k) => {
    const at = [k ? 0.6 : 0.05, 1.8, -0.4];
    return { side, at, parts: [
      thing('centrosomes', 'cyl', { r: 0.07, h: 0.3, mat: MITOSIS_LOOK.centrosome }, P(at)),
      thing('centrosomes', 'cyl', { r: 0.07, h: 0.3, mat: MITOSIS_LOOK.centrosome }, P(v3add(at, [0.14, 0.12, 0]), [0, 0, Math.PI / 2])),
    ] };
  });
  const OFFSETS = [[0, 0, 0], [0.14, 0.12, 0]];

  // Chromosomes: pair q, from mum (h 0) or dad (h 1). Each is two sister chromatids (b hidden until it's copied in
  // prophase) and a centromere bead. Where each sits in each phase is worked out here, once.
  const ring = Math.max(0.85, (2 * MITOSIS_LENGTHS.slice(0, pairs).reduce((s, l) => s + l, 0)) / (2 * Math.PI) * 1.25);
  // The metaphase plate: where each chromosome lies across the middle (x = 0), its centre (y, z) and its tilt, clear of
  // the others both in the plate and as the figure is first seen (the plate nearly edge on), so none hides another.
  // Kept as far apart as there is room for. The view as figure3d.js frames it: toward the camera, then right and up.
  const [tu, ti] = [turn * Math.PI / 180, tilt * Math.PI / 180];
  const eye = [Math.sin(tu) * Math.cos(ti), Math.sin(ti), Math.cos(tu) * Math.cos(ti)];
  const right = v3unit([eye[2], 0, -eye[0]]), up = [eye[1] * right[2] - eye[2] * right[1], eye[2] * right[0] - eye[0] * right[2], eye[0] * right[1] - eye[1] * right[0]];
  const seenAt = (yy, zz) => [right[1] * yy + right[2] * zz, up[1] * yy + up[2] * zz];
  const plateFor = gap => {
    const spots = [];
    for (let j = 0; j < n; j++) {
      const L = MITOSIS_LENGTHS[Math.floor(j / 2)];
      let spot = null;
      for (let i = 1; i < 3000 && !spot; i++) {
        const yy = (mitosisSpread(i + 13 * j, 2) - 0.5) * 3.8, zz = (mitosisSpread(i + 13 * j, 3) - 0.5) * 3.0, tilt = (mitosisSpread(i + 13 * j, 5) - 0.5) * 1.0;
        const ends = [[yy - (Math.cos(tilt) * L) / 2, zz - (Math.sin(tilt) * L) / 2], [yy + (Math.cos(tilt) * L) / 2, zz + (Math.sin(tilt) * L) / 2]];
        if (ends.some(([a, b]) => Math.hypot(a, b) > 2.15)) continue;
        const shown = ends.map(([a, b]) => seenAt(a, b));
        if (spots.every(s => segmentGap(ends, s.ends) > 0.36 && segmentGap(shown, s.shown) > gap)) spot = { y: yy, z: zz, tilt, ends, shown };
      }
      if (!spot) return null;
      spots.push(spot);
    }
    return spots;
  };
  const plate = plateFor(0.8) || plateFor(0.65) || plateFor(0.5) || plateFor(0.38) || plateFor(0.26) || plateFor(0)
    || Array.from({ length: n }, (_, j) => ({ y: (j - (n - 1) / 2) * 0.45, z: 0, tilt: 0 }));
  const chromosomes = [];
  for (let j = 0; j < n; j++) {
    const q = Math.floor(j / 2), L = MITOSIS_LENGTHS[q], color = (j % 2 ? MITOSIS_DAD : MITOSIS_MUM)[q];
    // Interphase: loose and thin, anywhere in the nucleus.
    const loose = [(mitosisSpread(j + 1, 2) - 0.5) * 1.1, (mitosisSpread(j + 1, 3) - 0.5) * 1.1, (mitosisSpread(j + 1, 5) - 0.5) * 0.7];
    const looseTurn = [mitosisSpread(j + 3, 3) * Math.PI, 0, mitosisSpread(j + 3, 2) * Math.PI * 2];
    // Prophase: coiled up round a ring where the nucleus was, lying along it.
    const phi = (2 * Math.PI * j) / n + 0.3, theta = phi + Math.PI / 2 + (mitosisSpread(j + 5, 2) - 0.5) * 0.6;
    const pro = { c: [Math.cos(phi) * ring, Math.sin(phi) * ring, j % 2 ? 0.25 : -0.25], r: [0, 0, theta], side: [Math.cos(theta), Math.sin(theta), 0] };
    const spot = plate[j];
    const chromatid = (s, pose) => thing('chromosomes', 'capsule', { r: 0.12, h: L, mat: 'tissue:' + color }, pose);
    const a = chromatid(1, P(loose, looseTurn, 0.5)), b = chromatid(2, P(loose, looseTurn, 0));
    const bead = thing('centromeres', 'sphere', { r: 0.14, mat: MITOSIS_LOOK.centromere }, P(pro.c, [0, 0, 0], 0));
    chromosomes.push({ j, q, L, a, b, bead, pro, meta: { c: [0, spot.y, spot.z], r: [spot.tilt, 0, 0] } });
  }
  // How far apart two flat segments are (0 when they cross).
  function segmentGap(s1, s2) {
    const pd = (p, a, b) => { const d = [b[0] - a[0], b[1] - a[1]], l = d[0] ** 2 + d[1] ** 2 || 1;
      const t = Math.max(0, Math.min(1, ((p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1]) / l)); return Math.hypot(p[0] - a[0] - t * d[0], p[1] - a[1] - t * d[1]); };
    const cross = (a, b, c) => (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
    const [p1, p2] = s1, [p3, p4] = s2;
    if (cross(p1, p2, p3) * cross(p1, p2, p4) < 0 && cross(p3, p4, p1) * cross(p3, p4, p2) < 0) return 0;
    return Math.min(pd(p1, p3, p4), pd(p2, p3, p4), pd(p3, p1, p2), pd(p4, p1, p2));
  }

  // Where a chromatid's spindle fibre holds it: its middle while it lies across, its leading end once it is pulled
  // round to point at its pole (its pose's turn about z: 0 across, ±π/2 along the spindle).
  const along = r => [-Math.sin(r[2]), Math.cos(r[2]) * Math.cos(r[0]), Math.cos(r[2]) * Math.sin(r[0])];
  const holdPoint = (pose, L) => v3add(pose.c, v3mul(along(pose.r), (L / 2) * Math.abs(Math.sin(pose.r[2]))));
  const poleOf = side => [side * POLE, 0, 0];
  // Fibres: one to each chromatid from its pole (a to the left, b to the right), and a few from pole to pole.
  const sisterAt = (ch, side, pose) => v3add(pose.c, v3mul(side < 0 ? [-1, 0, 0] : [1, 0, 0], 0.12));
  const fibres = [];
  for (const ch of chromosomes) {
    for (const side of [-1, 1]) {
      const metaHold = sisterAt(ch, side, ch.meta), proHold = v3add(ch.pro.c, v3mul(ch.pro.side, side * 0.12));
      const longest = Math.max(v3len(v3sub(metaHold, poleOf(side))), v3len(v3sub(proHold, poleOf(side))));
      fibres.push({ ch, side, longest, item: thing('spindle fibres', 'cyl', { r: 0.035, h: longest, mat: MITOSIS_LOOK.spindle }, P(poleOf(side), [0, 0, 0], 0)) });
    }
  }
  const polar = [];
  for (let k = 0; k < 4; k++) {
    const side = k % 2 ? 1 : -1, end = [-side * 0.4, (k < 2 ? 1 : -1) * 0.9, (k % 3 ? 0.5 : -0.5)];
    const d = v3sub(end, poleOf(side)), longest = v3len(d);
    polar.push({ side, end, longest, item: thing('spindle fibres', 'cyl', { r: 0.03, h: longest, mat: MITOSIS_LOOK.spindle }, P(poleOf(side), [0, 0, 0], 0)) });
  }
  const fibrePose = (from, to, longest, reach = 1) => {
    const d = v3sub(to, from), len = v3len(d) * reach;
    return { c: v3add(from, v3mul(v3unit(d), len / 2)), r: eulerFromY(d), s: len > 1e-3 ? len / longest : 0 };
  };

  // ---- planning a call's moves: keys [[seconds from the call's start, pose], …] per thing, eased between.
  const ease = k => k * k * (3 - 2 * k);
  const turnTo = (p, q, k) => p + Math.atan2(Math.sin(q - p), Math.cos(q - p)) * k;
  const mix = (a, b, k) => ({ c: v3lerp(a.c, b.c, k), r: a.r.map((v, i) => turnTo(v, b.r[i], k)), s: a.s + (b.s - a.s) * k });
  function poseAt(keys, t) {
    if (t <= keys[0][0]) return keys[0][1];
    for (let j = 0; j < keys.length - 1; j++) {
      const [t0, a] = keys[j], [t1, b] = keys[j + 1];
      if (t <= t1) return mix(a, b, ease(Math.min(1, Math.max(0, (t - t0) / Math.max(1e-6, t1 - t0)))));
    }
    return keys[keys.length - 1][1];
  }
  let plan = null, clock = 0;
  const keysOf = item => { if (!plan.has(item)) plan.set(item, [[0, item.pose]]); return plan.get(item); };
  // Arrive at a pose (any of c, r, s; the rest as it was) by t seconds into this phase.
  const to = (item, t, pose = {}) => {
    const keys = keysOf(item), last = keys[keys.length - 1][1];
    keys.push([clock + t, { c: pose.c || last.c, r: pose.r || last.r, s: pose.s === undefined ? last.s : pose.s }]);
  };
  const now = (item, t) => (plan.has(item) ? poseAt(plan.get(item), clock + t) : item.pose);
  // Flip the outline to shape i at t.
  const outline = (i, t) => { to(shapes[shown], t); to(shapes[shown], t + 0.06, { s: 0 }); to(shapes[i], t, { s: 0 }); to(shapes[i], t + 0.06, { s: 1 }); shown = i; };
  // Fibres follow the chromatids they hold, sampled every tenth of a second: reach grows 0 → 1 over [from, till].
  const follow = (seconds, reach = () => 1) => {
    for (let t = 0; t <= seconds + 1e-6; t += 0.1) {
      for (const fb of fibres) {
        const chromatid = fb.side < 0 ? fb.ch.a : fb.ch.b;
        to(fb.item, t, fibrePose(poleOf(fb.side), holdPoint(now(chromatid, t), fb.ch.L), fb.longest, reach(t)));
      }
    }
  };

  const PHASES = {
    prophase() {
      for (const ch of chromosomes) {
        const at = ch.a.pose; // the copy appears on the original, then they part a little as they coil up
        to(ch.b, 0.3, { c: at.c, r: at.r, s: 0 }); to(ch.b, 0.9, { s: 0.5 });
        to(ch.a, 0.9);
        to(ch.a, 2.2, { c: v3add(ch.pro.c, v3mul(ch.pro.side, -0.12)), r: ch.pro.r, s: 1 });
        to(ch.b, 2.2, { c: v3add(ch.pro.c, v3mul(ch.pro.side, 0.12)), r: ch.pro.r, s: 1 });
        to(ch.bead, 1.7, { c: ch.pro.c, s: 0 }); to(ch.bead, 2.3, { s: 1 });
      }
      to(nucleus, 1.4); to(nucleus, 2.7, { s: 0 });
      to(nucleolus, 1.0); to(nucleolus, 2.0, { s: 0 });
      for (const cs of centrosomes) cs.parts.forEach((part, k) => { to(part, 0.8); to(part, 3.0, { c: v3add(poleOf(cs.side), OFFSETS[k]) }); });
      return 3.1;
    },
    metaphase() {
      for (const ch of chromosomes) {
        for (const [part, side] of [[ch.a, -1], [ch.b, 1]]) { to(part, 0.5); to(part, 2.6, { c: sisterAt(ch, side, ch.meta), r: ch.meta.r }); }
        to(ch.bead, 0.5); to(ch.bead, 2.6, { c: ch.meta.c });
      }
      follow(3.0, t => ease(Math.min(1, Math.max(0, (t - 0.1) / 1.0))));
      for (const p of polar) { to(p.item, 0.3, fibrePose(poleOf(p.side), p.end, p.longest, 0)); to(p.item, 1.4, fibrePose(poleOf(p.side), p.end, p.longest)); }
      return 3.0;
    },
    anaphase() {
      for (const ch of chromosomes) {
        for (const [part, side] of [[ch.a, -1], [ch.b, 1]]) {
          const tilt = ch.meta.r[0];
          to(part, 0.3);
          to(part, 2.6, { c: [side * (1.5 - ch.L / 2), ch.meta.c[1] * 0.7, ch.meta.c[2] * 0.7], r: [tilt, 0, side < 0 ? Math.PI / 2 : -Math.PI / 2] });
        }
        to(ch.bead, 0.2); to(ch.bead, 0.5, { s: 0 });
      }
      follow(2.8);
      outline(1, 0.9); outline(2, 1.6); outline(3, 2.3);
      return 2.8;
    },
    telophase() {
      // The fibres let go and shrink back into their poles.
      for (let t = 0; t <= 0.8 + 1e-6; t += 0.1) {
        const reach = 1 - ease(t / 0.8);
        for (const fb of fibres) to(fb.item, t, fibrePose(poleOf(fb.side), holdPoint(now(fb.side < 0 ? fb.ch.a : fb.ch.b, 0), fb.ch.L), fb.longest, reach));
        for (const p of polar) to(p.item, t, fibrePose(poleOf(p.side), p.end, p.longest, reach));
      }
      for (const ch of chromosomes) {
        for (const [part, side, k] of [[ch.a, -1, ch.j * 2], [ch.b, 1, ch.j * 2 + 1]]) {
          const off = [(mitosisSpread(k + 7, 2) - 0.5) * 0.7, (mitosisSpread(k + 7, 3) - 0.5) * 0.8, (mitosisSpread(k + 7, 5) - 0.5) * 0.6];
          to(part, 0.4);
          to(part, 2.4, { c: v3add([side * 1.9, 0, 0], off), r: [mitosisSpread(k + 2, 3) * Math.PI, 0, mitosisSpread(k + 2, 2) * Math.PI * 2], s: 0.5 });
        }
      }
      for (const d of daughters) {
        to(d.nucleus, 1.1); to(d.nucleus, 2.6, { s: 1 });
        to(d.nucleolus, 1.6); to(d.nucleolus, 2.8, { s: 1 });
      }
      for (const cs of centrosomes) cs.parts.forEach((part, k) => { to(part, 0.6); to(part, 2.6, { c: v3add([cs.side * 1.9, 1.3, -0.3], OFFSETS[k]) }); });
      outline(4, 1.2); outline(5, 2.4);
      return 3.0;
    },
    cytokinesis() {
      // The two halves move apart as the middle pinches shut.
      const shift = (item, side) => { to(item, 0.3); to(item, 2.5, { c: v3add(keysOf(item)[keysOf(item).length - 1][1].c, [side * 1.0, 0, 0]) }); };
      for (const ch of chromosomes) { shift(ch.a, -1); shift(ch.b, 1); }
      for (const d of daughters) { shift(d.nucleus, d.side); shift(d.nucleolus, d.side); }
      for (const cs of centrosomes) cs.parts.forEach(part => shift(part, cs.side));
      outline(6, 0.7); outline(7, 1.4); outline(8, 2.1);
      return 2.8;
    },
  };

  // Labels: each the first time its part is there; a label goes when its part does (the nucleus in prophase).
  const tagged = new Set();
  const NAMES = { membrane: 'cell membrane', cell: 'cell membrane', 'cell membrane': 'cell membrane', nucleus: 'nucleus', nuclei: 'new nuclei',
    'new nucleus': 'new nuclei', 'new nuclei': 'new nuclei', chromosome: 'chromosomes', chromosomes: 'chromosomes', chromatid: 'chromosomes',
    chromatids: 'chromosomes', 'sister chromatids': 'chromosomes', dna: 'chromosomes', centromere: 'centromeres', centromeres: 'centromeres',
    centrosome: 'centrosomes', centrosomes: 'centrosomes', centriole: 'centrosomes', centrioles: 'centrosomes', spindle: 'spindle fibres',
    'spindle fibres': 'spindle fibres', 'spindle fibers': 'spindle fibres', fibres: 'spindle fibres', fibers: 'spindle fibres', nucleolus: 'nucleolus' };
  const nameOf = which => NAMES[String(which).trim().toLowerCase()] || null;
  const tagOn = (item, words, o2) => item.handle.label(String(words), options(o2));
  const autoTag = (key, item, words, at) => {
    if (opts.labels === false || tagged.has(key)) return;
    tagged.add(key);
    tagOn(item, words, at ? { at } : {});
  };
  const firstOf = name => (handles.get(name) || [])[0];
  autoTag('membrane', shapes[0], 'cell membrane', [1.7, -1.9, 0.4]);
  autoTag('nucleus', nucleus, 'nucleus', [0.9, -0.9, 0.2]);
  autoTag('dna', chromosomes[0].a, 'DNA (chromosomes)');
  autoTag('centrosomes', centrosomes[0].parts[0], 'centrosomes');
  const PHASE_TAGS = {
    // On the beads, so they go when the copies part in anaphase.
    prophase: () => { autoTag('sisters', chromosomes[0].bead, 'two copies, joined'); autoTag('centromere', chromosomes[n - 1].bead, 'centromere'); },
    metaphase: () => autoTag('spindle', fibres[fibres.length - 1].item, 'spindle fibres'),
    telophase: () => autoTag('new nuclei', daughters[1].nucleus, 'new nucleus', [0.6, -0.7, 0.2]),
    cytokinesis: () => autoTag('two cells', shapes[8], 'two new cells', [3.9, -1.3, 0.3]),
  };

  let phase = 'interphase';
  // Plays every phase after the current one up to `target`, back to back, as one mark.
  function playTo(target) {
    const goal = MITOSIS_PHASES.indexOf(target);
    let at = MITOSIS_PHASES.indexOf(phase);
    if (goal <= at) { warn(goal < 0 ? 'mitosis: no phase called ' + target + ' (' + MITOSIS_PHASES.join(', ') + ')' : 'mitosis: already at ' + phase); return handle; }
    plan = new Map();
    clock = 0;
    const tags = [];
    while (at < goal) {
      at++;
      clock += PHASES[MITOSIS_PHASES[at]]();
      if (PHASE_TAGS[MITOSIS_PHASES[at]]) tags.push(PHASE_TAGS[MITOSIS_PHASES[at]]);
    }
    const total = clock + 0.05, FRAMES = Math.max(24, Math.min(240, Math.round(total * 24)));
    const round = v => Math.round(v * 1000) / 1000;
    const list = [];
    for (const [item, keys] of plan) {
      const frames = Array.from({ length: FRAMES }, (_, k) => { const p = poseAt(keys, (k / (FRAMES - 1)) * total); return [...p.c, ...p.r, p.s].map(round); });
      list.push({ part: item.handle.id, frames });
      item.pose = keys[keys.length - 1][1];
    }
    push({ do: 'motion3d', fig: f.id, kind: 'paths', params: { list, period: total, once: true } });
    plan = null;
    phase = MITOSIS_PHASES[goal];
    handle.phase = phase;
    tags.forEach(tag => tag());
    return handle;
  }
  const handle = {
    figure: f, phase, phases: MITOSIS_PHASES.slice(), pairs,
    next: () => (phase === 'cytokinesis' ? (warn('mitosis: it has already divided'), handle) : playTo(MITOSIS_PHASES[MITOSIS_PHASES.indexOf(phase) + 1])),
    to: target => playTo(String(target).trim().toLowerCase()),
    label: (which, words, o2) => { const name = nameOf(which); return name && firstOf(name) ? tagOn(firstOf(name), words, o2) : (warn('mitosis label: no ' + which), null); },
    // Points a part out while it is talked about: every piece of it swells gently.
    point: which => { const name = nameOf(which); if (!name || !handles.has(name)) { warn('mitosis point: no ' + which); return null; } handles.get(name).forEach(t => t.handle.pulse({ amount: 0.1, period: 1.2 })); return handle; },
    part: which => { const name = nameOf(which); return name && handles.has(name) ? partGroup(handles.get(name).map(item => item.handle)) : noPart('mitosis', which); },
    parts: [...new Set(Object.values(NAMES))],
    view: o2 => f.view(o2),
  };
  return handle;
}
// What mitosis() hands back when it couldn't draw: calls on it do nothing.
const noMitosis = () => new Proxy({}, { get: (_, key) => key === 'figure' ? nothing() : key === 'part' || key === 'atom' ? () => nothing() : () => null });
api.mitosis = withFigure(mitosis);

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
    part: which => (find(which) ? partGroup(find(which)) : noPart('cell', which)),
    view: o2 => f.view(o2),
  };
}
// What cell() hands back when it couldn't build one: calls on it do nothing.
const noCell = () => new Proxy({}, { get: (_, key) => key === 'figure' ? nothing() : key === 'part' || key === 'atom' ? () => nothing() : () => null });
api.cell = withFigure(cell);

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
