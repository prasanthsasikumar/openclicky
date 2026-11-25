// OpenClicky drawing stage: the pen's sound, made here (Web Audio), with no audio file anywhere.
//
// Off unless the app asks for it (beginStream {sound}, or ?sound=1 in a preview). Two sounds:
//   a bed    a quiet hiss while a stroke is drawn, whose level and brightness follow the pen's speed through the
//            stroke (StagePen.soundCurves), so it starts, swells and stops with the ink;
//   a tick   a soft tap when a mark lands (a dot, a card, a photo, a 3D part, an arrow's head).
// The audio clock only runs while something is drawn: rest() (the drawing finished, the stage hidden or wiped)
// suspends it, so a silent stage keeps no audio device awake.
'use strict';

(function () {
  const Pen = window.StagePen;
  const BED_LEVEL = 0.07, TICK_LEVEL = 0.05, MOST_VOICES = 4;
  let context = null, master = null, hiss = null, level = 0, voices = 0, restTimer = 0;

  // Two seconds of soft noise, the same every time, its end faded into its start so the loop has no seam.
  function hissBuffer() {
    const rate = context.sampleRate, n = Math.floor(rate * 2), fade = Math.floor(rate * 0.05);
    const buffer = context.createBuffer(1, n, rate), data = buffer.getChannelData(0), rnd = Pen.rng(7);
    let low = 0;
    for (let i = 0; i < n + fade; i++) {
      // White noise leaned towards the low end (one pole), so the hiss is paper rather than static.
      low += 0.35 * ((rnd() * 2 - 1) - low);
      if (i < n) data[i] = low * 1.8;
      else { const k = (i - n) / fade; data[i - n] = data[i - n] * k + low * 1.8 * (1 - k); }
    }
    return buffer;
  }

  function start() {
    if (context) return true;
    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass || !Pen) return false;
    try {
      context = new AudioContextClass();
      master = context.createGain();
      master.gain.value = level;
      master.connect(context.destination);
      hiss = context.createBufferSource();
      hiss.buffer = hissBuffer();
      hiss.loop = true;
      hiss.start();
    } catch {
      context = null;
      return false;
    }
    return true;
  }

  function awake() {
    clearTimeout(restTimer);
    if (!(level > 0) || !start()) return false;
    if (context.state === 'suspended') context.resume().catch(() => {});
    return true;
  }

  // How loud the stage's sounds are, 0 (off) to 1; true is 1.
  function set(value) {
    level = value === true ? 1 : Pen ? Pen.clamp(Number(value) || 0, 0, 1) : 0;
    if (level > 0) clearTimeout(restTimer);
    if (!context) return;
    master.gain.cancelScheduledValues(context.currentTime);
    master.gain.setValueAtTime(level, context.currentTime);
    if (!(level > 0)) rest();
  }

  // A stroke's bed: `speeds` is the pen's speed through it (StagePen.speedCurve), starting `delay` seconds from now.
  function stroke(speeds, seconds, delay, texture) {
    if (!(seconds > 0.05) || voices >= MOST_VOICES || !awake()) return;
    const curves = Pen.soundCurves(speeds, texture), at = context.currentTime + Math.max(0, delay || 0) + 0.02;
    const filter = context.createBiquadFilter(), gain = context.createGain();
    filter.type = 'bandpass';
    filter.Q.value = 0.9;
    gain.gain.value = 0;
    try {
      filter.frequency.setValueCurveAtTime(Float32Array.from(curves.frequency), at, seconds);
      gain.gain.setValueCurveAtTime(Float32Array.from(curves.gain, v => v * BED_LEVEL), at, seconds);
    } catch {
      return;
    }
    hiss.connect(filter);
    filter.connect(gain);
    gain.connect(master);
    voices++;
    setTimeout(() => { voices--; try { hiss.disconnect(filter); } catch { /* already let go */ } gain.disconnect(); }, ((delay || 0) + seconds + 0.2) * 1000);
  }

  // A mark landing, `delay` seconds from now: a short falling tap. `strength` 0 to 1.
  function tick(delay, strength) {
    if (!awake()) return;
    const at = context.currentTime + Math.max(0, delay || 0) + 0.02, peak = TICK_LEVEL * Pen.clamp(strength == null ? 1 : strength, 0, 1);
    const tone = context.createOscillator(), gain = context.createGain();
    tone.type = 'sine';
    tone.frequency.setValueAtTime(1250, at);
    tone.frequency.exponentialRampToValueAtTime(520, at + 0.07);
    gain.gain.setValueAtTime(0, at);
    gain.gain.linearRampToValueAtTime(peak, at + 0.006);
    gain.gain.exponentialRampToValueAtTime(0.0001, at + 0.09);
    tone.connect(gain);
    gain.connect(master);
    tone.start(at);
    tone.stop(at + 0.11);
    tone.onended = () => gain.disconnect();
  }

  // Quiet at once (the voice was stopped): what is scheduled plays into nothing. set() brings the level back.
  function hush() {
    if (!context) return;
    master.gain.cancelScheduledValues(context.currentTime);
    master.gain.setTargetAtTime(0, context.currentTime, 0.03);
  }

  // Nothing more to draw for now: the audio clock stops after `after` seconds (0: at once).
  function rest(after) {
    clearTimeout(restTimer);
    if (!context) return;
    restTimer = setTimeout(() => { if (context && context.state === 'running') context.suspend().catch(() => {}); }, Math.max(0, after || 0) * 1000);
  }

  window.StageSound = { set, stroke, tick, hush, rest, isOn: () => level > 0 };
})();
