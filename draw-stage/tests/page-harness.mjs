// A page for the stage's scripts to run in under node: just enough of a document, an animation clock and a worker
// for stage.js to draw a program from start to finish on a clock the test turns by hand. Nothing is rendered: what
// the page did (its messages to the app, the elements it made, every animation with its start and length, the audio
// it scheduled) is what the tests read. Used by stage-page.test.mjs.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const folder = join(dirname(fileURLToPath(import.meta.url)), '..');
const read = name => readFileSync(join(folder, name), 'utf8');
export const sample = name => read(`samples/${name}.js`);

export function openStage(options = {}) {
  const page = { time: 0, sent: [], animations: [], made: [], audio: { contexts: 0, curves: [], ticks: 0, suspended: 0, state: 'none' } };
  const timers = [];
  let frames = [], timerId = 0;

  class FakeNode {
    constructor(tagName) {
      Object.assign(this, { tagName, attributes: {}, children: [], style: {}, parentNode: null, hidden: false, textContent: '', dataset: {}, disabled: false, className: '', offsetWidth: 0, offsetHeight: 0 });
      this.classList = { toggle() {}, add() {}, remove() {} };
    }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    getAttribute(name) { return name in this.attributes ? this.attributes[name] : null; }
    removeAttribute(name) { delete this.attributes[name]; }
    appendChild(child) { return this.insertBefore(child, null); }
    insertBefore(child, before) {
      if (child.parentNode) child.parentNode.removeChild(child);
      const at = before ? this.children.indexOf(before) : -1;
      if (at < 0) this.children.push(child); else this.children.splice(at, 0, child);
      child.parentNode = this;
      return child;
    }
    removeChild(child) { const at = this.children.indexOf(child); if (at >= 0) this.children.splice(at, 1); child.parentNode = null; return child; }
    remove() { if (this.parentNode) this.parentNode.removeChild(this); }
    get firstChild() { return this.children[0] || null; }
    get lastChild() { return this.children[this.children.length - 1] || null; }
    addEventListener() {}
    setPointerCapture() {}
    descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || (selector.startsWith('.') ? new FakeNode('span') : null); }
    querySelectorAll(selector) { return selector.includes('data-kind') ? this.descendants().filter(node => node.attributes['data-kind']) : []; }
    // Words are measured as half their font size a letter, from their anchor.
    getBBox() {
      const size = Number(this.attributes['font-size']) || 26, width = this.textContent.length * size * 0.5;
      const x = Number(this.attributes.x) || 0, anchor = this.attributes['text-anchor'];
      return { x: anchor === 'end' ? x - width : anchor === 'middle' ? x - width / 2 : x, y: (Number(this.attributes.y) || 0) - size / 2, width, height: size };
    }
    getBoundingClientRect() { return { left: 100, top: 100, width: 400, height: 300 }; }
    getTotalLength() { return 100; }
    animate(keyframes, timing) {
      const o = typeof timing === 'number' ? { duration: timing } : timing || {};
      const animation = { node: this, keyframes, easing: o.easing || 'linear', madeAt: page.time, start: page.time + (o.delay || 0) / 1000, seconds: (o.duration || 0) / 1000, cancelled: false, onfinish: null, done: false };
      animation.end = animation.start + animation.seconds;
      animation.cancel = () => { animation.cancelled = true; };
      page.animations.push(animation);
      return animation;
    }
  }

  const byId = {};
  for (const id of ['stage', 'caption', 'tools', 'paper', 'board', 'grip', 'saving', 'labels3d', 'figures']) byId[id] = new FakeNode(id === 'stage' || id === 'labels3d' ? 'svg' : 'div');
  const body = new FakeNode('body');
  const document = {
    body,
    getElementById: id => byId[id] || null,
    createElementNS: (_, name) => { const node = new FakeNode(name); page.made.push(node); return node; }
  };

  const setTimer = (run, ms) => { timers.push({ id: ++timerId, at: page.time + Math.max(0, ms || 0) / 1000, run }); return timerId; };
  const clearTimer = id => { const at = timers.findIndex(timer => timer.id === id); if (at >= 0) timers.splice(at, 1); };

  // The audio a page would schedule: counted, never played.
  const param = kind => ({
    value: 0, setValueAtTime() {}, cancelScheduledValues() {}, setTargetAtTime() {}, linearRampToValueAtTime() {}, exponentialRampToValueAtTime() {},
    setValueCurveAtTime(curve, at, seconds) { page.audio.curves.push({ kind, curve: Array.from(curve), at, seconds }); }
  });
  class FakeAudioContext {
    constructor() { page.audio.contexts++; page.audio.state = this.state = 'running'; this.sampleRate = 8000; this.destination = {}; }
    get currentTime() { return page.time; }
    createBuffer(_, n) { const data = new Float32Array(n); return { getChannelData: () => data }; }
    createBufferSource() { return { connect() {}, disconnect() {}, start() {} }; }
    createGain() { return { gain: param('gain'), connect() {}, disconnect() {} }; }
    createBiquadFilter() { return { frequency: param('frequency'), Q: param('q'), connect() {}, disconnect() {} }; }
    createOscillator() { return { frequency: param('pitch'), connect() {}, start() { page.audio.ticks++; }, stop() {} }; }
    resume() { page.audio.state = this.state = 'running'; return Promise.resolve(); }
    suspend() { page.audio.suspended++; page.audio.state = this.state = 'suspended'; return Promise.resolve(); }
  }

  // The program's worker: stage-worker.js in a context of its own, its messages passed as copies a moment later.
  class FakeWorker {
    constructor(name) {
      const self = { console: { log() {}, warn() {}, error() {} } };
      self.self = self;
      self.postMessage = data => { const copy = JSON.stringify(data); setTimer(() => { if (!this.terminated && this.onmessage) this.onmessage({ data: JSON.parse(copy) }); }, 1); };
      vm.createContext(self);
      vm.runInContext(read(name), self, { filename: name });
      this.inside = self;
    }
    postMessage(data) { const copy = JSON.stringify(data); setTimer(() => { if (!this.terminated) this.inside.onmessage({ data: JSON.parse(copy) }); }, 1); }
    terminate() { this.terminated = true; }
  }

  const window = {
    document, console: { log() {}, warn() {}, error() {} }, URLSearchParams, Worker: FakeWorker,
    performance: { now: () => page.time * 1000 },
    requestAnimationFrame: run => { frames.push(run); return 0; },
    setTimeout: setTimer, clearTimeout: clearTimer,
    innerWidth: options.width || 1280, innerHeight: options.height || 720,
    location: { search: options.search || '' },
    matchMedia: () => ({ matches: !!options.systemReducesMotion }),
    addEventListener() {},
    // The app's side, as WKWebView gives it: every message the page sends is kept with the time it was sent.
    webkit: { messageHandlers: { stage: { postMessage: message => page.sent.push(Object.assign({ at: page.time }, message)) } } }
  };
  if (options.audio !== false) window.AudioContext = FakeAudioContext;
  window.window = window;
  vm.createContext(window);
  for (const script of ['stage-pen.js', 'stage-sound.js', 'stage.js']) vm.runInContext(read(script), window, { filename: script });

  // Turns the clock on by `seconds`, a sixtieth at a time: timers, then the frame, then the animations that ended.
  page.run = seconds => {
    const until = page.time + seconds;
    while (page.time < until) {
      page.time += 1 / 60;
      for (let due = timers.filter(timer => timer.at <= page.time); due.length; due = timers.filter(timer => timer.at <= page.time)) {
        for (const timer of due) { clearTimer(timer.id); timer.run(); }
      }
      const now = frames;
      frames = [];
      for (const run of now) run(page.time * 1000);
      for (const animation of page.animations) {
        if (!animation.done && !animation.cancelled && animation.end <= page.time) { animation.done = true; if (animation.onfinish) animation.onfinish(); }
      }
    }
  };
  page.runUntil = (type, limit = 120) => {
    const from = page.sent.length;
    for (let waited = 0; waited < limit; waited += 0.1) {
      page.run(0.1);
      const found = page.sent.slice(from).find(message => message.type === type);
      if (found) return found;
    }
    return null;
  };
  page.handle = message => window.drawStage.handle(message);
  page.draw = (program, begin = {}) => {
    page.handle(Object.assign({ type: 'beginStream', w: 1280, h: 720 }, begin));
    page.handle({ type: 'feed', code: program, done: true });
  };
  page.nodes = byId;
  page.withClass = name => page.made.filter(node => node.attributes.class === name);
  // The animations that draw ink: a stroke's reveal (the path in its mask) and the sweep that uncovers words.
  page.inkAnimations = () => page.animations.filter(a => a.node.attributes.class === 'reveal' || (a.node.tagName === 'rect' && a.node.parentNode && a.node.parentNode.tagName === 'clipPath'));
  page.penAnimations = () => page.animations.filter(a => a.node.parentNode === page.pen && a.keyframes[0].transform);
  page.pen = page.made.find(node => node.attributes.id === 'pen');
  return page;
}
