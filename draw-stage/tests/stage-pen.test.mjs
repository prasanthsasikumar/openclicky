// The pen's arithmetic (../stage-pen.js): timing, pacing to a spoken line, flights, stroke shapes, sound curves.
// Run with `npm run test:stage` (node --test).
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import test from 'node:test';

const Pen = createRequire(import.meta.url)('../stage-pen.js');
const near = (actual, expected, tolerance = 1e-6) => assert.ok(Math.abs(actual - expected) <= tolerance, `${actual} is not within ${tolerance} of ${expected}`);
const line = (length, extra) => Object.assign({ do: 'line', pts: [[100, 100], [100 + length, 100]] }, extra);

test('pen time goes with the stroke length, between 0.3 and 1.4 seconds', () => {
  near(Pen.penSeconds(0), 0.3);
  near(Pen.penSeconds(50), 0.3);
  near(Pen.penSeconds(700), 0.75);
  near(Pen.penSeconds(1400), 1.25);
  near(Pen.penSeconds(5000), 1.4);
  near(Pen.penSeconds(NaN), 0.3);
  let before = 0;
  for (let length = 0; length <= 3000; length += 50) { assert.ok(Pen.penSeconds(length) >= before); before = Pen.penSeconds(length); }
});

test('the pen ease is the cubic-bezier it says it is', () => {
  assert.equal(Pen.PEN_EASE_CSS, 'cubic-bezier(0.35,0.2,0.55,0.95)');
  near(Pen.penEase(0), 0);
  near(Pen.penEase(1), 1);
  near(Pen.penEase(-1), 0);
  near(Pen.penEase(2), 1);
  let before = 0;
  for (let t = 0; t <= 1.0001; t += 0.02) { const v = Pen.penEase(t); assert.ok(v >= before - 1e-9); before = v; }
  // A linear bezier is the identity; the solver is checked against a point worked out by hand: at s = 0.5 the
  // curve (.35,.2,.55,.95) is at x = 0.4625, y = 0.55625.
  const linear = Pen.bezierEase(1 / 3, 1 / 3, 2 / 3, 2 / 3);
  for (const t of [0.1, 0.37, 0.9]) near(linear(t), t, 1e-5);
  near(Pen.penEase(0.4625), 0.55625, 1e-5);
});

test('a stroke lets the next mark start at 0.7 of its time', () => {
  const timing = Pen.naturalTiming(line(700));
  near(timing.seconds, 0.75);
  near(timing.advance, 0.75 * 0.7);
  assert.equal(timing.pen, true);
  for (const mark of [{ do: 'rect', at: [0, 0], w: 200, h: 100 }, { do: 'ellipse', at: [0, 0], rx: 80, ry: 50 }, { do: 'poly', pts: [[0, 0], [100, 0], [50, 80]] }]) {
    const t = Pen.naturalTiming(mark);
    near(t.seconds, Pen.penSeconds(Pen.markLength(mark)));
    near(t.advance / t.seconds, 0.7);
  }
});

test('mark lengths: a closed shape goes all the way round', () => {
  near(Pen.markLength({ do: 'rect', at: [0, 0], w: 200, h: 100 }), 600);
  near(Pen.markLength({ do: 'poly', pts: [[0, 0], [30, 0], [30, 40]] }), 120);
  near(Pen.markLength({ do: 'ellipse', at: [0, 0], rx: 50, ry: 50 }), Math.PI * 100 * 1.06);
  near(Pen.markLength({ do: 'dot', at: [0, 0], r: 4 }), 0);
});

test('an arrow is its shaft and then its head; other marks keep their own times', () => {
  const arrow = Pen.naturalTiming({ do: 'arrow', pts: [[0, 0], [700, 0]] });
  near(arrow.seconds, 0.75 + 0.16);
  near(arrow.advance, 0.75 + 0.1);
  assert.equal(Pen.naturalTiming({ do: 'card', at: [0, 0], w: 1, h: 1 }).pen, false);
  near(Pen.naturalTiming({ do: 'part3d', params: { quick: true } }).seconds, 0.1);
  near(Pen.naturalTiming({ do: 'part3d', params: {} }).advance, 0.64);
  near(Pen.naturalTiming({ do: 'text', text: 'thrust' }).seconds, 0.3);
  near(Pen.naturalTiming({ do: 'something new' }).seconds, 0.2);
});

test('the cursor flies to a far mark, not to a near one, and touches down for the first', () => {
  near(Pen.flightSeconds(null, [10, 10]), 0.3);
  near(Pen.flightSeconds([0, 0], [100, 0]), 0);
  near(Pen.flightSeconds([0, 0], [520, 0]), 0.18 + 520 / 2600);
  near(Pen.flightSeconds([0, 0], [5000, 0]), 0.5);
  near(Pen.flightSeconds([0, 0], null), 0);
});

test('a flight is an arc from one point to the other, lifted in the middle', () => {
  const arc = Pen.flightArc([0, 100], [400, 100], 8);
  assert.equal(arc.length, 9);
  assert.deepEqual(arc[0], [0, 100]);
  assert.deepEqual(arc[8], [400, 100]);
  near(arc[4][0], 200);
  near(arc[4][1], 100 - 36);
  assert.ok(Pen.flightArc([0, 0], [50, 0], 4)[2][1] > -7);
});

// The time the plan takes: flights, the gaps between marks' starts, and the last mark's own time.
function planned(steps, plan) {
  let total = 0;
  steps.forEach((step, i) => {
    total += (step.lead || 0) * plan.lead;
    total += i < steps.length - 1 ? (steps[i + 1].lead > 0 ? step.seconds : step.advance) * plan.spread : step.seconds * plan.pace;
  });
  return total;
}
const strokes = (...seconds) => seconds.map(s => ({ seconds: s, advance: s * 0.7, lead: 0 }));

test('a line with exactly enough time is drawn at its natural speed', () => {
  const steps = strokes(0.5, 0.8, 0.4);
  const natural = 0.35 + 0.56 + 0.4;
  const plan = Pen.planLine(steps, natural);
  near(plan.pace, 1);
  near(plan.spread, 1);
  near(plan.rest, 0);
});

test('with time to spare the marks end with the line: slower pen, then wider gaps', () => {
  const steps = strokes(0.5, 0.8, 0.4, 0.6);
  for (const available of [2.0, 2.6, 3.4, 4.2]) {
    const plan = Pen.planLine(steps, available);
    assert.ok(plan.pace >= 1 && plan.pace <= Pen.PACE.slowest + 1e-9, `pace ${plan.pace}`);
    assert.ok(plan.spread >= plan.pace - 1e-9 && plan.spread <= Pen.PACE.widest + 1e-9, `spread ${plan.spread}`);
    near(planned(steps, plan), available, 1e-6);
    near(plan.rest, 0);
  }
});

test('a very long line leaves a rest at its end instead of a crawling pen', () => {
  const steps = strokes(0.5, 0.5);
  const plan = Pen.planLine(steps, 20);
  near(plan.pace, Pen.PACE.slowest);
  near(plan.spread, Pen.PACE.widest);
  near(plan.rest, 20 - planned(steps, plan));
  assert.ok(plan.rest > 15);
});

test('with too little time the pen speeds up and still ends with the line', () => {
  const steps = strokes(0.9, 0.9, 0.9, 0.9);
  const natural = 0.63 * 3 + 0.9;
  for (const share of [0.9, 0.6, 0.4]) {
    const plan = Pen.planLine(steps, natural * share);
    near(plan.pace, share);
    near(planned(steps, plan), natural * share, 1e-6);
  }
  // Under the fastest pen the marks crowd together, down to a floor; then the line ends first.
  const tight = Pen.planLine(steps, natural * 0.25);
  near(tight.pace, Pen.PACE.fastest);
  assert.ok(tight.spread < tight.pace && tight.spread >= Pen.PACE.tightest);
  near(planned(steps, tight), natural * 0.25, 1e-6);
  const late = Pen.planLine(steps, 0);
  near(late.pace, Pen.PACE.fastest);
  near(late.spread, Pen.PACE.tightest);
});

test('a line still being written is not stretched to fill its time', () => {
  const plan = Pen.planLine(strokes(0.5, 0.5), 10, { open: true });
  near(plan.pace, Pen.PACE.openSlowest);
  near(plan.spread, Pen.PACE.openSlowest);
});

test('a mark the cursor flies to waits for the stroke before it to end', () => {
  const steps = [{ seconds: 0.6, advance: 0.42, lead: 0.3 }, { seconds: 0.5, advance: 0.35, lead: 0.4 }, { seconds: 0.4, advance: 0.28, lead: 0 }];
  const natural = 0.3 + 0.6 + 0.4 + 0.35 + 0.4;
  const plan = Pen.planLine(steps, natural);
  near(plan.pace, 1);
  near(plan.spread, 1);
  near(plan.lead, 1);
  // Hurried, the flights shorten with the pen.
  const rushed = Pen.planLine(steps, natural * 0.5);
  near(rushed.lead, 0.5);
  near(planned(steps, rushed), natural * 0.5, 1e-6);
});

test('a voice cut short: everything left, quickly, without flights', () => {
  const plan = Pen.planLine([{ seconds: 1, advance: 0.7, lead: 0.4 }], 0, { finishing: true });
  assert.deepEqual(plan, { pace: Pen.PACE.fastest, spread: Pen.PACE.tightest, lead: 0, rest: 0 });
  near(Pen.planLine([], 3).rest, 3);
});

test('a single mark is slowed, not delayed', () => {
  const plan = Pen.planLine(strokes(0.5), 0.6);
  near(plan.pace, 1.2);
  near(plan.rest, 0);
});

test('the voice’s own speed replaces the guess once lines have been heard', () => {
  near(Pen.speechRate([]), 2.6);
  near(Pen.speechRate([{ words: 2, seconds: 0.2 }]), 2.6);
  near(Pen.speechRate([{ words: 12, seconds: 4 }, { words: 9, seconds: 3 }]), 3);
  near(Pen.speechRate([{ words: 30, seconds: 1 }]), 4.5);
  near(Pen.speechRate([{ words: 100, seconds: 10 }, { words: 6, seconds: 3 }, { words: 6, seconds: 3 }, { words: 6, seconds: 3 }, { words: 6, seconds: 3 }]), 2);
  near(Pen.lineSeconds(13), 5);
  near(Pen.lineSeconds(12, 3), 4);
  near(Pen.lineSeconds(1), 0.8);
});

test('the same mark and seed give the same line; another seed another', () => {
  for (const mark of [line(300), { do: 'rect', at: [10, 20], w: 200, h: 120 }, { do: 'ellipse', at: [300, 300], rx: 90, ry: 60 }, { do: 'poly', pts: [[0, 0], [120, 10], [60, 90]] }]) {
    assert.deepEqual(Pen.centreline(mark, 5), Pen.centreline(mark, 5));
    assert.notDeepEqual(Pen.centreline(mark, 5), Pen.centreline(mark, 6));
  }
});

test('a hand’s line starts and ends where the mark does and stays close to it', () => {
  const path = Pen.centreline(line(400), 3);
  assert.deepEqual(path[0], [100, 100]);
  assert.deepEqual(path[path.length - 1].map(Math.round), [500, 100]);
  for (const [x, y] of path) { assert.ok(x >= 99 && x <= 501); assert.ok(Math.abs(y - 100) < 6, `y ${y}`); }
  // Points close together, so the ink's outline and the cursor follow the curve.
  for (let i = 1; i < path.length; i++) assert.ok(Pen.distance(path[i - 1], path[i]) < 12);
  // A curve() keeps the points it was given.
  const smooth = Pen.centreline({ do: 'line', smooth: true, pts: [[0, 0], [50, 40], [100, 0]] }, 1);
  assert.ok(smooth.some(p => Math.abs(p[0] - 50) < 1e-9 && Math.abs(p[1] - 40) < 1e-9));
});

test('closed shapes are closed by hand: a little past the start', () => {
  const rect = Pen.centreline({ do: 'rect', at: [100, 100], w: 300, h: 200 }, 2);
  const end = rect[rect.length - 1];
  assert.ok(end[0] > 100.5 && end[0] < 113, `x ${end[0]}`);
  assert.ok(Math.abs(end[1] - 100) < 3);
  near(Pen.polylineLength(rect), 1000, 30);
  const ellipse = { do: 'ellipse', at: [400, 300], rx: 120, ry: 80 };
  const round = Pen.centreline(ellipse, 9);
  for (const [x, y] of round) {
    const r = Math.hypot((x - 400) / 120, (y - 300) / 80);
    assert.ok(r > 0.93 && r < 1.07, `radius ${r}`);
  }
  const turned = Pen.ellipsePoints(ellipse, Pen.rng(9));
  const sweep = (turned.length - 1) / Math.min(72, Math.max(28, Math.round(200 / 2.5)));
  assert.ok(sweep >= 1.04 && sweep <= 1.11, `sweep ${sweep}`);
});

test('marks without usable points have no line', () => {
  assert.equal(Pen.centreline({ do: 'line', pts: [[0, 0]] }, 1), null);
  assert.equal(Pen.centreline({ do: 'line', pts: [[0, 0], [NaN, 3]] }, 1), null);
  assert.equal(Pen.centreline({ do: 'rect', at: [0, 0], w: NaN, h: 3 }, 1), null);
  assert.equal(Pen.centreline({ do: 'text', at: [0, 0], text: 'x' }, 1), null);
});

// The outline's width at a share of the way along a straight horizontal stroke: the gap between its two sides.
function widthAt(shape, x) {
  const ys = [];
  for (let i = 0; i < shape.length; i++) {
    const a = shape[i], b = shape[(i + 1) % shape.length];
    if ((a[0] - x) * (b[0] - x) <= 0 && a[0] !== b[0]) ys.push(a[1] + (b[1] - a[1]) * (x - a[0]) / (b[0] - a[0]));
  }
  return Math.max(...ys) - Math.min(...ys);
}

test('the ink is a closed outline, full in the middle, tapered at both ends, thinner where the pen lifts', () => {
  const points = [[0, 0], [300, 0]];
  const shape = Pen.outline(points, 4, { phase: 1 });
  assert.ok(shape.length > 40);
  for (const p of shape) assert.ok(Number.isFinite(p[0]) && Number.isFinite(p[1]));
  const middle = widthAt(shape, 150), start = widthAt(shape, 1), end = widthAt(shape, 299);
  assert.ok(middle > 4 * 0.9 && middle < 4 * 1.1, `middle ${middle}`);
  assert.ok(start < middle * 0.7, `start ${start}`);
  assert.ok(end < start, `end ${end} should be thinner than start ${start}`);
  // Round ends reach a little past the line's ends, never far.
  const xs = shape.map(p => p[0]);
  assert.ok(Math.min(...xs) < 0 && Math.min(...xs) > -2.5);
  assert.ok(Math.max(...xs) > 300 && Math.max(...xs) < 302.5);
  assert.deepEqual(Pen.outline(points, 4, { phase: 1 }), shape);
  assert.notDeepEqual(Pen.outline(points, 4, { phase: 2 }), shape);
});

test('taper 0 keeps the ends full; a point is a dab', () => {
  const shape = Pen.outline([[0, 0], [200, 0]], 6, { taper: 0 });
  assert.ok(widthAt(shape, 2) > 6 * 0.9);
  assert.ok(widthAt(shape, 198) > 6 * 0.9);
  const dab = Pen.outline([[5, 5], [5, 5]], 6);
  assert.equal(dab.length, 8);
  for (const p of dab) near(Pen.distance(p, [5, 5]), 3);
});

test('the outline follows a curve on both sides', () => {
  const arc = Array.from({ length: 40 }, (_, i) => [200 + 100 * Math.cos(i / 39 * Math.PI), 200 + 100 * Math.sin(i / 39 * Math.PI)]);
  for (const p of Pen.outline(arc, 5)) {
    const r = Math.hypot(p[0] - 200, p[1] - 200);
    assert.ok(r > 96 && r < 104, `radius ${r}`);
  }
});

test('resampling and points along a line', () => {
  const pts = Pen.resample([[0, 0], [10, 0], [22, 0]], 4);
  assert.deepEqual(pts.map(p => p[0]), [0, 4, 8, 12, 16, 20, 22]);
  // Round a corner the spacing is along the line, and the end is kept.
  const bent = Pen.resample([[0, 0], [10, 0], [10, 10]], 4);
  assert.deepEqual(bent, [[0, 0], [4, 0], [8, 0], [10, 2], [10, 6], [10, 10]]);
  assert.deepEqual(Pen.pointAt([[0, 0], [10, 0], [10, 10]], 0.75), [10, 5]);
  assert.deepEqual(Pen.pointAt([[0, 0], [10, 0]], 7), [10, 0]);
  const along = Pen.sampleAlong([[0, 0], [100, 0]], 4);
  assert.deepEqual(along.map(p => p[0]), [0, 25, 50, 75, 100]);
  assert.deepEqual(Pen.sampleAlong([[0, 0], [100, 0]], 2, 0.5).map(p => p[0]), [50, 75, 100]);
});

test('dashes cover the line in pieces of the asked length', () => {
  const pieces = Pen.dashes([[0, 0], [100, 0]], 10, 8);
  assert.equal(pieces.length, 6);
  for (const piece of pieces.slice(0, -1)) near(Pen.polylineLength(piece), 10);
  assert.deepEqual(pieces[1][0], [18, 0]);
  // A dash round a corner keeps the corner.
  const bent = Pen.dashes([[0, 0], [5, 0], [5, 20]], 10, 100);
  assert.deepEqual(bent[0], [[0, 0], [5, 0], [5, 5]]);
});

test('an arrow’s head is two flicks back from its tip', () => {
  const barbs = Pen.arrowBarbs([[0, 0], [100, 0]]);
  assert.equal(barbs.length, 2);
  for (const [tip, end] of barbs) {
    assert.deepEqual(tip, [100, 0]);
    assert.ok(end[0] < 100);
    near(Pen.distance(tip, end), 22);
  }
  near(barbs[0][1][1], -barbs[1][1][1]);
});

test('path data is straight pieces, closed when asked', () => {
  assert.equal(Pen.pathData([[0, 0], [10.04, 5.55]], false), 'M0.0 0.0L10.0 5.5');
  assert.equal(Pen.pathData([[0, 0], [1, 0], [1, 1]], true), 'M0.0 0.0L1.0 0.0L1.0 1.0Z');
});

test('the pen’s speed through a stroke rises and falls with the ink', () => {
  const speeds = Pen.speedCurve(1400, 1.25, 24);
  assert.equal(speeds.length, 25);
  const peak = Math.max(...speeds);
  assert.ok(peak > speeds[0] && peak > speeds[24]);
  assert.ok(speeds.indexOf(peak) > 4 && speeds.indexOf(peak) < 20);
  // The speeds add up to the stroke: mean speed × time = length.
  const mean = speeds.reduce((a, b) => a + b, 0) / speeds.length;
  near(mean * 1400 * 1.25, 1400, 80);
  // A short stroke in the same time is slower all the way.
  const slow = Pen.speedCurve(200, 1.25, 24);
  assert.ok(slow.every((v, i) => v <= speeds[i] + 1e-9));
  for (const v of Pen.speedCurve(1e6, 0.3, 8)) assert.ok(v <= 1.5);
});

test('the bed is silent at both ends, and louder and brighter the faster the pen goes', () => {
  const speeds = [0.2, 0.2, 0.6, 1, 0.4, 0.2];
  const { gain, frequency } = Pen.soundCurves(speeds, 'pen');
  assert.equal(gain[0], 0);
  assert.equal(gain[5], 0);
  assert.ok(gain[3] > gain[2] && gain[2] > gain[1] && gain[1] > 0);
  assert.ok(gain.every(v => v >= 0 && v <= 1));
  assert.ok(frequency[3] > frequency[2] && frequency[2] > frequency[1]);
  near(frequency[3], Pen.TEXTURES.pen.high);
  near(Pen.soundCurves([0, 0, 0], 'pen').frequency[1], Pen.TEXTURES.pen.low);
  assert.ok(Pen.soundCurves(speeds, 'writing').gain[3] < gain[3]);
  assert.deepEqual(Pen.soundCurves(speeds, 'no such texture'), Pen.soundCurves(speeds, 'pen'));
  const writing = Pen.writingCurve(6, 24);
  assert.equal(writing.length, 25);
  assert.ok(writing.every(v => v >= 0.3 && v <= 0.6 + 1e-9));
});

test('the seeded randomness repeats', () => {
  const a = Pen.rng(42), b = Pen.rng(42), c = Pen.rng(43);
  const first = [a(), a(), a()];
  assert.deepEqual(first, [b(), b(), b()]);
  assert.notDeepEqual(first, [c(), c(), c()]);
  assert.ok(first.every(v => v >= 0 && v < 1));
});
