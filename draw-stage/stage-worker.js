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
//   chart(x, y, w, h, {x, y, grid})         axes with numbered ticks ({from, to, step, label, format} or {labels}) → g.line/
//                                           area/bars/dot/label/hline/vline(…) placed by value, g.at(x, y)
//   timeline(x, y, w, {from, to, step})     a line of years → t.event(when, words), t.span(from, to, words), t.at(when)
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
  sqrt: Math.sqrt, pow: Math.pow, exp: Math.exp, log: Math.log, round: Math.round, floor: Math.floor, ceil: Math.ceil, hypot: Math.hypot,
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
