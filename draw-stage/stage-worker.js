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
//   molecule(x, y, w, h, {name} | {atoms, bonds})  a 3D ball-and-stick model, its shape from chemistry (VSEPR) →
//                                           m.label(i, words), m.atom(i), m.view({…})
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
const noMolecule = () => new Proxy({}, { get: () => () => null });

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
    label: (which, words, o2) => {
      const i = pick(which);
      return parts[i] ? parts[i].label(words, o2) : (warn('molecule label: no atom ' + JSON.stringify(which)), null);
    },
    view: o2 => f.view(o2),
  };
}

api.molecule = molecule;

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
