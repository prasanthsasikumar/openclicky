// OpenClicky drawing stage: the pen's arithmetic.
//
// Everything here is a pure function of its arguments (no page, no clock, no sound), so it is unit-tested with node
// (draw-stage/tests/stage-pen.test.mjs) and used by the page (stage.js) as window.StagePen:
//
//   timing   how long the pen takes over a stroke of a given length, when the next mark may start (marks overlap),
//            how long the pen's cursor flies to a mark, and the plan that makes a spoken line's marks end with it;
//   strokes  a hand's centreline through a mark's points (seeded, so a mark is drawn the same every time), and the
//            filled, tapered outline round it that the page reveals along its length;
//   sound    how loud and how bright the pen's sound is at each moment of a stroke, from the pen's speed.
'use strict';

(function (root) {
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
  const distance = (a, b) => Math.hypot(b[0] - a[0], b[1] - a[1]);

  // ------------------------------------------------------------------ timing
  // The pen: a quarter of a second to set off, then 1400 canvas units a second, never under 0.3 s or over 1.4 s.
  const PEN = { base: 0.25, unitsPerSecond: 1400, shortest: 0.3, longest: 1.4, overlap: 0.7, head: 0.16 };
  const PEN_EASE = [0.35, 0.2, 0.55, 0.95];
  const PEN_EASE_CSS = `cubic-bezier(${PEN_EASE.join(',')})`;

  const penSeconds = length => clamp(PEN.base + Math.max(0, length || 0) / PEN.unitsPerSecond, PEN.shortest, PEN.longest);

  // A CSS cubic-bezier as a function of time (0 to 1), so the cursor and the sound keep to the curve the ink is on.
  function bezierEase(x1, y1, x2, y2) {
    const cx = 3 * x1, bx = 3 * (x2 - x1) - cx, ax = 1 - cx - bx;
    const cy = 3 * y1, by = 3 * (y2 - y1) - cy, ay = 1 - cy - by;
    const xAt = s => ((ax * s + bx) * s + cx) * s, yAt = s => ((ay * s + by) * s + cy) * s;
    return t => {
      if (!(t > 0)) return 0;
      if (t >= 1) return 1;
      let lo = 0, hi = 1, s = t;
      for (let i = 0; i < 30; i++) {
        const x = xAt(s);
        if (Math.abs(x - t) < 1e-7) break;
        if (x < t) lo = s; else hi = s;
        s = (lo + hi) / 2;
      }
      return yAt(s);
    };
  }
  const penEase = bezierEase(...PEN_EASE);

  const polylineLength = pts => pts.reduce((sum, p, i) => i ? sum + distance(pts[i - 1], p) : 0, 0);

  // How far the pen travels over a mark, in canvas units (0 for a mark the pen does not draw).
  function markLength(mark) {
    switch (mark.do) {
      case 'line': case 'arrow': return polylineLength(mark.pts || []);
      case 'poly': return mark.pts && mark.pts.length ? polylineLength(mark.pts.concat([mark.pts[0]])) : 0;
      case 'ellipse': return Math.PI * (Math.abs(mark.rx) + Math.abs(mark.ry)) * 1.06;
      case 'rect': return 2 * (Math.abs(mark.w) + Math.abs(mark.h));
      default: return 0;
    }
  }

  // A mark at its natural speed: `seconds` to draw it, `advance` until the next mark may start (a pen stroke lets the
  // next one start when it is 0.7 of the way through), and `pen` when the pen's cursor draws it.
  function naturalTiming(mark) {
    const stroke = seconds => ({ seconds, advance: seconds * PEN.overlap, pen: true });
    const whole = seconds => ({ seconds, advance: seconds + 0.04, pen: false });
    switch (mark.do) {
      case 'line': case 'poly': case 'ellipse': case 'rect': return stroke(penSeconds(markLength(mark)));
      case 'arrow': {
        // The shaft, then the head's two flicks.
        const shaft = penSeconds(markLength(mark));
        return { seconds: shaft + PEN.head, advance: shaft + 0.1, pen: true };
      }
      case 'text': return stroke(clamp(String(mark.text).length * 0.045, 0.3, 1.6));
      case 'highlight': return stroke(0.45);
      case 'dot': return { seconds: 0.2, advance: 0.1, pen: false };
      case 'card': return whole(0.4);
      case 'photo': return { seconds: 0.5, advance: 0.3, pen: false };
      case 'figure': return whole(0.5);
      // quick: one of many small parts (a DNA's nucleotides)
      case 'part3d': return whole(mark.params && mark.params.hidden ? 0.02 : mark.params && mark.params.quick ? 0.1 : 0.6);
      case 'label3d': return whole(0.45);
      case 'view3d': return whole(0.5);
      case 'motion3d': return whole(0.05);
      case 'change3d': case 'tempo3d': case 'labels3d': return whole(0.15);
      case 'change2d': return whole(0.4);
      case 'quiz': return whole(0.6);
      default: return whole(0.2);
    }
  }

  // Where words sit, near enough to send the pen there before they are measured: [left, middle] to [right, middle].
  function textEnds(mark) {
    const size = mark.size > 0 ? mark.size : 26, width = String(mark.text).length * size * 0.5;
    const left = mark.align === 'start' ? mark.at[0] : mark.align === 'end' ? mark.at[0] - width : mark.at[0] - width / 2;
    return { from: [left, mark.at[1]], to: [left + width, mark.at[1]] };
  }

  // The pen's cursor flies to a mark before its ink starts: not at all to one close by, a touch-down for the first.
  const FLIGHT = { near: 110, first: 0.3, base: 0.18, unitsPerSecond: 2600, longest: 0.5 };
  function flightSeconds(from, to) {
    if (!to) return 0;
    if (!from) return FLIGHT.first;
    const d = distance(from, to);
    return d < FLIGHT.near ? 0 : Math.min(FLIGHT.longest, FLIGHT.base + d / FLIGHT.unitsPerSecond);
  }

  // The flight's path: a shallow arc, lifted off the page in the middle, as `n` + 1 points.
  function flightArc(from, to, n) {
    const count = Math.max(2, n || 8), d = distance(from, to), lift = Math.min(d * 0.12, 36);
    const out = [];
    for (let i = 0; i <= count; i++) {
      const t = i / count, up = 4 * t * (1 - t) * lift;
      out.push([from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t - up]);
    }
    return out;
  }

  // A spoken line's marks, spread so the last one ends as the line does.
  //
  // `steps` are the marks still to draw for the line, in order, each {seconds, advance, lead}: its natural time, when
  // the next may start, and its cursor's flight (a mark the cursor flies to waits for the stroke before it to end).
  // `available` is the time left until the line ends. The answer is three factors: `pace` on each mark's own time,
  // `spread` on the time between two marks' starts, `lead` on the flights; and `rest`, the time left over at the end.
  //   time to spare   the pen slows down (to 1.5×, or 1.3× on a line still being written), then the marks move apart
  //                   (to 3×), and what is still left is a rest;
  //   too little      the pen and its flights speed up together (to 0.35×), then the marks crowd closer (to 0.2×).
  const PACE = { fastest: 0.35, slowest: 1.5, openSlowest: 1.3, widest: 3, tightest: 0.2 };
  function planLine(steps, available, options) {
    const o = options || {};
    if (!steps || !steps.length) return { pace: 1, spread: 1, lead: 1, rest: Math.max(0, available || 0) };
    let leads = 0, gaps = 0;
    steps.forEach((step, i) => {
      leads += step.lead || 0;
      if (i < steps.length - 1) gaps += steps[i + 1].lead > 0 ? step.seconds : step.advance;
    });
    const last = steps[steps.length - 1].seconds;
    const total = (pace, spread, lead) => lead * leads + spread * gaps + pace * last;
    const answer = (pace, spread, lead) => ({ pace, spread, lead, rest: Math.max(0, (available || 0) - total(pace, spread, lead)) });
    // The voice was cut short: everything left, quickly, with no flights.
    if (o.finishing) return { pace: PACE.fastest, spread: PACE.tightest, lead: 0, rest: 0 };
    const natural = total(1, 1, 1);
    const fit = available > 0.05 && natural > 0 ? available / natural : 0;
    if (fit >= 1) {
      const pace = Math.min(fit, o.open ? PACE.openSlowest : PACE.slowest);
      // A line still being written may get more marks: its marks are not moved apart to fill the time yet.
      const spread = o.open || !(gaps > 0) ? pace : clamp((available - leads - pace * last) / gaps, pace, PACE.widest);
      return answer(pace, spread, 1);
    }
    const pace = Math.max(PACE.fastest, fit);
    const spread = gaps > 0 ? clamp((available - pace * leads - pace * last) / gaps, PACE.tightest, pace) : pace;
    return answer(pace, spread, pace);
  }

  // How long a line takes to say. The voice's own speed, once a few lines have been heard, replaces the guess of
  // 2.6 words a second: `spoken` is [{words, seconds}], the lines heard so far.
  const WORDS_PER_SECOND = 2.6;
  function speechRate(spoken) {
    const heard = (spoken || []).filter(line => line.words >= 3 && line.seconds > 0.3).slice(-4);
    if (!heard.length) return WORDS_PER_SECOND;
    const words = heard.reduce((sum, line) => sum + line.words, 0), seconds = heard.reduce((sum, line) => sum + line.seconds, 0);
    return clamp(words / seconds, 1.5, 4.5);
  }
  const lineSeconds = (words, rate) => Math.max(0.8, words / (rate > 0 ? rate : WORDS_PER_SECOND));

  // ------------------------------------------------------------------ strokes
  // A little randomness that is the same for the same seed every time.
  function rng(seed) {
    let a = (seed * 2654435761) >>> 0;
    return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
  }

  // A hand's line through corners: each edge bows a little to one side and wobbles along its way; a closed shape is
  // closed by hand, the pen running a little past where it started.
  function handPoints(pts, rnd, closed) {
    const out = [];
    const all = closed ? pts.concat([pts[0]]) : pts;
    for (let i = 0; i < all.length - 1; i++) {
      const [x0, y0] = all[i], [x1, y1] = all[i + 1];
      const d = Math.hypot(x1 - x0, y1 - y0), steps = d > 24 ? Math.max(2, Math.round(d / 40)) : 1;
      const nx = -(y1 - y0) / (d || 1), ny = (x1 - x0) / (d || 1), wobble = Math.min(1.6, d * 0.006);
      const bow = (rnd() - 0.5) * 2 * Math.min(2.4, d * 0.02);
      for (let s = 0; s < steps; s++) {
        const t = s / steps, j = s === 0 ? 0 : (rnd() - 0.5) * 2 * wobble + Math.sin(Math.PI * t) * bow;
        out.push([x0 + (x1 - x0) * t + nx * j, y0 + (y1 - y0) * t + ny * j]);
      }
    }
    out.push(all[all.length - 1]);
    if (closed && all.length > 2) {
      const [x0, y0] = all[0], [x1, y1] = all[1], d = Math.hypot(x1 - x0, y1 - y0);
      const past = Math.min(8, d * 0.05) * (0.5 + rnd());
      if (d > 0 && past > 0.5) out.push([x0 + (x1 - x0) / d * past, y0 + (y1 - y0) / d * past + 0.8]);
    }
    return out;
  }

  // An ellipse as a hand draws it: from anywhere on it, not quite round, ending a little past where it began and a
  // little inside or outside it.
  function ellipsePoints(mark, rnd) {
    const rx = Math.abs(mark.rx), ry = Math.abs(mark.ry);
    const n = clamp(Math.round((rx + ry) / 2.5), 28, 72), start = rnd() * Math.PI * 2, past = 0.05 + rnd() * 0.05;
    const a1 = 0.01 + rnd() * 0.012, p1 = rnd() * Math.PI * 2, a2 = 0.005 + rnd() * 0.008, p2 = rnd() * Math.PI * 2;
    const drift = (rnd() - 0.5) * 0.035;
    const out = [];
    for (let i = 0; i <= Math.round(n * (1 + past)); i++) {
      const turn = i / n, a = start + turn * Math.PI * 2;
      const r = 1 + a1 * Math.sin(2 * a + p1) + a2 * Math.sin(3 * a + p2) + drift * turn;
      out.push([mark.at[0] + Math.cos(a) * rx * r, mark.at[1] + Math.sin(a) * ry * r]);
    }
    return out;
  }

  // A smooth curve through the points (Catmull-Rom), as points about `step` apart.
  function smoothPoints(pts, step) {
    if (pts.length < 3) return pts.map(p => [p[0], p[1]]);
    const out = [[pts[0][0], pts[0][1]]], spacing = step > 0 ? step : 4;
    for (let i = 0; i < pts.length - 1; i++) {
      const p0 = pts[i - 1] || pts[i], p1 = pts[i], p2 = pts[i + 1], p3 = pts[i + 2] || p2;
      const pieces = clamp(Math.ceil(distance(p1, p2) / spacing), 1, 24);
      for (let s = 1; s <= pieces; s++) {
        const t = s / pieces, t2 = t * t, t3 = t2 * t;
        const at = k => 0.5 * (2 * p1[k] + (p2[k] - p0[k]) * t + (2 * p0[k] - 5 * p1[k] + 4 * p2[k] - p3[k]) * t2 + (3 * p1[k] - p0[k] - 3 * p2[k] + p3[k]) * t3);
        out.push([at(0), at(1)]);
      }
    }
    return out;
  }

  const finite = p => Array.isArray(p) && Number.isFinite(p[0]) && Number.isFinite(p[1]);

  // The line the pen's tip follows over a mark, as points close together; null for a mark that is not a pen stroke
  // (or has no usable points). The same mark and seed give the same line.
  function centreline(mark, seed) {
    const rnd = rng(seed);
    let corners = null;
    switch (mark.do) {
      case 'line': case 'arrow': {
        const pts = (mark.pts || []).filter(finite);
        if (pts.length < 2) return null;
        corners = mark.smooth ? pts : handPoints(pts, rnd, false);
        break;
      }
      case 'poly': {
        const pts = (mark.pts || []).filter(finite);
        if (pts.length < 2) return null;
        corners = handPoints(pts, rnd, true);
        break;
      }
      case 'rect': {
        if (!finite(mark.at) || !Number.isFinite(mark.w) || !Number.isFinite(mark.h)) return null;
        const [x, y] = mark.at;
        corners = handPoints([[x, y], [x + mark.w, y], [x + mark.w, y + mark.h], [x, y + mark.h]], rnd, true);
        break;
      }
      case 'ellipse':
        if (!finite(mark.at) || !Number.isFinite(mark.rx) || !Number.isFinite(mark.ry)) return null;
        corners = ellipsePoints(mark, rnd);
        break;
      default: return null;
    }
    return smoothPoints(corners, 4);
  }

  // The points of a line, `step` apart along it (the last one kept).
  function resample(points, step) {
    const out = [[points[0][0], points[0][1]]];
    let carried = 0;
    for (let i = 1; i < points.length; i++) {
      const a = points[i - 1], b = points[i], d = distance(a, b);
      if (!(d > 0)) continue;
      let at = step - carried;
      while (at <= d) { out.push([a[0] + (b[0] - a[0]) * at / d, a[1] + (b[1] - a[1]) * at / d]); at += step; }
      carried = d - (at - step);
    }
    const end = points[points.length - 1];
    if (distance(out[out.length - 1], end) > step * 0.25 || out.length < 2) out.push([end[0], end[1]]);
    else out[out.length - 1] = [end[0], end[1]];
    return out;
  }

  // The point a `fraction` (0 to 1) of the way along a line.
  function pointAt(points, fraction) {
    const total = polylineLength(points);
    let left = clamp(fraction, 0, 1) * total;
    for (let i = 1; i < points.length; i++) {
      const a = points[i - 1], b = points[i], d = distance(a, b);
      if (left <= d && d > 0) return [a[0] + (b[0] - a[0]) * left / d, a[1] + (b[1] - a[1]) * left / d];
      left -= d;
    }
    const end = points[points.length - 1];
    return [end[0], end[1]];
  }

  // `n` + 1 points evenly spaced along a line, from the fraction `from` (0 by default) to its end.
  function sampleAlong(points, n, from) {
    const out = [], start = from > 0 ? from : 0;
    for (let i = 0; i <= n; i++) out.push(pointAt(points, start + (1 - start) * i / n));
    return out;
  }

  // The ink of one stroke: a closed outline round the centreline, `width` across. The pen presses in over the first
  // stretch and lifts off over the last (the end thinner than the start, as a flick is), and the width breathes a
  // little along the way. `taper` 0 keeps the ends full; `phase` shifts the breathing (seeded by the caller).
  const TIP = { start: 0.55, end: 0.3 };
  const ease3 = t => t * t * (3 - 2 * t);
  function outline(points, width, options) {
    const o = options || {}, taper = o.taper == null ? 1 : Math.max(0, o.taper), phase = o.phase || 0;
    const half = Math.max(0.2, width / 2), total = polylineLength(points);
    if (!(total > 0)) {
      // No length: a dab of the pen.
      const [x, y] = points[0];
      return Array.from({ length: 8 }, (_, k) => [x + Math.cos(k * Math.PI / 4) * half, y + Math.sin(k * Math.PI / 4) * half]);
    }
    const pts = resample(points, clamp(total / 160, 2, 6)), n = pts.length;
    const ramp = Math.min(total * 0.28, width * 5) * taper;
    const left = [], right = [], widths = [], tangents = [];
    let u = 0;
    for (let i = 0; i < n; i++) {
      if (i) u += distance(pts[i - 1], pts[i]);
      const a = pts[Math.max(0, i - 1)], b = pts[Math.min(n - 1, i + 1)];
      const d = distance(a, b) || 1, tx = (b[0] - a[0]) / d, ty = (b[1] - a[1]) / d;
      let press = 1;
      if (ramp > 0) {
        const down = TIP.start + (1 - TIP.start) * ease3(Math.min(1, u / ramp));
        const up = TIP.end + (1 - TIP.end) * ease3(Math.min(1, (total - u) / ramp));
        press = Math.min(down, up);
      }
      const w = half * press * (1 + 0.06 * Math.sin(u / 8 + phase) + 0.03 * Math.sin(u / 3.1 + phase * 1.7 + 1));
      left.push([pts[i][0] - ty * w, pts[i][1] + tx * w]);
      right.push([pts[i][0] + ty * w, pts[i][1] - tx * w]);
      widths.push(w);
      tangents.push([tx, ty]);
    }
    // Round ends: three points round each tip, from one side to the other.
    const cap = (centre, [tx, ty], w, sign) => [60, 0, -60].map(degrees => {
      const angle = degrees * Math.PI / 180, along = Math.cos(angle) * w * sign, across = Math.sin(angle) * w * sign;
      return [centre[0] + tx * along - ty * across, centre[1] + ty * along + tx * across];
    });
    return left.concat(cap(pts[n - 1], tangents[n - 1], widths[n - 1], 1), right.reverse(), cap(pts[0], tangents[0], widths[0], -1));
  }

  // A line cut into dashes: `on` long, `off` apart.
  function dashes(points, on, off) {
    const total = polylineLength(points), out = [];
    if (!(total > 0) || !(on > 0)) return [points];
    for (let from = 0; from < total; from += on + Math.max(0, off)) {
      const to = Math.min(total, from + on);
      if (to - from < on * 0.25 && out.length) break;
      const piece = [pointAt(points, from / total)];
      // The line's own points between the dash's ends, so a dash follows a curve.
      let u = 0;
      for (let i = 1; i < points.length; i++) {
        u += distance(points[i - 1], points[i]);
        if (u > from && u < to) piece.push(points[i]);
      }
      piece.push(pointAt(points, to / total));
      out.push(piece);
    }
    return out;
  }

  // An arrow's head: two flicks back from its tip.
  function arrowBarbs(pts) {
    const tip = pts[pts.length - 1], back = pts[pts.length - 2];
    const angle = Math.atan2(tip[1] - back[1], tip[0] - back[0]);
    const size = Math.min(22, Math.max(10, polylineLength(pts) * 0.25));
    return [-1, 1].map(side => {
      const a = angle + Math.PI + side * 0.5;
      return [[tip[0], tip[1]], [tip[0] + Math.cos(a) * size, tip[1] + Math.sin(a) * size]];
    });
  }

  // SVG path data through points (straight pieces; the points are close together).
  function pathData(points, closed) {
    return 'M' + points.map(p => p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join('L') + (closed ? 'Z' : '');
  }

  // ------------------------------------------------------------------ sound
  // The pen's speed through a stroke of `length` units drawn in `seconds` on the pen's curve, at `n` + 1 moments,
  // as a share of the pen's cruising speed (so it starts near nothing, swells, and falls away with the ink).
  function speedCurve(length, seconds, n) {
    const count = Math.max(2, n || 24), out = [], h = 0.01;
    for (let i = 0; i <= count; i++) {
      const t = i / count, slope = (penEase(Math.min(1, t + h)) - penEase(Math.max(0, t - h))) / (Math.min(1, t + h) - Math.max(0, t - h));
      out.push(clamp(length * slope / Math.max(0.05, seconds) / PEN.unitsPerSecond, 0, 1.5));
    }
    return out;
  }

  // Handwriting's speed: a small push for each letter.
  function writingCurve(letters, n) {
    const count = Math.max(2, n || 24), pushes = clamp(letters, 1, 40), out = [];
    for (let i = 0; i <= count; i++) out.push(0.3 + 0.3 * Math.abs(Math.sin(Math.PI * pushes * i / count)));
    return out;
  }

  // What a stroke sounds like at each of those moments: `gain` (0 to 1: nothing when the pen is still, at both ends
  // always) and the `frequency` its hiss is centred on (brighter as the pen speeds up). Textures: the pen on the
  // screen, handwriting (thinner and higher), the marker (lower and broader).
  const TEXTURES = {
    pen: { level: 1, low: 700, high: 2600 },
    writing: { level: 0.7, low: 1500, high: 3000 },
    marker: { level: 0.85, low: 350, high: 900 }
  };
  function soundCurves(speeds, texture) {
    const voice = TEXTURES[texture] || TEXTURES.pen, last = speeds.length - 1;
    return {
      gain: speeds.map((v, i) => i === 0 || i === last ? 0 : voice.level * Math.pow(clamp(v, 0, 1), 0.6)),
      frequency: speeds.map(v => voice.low + (voice.high - voice.low) * clamp(v, 0, 1))
    };
  }

  const api = {
    PEN, PEN_EASE, PEN_EASE_CSS, PACE, FLIGHT, TEXTURES, WORDS_PER_SECOND,
    clamp, distance, penSeconds, bezierEase, penEase, polylineLength, markLength, naturalTiming, textEnds,
    flightSeconds, flightArc, planLine, speechRate, lineSeconds,
    rng, handPoints, ellipsePoints, smoothPoints, centreline, resample, pointAt, sampleAlong, outline, dashes,
    arrowBarbs, pathData, speedCurve, writingCurve, soundCurves
  };
  root.StagePen = api;
  if (typeof module === 'object' && module && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
