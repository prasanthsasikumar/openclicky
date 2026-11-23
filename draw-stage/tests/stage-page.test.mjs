// The stage's page (../stage.js with ../stage-pen.js, ../stage-sound.js and the real ../stage-worker.js) drawing
// programs from start to finish under node, on a clock turned by hand (page-harness.mjs). Nothing is rendered or
// heard here: these check what the page schedules, not how it looks or sounds.
import assert from 'node:assert/strict';
import test from 'node:test';
import { openStage, sample } from './page-harness.mjs';

const types = page => page.sent.map(message => message.type);
const errors = page => page.sent.filter(message => message.type === 'error');
// Four strokes back and forth, each starting near where the last ended, under a short line, and one far away under a longer one.
const strokes = `
step(1); say("Look.")
line([[100, 100], [400, 100]])
line([[400, 130], [100, 130]])
line([[100, 160], [400, 160]])
line([[400, 190], [100, 190]])
step(2); say("And over here there is one more line to see, far away.")
line([[900, 600], [1100, 600]], {color: 'red'})
`;

test('the bundled rocket is drawn to the end with nothing going wrong', () => {
  const page = openStage();
  assert.deepEqual(types(page).filter(type => type !== 'figures'), ['ready']);
  page.draw(sample('rocket'));
  const finished = page.runUntil('finished');
  assert.ok(finished, 'the page never said finished');
  assert.deepEqual(errors(page), []);
  assert.equal(page.sent.filter(message => message.type === 'line').length, 3);
  // Every stroke is ink with an outline, and its mask is gone once it is drawn.
  assert.ok(page.withClass('ink').length >= 9);
  assert.ok(page.withClass('ink').every(node => node.attributes.d.endsWith('Z') && node.attributes.fill));
  assert.equal(page.nodes.stage.descendants().filter(node => node.tagName === 'mask').length, 0);
  assert.equal(page.nodes.stage.descendants().filter(node => node.attributes.mask).length, 0);
  // The pen's ease on every stroke.
  for (const animation of page.animations.filter(a => a.node.attributes.class === 'reveal')) assert.equal(animation.easing, 'cubic-bezier(0.35,0.2,0.55,0.95)');
  // The last ink dries just before the last line ends, and the page says finished as it does.
  const lastInk = Math.max(...page.inkAnimations().map(a => a.end));
  assert.ok(finished.at - lastInk > 0 && finished.at - lastInk < 0.4, `ink ended ${finished.at - lastInk} s before finished`);
  // No sound unless the app asks.
  assert.equal(page.audio.contexts, 0);
});

test('the same program is drawn with the same strokes every time', () => {
  const shapes = () => { const page = openStage(); page.draw(sample('rocket')); page.runUntil('finished'); return page.withClass('ink').map(node => node.attributes.d); };
  assert.deepEqual(shapes(), shapes());
});

test('hurried strokes overlap, and a line’s marks end with the line', () => {
  const page = openStage();
  page.draw(strokes);
  assert.ok(page.runUntil('finished'));
  assert.deepEqual(errors(page), []);
  const ink = page.inkAnimations();
  assert.equal(ink.length, 5);
  // "Look." lasts 0.8 s from when it arrives: the four strokes start before the one before has ended.
  const lineOne = page.sent.find(message => message.type === 'line');
  for (let i = 1; i < 4; i++) assert.ok(ink[i].start < ink[i - 1].end - 0.02, `stroke ${i} started at ${ink[i].start}, the one before ended at ${ink[i - 1].end}`);
  const end = lineOne.at + 0.8;
  assert.ok(Math.abs(ink[3].end - (end - 0.12)) < 0.12, `the fourth stroke ended at ${ink[3].end}, the line at ${end}`);
  // The far stroke belongs to the second line: it waits for it, and the cursor flies there first.
  assert.ok(ink[4].start > end + 0.25);
  assert.ok(ink[4].start - ink[4].madeAt > 0.2);
});

test('the pen flies to a far mark, hops to a near one, and is gone when the drawing is finished', () => {
  const page = openStage();
  page.draw(strokes);
  page.runUntil('finished');
  const moves = page.penAnimations();
  const flights = moves.filter(a => a.easing === 'ease-in-out'), follows = moves.filter(a => a.easing !== 'ease-in-out');
  assert.equal(follows.length, 5);
  // A touch-down for the first mark and a flight to the far one; none between the four close together.
  assert.equal(flights.length, 2);
  assert.ok(flights[1].seconds > 0.3 && flights[1].seconds <= 0.5);
  // A flight ends on the stroke's start, where the ink then starts.
  assert.equal(flights[1].keyframes.at(-1).transform, follows[4].keyframes[0].transform);
  assert.ok(Math.abs(flights[1].end - follows[4].start) < 1e-9);
  // A hop: the cursor's first keyframe is where the pen was, then it is on the new stroke.
  assert.equal(follows[1].keyframes[0].offset, 0);
  assert.ok(follows[1].keyframes[1].offset > 0 && follows[1].keyframes[1].offset < 0.1);
  assert.equal(page.pen.children[0].style.opacity, 0);
  // The cursor is in a layer of its own: never among the drawing's own marks.
  assert.equal(page.pen.parentNode, page.nodes.board);
  assert.ok(!page.nodes.stage.descendants().includes(page.pen));
});

test('Reduce Motion: marks appear in place, in step with the lines, with no pen', () => {
  for (const open of [() => openStage({ systemReducesMotion: true }), () => openStage({ search: '?motion=reduce' })]) {
    const page = open();
    page.draw(sample('rocket'));
    assert.ok(page.runUntil('finished'));
    assert.deepEqual(errors(page), []);
    assert.equal(page.made.filter(node => node.tagName === 'mask' || node.tagName === 'clipPath').length, 0);
    assert.equal(page.penAnimations().length, 0);
    // Nothing moves: every animation is a fade.
    const drawn = page.animations.filter(a => a.node.parentNode !== page.pen);
    assert.ok(drawn.length > 10);
    for (const animation of drawn) assert.ok(animation.keyframes.every(frame => !('transform' in frame) && !('strokeDashoffset' in frame) && !('width' in frame)), JSON.stringify(animation.keyframes));
    // Still spread over the lines: the last mark appears in the last line's time, not all at once.
    assert.ok(Math.max(...drawn.map(a => a.start)) > 10);
  }
});

test('the app’s word on Reduce Motion beats the system’s, either way', () => {
  const asked = openStage();
  asked.draw(strokes, { reduceMotion: true });
  asked.runUntil('finished');
  assert.equal(asked.penAnimations().length, 0);
  const refused = openStage({ systemReducesMotion: true });
  refused.draw(strokes, { reduceMotion: false });
  refused.runUntil('finished');
  assert.equal(refused.penAnimations().filter(a => a.easing !== 'ease-in-out').length, 5);
});

test('sound is scheduled only when asked for, follows each stroke, and the audio clock stops afterwards', () => {
  const page = openStage();
  page.draw(strokes, { sound: true });
  const finished = page.runUntil('finished');
  assert.equal(page.audio.contexts, 1);
  const gains = page.audio.curves.filter(curve => curve.kind === 'gain'), tones = page.audio.curves.filter(curve => curve.kind === 'frequency');
  assert.ok(gains.length >= 4 && gains.length <= 5, `${gains.length} beds`); // at most four at once: a fifth overlapping stroke is left out
  assert.equal(tones.length, gains.length);
  const ink = page.inkAnimations();
  for (const bed of gains) {
    // Silent at both ends, loudest in between, quiet overall.
    assert.equal(bed.curve[0], 0);
    assert.equal(bed.curve.at(-1), 0);
    assert.ok(Math.max(...bed.curve) > 0 && Math.max(...bed.curve) <= 0.07 + 1e-9);
    // In step with a stroke's ink.
    assert.ok(ink.some(a => Math.abs(a.start + 0.02 - bed.at) < 0.001 && Math.abs(a.seconds - bed.seconds) < 0.001), `no stroke at ${bed.at}`);
  }
  assert.equal(page.audio.state, 'running');
  page.run(1.5);
  assert.equal(page.audio.state, 'suspended');
  assert.ok(finished);

  const quiet = openStage({ search: '?sound=1' });
  quiet.draw(sample('rocket'));
  quiet.runUntil('finished');
  assert.ok(quiet.audio.curves.length > 0, 'a preview can ask for sound in its address');
  assert.ok(quiet.audio.ticks >= 3, 'a card, a dot or an arrow head lands with a tick');

  const off = openStage({ search: '?sound=1' });
  off.draw(strokes, { sound: false });
  off.runUntil('finished');
  assert.equal(off.audio.contexts, 0);
});

test('a page without Web Audio still draws when asked for sound', () => {
  const page = openStage({ audio: false });
  page.draw(strokes, { sound: true });
  assert.ok(page.runUntil('finished'));
  assert.deepEqual(errors(page), []);
});

test('finishNow draws what is left at once, without the pen or its sound', () => {
  const page = openStage();
  page.draw(sample('rocket'), { sound: true });
  page.run(2);
  const beds = page.audio.curves.length, moves = page.penAnimations().length, at = page.time;
  page.handle({ type: 'finishNow' });
  const finished = page.runUntil('finished');
  assert.ok(finished && finished.at - at < 4, `finished ${finished && finished.at - at} s after finishNow`);
  assert.equal(page.audio.curves.length, beds);
  assert.equal(page.penAnimations().length, moves);
  assert.ok(page.withClass('ink').length >= 9);
});

test('on the voice’s clock each line’s marks wait for the line and finished waits for the voice', () => {
  const page = openStage();
  page.draw(strokes, { pace: 'speech' });
  page.run(1);
  assert.equal(page.sent.filter(message => message.type === 'lineDue').length, 1);
  assert.equal(page.inkAnimations().length, 0);
  page.handle({ type: 'lineStarted', k: 0 });
  page.run(1.5);
  assert.equal(page.inkAnimations().length, 4);
  page.handle({ type: 'lineEnded', k: 0 });
  page.run(0.5);
  assert.equal(page.inkAnimations().length, 4);
  page.handle({ type: 'lineStarted', k: 1 });
  page.run(2);
  assert.equal(page.inkAnimations().length, 5);
  assert.ok(!types(page).includes('finished'));
  page.handle({ type: 'lineEnded', k: 1 });
  assert.ok(page.runUntil('finished', 2));
});

test('a follow-up keeps the drawing and takes the sound setting it is given', () => {
  const page = openStage();
  page.draw(strokes);
  page.runUntil('finished');
  const before = page.withClass('ink').length;
  page.handle({ type: 'continueStream', sound: true });
  page.handle({ type: 'feed', code: 'step(3); say("One more.")\nline([[100, 300], [500, 300]])', done: true });
  assert.ok(page.runUntil('finished'));
  assert.equal(page.withClass('ink').length, before + 1);
  assert.equal(page.audio.contexts, 1);
  assert.deepEqual(errors(page), []);
});

test('dashed strokes, filled shapes and arrows are ink too', () => {
  const page = openStage();
  page.draw(`
step(1); say("A dashed line, a filled box, a circle and an arrow.")
line([[100, 100], [400, 100]], {dashed: true})
rect(100, 200, 200, 100, {fill: 'yellow'})
circle(600, 300, 60)
arrow([[100, 400], [300, 400]])
dot(700, 500, 6)
highlight(100, 500, 200, 30)
text(400, 600, 'words')
`);
  assert.ok(page.runUntil('finished'));
  assert.deepEqual(errors(page), []);
  const ink = page.withClass('ink');
  assert.equal(ink.length, 6); // the line, the box, the circle, the arrow's shaft and its two flicks
  assert.ok(ink[0].attributes.d.split('M').length - 1 > 10, 'a dashed stroke is many short outlines');
  assert.equal(page.withClass('fade').length, 1);
  assert.equal(page.withClass('halo').length, 6);
});
