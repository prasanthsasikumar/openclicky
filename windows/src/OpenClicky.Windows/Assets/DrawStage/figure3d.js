// OpenClicky drawing stage: 3D figures (phase 2), with three.js r172 (vendor/three, MIT).
//
// A program's figure(x, y, w, h) is a little 3D stage on the canvas; its parts (box, cyl, cone, sphere, torus,
// capsule, tube, pipe, lathe, extrude, spring, gear, blob, arrow) are built from real geometry with real materials
// (steel, glass, rubber, tissue, glowing…) and grow in while their line is spoken. A part can be cut open to show
// its inside, repeated (copies, around an axis, mirrored), and every part has an ink outline, so a figure reads as
// drawn as well as built. A studio look (soft key light with shadows on a ground, a rim light behind) keeps it solid
// on the card. Labels are little tags that follow their part with a leader line; motions (spin, bob, stroke, pulse,
// orbit, move) keep a machine running. stage.js hands each mark here through window.Figure3D.apply as the pacer
// releases it.
//
// A piece (f.piece({make, size})) is made by the app (a 3D generator, or the drawing model from parts): it shows as a
// faint box of its size until the app sends it (Figure3D.pieceArrived), then the real shape is fitted into that box,
// and from then on it is a part like any other.
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
  alu: { color: '#D3D6DA', metalness: 0.85, roughness: 0.28 }, titanium: { color: '#8C939C', metalness: 0.8, roughness: 0.3 }, gold: { color: '#D4AF37', metalness: 1, roughness: 0.25 },
  copper: { color: '#B87333', metalness: 1, roughness: 0.3 }, brass: { color: '#B5A642', metalness: 1, roughness: 0.3 },
  plastic: { color: '#9AA0A6', metalness: 0, roughness: 0.45 }, paint: { color: '#B83A33', metalness: 0.1, roughness: 0.35 },
  matte: { color: '#9AA0A6', metalness: 0, roughness: 0.85 }, rubber: { color: '#26272B', metalness: 0, roughness: 0.92 },
  wood: { color: '#9A6B3F', metalness: 0, roughness: 0.8 }, ceramic: { color: '#EDEAE4', metalness: 0, roughness: 0.25 },
  soft: { color: '#E0AC8E', metalness: 0, roughness: 0.7 }, stone: { color: '#A39E93', metalness: 0, roughness: 0.9 },
};
// Living and soft things: a wet sheen (clearcoat) and the soft glow light picks up at their edges (sheen).
const ORGANIC = {
  tissue: { color: '#C4505A', roughness: 0.42, clearcoat: 0.5, clearcoatRoughness: 0.35, sheen: 0.5, sheenColor: '#FFB3B3' },
  muscle: { color: '#A8323C', roughness: 0.5, clearcoat: 0.3, clearcoatRoughness: 0.4, sheen: 0.6, sheenColor: '#FF9C9C' },
  skin: { color: '#E0AC8E', roughness: 0.55, sheen: 0.4, sheenColor: '#FFE0D0' },
  bone: { color: '#E9E1CC', roughness: 0.6, clearcoat: 0.1 },
  leaf: { color: '#4E8A57', roughness: 0.5, clearcoat: 0.2, sheen: 0.4, sheenColor: '#C8F0B0' },
  pcb: { color: '#2E7D4F', roughness: 0.35, clearcoat: 0.6, clearcoatRoughness: 0.2 },
  fabric: { color: '#5C6B8A', roughness: 0.95, sheen: 0.8, sheenColor: '#FFFFFF' },
};
const INK = 0x1e2230;

const num = (v, d) => (typeof v === 'number' && isFinite(v) ? v : d);
// A size: a model's negative or zero size is read as its size, or the default.
const size = (v, d) => (typeof v === 'number' && isFinite(v) && Math.abs(v) > 1e-4 ? Math.abs(v) : d);
const vec3 = (p, d) => (Array.isArray(p) && p.length >= 3 && p.every(n => typeof n === 'number' && isFinite(n)) ? new THREE.Vector3(p[0], p[1], p[2]) : d);
const colorOf = (name, fallback) => new THREE.Color(TONES[name] || (typeof name === 'string' && /^#[0-9a-f]{6}$/i.test(name) ? name : fallback));
const AXES = { x: new THREE.Vector3(1, 0, 0), y: new THREE.Vector3(0, 1, 0), z: new THREE.Vector3(0, 0, 1) };

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
    const m = new THREE.MeshStandardMaterial({ color: c, emissive: c, emissiveIntensity: 1.4, roughness: 0.4 });
    m.userData.glow = true;
    return m;
  }
  let m;
  if (kind in ORGANIC) {
    const preset = ORGANIC[kind];
    m = new THREE.MeshPhysicalMaterial({
      color: colorOf(named, preset.color), metalness: 0, roughness: preset.roughness, clearcoat: preset.clearcoat || 0,
      clearcoatRoughness: preset.clearcoatRoughness || 0.3, sheen: preset.sheen || 0, sheenRoughness: 0.5,
      sheenColor: new THREE.Color(preset.sheenColor || '#FFFFFF'),
    });
  } else {
    const preset = MATERIALS[kind] || MATERIALS.plastic;
    const known = kind in MATERIALS;
    m = new THREE.MeshStandardMaterial({
      color: colorOf(named || (known ? null : kind), preset.color), metalness: preset.metalness, roughness: preset.roughness,
    });
  }
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
    case 'box': {
      const w = size(o.w, 1), h = size(o.h, 1), d = size(o.d, 1), round = num(o.round, 0);
      return round > 0 ? roundedBox(w, h, d, round) : new THREE.BoxGeometry(w, h, d);
    }
    case 'cyl': return orient(new THREE.CylinderGeometry(size(o.r2, size(o.r, 0.5)), size(o.r, 0.5), size(o.h, 1), 48, 1, !!o.open), o.axis);
    case 'cone': return orient(new THREE.ConeGeometry(size(o.r, 0.5), size(o.h, 1), 48), o.axis);
    case 'sphere': return new THREE.SphereGeometry(size(o.r, 0.5), 64, 40);
    case 'torus': return orient(new THREE.TorusGeometry(size(o.r, 1), size(o.tube, 0.15), 24, 96).rotateX(Math.PI / 2), o.axis);
    case 'capsule': return orient(new THREE.CapsuleGeometry(size(o.r, 0.3), size(o.h, 1), 12, 32), o.axis);
    case 'tube': {
      const pts = pointList(o.points, 3).slice(0, 200).map(p => new THREE.Vector3(p[0], p[1], p[2]));
      if (pts.length < 2) return null;
      const curve = new THREE.CatmullRomCurve3(pts, !!o.closed, 'centripetal');
      return new THREE.TubeGeometry(curve, Math.min(800, Math.max(32, pts.length * 20)), size(o.r, 0.08), 16, !!o.closed);
    }
    case 'pipe': return pipeGeometry(o);
    case 'lathe': {
      const pts = pointList(o.points, 2).slice(0, 200).map(p => new THREE.Vector2(Math.max(0, p[0]), p[1]));
      return pts.length >= 2 ? orient(new THREE.LatheGeometry(pts, 64), o.axis) : null;
    }
    case 'extrude': {
      const pts = pointList(o.points, 2).slice(0, 400);
      if (pts.length < 3) return null;
      const shape = new THREE.Shape(pts.map(p => new THREE.Vector2(p[0], p[1])));
      for (const hole of Array.isArray(o.holes) ? o.holes.slice(0, 20) : []) {
        const hp = pointList(hole, 2);
        if (hp.length >= 3) shape.holes.push(new THREE.Path(hp.map(p => new THREE.Vector2(p[0], p[1]))));
      }
      const depth = size(o.depth, 0.3), bevel = Math.min(depth * 0.3, num(o.bevel, 0.02));
      const g = new THREE.ExtrudeGeometry(shape, { depth, bevelEnabled: bevel > 0, bevelSize: bevel, bevelThickness: bevel, bevelSegments: 3, curveSegments: 16 });
      return orient(g.translate(0, 0, -depth / 2), o.axis, 'z');
    }
    case 'spring': {
      const coils = Math.max(1, Math.min(60, num(o.coils, 8))), r = size(o.r, 0.4), h = size(o.h, 2), steps = Math.round(coils * 24);
      const pts = [];
      for (let i = 0; i <= steps; i++) { const t = i / steps, a = t * coils * Math.PI * 2; pts.push(new THREE.Vector3(Math.cos(a) * r, t * h - h / 2, Math.sin(a) * r)); }
      return orient(new THREE.TubeGeometry(new THREE.CatmullRomCurve3(pts), steps * 2, size(o.wire, 0.05), 10, false), o.axis);
    }
    case 'gear': {
      const teeth = Math.max(4, Math.min(80, Math.round(num(o.teeth, 16)))), r = size(o.r, 1), depth = size(o.depth, 0.3);
      const root = r * 0.84, shape = new THREE.Shape();
      for (let i = 0; i < teeth; i++) {
        const a = (i / teeth) * Math.PI * 2, s = (Math.PI * 2) / teeth;
        const corners = [[root, a], [r, a + s * 0.15], [r, a + s * 0.45], [root, a + s * 0.6]];
        corners.forEach(([rad, ang], k) => (i === 0 && k === 0 ? shape.moveTo : shape.lineTo).call(shape, Math.cos(ang) * rad, Math.sin(ang) * rad));
      }
      shape.closePath();
      const hole = new THREE.Path().absarc(0, 0, size(o.hole, r * 0.2), 0, Math.PI * 2, true);
      shape.holes.push(hole);
      const g = new THREE.ExtrudeGeometry(shape, { depth, bevelEnabled: true, bevelSize: depth * 0.06, bevelThickness: depth * 0.06, bevelSegments: 2, curveSegments: 4 }).translate(0, 0, -depth / 2);
      return orient(g, o.axis, 'z');
    }
    case 'blob': return blobGeometry(o);
    default: return null;
  }
}

// A box with its edges and corners rounded by `radius`: a subdivided cube whose points are pushed out from an inner
// box, half of each face's rows going to the rounded band so the corners stay smooth.
function roundedBox(w, h, d, radius) {
  const half = [w / 2, h / 2, d / 2];
  const r = Math.min(radius, ...half) * 0.999;
  const inner = half.map(v => v - r);
  const g = new THREE.BoxGeometry(2, 2, 2, 16, 16, 16);
  const pos = g.attributes.position, nor = g.attributes.normal;
  const out = new THREE.Vector3(), core = new THREE.Vector3();
  for (let i = 0; i < pos.count; i++) {
    const s = [pos.getX(i), pos.getY(i), pos.getZ(i)];
    const m = s.map((v, k) => Math.sign(v) * (Math.abs(v) <= 0.5 ? (Math.abs(v) / 0.5) * inner[k] : inner[k] + ((Math.abs(v) - 0.5) / 0.5) * r));
    core.set(...m.map((v, k) => Math.max(-inner[k], Math.min(inner[k], v))));
    out.set(m[0] - core.x, m[1] - core.y, m[2] - core.z);
    if (out.lengthSq() < 1e-12) out.set(nor.getX(i), nor.getY(i), nor.getZ(i));
    out.normalize();
    pos.setXYZ(i, core.x + out.x * r, core.y + out.y * r, core.z + out.z * r);
    nor.setXYZ(i, out.x, out.y, out.z);
  }
  return g;
}

// A pipe through `points`: straight runs with bends of radius `bend` at the corners (a tube is smooth everywhere).
function pipeGeometry(o) {
  const pts = pointList(o.points, 3).slice(0, 100).map(p => new THREE.Vector3(p[0], p[1], p[2]));
  if (pts.length < 2) return null;
  const r = size(o.r, 0.1), bend = size(o.bend, r * 2.5);
  const path = new THREE.CurvePath();
  let from = pts[0];
  for (let i = 1; i < pts.length - 1; i++) {
    const corner = pts[i], before = pts[i - 1], after = pts[i + 1];
    const b = Math.min(bend, corner.distanceTo(before) / 2, corner.distanceTo(after) / 2);
    const p1 = corner.clone().addScaledVector(corner.clone().sub(before).normalize(), -b);
    const p2 = corner.clone().addScaledVector(after.clone().sub(corner).normalize(), b);
    if (from.distanceTo(p1) > 1e-6) path.add(new THREE.LineCurve3(from, p1));
    path.add(new THREE.QuadraticBezierCurve3(p1, corner, p2));
    from = p2;
  }
  path.add(new THREE.LineCurve3(from, pts[pts.length - 1]));
  return new THREE.TubeGeometry(path, Math.min(600, 24 + pts.length * 32), r, 20, false);
}

// ------------------------------------------------------------------ organic shapes (blob)
// One smooth surface over balls (ellipsoids), sticks (capsules that may taper) and paths, blended where they meet
// (`smooth`), with holes carved out: a heart with its chambers and arteries, a lung, a tooth, a kidney, a leaf. The
// surface is meshed from its distance field with surface nets, its normals taken from the field.
const CELL_EDGES = [];
for (let i = 0; i < 8; i++) for (const bit of [1, 2, 4]) if (!(i & bit)) CELL_EDGES.push([i, i | bit]);

function blobPrimitives(o) {
  const r0 = v => size(v, 0.5);
  const prims = [];
  const ball = b => {
    let c, rad;
    if (Array.isArray(b) && b.length >= 4 && b.every(n => typeof n === 'number' && isFinite(n))) {
      c = new THREE.Vector3(b[0], b[1], b[2]);
      rad = b.length >= 6 ? new THREE.Vector3(r0(b[3]), r0(b[4]), r0(b[5])) : new THREE.Vector3(r0(b[3]), r0(b[3]), r0(b[3]));
    } else if (b && typeof b === 'object') {
      c = vec3(b.at, null);
      const s = vec3(b.scale, new THREE.Vector3(1, 1, 1)), r = r0(b.r);
      rad = new THREE.Vector3(r * size(s.x, 1), r * size(s.y, 1), r * size(s.z, 1));
    }
    return c ? { kind: 'ball', c, rad, k: Math.min(rad.x, rad.y, rad.z), center: c, reach: Math.max(rad.x, rad.y, rad.z) } : null;
  };
  const stick = (a, b, r1, r2) => {
    if (!a || !b) return null;
    const ba = b.clone().sub(a), radius = Math.max(r1, r2);
    return { kind: 'stick', a, ba, len2: Math.max(1e-9, ba.lengthSq()), r1, r2, center: a.clone().lerp(b, 0.5), reach: ba.length() / 2 + radius };
  };
  for (const b of (Array.isArray(o.balls) ? o.balls : []).slice(0, 60)) { const p = ball(b); if (p) prims.push(p); }
  for (const s of (Array.isArray(o.sticks) ? o.sticks : []).slice(0, 60)) {
    const p = s && stick(vec3(s.from, null), vec3(s.to, null), r0(s.r), r0(num(s.r2, s.r)));
    if (p) prims.push(p);
  }
  for (const path of (Array.isArray(o.paths) ? o.paths : []).slice(0, 12)) {
    const pts = pointList(path && path.points, 3).map(q => new THREE.Vector3(q[0], q[1], q[2]));
    if (pts.length < 2) continue;
    const curve = new THREE.CatmullRomCurve3(pts, false, 'centripetal');
    const n = Math.min(32, pts.length * 6), r1 = r0(path.r), r2 = r0(num(path.r2, path.r));
    for (let i = 0; i < n; i++) {
      const p = stick(curve.getPoint(i / n), curve.getPoint((i + 1) / n), r1 + ((r2 - r1) * i) / n, r1 + ((r2 - r1) * (i + 1)) / n);
      if (p) prims.push(p);
    }
  }
  const holes = [];
  for (const h of (Array.isArray(o.holes) ? o.holes : []).slice(0, 30)) {
    const p = Array.isArray(h) || (h && h.at) ? ball(h) : h && stick(vec3(h.from, null), vec3(h.to, null), r0(h.r), r0(num(h.r2, h.r)));
    if (p) holes.push(p);
  }
  return { prims, holes };
}

function primDistance(q, x, y, z) {
  if (q.kind === 'ball') {
    const dx = (x - q.c.x) / q.rad.x, dy = (y - q.c.y) / q.rad.y, dz = (z - q.c.z) / q.rad.z;
    return (Math.sqrt(dx * dx + dy * dy + dz * dz) - 1) * q.k;
  }
  const px = x - q.a.x, py = y - q.a.y, pz = z - q.a.z;
  const h = Math.max(0, Math.min(1, (px * q.ba.x + py * q.ba.y + pz * q.ba.z) / q.len2));
  const dx = px - q.ba.x * h, dy = py - q.ba.y * h, dz = pz - q.ba.z * h;
  return Math.sqrt(dx * dx + dy * dy + dz * dz) - (q.r1 + (q.r2 - q.r1) * h);
}

function blobGeometry(o) {
  const { prims, holes } = blobPrimitives(o);
  if (!prims.length) return null;
  const k = Math.max(0.001, num(o.smooth, 0.25));
  const smin = (a, b) => { const h = Math.max(k - Math.abs(a - b), 0) / k; return Math.min(a, b) - h * h * k * 0.25; };
  const sdf = (x, y, z) => {
    let d = Infinity;
    for (const q of prims) {
      // A primitive too far away to reach the surface here is skipped.
      if (d !== Infinity) {
        const cx = x - q.center.x, cy = y - q.center.y, cz = z - q.center.z;
        if (Math.sqrt(cx * cx + cy * cy + cz * cz) - q.reach > d + k) continue;
      }
      const e = primDistance(q, x, y, z);
      d = d === Infinity ? e : smin(d, e);
    }
    for (const q of holes) d = -smin(-d, primDistance(q, x, y, z));
    return d;
  };
  const min = new THREE.Vector3(Infinity, Infinity, Infinity), max = new THREE.Vector3(-Infinity, -Infinity, -Infinity);
  for (const q of prims) {
    min.min(q.center.clone().subScalar(q.reach));
    max.max(q.center.clone().addScalar(q.reach));
  }
  const extent = max.clone().sub(min);
  const detail = Math.max(24, Math.min(96, num(o.detail, 64)));
  let cell = Math.max(extent.x, extent.y, extent.z) / detail;
  min.subScalar(k + cell * 2);
  max.addScalar(k + cell * 2);
  const dims = () => [0, 1, 2].map(a => Math.ceil((max.getComponent(a) - min.getComponent(a)) / cell) + 1);
  let [nx, ny, nz] = dims();
  while (nx * ny * nz > 700000) { cell *= 1.15; [nx, ny, nz] = dims(); }
  return surfaceNets(sdf, min, cell, nx, ny, nz);
}

function surfaceNets(sdf, origin, cell, nx, ny, nz) {
  const field = new Float32Array(nx * ny * nz);
  for (let z = 0, i = 0; z < nz; z++) for (let y = 0; y < ny; y++) for (let x = 0; x < nx; x++, i++) {
    field[i] = sdf(origin.x + x * cell, origin.y + y * cell, origin.z + z * cell);
  }
  const cx = nx - 1, cy = ny - 1, cz = nz - 1;
  const cellVertex = new Int32Array(cx * cy * cz).fill(-1);
  const positions = [];
  const corner = new Float32Array(8);
  for (let z = 0; z < cz; z++) for (let y = 0; y < cy; y++) for (let x = 0; x < cx; x++) {
    let inside = 0;
    for (let i = 0; i < 8; i++) {
      const v = field[(x + (i & 1)) + nx * ((y + ((i >> 1) & 1)) + ny * (z + ((i >> 2) & 1)))];
      corner[i] = v;
      if (v < 0) inside++;
    }
    if (inside === 0 || inside === 8) continue;
    let sx = 0, sy = 0, sz = 0, count = 0;
    for (const [a, b] of CELL_EDGES) {
      const va = corner[a], vb = corner[b];
      if ((va < 0) === (vb < 0)) continue;
      const t = va / (va - vb);
      sx += (a & 1) + ((b & 1) - (a & 1)) * t;
      sy += ((a >> 1) & 1) + (((b >> 1) & 1) - ((a >> 1) & 1)) * t;
      sz += ((a >> 2) & 1) + (((b >> 2) & 1) - ((a >> 2) & 1)) * t;
      count++;
    }
    cellVertex[x + cx * (y + cy * z)] = positions.length / 3;
    positions.push(origin.x + (x + sx / count) * cell, origin.y + (y + sy / count) * cell, origin.z + (z + sz / count) * cell);
  }
  if (positions.length < 9) return null;
  // A quad joins the four cells around every grid edge the surface crosses.
  const indices = [];
  const fieldStride = [1, nx, nx * ny], cellStride = [1, cx, cx * cy], dimsOf = [nx, ny, nz];
  const g = [0, 0, 0];
  for (let z = 0; z < nz; z++) for (let y = 0; y < ny; y++) for (let x = 0; x < nx; x++) {
    g[0] = x; g[1] = y; g[2] = z;
    const fi = x + nx * (y + ny * z), v0 = field[fi];
    for (let a = 0; a < 3; a++) {
      const b = (a + 1) % 3, c = (a + 2) % 3;
      if (g[a] + 1 >= dimsOf[a] || g[b] < 1 || g[c] < 1 || g[b] > dimsOf[b] - 2 || g[c] > dimsOf[c] - 2) continue;
      const v1 = field[fi + fieldStride[a]];
      if ((v0 < 0) === (v1 < 0)) continue;
      const base = x + cx * (y + cy * z);
      const q11 = cellVertex[base], q01 = cellVertex[base - cellStride[b]], q10 = cellVertex[base - cellStride[c]];
      const q00 = cellVertex[base - cellStride[b] - cellStride[c]];
      if (q00 < 0 || q01 < 0 || q10 < 0 || q11 < 0) continue;
      if (v0 < 0) indices.push(q00, q01, q11, q00, q11, q10);
      else indices.push(q00, q11, q01, q00, q10, q11);
    }
  }
  if (!indices.length) return null;
  // Normals from the field's slope.
  const normals = new Float32Array(positions.length), e = cell * 0.5;
  for (let i = 0; i < positions.length; i += 3) {
    const x = positions[i], y = positions[i + 1], z = positions[i + 2];
    const n = new THREE.Vector3(sdf(x + e, y, z) - sdf(x - e, y, z), sdf(x, y + e, z) - sdf(x, y - e, z), sdf(x, y, z + e) - sdf(x, y, z - e)).normalize();
    normals[i] = n.x; normals[i + 1] = n.y; normals[i + 2] = n.z;
  }
  // The quads' winding follows the field's slope (whichever way round the loops above made it).
  const p = i => new THREE.Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
  const [i0, i1, i2] = indices;
  const face = p(i1).sub(p(i0)).cross(p(i2).sub(p(i0)));
  if (face.dot(new THREE.Vector3(normals[i0 * 3], normals[i0 * 3 + 1], normals[i0 * 3 + 2])) < 0) {
    for (let i = 0; i < indices.length; i += 3) { const t = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = t; }
  }
  const geometryValue = new THREE.BufferGeometry();
  geometryValue.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  geometryValue.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
  geometryValue.setIndex(indices);
  geometryValue.userData.smooth = true;
  return geometryValue;
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

// ------------------------------------------------------------------ the pen's look: outlines, creases, cut faces
// The outline round a part: its back faces pushed out a pixel and a half on screen, in ink. Every point moves along
// the average of the normals at its place, so a box's corners stay closed.
function hullNormals(geometryValue) {
  if (geometryValue.attributes.hullNormal) return;
  if (!geometryValue.attributes.normal) geometryValue.computeVertexNormals();
  const pos = geometryValue.attributes.position, nor = geometryValue.attributes.normal;
  const sums = new Map(), keys = new Array(pos.count);
  for (let i = 0; i < pos.count; i++) {
    const key = `${Math.round(pos.getX(i) * 1e4)},${Math.round(pos.getY(i) * 1e4)},${Math.round(pos.getZ(i) * 1e4)}`;
    keys[i] = key;
    const sum = sums.get(key) || [0, 0, 0];
    sum[0] += nor.getX(i); sum[1] += nor.getY(i); sum[2] += nor.getZ(i);
    sums.set(key, sum);
  }
  const out = new Float32Array(pos.count * 3);
  for (let i = 0; i < pos.count; i++) {
    const [x, y, z] = sums.get(keys[i]), l = Math.hypot(x, y, z) || 1;
    out[i * 3] = x / l; out[i * 3 + 1] = y / l; out[i * 3 + 2] = z / l;
  }
  geometryValue.setAttribute('hullNormal', new THREE.BufferAttribute(out, 3));
}

function outlineMesh(figure, geometryValue, planes) {
  hullNormals(geometryValue);
  const m = new THREE.MeshBasicMaterial({ color: INK, side: THREE.BackSide });
  if (planes) m.clippingPlanes = planes;
  m.onBeforeCompile = shader => {
    shader.uniforms.outlineSize = figure.outlineSize;
    shader.vertexShader = shader.vertexShader
      .replace('void main() {', 'attribute vec3 hullNormal;\nuniform vec2 outlineSize;\nvoid main() {')
      .replace('#include <project_vertex>', `#include <project_vertex>
  vec2 outlineDir = (projectionMatrix * modelViewMatrix * vec4(hullNormal, 0.0)).xy;
  float outlineLength = length(outlineDir);
  if (outlineLength > 1e-6) gl_Position.xy += outlineDir / outlineLength * outlineSize * gl_Position.w;`);
  };
  const mesh = new THREE.Mesh(geometryValue, m);
  mesh.raycast = () => {};
  mesh.userData.ink = true;
  return mesh;
}

// Lines on the part's sharp edges (a box's corners, a gear's teeth), lighter than the outline.
function creaseLines(geometryValue, planes) {
  const m = new THREE.LineBasicMaterial({ color: INK, transparent: true, opacity: 0.4 });
  if (planes) m.clippingPlanes = planes;
  const lines = new THREE.LineSegments(new THREE.EdgesGeometry(geometryValue, 30), m);
  lines.raycast = () => {};
  return lines;
}

// Where a part is cut open: its inside walls drawn flat in a lighter shade of it, with a fine hatch, so the cut reads
// as a cut face.
function cutFaceMesh(geometryValue, partMaterial, planes) {
  const m = new THREE.MeshBasicMaterial({ color: partMaterial.color.clone().lerp(new THREE.Color('#FFFFFF'), 0.25), side: THREE.BackSide });
  m.clippingPlanes = planes;
  m.onBeforeCompile = shader => {
    shader.fragmentShader = shader.fragmentShader.replace('#include <dithering_fragment>',
      'gl_FragColor.rgb *= mix(0.88, 1.0, step(0.3, fract((gl_FragCoord.x + gl_FragCoord.y) / 10.0)));\n#include <dithering_fragment>');
  };
  const mesh = new THREE.Mesh(geometryValue, m);
  mesh.raycast = () => {};
  return mesh;
}

// cut: 'z' takes away the half of the part in front of the figure's middle (z > 0), '-x' the half at x < 0, and
// {axis, at} cuts at another place. The planes live in the figure's own frame and follow it as it turns.
function cutPlanes(figure, cut) {
  if (!cut) return null;
  const spec = typeof cut === 'string' ? { axis: cut } : cut;
  const m = /^(-?)([xyz])$/.exec(String(spec.axis || 'z'));
  if (!m) return null;
  const at = num(spec.at, 0), key = `${m[1]}${m[2]}:${at}`;
  let plane = figure.cuts.get(key);
  if (!plane) {
    const normal = AXES[m[2]].clone().multiplyScalar(m[1] ? 1 : -1);
    // Kept: the side where (±axis) · p ≤ at.
    plane = { local: new THREE.Plane(normal, at), world: new THREE.Plane() };
    figure.cuts.set(key, plane);
  }
  return [plane.world];
}

// One unit of a part: its mesh with its outline and creases (and cut face), or an arrow.
function partUnit(figure, kind, o) {
  if (kind === 'arrow') return arrowObject(o);
  if (kind === 'piece') return pieceSlot(o);
  const g = geometry(kind, o);
  if (!g) return null;
  const mat = material(o);
  const planes = cutPlanes(figure, o.cut);
  const open = kind === 'tube' || kind === 'pipe' || (kind === 'cyl' && o.open);
  if (open) mat.side = THREE.DoubleSide;
  const see = mat.transparent, glow = !!mat.userData.glow;
  const mesh = new THREE.Mesh(g, mat);
  mesh.castShadow = !see && !glow;
  mesh.receiveShadow = !see && !glow;
  const unit = new THREE.Group();
  unit.add(mesh);
  if (planes) {
    mat.clippingPlanes = planes;
    mat.clipShadows = true;
    if (!open) unit.add(cutFaceMesh(g, mat, planes));
  }
  if (!see && o.outline !== false) unit.add(outlineMesh(figure, g, planes));
  if (!g.userData.smooth && !see) unit.add(creaseLines(g, planes));
  return unit;
}

// copies: n with step: [dx, dy, dz] (in the part's own frame), or {n, step}: a row of the same part.
function withCopies(unit, o) {
  const spec = typeof o.copies === 'number' ? { n: o.copies, step: o.step } : o.copies && typeof o.copies === 'object' ? o.copies : null;
  const n = spec ? Math.max(1, Math.min(64, Math.round(num(spec.n, 1)))) : 1;
  if (n <= 1) return unit;
  const step = vec3(spec.step || o.step, new THREE.Vector3(1, 0, 0));
  const row = new THREE.Group();
  for (let k = 0; k < n; k++) {
    const copy = k === 0 ? unit : unit.clone();
    copy.position.copy(step).multiplyScalar(k);
    row.add(copy);
  }
  return row;
}

const easeOutBack = t => { const c = 1.6; return 1 + (c + 1) * Math.pow(t - 1, 3) + c * Math.pow(t - 1, 2); };

// A soft dark spot, for the shadow right under the figure.
let contactTexture = null;
function contactShadowTexture() {
  if (contactTexture) return contactTexture;
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 128;
  const g = canvas.getContext('2d');
  const gradient = g.createRadialGradient(64, 64, 0, 64, 64, 64);
  gradient.addColorStop(0, 'rgba(20,22,30,0.38)');
  gradient.addColorStop(0.5, 'rgba(20,22,30,0.12)');
  gradient.addColorStop(1, 'rgba(20,22,30,0)');
  g.fillStyle = gradient;
  g.fillRect(0, 0, 128, 128);
  contactTexture = new THREE.CanvasTexture(canvas);
  return contactTexture;
}

// ------------------------------------------------------------------ figures
function createFigure(m, rect, seconds) {
  const canvas = document.createElement('canvas');
  Object.assign(canvas.style, { left: `${rect.x}px`, top: `${rect.y}px`, width: `${rect.w}px`, height: `${rect.h}px`, opacity: '0' });
  layer.appendChild(canvas);
  const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true });
  renderer.setPixelRatio(pixelRatio());
  renderer.setSize(rect.w, rect.h, false);
  renderer.setClearColor(0x000000, 0);
  renderer.toneMapping = THREE.NeutralToneMapping;
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.localClippingEnabled = true;
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  const scene = new THREE.Scene();
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  scene.environmentIntensity = 0.85;
  // The studio: a key light up and to the side that casts soft shadows, a cool rim light behind (it follows the
  // camera), a little sky and ground light.
  const sun = new THREE.DirectionalLight(0xffffff, 1.7);
  sun.castShadow = true;
  sun.shadow.mapSize.set(2048, 2048);
  sun.shadow.radius = 5;
  sun.shadow.bias = -0.0004;
  sun.shadow.normalBias = 0.03;
  const rim = new THREE.DirectionalLight(0xdbe6ff, 1.1);
  scene.add(sun, sun.target, rim, rim.target, new THREE.HemisphereLight(0xffffff, 0x50525a, 0.35));
  // The ground under the figure: only its shadows show.
  const ground = new THREE.Mesh(new THREE.PlaneGeometry(1, 1).rotateX(-Math.PI / 2), new THREE.ShadowMaterial({ opacity: 0.2 }));
  ground.receiveShadow = true;
  const contact = new THREE.Mesh(new THREE.PlaneGeometry(1, 1).rotateX(-Math.PI / 2),
    new THREE.MeshBasicMaterial({ map: contactShadowTexture(), transparent: true, depthWrite: false, toneMapped: false }));
  const onGround = m.ground !== false;
  ground.visible = contact.visible = onGround;
  scene.add(ground, contact);
  const camera = new THREE.PerspectiveCamera(num(m.fov, 35), rect.w / rect.h, 0.01, 2000);
  const root = new THREE.Group();
  scene.add(root);
  const figure = {
    id: m.id, canvas, renderer, scene, camera, root, rect, pmrem, labels: [], sun, rim, ground, contact, cuts: new Map(),
    outlineSize: { value: new THREE.Vector2(2 * 1.4 / rect.w, 2 * 1.4 / rect.h) },
    tilt: num(m.tilt, 18), turn: num(m.turn, -28), spin: num(m.spin, 0), sway: m.spin ? 0 : 32, zoom: num(m.zoom, 1),
    center: new THREE.Vector3(), radius: 1, distance: 4, footprint: 1, floor: 0, framed: false, age: 0, clock: 0, tempo: 1, tempoTo: null, labelsShown: true,
    // How the person has turned it by dragging (any way round, no limit), and the camera's right and up.
    held: new THREE.Quaternion(), right: new THREE.Vector3(1, 0, 0), up: new THREE.Vector3(0, 1, 0),
  };
  figures.set(m.id, figure);
  wakeFrames();
  canvas.animate([{ opacity: 0 }, { opacity: 1 }], { duration: Math.max(150, seconds * 1000), fill: 'forwards' });
  interact(figure);
  reportFigures();
}

// ------------------------------------------------------------------ turning, zooming and clicking a figure
// The stage window takes the mouse only over a figure (it asks for that with the figures' boxes, reportFigures):
// a drag turns the figure any way round (left and right, up and down, all the way over), the wheel zooms, a part under
// the pointer lights up, and a click on it asks about it.
const host = window.chrome && window.chrome.webview;
const raycaster = new THREE.Raycaster(), pointerNdc = new THREE.Vector2();

// Where they are on the page, the board's move and size included (the page reports them with its own buttons' boxes:
// stage.js reportHits).
function figureRects() {
  return [...figures.values()].map(f => {
    const box = f.canvas.getBoundingClientRect();
    return { x: box.left, y: box.top, w: box.width, h: box.height };
  });
}

// How big the board is drawn (stage.js place.s): the figures render with more pixels when it is bigger, so they stay sharp.
let boardScale = 1;
const pixelRatio = () => Math.min(3, (window.devicePixelRatio || 1) * Math.max(1, boardScale));

function setScale(scale) {
  if (!(scale > 0) || scale === boardScale) return;
  boardScale = scale;
  for (const figure of figures.values()) {
    figure.renderer.setPixelRatio(pixelRatio());
    figure.renderer.setSize(figure.rect.w, figure.rect.h, false);
  }
}

function reportFigures() {
  if (window.drawStage && window.drawStage.reportHits) window.drawStage.reportHits();
  else if (host) host.postMessage({ type: 'figures', rects: figureRects() });
}

function partAt(figure, event) {
  const box = figure.canvas.getBoundingClientRect();
  pointerNdc.set(((event.clientX - box.left) / box.width) * 2 - 1, -((event.clientY - box.top) / box.height) * 2 + 1);
  raycaster.setFromCamera(pointerNdc, figure.camera);
  // A cut-away half is not there to click.
  const hit = raycaster.intersectObject(figure.root, true).find(h => h.object.isMesh &&
    !(h.object.material.clippingPlanes || []).some(plane => plane.distanceToPoint(h.point) < 0));
  if (!hit) return null;
  for (let object = hit.object; object; object = object.parent) {
    for (const part of parts.values()) if (part.object === object && part.figure === figure) return part;
  }
  return null;
}

function partName(part) {
  const label = part.figure.labels.find(l => l.part === part);
  return label ? label.text.textContent : (part.name || part.kind);
}

function setGlow(part, amount) {
  if (!part) return;
  part.object.traverse(child => {
    if (child.isMesh && child.material && 'emissiveIntensity' in child.material) {
      if (child.userData.baseEmissive === undefined) {
        child.userData.baseEmissive = child.material.emissive.clone();
        child.userData.baseIntensity = child.material.emissiveIntensity;
      }
      if (amount > 0) { child.material.emissive.set(0x2f6bff); child.material.emissiveIntensity = amount; }
      else { child.material.emissive.copy(child.userData.baseEmissive); child.material.emissiveIntensity = child.userData.baseIntensity; }
    }
  });
}

function interact(figure) {
  const canvas = figure.canvas;
  canvas.style.pointerEvents = 'auto';
  let drag = null, hovered = null;
  canvas.addEventListener('pointerdown', event => {
    drag = { x: event.clientX, y: event.clientY, moved: 0 };
    canvas.setPointerCapture(event.pointerId);
  });
  canvas.addEventListener('pointermove', event => {
    if (drag) {
      const dx = event.clientX - drag.x, dy = event.clientY - drag.y;
      drag.x = event.clientX;
      drag.y = event.clientY;
      drag.moved += Math.hypot(dx, dy);
      if (drag.moved >= 5) turnBy(figure, dx, dy);
      return;
    }
    const part = partAt(figure, event);
    if (part !== hovered) { setGlow(hovered, 0); setGlow(part, 0.35); hovered = part; }
    canvas.style.cursor = part ? 'pointer' : 'grab';
  });
  canvas.addEventListener('pointerup', event => {
    if (drag && drag.moved < 5) {
      const part = partAt(figure, event);
      if (part && host) {
        setGlow(part, 0.8);
        setTimeout(() => setGlow(part, part === hovered ? 0.35 : 0), 400);
        host.postMessage({ type: 'figurePart', text: partName(part) });
      }
    }
    drag = null;
  });
  canvas.addEventListener('pointerleave', () => { setGlow(hovered, 0); hovered = null; });
  canvas.addEventListener('wheel', event => {
    event.preventDefault();
    figure.zoom = Math.max(0.4, Math.min(3, figure.zoom * (event.deltaY < 0 ? 1.1 : 0.9)));
  }, { passive: false });
}

// A drag of dx, dy pixels turns the figure round the camera's up and right axes, as if the pointer rolled it. Its own
// sway and spin stop where they are, so it doesn't jump when it is taken hold of.
const turning = new THREE.Quaternion();
function turnBy(figure, dx, dy) {
  if (figure.sway || figure.spin) {
    figure.held.premultiply(turning.setFromAxisAngle(AXES.y, figure.turned || 0));
    figure.sway = 0;
    figure.spin = 0;
  }
  const perPixel = Math.PI / Math.max(240, Math.min(figure.rect.w, figure.rect.h));
  figure.held.premultiply(turning.setFromAxisAngle(figure.up, dx * perPixel));
  figure.held.premultiply(turning.setFromAxisAngle(figure.right, dy * perPixel));
  figure.held.normalize();
}

const degrees = v => THREE.MathUtils.degToRad(v);

function addPart(m, seconds) {
  const figure = figures.get(m.fig);
  if (!figure) return;
  const o = m.params || {};
  const placed = placedPart(figure, m.kind, o);
  if (!placed) return;
  const scale = typeof o.scale === 'number' ? new THREE.Vector3(o.scale, o.scale, o.scale) : vec3(o.scale, new THREE.Vector3(1, 1, 1));
  const parent = o.parent && parts.get(o.parent);
  (parent ? parent.object : figure.root).add(placed);
  const part = { id: m.id, kind: m.kind, name: typeof o.name === 'string' ? o.name : null, figure, object: placed, base: { position: placed.position.clone(), scale, rotation: placed.rotation.clone() }, motions: [], grow: { start: figure.age, seconds: Math.max(0.15, seconds) } };
  placed.scale.setScalar(1e-3);
  // hidden: made now but not shown until a path motion sizes it up (a cell that will divide holds its daughters' parts).
  if (o.hidden) { part.hidden = true; part.grow = null; }
  parts.set(m.id, part);
  if (m.kind === 'piece') {
    part.piece = o;
    if (pieceResults.has(m.id)) fillPiece(part, pieceResults.get(m.id));
  }
}

// A part's unit, repeated and placed: at, rot, copies, around and mirror.
function placedPart(figure, kind, o) {
  const unit = partUnit(figure, kind, o);
  if (!unit) return null;
  const body = withCopies(unit, o);
  const at = kind === 'arrow' ? new THREE.Vector3() : vec3(o.at, vec3(o.pos, new THREE.Vector3()));
  const rot = vec3(o.rot, null);
  // around: n (or {n, axis, center}) repeats the part round an axis through `center` (blades, teeth, bolts on a
  // flange); mirror: 'x' adds its mirror image across x = 0. The part then turns about that centre.
  const aroundSpec = typeof o.around === 'number' ? { n: o.around } : o.around && typeof o.around === 'object' ? o.around : null;
  const around = aroundSpec ? Math.max(1, Math.min(72, Math.round(num(aroundSpec.n, 1)))) : 1;
  const mirror = ['x', 'y', 'z'].includes(o.mirror) ? o.mirror : null;
  const placed = new THREE.Group();
  if (around > 1 || mirror) {
    const center = aroundSpec ? vec3(aroundSpec.center, new THREE.Vector3()) : new THREE.Vector3();
    const axis = AXES[['x', 'y', 'z'].includes(aroundSpec && aroundSpec.axis) ? aroundSpec.axis : 'y'];
    placed.position.copy(center);
    let first = true;
    for (let k = 0; k < around; k++) {
      for (const flip of mirror ? [1, -1] : [1]) {
        const holder = new THREE.Group();
        holder.quaternion.setFromAxisAngle(axis, (k / around) * Math.PI * 2);
        if (flip < 0) holder.scale[mirror] = -1;
        const inner = new THREE.Group();
        inner.position.copy(at).sub(center);
        if (rot) inner.rotation.set(degrees(rot.x), degrees(rot.y), degrees(rot.z));
        inner.add(first ? body : body.clone());
        first = false;
        holder.add(inner);
        placed.add(holder);
      }
    }
  } else {
    placed.add(body);
    placed.position.copy(at);
    if (rot) placed.rotation.set(degrees(rot.x), degrees(rot.y), degrees(rot.z));
  }
  return placed;
}

// ------------------------------------------------------------------ pieces the app makes
// A piece waits in a faint box of its size (a slot; copies, around and mirror make more slots) until its shape comes.
const pieceResults = new Map();
const waitingSlots = new Set();

function pieceSize(o) {
  const s = Array.isArray(o.size) ? o.size : [];
  return new THREE.Vector3(size(s[0], 1), size(s[1], 1), size(s[2], 1));
}

function pieceSlot(o) {
  const box = pieceSize(o);
  const m = new THREE.MeshStandardMaterial({ color: '#BFD0E6', transparent: true, opacity: 0.3, depthWrite: false, roughness: 0.6 });
  const mesh = new THREE.Mesh(roundedBox(box.x, box.y, box.z, Math.min(box.x, box.y, box.z) * 0.12), m);
  const slot = new THREE.Group();
  slot.userData.pieceSlot = true;
  slot.add(mesh);
  waitingSlots.add(m);
  return slot;
}

// The app's answer for piece `id` (pieceWanted → piece): kept until its part is drawn, then put in its slots.
function pieceArrived(message) {
  pieceResults.set(message.id, message);
  const part = parts.get(message.id);
  if (part) fillPiece(part, message);
}

async function fillPiece(part, message) {
  if (part.filled) return;
  part.filled = true;
  const o = part.piece, figure = part.figure;
  let model = null;
  try {
    if (message.kind === 'glb') model = await glbModel(message.data);
    else if (message.kind === 'obj') model = objModel(message.text, o);
    else if (message.kind === 'parts') model = partsModel(figure, message.parts, o);
  } catch (error) {
    if (host) host.postMessage({ type: 'log', text: 'piece ' + (o.make || '') + ' could not be shown: ' + (error && error.message || error) });
  }
  // Nothing came (or it couldn't be read): the box itself becomes the piece, solid, in the piece's material.
  if (!model) model = new THREE.Mesh(roundedBox(...pieceSize(o).toArray(), Math.min(...pieceSize(o).toArray()) * 0.12), material(o));
  // Parts were built for this box, so they are stretched to fill it (pieces meet as planned); a generated mesh keeps its
  // proportions.
  const fitted = fitToBox(model, pieceSize(o), message.kind === 'parts');
  dressModel(figure, fitted, o, message.kind !== 'parts');
  const slots = [];
  part.object.traverse(child => { if (child.userData.pieceSlot) slots.push(child); });
  slots.forEach((slot, k) => {
    for (const child of [...slot.children]) {
      slot.remove(child);
      if (child.material) waitingSlots.delete(child.material);
    }
    const shape = k === 0 ? fitted : fitted.clone();
    slot.add(shape);
    shape.scale.multiplyScalar(0.001);
    slot.userData.grow = { start: figure.age, seconds: 0.6, scale: shape.scale.clone().divideScalar(0.001), shape };
  });
}

// The model scaled to fit the box and centred in it: evenly, or stretched to fill it in each direction.
function fitToBox(model, box, stretch) {
  model.updateMatrixWorld(true);
  const bounds = new THREE.Box3().setFromObject(model);
  const extent = bounds.getSize(new THREE.Vector3()), centre = bounds.getCenter(new THREE.Vector3());
  const ratios = ['x', 'y', 'z'].filter(a => extent[a] > 1e-6).map(a => box[a] / extent[a]);
  const k = ratios.length ? Math.min(...ratios) : 1;
  const holder = new THREE.Group();
  model.position.sub(centre);
  const inner = new THREE.Group();
  inner.add(model);
  inner.scale.setScalar(k);
  if (stretch) for (const a of ['x', 'y', 'z']) if (extent[a] > 1e-6) inner.scale[a] = box[a] / extent[a];
  holder.add(inner);
  return holder;
}

// A made mesh as the figure's own parts look: shadows, its part's material when it has no colours of its own, the
// part's cut, and an ink outline.
function dressModel(figure, model, o, generated) {
  const planes = cutPlanes(figure, o.cut);
  const meshes = [];
  model.traverse(child => { if (child.isMesh) meshes.push(child); });
  for (const mesh of meshes) {
    if (!generated) continue;
    if (o.mat && !(mesh.material && (mesh.material.map || mesh.material.vertexColors))) mesh.material = material(o);
    mesh.castShadow = mesh.receiveShadow = true;
    if (planes) {
      mesh.material.clippingPlanes = planes;
      mesh.material.clipShadows = true;
      mesh.parent.add(cutFaceMesh(mesh.geometry, mesh.material, planes));
    }
    if (mesh.geometry.attributes.position.count < 150000) {
      const outline = outlineMesh(figure, mesh.geometry, planes);
      outline.position.copy(mesh.position); outline.quaternion.copy(mesh.quaternion); outline.scale.copy(mesh.scale);
      mesh.parent.add(outline);
    }
  }
}

// Parts of the figure language (from the drawing model): each built and placed as a part is, inside the piece.
function partsModel(figure, specs, o) {
  const group = new THREE.Group();
  for (const spec of Array.isArray(specs) ? specs : []) {
    if (!spec || typeof spec !== 'object' || spec.kind === 'piece') continue;
    const params = Object.assign({}, spec, o.cut && !spec.cut ? { cut: o.cut } : {});
    delete params.parent;
    try {
      const placed = placedPart(figure, String(spec.kind), params);
      if (placed) group.add(placed);
    } catch (_) { /* a part that can't be built is left out */ }
  }
  if (!group.children.length) throw new Error('no parts');
  return group;
}

// An OBJ mesh (LLaMA-Mesh's: "v x y z" and "f a b c" lines, maybe with vertex colours), faceted.
function objModel(text, o) {
  const vertices = [], colours = [], positions = [], faceColours = [];
  let coloured = false;
  for (const line of String(text).split('\n')) {
    const p = line.trim().split(/\s+/);
    if (p[0] === 'v' && p.length >= 4) {
      vertices.push([+p[1], +p[2], +p[3]]);
      if (p.length >= 7) { colours.push([+p[4], +p[5], +p[6]]); coloured = true; } else colours.push(null);
    } else if (p[0] === 'f' && p.length >= 4) {
      const ids = p.slice(1).map(c => { const i = parseInt(c, 10); return i < 0 ? vertices.length + i : i - 1; });
      for (let k = 1; k + 1 < ids.length; k++) {
        for (const i of [ids[0], ids[k], ids[k + 1]]) {
          const v = vertices[i];
          if (!v) throw new Error('a face names a missing vertex');
          positions.push(...v);
          const c = colours[i] || [0.7, 0.7, 0.7];
          faceColours.push(...c.map(x => x > 1 ? x / 255 : x));
        }
      }
    }
  }
  if (positions.length < 9) throw new Error('no faces');
  // Faces wound inward (the mesh's volume comes out negative) are turned outward, so its outline stays behind it.
  let volume = 0;
  for (let i = 0; i < positions.length; i += 9) {
    const [ax, ay, az, bx, by, bz, cx, cy, cz] = positions.slice(i, i + 9);
    volume += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
  }
  if (volume < 0) {
    for (let i = 0; i < positions.length; i += 9) {
      for (const list of [positions, faceColours]) for (let k = 0; k < 3; k++) [list[i + 3 + k], list[i + 6 + k]] = [list[i + 6 + k], list[i + 3 + k]];
    }
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  if (coloured) g.setAttribute('color', new THREE.Float32BufferAttribute(faceColours, 3));
  g.computeVertexNormals();
  const m = coloured && !o.mat ? new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.55 }) : material(o);
  m.side = THREE.DoubleSide;
  return new THREE.Mesh(g, m);
}

// A GLB file (a data URL), read here: meshes with their nodes' placement, positions, normals, UVs, vertex colours and
// indices, and each material's base colour, metalness, roughness and colour texture. (Draco-compressed files can't be.)
async function glbModel(dataUrl) {
  const bytes = Uint8Array.from(atob(String(dataUrl).split(',')[1] || ''), c => c.charCodeAt(0));
  const view = new DataView(bytes.buffer);
  if (bytes.length < 20 || view.getUint32(0, true) !== 0x46546C67) throw new Error('not a GLB file');
  let json = null, bin = null;
  for (let offset = 12; offset + 8 <= bytes.length;) {
    const length = view.getUint32(offset, true), type = view.getUint32(offset + 4, true);
    const chunk = bytes.subarray(offset + 8, offset + 8 + length);
    if (type === 0x4E4F534A) json = JSON.parse(new TextDecoder().decode(chunk));
    else if (type === 0x004E4942) bin = chunk;
    offset += 8 + length;
  }
  if (!json) throw new Error('the GLB has no JSON');
  if ((json.extensionsUsed || []).includes('KHR_draco_mesh_compression')) throw new Error('Draco-compressed GLB files are not read');
  const COMPONENTS = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4 };
  const ARRAYS = { 5120: Int8Array, 5121: Uint8Array, 5122: Int16Array, 5123: Uint16Array, 5125: Uint32Array, 5126: Float32Array };
  const attribute = index => {
    const a = json.accessors[index], view = json.bufferViews[a.bufferView];
    const Arr = ARRAYS[a.componentType], n = COMPONENTS[a.type], element = Arr.BYTES_PER_ELEMENT * n;
    const start = (view.byteOffset || 0) + (a.byteOffset || 0), stride = view.byteStride || element;
    const out = new Uint8Array(a.count * element);
    for (let i = 0; i < a.count; i++) out.set(bin.subarray(start + i * stride, start + i * stride + element), i * element);
    return new THREE.BufferAttribute(new Arr(out.buffer), n, !!a.normalized);
  };
  const textures = new Map();
  const texture = async index => {
    if (textures.has(index)) return textures.get(index);
    const image = json.images[json.textures[index].source];
    const v = json.bufferViews[image.bufferView];
    const blob = new Blob([bin.subarray(v.byteOffset || 0, (v.byteOffset || 0) + v.byteLength)], { type: image.mimeType || 'image/png' });
    const t = new THREE.Texture(await createImageBitmap(blob));
    t.colorSpace = THREE.SRGBColorSpace;
    t.flipY = false;
    t.needsUpdate = true;
    textures.set(index, t);
    return t;
  };
  const materials = new Map();
  const materialAt = async index => {
    if (index === undefined) return new THREE.MeshStandardMaterial({ color: '#B9BEC6', roughness: 0.5 });
    if (materials.has(index)) return materials.get(index);
    const spec = json.materials[index] || {}, pbr = spec.pbrMetallicRoughness || {};
    const c = pbr.baseColorFactor || [1, 1, 1, 1];
    const m = new THREE.MeshStandardMaterial({
      color: new THREE.Color(c[0], c[1], c[2]), metalness: num(pbr.metallicFactor, 0.1), roughness: num(pbr.roughnessFactor, 0.6),
      side: spec.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
    });
    if (c[3] < 1 || spec.alphaMode === 'BLEND') { m.transparent = true; m.opacity = c[3]; }
    if (pbr.baseColorTexture) m.map = await texture(pbr.baseColorTexture.index);
    materials.set(index, m);
    return m;
  };
  const node = async index => {
    const spec = json.nodes[index], object = new THREE.Group();
    if (spec.matrix) object.applyMatrix4(new THREE.Matrix4().fromArray(spec.matrix));
    else {
      if (spec.translation) object.position.fromArray(spec.translation);
      if (spec.rotation) object.quaternion.fromArray(spec.rotation);
      if (spec.scale) object.scale.fromArray(spec.scale);
    }
    if (spec.mesh !== undefined) {
      for (const primitive of json.meshes[spec.mesh].primitives) {
        if (primitive.mode !== undefined && primitive.mode !== 4) continue;
        const g = new THREE.BufferGeometry(), at = primitive.attributes;
        if (at.POSITION === undefined) continue;
        g.setAttribute('position', attribute(at.POSITION));
        if (at.NORMAL !== undefined) g.setAttribute('normal', attribute(at.NORMAL));
        if (at.TEXCOORD_0 !== undefined) g.setAttribute('uv', attribute(at.TEXCOORD_0));
        if (at.COLOR_0 !== undefined) g.setAttribute('color', attribute(at.COLOR_0));
        if (primitive.indices !== undefined) g.setIndex(attribute(primitive.indices));
        if (at.NORMAL === undefined) g.computeVertexNormals();
        const m = await materialAt(primitive.material);
        if (at.COLOR_0 !== undefined) m.vertexColors = true;
        object.add(new THREE.Mesh(g, m));
      }
    }
    for (const child of spec.children || []) object.add(await node(child));
    return object;
  };
  const scene = (json.scenes || [])[json.scene || 0];
  const roots = scene ? scene.nodes : (json.nodes || []).map((_, i) => i);
  const model = new THREE.Group();
  for (const index of roots) model.add(await node(index));
  return model;
}


// A label: a little paper tag with a coloured edge, tied to its part by a leader line that ends in a dot.
const LABEL_TONES = { ink: '#1E2230', blue: '#2F6BFF', red: '#E5484D', green: '#25A55F', orange: '#F28C28', purple: '#7C5CD6', yellow: '#E3B23C', teal: '#1F9E99', pink: '#E0679A' };

function addLabel(m, seconds) {
  const part = parts.get(m.part);
  const figure = figures.get(m.fig) || (part && part.figure);
  if (!figure) return;
  const color = LABEL_TONES[m.color] || (typeof m.color === 'string' && /^#[0-9a-f]{6}$/i.test(m.color) ? m.color : LABEL_TONES.green);
  const make = (tag, attributes) => {
    const node = document.createElementNS(SVG_NS, tag);
    for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, String(value));
    return node;
  };
  const group = make('g', {});
  const line = make('line', { stroke: LABEL_TONES.ink, 'stroke-width': 2, 'stroke-linecap': 'round' });
  const dot = make('circle', { r: 4.5, fill: LABEL_TONES.ink, stroke: '#FFFFFF', 'stroke-width': 1.5 });
  const tag = make('rect', { rx: 6, fill: '#FFFDF6', 'fill-opacity': 0.96, stroke: color, 'stroke-width': 1.5 });
  const edge = make('rect', { rx: 3, width: 6, fill: color });
  const textSize = num(m.size, 20);
  const text = make('text', { fill: LABEL_TONES.ink, 'font-size': textSize, 'dominant-baseline': 'central', 'text-anchor': 'start' });
  text.textContent = m.text;
  group.append(line, tag, edge, text, dot);
  labelLayer.appendChild(group);
  group.animate([{ opacity: 0 }, { opacity: 1 }], { duration: Math.max(150, seconds * 1000), fill: 'forwards' });
  figure.labels.push({ part, at: vec3(m.at, null), group, line, dot, tag, edge, text, size: textSize, width: 0, measured: 0 });
}

function addMotion(m) {
  // paths: many parts' paths in one mark, so they all start at the same moment (a cell dividing, phase by phase).
  if (m.kind === 'paths') {
    const o = m.params || {};
    for (const one of Array.isArray(o.list) ? o.list.slice(0, 400) : []) {
      if (one && typeof one.part === 'string') addMotion({ part: one.part, kind: 'path', params: { frames: one.frames, period: o.period, once: o.once } });
    }
    return;
  }
  const part = parts.get(m.part);
  if (!part) return;
  const o = m.params || {};
  if (m.kind === 'move') {
    part.move = { from: part.base.position.clone(), to: vec3(o.to, part.base.position.clone()), start: part.figure.age, seconds: num(o.secs, 0.8) };
    return;
  }
  // sync: the motion keeps the figure's own clock (from when it was made), so parts that move together stay in step
  // however late each one is added: two gears' teeth, a crank and its rod. path: frames the program worked out.
  const frames = Array.isArray(o.frames)
    ? o.frames.slice(0, 720).filter(f => Array.isArray(f) && (f.length === 3 || f.length === 6 || f.length === 7) && f.every(n => typeof n === 'number' && isFinite(n)))
    : null;
  // A new path takes the part over from where the old one left it: the old one goes.
  if (m.kind === 'path') part.motions = part.motions.filter(motion => motion.kind !== 'path');
  part.motions.push({ kind: m.kind, axis: ['x', 'y', 'z'].includes(o.axis) ? o.axis : 'y', period: Math.max(0.2, num(o.period, m.kind === 'spin' ? 3 : 2)),
    amount: num(o.amount, m.kind === 'pulse' ? 0.08 : 0.3), center: vec3(o.center, new THREE.Vector3()), start: o.sync ? 0 : part.figure.clock,
    reverse: !!o.reverse, frames, once: !!o.once });
}

// A follow-up's change to a part, in place (change3d): size (times as big as it is now; 0 hides it), color, opacity,
// hidden (true takes it away, false brings it back), over secs.
function changePart(m) {
  const part = parts.get(m.part);
  if (!part) return;
  const o = m.params || {};
  const t = part.figure.age, seconds = Math.max(0.05, num(o.secs, 0.8));
  // to: the size it is going to; kept: its size while it is hidden, to come back to.
  const k = part.sizeK = part.sizeK || { now: 1, from: 1, to: 1, start: 0, seconds: 1, kept: null };
  let target = k.to;
  if (o.hidden === true && k.kept === null) { k.kept = k.to; target = 0; }
  if (o.hidden === false && k.kept !== null) { target = k.kept; k.kept = null; }
  if (typeof o.size === 'number' && isFinite(o.size) && o.size > 0) {
    const times = Math.min(5, o.size);
    if (k.kept !== null) k.kept *= times; else target *= times;
  }
  Object.assign(k, { from: k.now, to: target, start: t, seconds });
  const color = typeof o.color === 'string' ? colorOf(o.color, null) : null;
  const opacity = typeof o.opacity === 'number' && isFinite(o.opacity) ? Math.max(0.05, Math.min(1, o.opacity)) : null;
  if (color || opacity !== null) {
    part.object.traverse(child => {
      if (!child.isMesh || child.userData.ink) return;
      for (const mat of Array.isArray(child.material) ? child.material : [child.material]) {
        if (!mat || mat.map) continue;
        if (color && mat.color) mat.userData.recolor = { from: mat.color.clone(), to: color.clone(), start: t, seconds };
        if (opacity !== null) { mat.transparent = true; mat.depthWrite = opacity >= 0.99; mat.userData.refade = { from: mat.opacity, to: opacity, start: t, seconds }; }
      }
    });
    part.tweening = true;
  }
}

// A follow-up's tempo for every motion in a figure (tempo3d): 0.5 half speed, 2 twice as fast, eased in.
function setTempo(m) {
  const figure = figures.get(m.fig);
  if (!figure || !(typeof m.tempo === 'number' && isFinite(m.tempo))) return;
  figure.tempoTo = { from: figure.tempo, to: Math.max(0.05, Math.min(5, m.tempo)), start: figure.age, seconds: Math.max(0.1, num(m.secs, 0.8)) };
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
const box = new THREE.Box3(), point = new THREE.Vector3(), away = new THREE.Vector3(), corner = new THREE.Vector3();
const toCamera = new THREE.Vector3(), right = new THREE.Vector3(), up = new THREE.Vector3(), swayed = new THREE.Quaternion();

function updatePart(part, t, clock = t) {
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
    const phase = ((clock - motion.start) / motion.period) * Math.PI * 2;
    switch (motion.kind) {
      case 'spin': object.rotation[motion.axis] += (motion.reverse ? -1 : 1) * phase; break;
      case 'bob': position[motion.axis] += Math.sin(phase) * motion.amount; break;
      case 'stroke': position[motion.axis] += (0.5 - 0.5 * Math.cos(phase)) * motion.amount; break;
      case 'pulse': scale *= 1 + Math.sin(phase) * motion.amount; break;
      case 'orbit': {
        const offset = position.clone().sub(motion.center).applyAxisAngle(AXES[motion.axis], phase);
        position = motion.center.clone().add(offset);
        break;
      }
      case 'path': {
        // Once a period through frames, [x, y, z] or [x, y, z, rx, ry, rz] (radians), in its parent's frame: a
        // connecting rod, a piston, a rocker, a chain's links. Between frames it moves straight and turns the short way.
        const frames = motion.frames;
        if (!frames || !frames.length) break;
        // once: plays through a single time and holds its last frame (a strand unzipping), else it loops.
        const n = frames.length;
        const u = motion.once ? Math.min(1, Math.max(0, (clock - motion.start) / motion.period)) * (n - 1) : (((clock - motion.start) / motion.period) % 1 + 1) % 1 * n;
        const i = Math.floor(u), k = u - i;
        const a = frames[i % n], b = frames[motion.once ? Math.min(i + 1, n - 1) : (i + 1) % n];
        position.set(a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k);
        if (a.length >= 6 && b.length >= 6) {
          const turn = (p, q) => p + Math.atan2(Math.sin(q - p), Math.cos(q - p)) * k;
          object.rotation.set(turn(a[3], b[3]), turn(a[4], b[4]), turn(a[5], b[5]));
        }
        // A seventh number sizes it: a heart chamber squeezing, a blood cell giving way to its other colour (0 hides it).
        if (a.length === 7 && b.length === 7) scale *= a[6] + (b[6] - a[6]) * k;
        break;
      }
    }
  }
  object.position.copy(position);
  if (part.hidden && !part.motions.some(motion => motion.kind === 'path')) scale = 0;
  if (part.sizeK) {
    const k = part.sizeK, e = Math.min(1, (t - k.start) / k.seconds);
    k.now = k.from + (k.to - k.from) * e * e * (3 - 2 * e);
    scale *= k.now;
  }
  if (part.tweening) {
    let busy = false;
    part.object.traverse(child => {
      if (!child.isMesh || child.userData.ink) return;
      for (const mat of Array.isArray(child.material) ? child.material : [child.material]) {
        for (const key of ['recolor', 'refade']) {
          const tw = mat && mat.userData[key];
          if (!tw) continue;
          const e = Math.min(1, (t - tw.start) / tw.seconds);
          if (key === 'recolor') mat.color.copy(tw.from).lerp(tw.to, e); else mat.opacity = tw.from + (tw.to - tw.from) * e;
          if (e >= 1) mat.userData[key] = null; else busy = true;
        }
      }
    });
    part.tweening = busy;
  }
  object.scale.copy(part.base.scale).multiplyScalar(Math.max(1e-3, g * scale));
  // Shrunk away (hidden, or sized 0 by its path): it isn't drawn at all (its ink outline would leave a dot) and its
  // labels go too.
  object.visible = g * scale > 2e-3;
  part.shown = g * scale > 0.05;
}

function updateFigure(figure, dt) {
  figure.age += dt;
  const t = figure.age;
  if (figure.tempoTo) {
    const tw = figure.tempoTo, e = Math.min(1, (t - tw.start) / tw.seconds);
    figure.tempo = tw.from + (tw.to - tw.from) * e;
    if (e >= 1) figure.tempoTo = null;
  }
  figure.clock += dt * figure.tempo;
  for (const m of waitingSlots) m.opacity = 0.22 + 0.12 * Math.sin(t * 4);
  figure.root.traverse(child => {
    const grow = child.userData.grow;
    if (!grow) return;
    const k = Math.min(1, (t - grow.start) / grow.seconds);
    grow.shape.scale.copy(grow.scale).multiplyScalar(Math.max(1e-3, easeOutBack(k)));
    if (k >= 1) child.userData.grow = null;
  });
  for (const part of parts.values()) if (part.figure === figure) updatePart(part, t, figure.clock);
  const sway = figure.sway ? Math.sin((t / 14) * Math.PI * 2) * figure.sway : 0;
  const turned = THREE.MathUtils.degToRad(sway) + figure.spin * t * Math.PI * 2 / 60;
  figure.turned = turned;
  // The figure's own box, as the person turned it but without its sway, so the framing holds still while it sways.
  figure.root.quaternion.copy(figure.held);
  figure.root.updateMatrixWorld(true);
  box.setFromObject(figure.root);
  figure.root.quaternion.premultiply(swayed.setFromAxisAngle(AXES.y, turned));
  figure.root.updateMatrixWorld(true);
  for (const plane of figure.cuts.values()) plane.world.copy(plane.local).applyMatrix4(figure.root.matrixWorld);
  const camera = figure.camera;
  const tilt = THREE.MathUtils.degToRad(figure.tilt), turn = THREE.MathUtils.degToRad(figure.turn);
  toCamera.set(Math.sin(turn) * Math.cos(tilt), Math.sin(tilt), Math.cos(turn) * Math.cos(tilt));
  if (!box.isEmpty()) {
    // Frame everything built so far, tightly and smoothly (a part growing in pulls the camera back a little): the
    // distance at which every corner of the box fits the view, up and down and across.
    box.getCenter(point);
    const center = point.clone().applyAxisAngle(AXES.y, turned);
    right.crossVectors(AXES.y, toCamera).normalize();
    up.crossVectors(toCamera, right);
    figure.right.copy(right);
    figure.up.copy(up);
    const tanV = Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2), tanH = tanV * camera.aspect;
    let needed = 0;
    for (let i = 0; i < 8; i++) {
      corner.set(i & 1 ? box.max.x : box.min.x, i & 2 ? box.max.y : box.min.y, i & 4 ? box.max.z : box.min.z)
        .applyAxisAngle(AXES.y, turned).sub(center);
      needed = Math.max(needed, corner.dot(toCamera) + Math.max(Math.abs(corner.dot(right)) / tanH, Math.abs(corner.dot(up)) / tanV));
    }
    const k = figure.framed ? 0.08 : 1;
    figure.center.lerp(center, k);
    figure.distance += (Math.max(0.5, needed * 1.08) - figure.distance) * k;
    figure.radius += (Math.max(0.2, box.getSize(corner).length() / 2) - figure.radius) * k;
    figure.footprint += (Math.max(corner.x, corner.z) - figure.footprint) * k;
    figure.floor += (box.min.y - figure.floor) * k;
    figure.framed = true;
  }
  const r = figure.radius, c = figure.center, distance = figure.distance / figure.zoom;
  camera.position.copy(toCamera).multiplyScalar(distance).add(c);
  camera.near = distance / 100;
  camera.far = distance * 10;
  camera.updateProjectionMatrix();
  camera.lookAt(c);
  // The lights and the ground keep to the figure's size.
  const { sun, rim, ground, contact } = figure;
  sun.position.set(c.x + r * 1.6, c.y + r * 3.4, c.z + r * 2.2);
  sun.target.position.copy(c);
  const shadowCamera = sun.shadow.camera;
  if (Math.abs(shadowCamera.right - r * 1.8) > r * 0.03) {
    shadowCamera.left = shadowCamera.bottom = -r * 1.8;
    shadowCamera.right = shadowCamera.top = r * 1.8;
    shadowCamera.near = r * 0.1;
    shadowCamera.far = r * 10;
    shadowCamera.updateProjectionMatrix();
  }
  away.copy(c).sub(camera.position).normalize();
  rim.position.copy(c).addScaledVector(away, r * 3).add(corner.set(0, r * 2, 0));
  rim.target.position.copy(c);
  ground.position.set(c.x, figure.floor - r * 0.01, c.z);
  ground.scale.setScalar(r * 8);
  contact.position.set(c.x, figure.floor - r * 0.005, c.z);
  contact.scale.setScalar(figure.footprint * 1.25);
  figure.renderer.render(figure.scene, camera);
  placeLabels(figure, dt);
}

function placeLabels(figure, dt) {
  const { rect } = figure;
  const middle = { x: rect.x + rect.w / 2, y: rect.y + rect.h / 2 };
  const placed = [];
  figure.labels.forEach((label, index) => {
    const away = !figure.labelsShown || (label.part && label.part.shown === false);
    label.group.style.display = away ? 'none' : '';
    if (away) return;
    if (label.part) {
      if (label.at) point.copy(label.at).applyMatrix4(label.part.object.matrixWorld);
      else { box.setFromObject(label.part.object); box.getCenter(point); }
    } else point.copy(label.at || figure.center);
    point.project(figure.camera);
    const ax = rect.x + (point.x * 0.5 + 0.5) * rect.w, ay = rect.y + (-point.y * 0.5 + 0.5) * rect.h;
    // A tag keeps its side until its part has clearly crossed the middle, so a part on the figure's axis (a rocket's
    // nozzle, swaying) doesn't flip it from side to side every frame.
    const offset = ax - middle.x;
    if (!label.side || offset * label.side < -rect.w * 0.08) label.side = offset < 0 ? -1 : 1;
    const length = Math.hypot(offset, ay - middle.y) || 1;
    const dx = label.side * Math.max(0.35, Math.abs(offset) / length), dy = (ay - middle.y) / length;
    const reach = Math.max(60, rect.w * 0.18);
    const lx = Math.max(rect.x - 120, Math.min(rect.x + rect.w + 120, ax + dx * reach));
    const ly = Math.max(rect.y + 16, Math.min(rect.y + rect.h - 16, ay + dy * reach * 0.6));
    placed.push({ label, index, ax, ay, lx, ly, side: label.side });
  });
  // Tags on the same side keep a tag's height apart, so two parts near each other never print on top of each other.
  // They stack in the order of their parts (ties by the order they came in), so two tags at about the same height
  // don't swap places to and fro.
  for (const side of [-1, 1]) {
    const column = placed.filter(p => p.side === side).sort((a, b) => (a.ay - b.ay) || (a.index - b.index));
    for (let i = 1; i < column.length; i++) column[i].ly = Math.max(column[i].ly, column[i - 1].ly + column[i - 1].label.size * 1.75 + 6);
    const overflow = column.length ? column[column.length - 1].ly - (rect.y + rect.h - 16) : 0;
    if (overflow > 0) for (const p of column) p.ly -= overflow;
  }
  // Tags glide to where they belong rather than jump (the dot stays on its part).
  const glide = 1 - Math.exp(-Math.max(0, dt) * 8);
  for (const p of placed) {
    const { label } = p;
    if (label.x === undefined || label.shownSide !== p.side) { label.x = p.lx; label.y = p.ly; label.shownSide = p.side; }
    label.x += (p.lx - label.x) * glide;
    label.y += (p.ly - label.y) * glide;
    p.lx = label.x;
    p.ly = label.y;
  }
  for (const { label, ax, ay, lx, ly, side } of placed) {
    // The text is measured once its font has loaded (the first second or so).
    if (label.measured < 60) { label.width = label.text.getComputedTextLength() || label.text.textContent.length * label.size * 0.5; label.measured++; }
    const pad = 9, h = label.size * 1.6, w = label.width + pad * 2 + 4;
    // Tags stay inside the figure's box, so they stay on its card (and in a saved video's frame).
    const left = Math.max(rect.x + 4, Math.min(rect.x + rect.w - w - 4, side > 0 ? lx : lx - w));
    label.line.setAttribute('x1', ax); label.line.setAttribute('y1', ay);
    label.line.setAttribute('x2', side > 0 ? left : left + w); label.line.setAttribute('y2', ly);
    label.dot.setAttribute('cx', ax); label.dot.setAttribute('cy', ay);
    label.tag.setAttribute('x', left); label.tag.setAttribute('y', ly - h / 2);
    label.tag.setAttribute('width', w); label.tag.setAttribute('height', h);
    label.edge.setAttribute('x', left); label.edge.setAttribute('y', ly - h / 2); label.edge.setAttribute('height', h);
    label.text.setAttribute('x', left + pad + 4);
    label.text.setAttribute('y', ly);
  }
}

// Frames are asked for only while there are figures: an empty stage asks for none.
let framing = false;
function frame(nowMs) {
  const now = nowMs / 1000, dt = Math.min(0.1, Math.max(0, now - clock.last));
  clock.last = now;
  for (const figure of figures.values()) updateFigure(figure, dt);
  if (!figures.size) { framing = false; return; }
  requestAnimationFrame(frame);
}

function wakeFrames() {
  if (framing) return;
  framing = true;
  clock.last = performance.now() / 1000;
  requestAnimationFrame(frame);
}

// ------------------------------------------------------------------ what stage.js calls
window.Figure3D = {
  /** The figures' boxes in page pixels. */
  rects: figureRects,
  /** One released mark: rect is the figure's box in page pixels (figure marks only). */
  apply(m, seconds, rect) {
    switch (m.do) {
      case 'figure': createFigure(m, rect, seconds); break;
      case 'part3d': addPart(m, seconds); break;
      case 'label3d': addLabel(m, seconds); break;
      case 'motion3d': addMotion(m); break;
      case 'view3d': setView(m); break;
      case 'change3d': changePart(m); break;
      case 'tempo3d': setTempo(m); break;
      case 'labels3d': { const figure = figures.get(m.fig); if (figure) figure.labelsShown = m.shown !== false; break; }
    }
  },
  /** The app's answer for a piece the page asked for (pieceWanted). */
  pieceArrived,
  /** The board is drawn at this size (1 as drawn). */
  setScale,
  clear() {
    for (const figure of figures.values()) {
      figure.renderer.dispose();
      figure.pmrem.dispose();
      figure.canvas.remove();
      for (const label of figure.labels) label.group.remove();
    }
    figures.clear();
    parts.clear();
    pieceResults.clear();
    waitingSlots.clear();
    reportFigures();
  },
};
