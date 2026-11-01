// OpenClicky drawing stage: 3D figures (phase 2), with three.js r172 (vendor/three, MIT).
//
// A program's figure(x, y, w, h) is a little 3D stage on the canvas; its parts (box, cyl, cone, sphere, torus,
// capsule, tube, lathe, extrude, spring, gear, arrow) are built from real geometry with real materials (steel,
// glass, rubber, glowing…) and grow in while their line is spoken. Labels follow their part on screen with a
// leader line; motions (spin, bob, stroke, pulse, orbit, move) keep a machine running. stage.js hands each mark
// here through window.Figure3D.apply as the pacer releases it.
import * as THREE from './vendor/three/three.module.min.js';
import { RoomEnvironment } from './vendor/three/RoomEnvironment.js';

const layer = document.getElementById('figures');
const labelLayer = document.getElementById('labels3d');
const SVG_NS = 'http://www.w3.org/2000/svg';

const figures = new Map();
const parts = new Map();
const clock = { last: performance.now() / 1000 };

// Colours as real things have them (painted steel, rubber, brick), not the pen's bright inks.
const TONES = {
  ink: '#2C2E33', black: '#1E1F22', white: '#E8E6E1', grey: '#8B8E93', silver: '#C9CCD1', red: '#B83A33', orange: '#D9782F',
  yellow: '#E3B83B', green: '#4E8A57', teal: '#2F8F8C', blue: '#3A64A8', purple: '#7E62B8', pink: '#D07B92', brown: '#7A5235',
  gold: '#D4AF37', copper: '#B87333', brass: '#B5A642', cream: '#E4D9C2', skin: '#E0AC8E',
};
const MATERIALS = {
  steel: { color: '#B9BEC6', metalness: 0.9, roughness: 0.32 }, metal: { color: '#B9BEC6', metalness: 0.9, roughness: 0.35 },
  chrome: { color: '#DDE1E6', metalness: 1, roughness: 0.08 }, iron: { color: '#5C6168', metalness: 0.8, roughness: 0.55 },
  alu: { color: '#D3D6DA', metalness: 0.85, roughness: 0.28 }, gold: { color: '#D4AF37', metalness: 1, roughness: 0.25 },
  copper: { color: '#B87333', metalness: 1, roughness: 0.3 }, brass: { color: '#B5A642', metalness: 1, roughness: 0.3 },
  plastic: { color: '#9AA0A6', metalness: 0, roughness: 0.45 }, paint: { color: '#B83A33', metalness: 0.1, roughness: 0.35 },
  matte: { color: '#9AA0A6', metalness: 0, roughness: 0.85 }, rubber: { color: '#26272B', metalness: 0, roughness: 0.92 },
  wood: { color: '#9A6B3F', metalness: 0, roughness: 0.8 }, ceramic: { color: '#EDEAE4', metalness: 0, roughness: 0.25 },
  soft: { color: '#E0AC8E', metalness: 0, roughness: 0.7 },
};

const num = (v, d) => (typeof v === 'number' && isFinite(v) ? v : d);
const vec3 = (p, d) => (Array.isArray(p) && p.length >= 3 && p.every(n => typeof n === 'number' && isFinite(n)) ? new THREE.Vector3(p[0], p[1], p[2]) : d);
const colorOf = (name, fallback) => new THREE.Color(TONES[name] || (typeof name === 'string' && /^#[0-9a-f]{6}$/i.test(name) ? name : fallback));

function material(o) {
  const [kind, tint] = String(o.mat || 'plastic').toLowerCase().split(':').map(s => s.trim());
  const named = o.color || tint;
  if (kind === 'glass' || kind === 'water') {
    return new THREE.MeshPhysicalMaterial({
      color: colorOf(named, kind === 'water' ? '#5FA8D3' : '#CFE6F2'), metalness: 0, roughness: 0.05, transparent: true,
      opacity: num(o.opacity, kind === 'water' ? 0.5 : 0.28), depthWrite: false, side: THREE.DoubleSide, envMapIntensity: 1.2,
    });
  }
  if (kind === 'glow') {
    const c = colorOf(named, '#FF9A3C');
    return new THREE.MeshStandardMaterial({ color: c, emissive: c, emissiveIntensity: 1.4, roughness: 0.4 });
  }
  const preset = MATERIALS[kind] || MATERIALS.plastic;
  const known = kind in MATERIALS;
  const m = new THREE.MeshStandardMaterial({
    color: colorOf(named || (known ? null : kind), preset.color), metalness: preset.metalness, roughness: preset.roughness,
  });
  if (num(o.opacity, 1) < 1) { m.transparent = true; m.opacity = o.opacity; m.depthWrite = false; }
  return m;
}

// Turns a part built along y to point along `axis` ('x', 'z', '-x' … or [x, y, z]).
function orient(geometry, axis, natural = 'y') {
  if (!axis || axis === natural) return geometry;
  const unit = { x: [1, 0, 0], y: [0, 1, 0], z: [0, 0, 1] };
  let to;
  if (Array.isArray(axis)) to = vec3(axis, null);
  else { const m = /^(-?)([xyz])$/.exec(String(axis)); if (m) to = new THREE.Vector3(...unit[m[2]]).multiplyScalar(m[1] ? -1 : 1); }
  if (!to || to.lengthSq() < 1e-9) return geometry;
  const from = new THREE.Vector3(...unit[natural]);
  return geometry.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(from, to.normalize()));
}

function pointList(points, dims) {
  return (Array.isArray(points) ? points : []).filter(p => Array.isArray(p) && p.length >= dims && p.slice(0, dims).every(n => isFinite(n)));
}

function geometry(kind, o) {
  switch (kind) {
    case 'box': return new THREE.BoxGeometry(num(o.w, 1), num(o.h, 1), num(o.d, 1));
    case 'cyl': return orient(new THREE.CylinderGeometry(num(o.r2, num(o.r, 0.5)), num(o.r, 0.5), num(o.h, 1), 48, 1, !!o.open), o.axis);
    case 'cone': return orient(new THREE.ConeGeometry(num(o.r, 0.5), num(o.h, 1), 48), o.axis);
    case 'sphere': return new THREE.SphereGeometry(num(o.r, 0.5), 48, 32);
    case 'torus': return orient(new THREE.TorusGeometry(num(o.r, 1), num(o.tube, 0.15), 24, 96).rotateX(Math.PI / 2), o.axis);
    case 'capsule': return orient(new THREE.CapsuleGeometry(num(o.r, 0.3), num(o.h, 1), 8, 24), o.axis);
    case 'tube': {
      const pts = pointList(o.points, 3).map(p => new THREE.Vector3(p[0], p[1], p[2]));
      if (pts.length < 2) return null;
      const curve = new THREE.CatmullRomCurve3(pts, !!o.closed);
      return new THREE.TubeGeometry(curve, Math.max(32, pts.length * 20), num(o.r, 0.08), 16, !!o.closed);
    }
    case 'lathe': {
      const pts = pointList(o.points, 2).map(p => new THREE.Vector2(Math.max(0, p[0]), p[1]));
      return pts.length >= 2 ? orient(new THREE.LatheGeometry(pts, 64), o.axis) : null;
    }
    case 'extrude': {
      const pts = pointList(o.points, 2);
      if (pts.length < 3) return null;
      const shape = new THREE.Shape(pts.map(p => new THREE.Vector2(p[0], p[1])));
      const depth = num(o.depth, 0.3);
      return new THREE.ExtrudeGeometry(shape, { depth, bevelEnabled: true, bevelSize: 0.015, bevelThickness: 0.015, bevelSegments: 2 }).translate(0, 0, -depth / 2);
    }
    case 'spring': {
      const coils = Math.max(1, Math.min(60, num(o.coils, 8))), r = num(o.r, 0.4), h = num(o.h, 2), steps = Math.round(coils * 24);
      const pts = [];
      for (let i = 0; i <= steps; i++) { const t = i / steps, a = t * coils * Math.PI * 2; pts.push(new THREE.Vector3(Math.cos(a) * r, t * h - h / 2, Math.sin(a) * r)); }
      return orient(new THREE.TubeGeometry(new THREE.CatmullRomCurve3(pts), steps * 2, num(o.wire, 0.05), 10, false), o.axis);
    }
    case 'gear': {
      const teeth = Math.max(4, Math.min(80, Math.round(num(o.teeth, 16)))), r = num(o.r, 1), depth = num(o.depth, 0.3);
      const root = r * 0.84, shape = new THREE.Shape();
      for (let i = 0; i < teeth; i++) {
        const a = (i / teeth) * Math.PI * 2, s = (Math.PI * 2) / teeth;
        const corners = [[root, a], [r, a + s * 0.15], [r, a + s * 0.45], [root, a + s * 0.6]];
        corners.forEach(([rad, ang], k) => (i === 0 && k === 0 ? shape.moveTo : shape.lineTo).call(shape, Math.cos(ang) * rad, Math.sin(ang) * rad));
      }
      shape.closePath();
      const hole = new THREE.Path().absarc(0, 0, num(o.hole, r * 0.2), 0, Math.PI * 2, true);
      shape.holes.push(hole);
      const g = new THREE.ExtrudeGeometry(shape, { depth, bevelEnabled: false, curveSegments: 4 }).translate(0, 0, -depth / 2);
      return orient(g, o.axis, 'z');
    }
    default: return null;
  }
}

// A straight 3D arrow from `from` to `to` (a shaft and a head), in one colour.
function arrowObject(o) {
  const from = vec3(o.from, new THREE.Vector3()), to = vec3(o.to, new THREE.Vector3(0, 1, 0));
  const dir = to.clone().sub(from), length = dir.length();
  if (length < 1e-6) return null;
  const r = num(o.r, length * 0.025), head = Math.min(length * 0.35, r * 6);
  const mat = new THREE.MeshStandardMaterial({ color: colorOf(o.color, '#D9782F'), emissive: colorOf(o.color, '#D9782F'), emissiveIntensity: 0.35, roughness: 0.5 });
  const group = new THREE.Group();
  const shaft = new THREE.Mesh(new THREE.CylinderGeometry(r, r, length - head, 16).translate(0, (length - head) / 2, 0), mat);
  const tip = new THREE.Mesh(new THREE.ConeGeometry(r * 2.6, head, 24).translate(0, length - head / 2, 0), mat);
  group.add(shaft, tip);
  group.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.normalize());
  group.position.copy(from);
  return group;
}

// The pen's outline on every edge, so a part reads as drawn as well as built.
function withInk(mesh, geometryValue) {
  const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometryValue, 28),
    new THREE.LineBasicMaterial({ color: 0x1e2230, transparent: true, opacity: 0.35 }));
  mesh.add(edges);
  return mesh;
}

const easeOutBack = t => { const c = 1.6; return 1 + (c + 1) * Math.pow(t - 1, 3) + c * Math.pow(t - 1, 2); };

// ------------------------------------------------------------------ figures
function createFigure(m, rect, seconds) {
  const canvas = document.createElement('canvas');
  Object.assign(canvas.style, { left: `${rect.x}px`, top: `${rect.y}px`, width: `${rect.w}px`, height: `${rect.h}px`, opacity: '0' });
  layer.appendChild(canvas);
  const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true });
  renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
  renderer.setSize(rect.w, rect.h, false);
  renderer.setClearColor(0x000000, 0);
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  const scene = new THREE.Scene();
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  const sun = new THREE.DirectionalLight(0xffffff, 1.2);
  sun.position.set(3, 6, 4);
  scene.add(sun, new THREE.HemisphereLight(0xffffff, 0x50525a, 0.5));
  const camera = new THREE.PerspectiveCamera(num(m.fov, 35), rect.w / rect.h, 0.01, 2000);
  const root = new THREE.Group();
  scene.add(root);
  const figure = {
    id: m.id, canvas, renderer, scene, camera, root, rect, pmrem, labels: [],
    tilt: num(m.tilt, 18), turn: num(m.turn, -28), spin: num(m.spin, 0), sway: m.spin ? 0 : 32, zoom: num(m.zoom, 1),
    center: new THREE.Vector3(), radius: 1, framed: false, age: 0,
  };
  figures.set(m.id, figure);
  canvas.animate([{ opacity: 0 }, { opacity: 1 }], { duration: Math.max(150, seconds * 1000), fill: 'forwards' });
}

function addPart(m, seconds) {
  const figure = figures.get(m.fig);
  if (!figure) return;
  const o = m.params || {};
  let object;
  if (m.kind === 'arrow') object = arrowObject(o);
  else {
    const g = geometry(m.kind, o);
    if (g) object = withInk(new THREE.Mesh(g, material(o)), g);
  }
  if (!object) return;
  const placed = new THREE.Group();
  placed.add(object);
  if (m.kind !== 'arrow') placed.position.copy(vec3(o.at, vec3(o.pos, new THREE.Vector3())));
  const rot = vec3(o.rot, null);
  if (rot) placed.rotation.set(THREE.MathUtils.degToRad(rot.x), THREE.MathUtils.degToRad(rot.y), THREE.MathUtils.degToRad(rot.z));
  const scale = typeof o.scale === 'number' ? new THREE.Vector3(o.scale, o.scale, o.scale) : vec3(o.scale, new THREE.Vector3(1, 1, 1));
  const parent = o.parent && parts.get(o.parent);
  (parent ? parent.object : figure.root).add(placed);
  const part = { id: m.id, figure, object: placed, base: { position: placed.position.clone(), scale, rotation: placed.rotation.clone() }, motions: [], grow: { start: figure.age, seconds: Math.max(0.15, seconds) } };
  placed.scale.setScalar(1e-3);
  parts.set(m.id, part);
}

function addLabel(m, seconds) {
  const part = parts.get(m.part);
  const figure = figures.get(m.fig) || (part && part.figure);
  if (!figure) return;
  const group = document.createElementNS(SVG_NS, 'g');
  const line = document.createElementNS(SVG_NS, 'line');
  const text = document.createElementNS(SVG_NS, 'text');
  const tone = { ink: '#1E2230', blue: '#2F6BFF', red: '#E5484D', green: '#25A55F', orange: '#F28C28', purple: '#7C5CD6' };
  const color = tone[m.color] || (typeof m.color === 'string' && /^#[0-9a-f]{6}$/i.test(m.color) ? m.color : tone.ink);
  line.setAttribute('stroke', color);
  line.setAttribute('stroke-width', '2');
  line.setAttribute('stroke-linecap', 'round');
  text.setAttribute('fill', color);
  text.setAttribute('font-size', String(num(m.size, 22)));
  text.setAttribute('dominant-baseline', 'middle');
  text.textContent = m.text;
  group.append(line, text);
  labelLayer.appendChild(group);
  group.animate([{ opacity: 0 }, { opacity: 1 }], { duration: Math.max(150, seconds * 1000), fill: 'forwards' });
  figure.labels.push({ part, at: vec3(m.at, null), group, line, text });
}

function addMotion(m) {
  const part = parts.get(m.part);
  if (!part) return;
  const o = m.params || {};
  if (m.kind === 'move') {
    part.move = { from: part.base.position.clone(), to: vec3(o.to, part.base.position.clone()), start: part.figure.age, seconds: num(o.secs, 0.8) };
    return;
  }
  part.motions.push({ kind: m.kind, axis: ['x', 'y', 'z'].includes(o.axis) ? o.axis : 'y', period: Math.max(0.2, num(o.period, m.kind === 'spin' ? 3 : 2)),
    amount: num(o.amount, m.kind === 'pulse' ? 0.08 : 0.3), center: vec3(o.center, new THREE.Vector3()), start: part.figure.age, reverse: !!o.reverse });
}

function setView(m) {
  const figure = figures.get(m.fig);
  if (!figure) return;
  if (isFinite(m.tilt)) figure.tilt = Math.max(-80, Math.min(80, m.tilt));
  if (isFinite(m.turn)) figure.turn = m.turn;
  if (isFinite(m.zoom)) figure.zoom = Math.max(0.3, Math.min(3, m.zoom));
  if (isFinite(m.spin)) { figure.spin = m.spin; figure.sway = m.spin ? 0 : 32; }
}

// ------------------------------------------------------------------ every frame
const box = new THREE.Box3(), sphere = new THREE.Sphere(), point = new THREE.Vector3();

function updatePart(part, t) {
  const object = part.object;
  const grown = part.grow ? Math.min(1, (t - part.grow.start) / part.grow.seconds) : 1;
  if (grown >= 1) part.grow = null;
  const g = Math.max(1e-3, easeOutBack(grown));
  let position = part.base.position.clone();
  if (part.move) {
    const k = Math.min(1, (t - part.move.start) / part.move.seconds), e = k * k * (3 - 2 * k);
    position = part.move.from.clone().lerp(part.move.to, e);
    if (k >= 1) { part.base.position.copy(part.move.to); part.move = null; }
  }
  let scale = 1;
  object.rotation.copy(part.base.rotation);
  for (const motion of part.motions) {
    const phase = ((t - motion.start) / motion.period) * Math.PI * 2;
    switch (motion.kind) {
      case 'spin': object.rotation[motion.axis] += (motion.reverse ? -1 : 1) * phase; break;
      case 'bob': position[motion.axis] += Math.sin(phase) * motion.amount; break;
      case 'stroke': position[motion.axis] += (0.5 - 0.5 * Math.cos(phase)) * motion.amount; break;
      case 'pulse': scale *= 1 + Math.sin(phase) * motion.amount; break;
      case 'orbit': {
        const offset = position.clone().sub(motion.center).applyAxisAngle(new THREE.Vector3(motion.axis === 'x' ? 1 : 0, motion.axis === 'y' ? 1 : 0, motion.axis === 'z' ? 1 : 0), phase);
        position = motion.center.clone().add(offset);
        break;
      }
    }
  }
  object.position.copy(position);
  object.scale.copy(part.base.scale).multiplyScalar(g * scale);
}

function updateFigure(figure, dt) {
  figure.age += dt;
  const t = figure.age;
  for (const part of parts.values()) if (part.figure === figure) updatePart(part, t);
  const sway = figure.sway ? Math.sin((t / 14) * Math.PI * 2) * figure.sway : 0;
  figure.root.rotation.y = THREE.MathUtils.degToRad(sway) + figure.spin * t * Math.PI * 2 / 60;
  // Frame everything built so far, smoothly (a part growing in pulls the camera back a little).
  figure.root.updateMatrixWorld(true);
  box.setFromObject(figure.root);
  if (!box.isEmpty()) {
    box.getBoundingSphere(sphere);
    const k = figure.framed ? 0.08 : 1;
    figure.center.lerp(sphere.center, k);
    figure.radius += (Math.max(0.2, sphere.radius) - figure.radius) * k;
    figure.framed = true;
  }
  const camera = figure.camera;
  const distance = (figure.radius / Math.sin(THREE.MathUtils.degToRad(camera.fov) / 2)) * 1.08 / figure.zoom;
  const tilt = THREE.MathUtils.degToRad(figure.tilt), turn = THREE.MathUtils.degToRad(figure.turn);
  camera.position.set(Math.sin(turn) * Math.cos(tilt), Math.sin(tilt), Math.cos(turn) * Math.cos(tilt)).multiplyScalar(distance).add(figure.center);
  camera.near = distance / 100;
  camera.far = distance * 10;
  camera.updateProjectionMatrix();
  camera.lookAt(figure.center);
  figure.renderer.render(figure.scene, camera);
  placeLabels(figure);
}

function placeLabels(figure) {
  const { rect } = figure;
  const middle = { x: rect.x + rect.w / 2, y: rect.y + rect.h / 2 };
  const placed = [];
  for (const label of figure.labels) {
    if (label.part) {
      if (label.at) point.copy(label.at).applyMatrix4(label.part.object.matrixWorld);
      else { box.setFromObject(label.part.object); box.getCenter(point); }
    } else point.copy(label.at || figure.center);
    point.project(figure.camera);
    const ax = rect.x + (point.x * 0.5 + 0.5) * rect.w, ay = rect.y + (-point.y * 0.5 + 0.5) * rect.h;
    let dx = ax - middle.x, dy = ay - middle.y;
    const length = Math.hypot(dx, dy) || 1;
    dx /= length; dy /= length;
    if (Math.abs(dx) < 0.35) dx = dx < 0 ? -0.35 : 0.35;                   // labels sit to the side, not on top of each other
    const reach = Math.max(60, rect.w * 0.18);
    const lx = Math.max(rect.x - 120, Math.min(rect.x + rect.w + 120, ax + dx * reach));
    const ly = Math.max(rect.y + 12, Math.min(rect.y + rect.h - 12, ay + dy * reach * 0.6));
    placed.push({ label, ax, ay, lx, ly, side: dx > 0 ? 1 : -1 });
  }
  // Labels on the same side keep a line's height apart, so two parts near each other never print on top of each other.
  const gap = 28;
  for (const side of [-1, 1]) {
    const column = placed.filter(p => p.side === side).sort((a, b) => a.ly - b.ly);
    for (let i = 1; i < column.length; i++) column[i].ly = Math.max(column[i].ly, column[i - 1].ly + gap);
    const overflow = column.length ? column[column.length - 1].ly - (rect.y + rect.h - 12) : 0;
    if (overflow > 0) for (const p of column) p.ly -= overflow;
  }
  for (const { label, ax, ay, lx, ly, side } of placed) {
    label.line.setAttribute('x1', ax); label.line.setAttribute('y1', ay);
    label.line.setAttribute('x2', lx + side * -4); label.line.setAttribute('y2', ly);
    label.text.setAttribute('x', lx + side * 4);
    label.text.setAttribute('y', ly);
    label.text.setAttribute('text-anchor', side > 0 ? 'start' : 'end');
  }
}

function frame(nowMs) {
  const now = nowMs / 1000, dt = Math.min(0.1, Math.max(0, now - clock.last));
  clock.last = now;
  for (const figure of figures.values()) updateFigure(figure, dt);
  requestAnimationFrame(frame);
}
requestAnimationFrame(frame);

// ------------------------------------------------------------------ what stage.js calls
window.Figure3D = {
  /** One released mark: rect is the figure's box in page pixels (figure marks only). */
  apply(m, seconds, rect) {
    switch (m.do) {
      case 'figure': createFigure(m, rect, seconds); break;
      case 'part3d': addPart(m, seconds); break;
      case 'label3d': addLabel(m, seconds); break;
      case 'motion3d': addMotion(m); break;
      case 'view3d': setView(m); break;
    }
  },
  clear() {
    for (const figure of figures.values()) {
      figure.renderer.dispose();
      figure.pmrem.dispose();
      figure.canvas.remove();
      for (const label of figure.labels) label.group.remove();
    }
    figures.clear();
    parts.clear();
  },
};
