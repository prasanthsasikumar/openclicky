step(1); say("Let me build a 3D model of the human skull for you.")
const c = {x: 300, y: 80, w: 680, h: 500}
text(c.x + c.w/2, c.y - 22, 'Human Skull - 3D Model', {size: 26, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 12, turn: -22, zoom: 1.15, ground: false})

// main brain case and face in one smooth bone shape
const skull = f.blob({
  balls: [
    [0, 0.85, -0.05, 0.95, 0.75, 0.95],
    [0, 0.35, 0.05, 0.98, 0.75, 0.92],
    [0, 0.35, 0.62, 0.82, 0.72, 0.55],
    [-0.75, 0.10, 0.05, 0.32, 0.42, 0.38],
    [0.75, 0.10, 0.05, 0.32, 0.42, 0.38],
    [0, -0.25, 0.68, 0.62, 0.38, 0.32],
    [0, -0.10, -0.75, 0.68, 0.62, 0.52],
    [0, 0.55, -0.35, 0.72, 0.55, 0.55]
  ],
  holes: [
    [-0.33, 0.02, 0.92, 0.28],
    [0.33, 0.02, 0.92, 0.28],
    [0, -0.22, 0.96, 0.20]
  ],
  smooth: 0.42,
  mat: 'bone'
})

// dark depth inside orbits and nasal cavity
const orbitDepthL = f.sphere({r: 0.22, at: [-0.33, 0.02, 0.72], mat: 'rubber', color: '#1a1a1a', opacity: 0.95})
const orbitDepthR = f.sphere({r: 0.22, at: [0.33, 0.02, 0.72], mat: 'rubber', color: '#1a1a1a', opacity: 0.95})
const nasalDepth = f.sphere({r: 0.14, at: [0, -0.22, 0.78], mat: 'rubber', color: '#1a1a1a', opacity: 0.95})

// lower jaw - separate movable part
const mandible = f.blob({
  balls: [
    [-0.55, -0.58, 0.25, 0.16],
    [0.55, -0.58, 0.25, 0.16],
    [-0.45, -0.75, 0.45, 0.13],
    [0.45, -0.75, 0.45, 0.13],
    [0, -0.88, 0.58, 0.18, 0.12, 0.14]
  ],
  sticks: [
    {from: [-0.55, -0.58, 0.25], to: [-0.45, -0.75, 0.45], r: 0.09, r2: 0.08},
    {from: [0.55, -0.58, 0.25], to: [0.45, -0.75, 0.45], r: 0.09, r2: 0.08},
    {from: [-0.45, -0.75, 0.45], to: [0, -0.88, 0.58], r: 0.11, r2: 0.11},
    {from: [0.45, -0.75, 0.45], to: [0, -0.88, 0.58], r: 0.11, r2: 0.11}
  ],
  paths: [
    {points: [[-0.55, -0.58, 0.25], [-0.58, -0.42, 0.18], [-0.57, -0.30, 0.06]], r: 0.07},
    {points: [[0.55, -0.58, 0.25], [0.58, -0.42, 0.18], [0.57, -0.30, 0.06]], r: 0.07}
  ],
  smooth: 0.35,
  mat: 'bone'
})

// teeth rows
const upperTeeth = f.box({w: 0.68, h: 0.12, d: 0.08, at: [0, -0.44, 0.73], mat: 'ceramic', color: '#FFF8E7'})
for (let i = -3; i <= 3; i++) {
  f.box({w: 0.08, h: 0.13, d: 0.08, at: [i*0.11, -0.50, 0.76], mat: 'ceramic', color: '#FFFEFA'})
}
for (let i = -3; i <= 3; i++) {
  f.box({w: 0.07, h: 0.10, d: 0.07, at: [i*0.11, -0.78, 0.62], parent: mandible, mat: 'ceramic', color: '#FFFEFA'})
}

step(2); say("At the front are the two orbits for the eyes and the nasal opening in the middle.")
const oL = f.sphere({r: 0.02, at: [-0.33, 0.02, 1.02], opacity: 0})
const oR = f.sphere({r: 0.02, at: [0.33, 0.02, 1.02], opacity: 0})
const oN = f.sphere({r: 0.02, at: [0, -0.22, 1.06], opacity: 0})
oL.label('orbit', {color: 'blue'})
oR.label('orbit', {color: 'blue'})
oN.label('nasal opening', {color: 'teal'})

step(3); say("The large dome on top is the cranium which protects the brain, and it joins the face below.")
f.view({tilt: 10, turn: -52, zoom: 1.15})
const cr = f.sphere({r: 0.02, at: [0, 1.05, -0.10], opacity: 0})
const mx = f.sphere({r: 0.02, at: [0, -0.25, 0.85], opacity: 0})
cr.label('cranium', {color: 'purple'})
mx.label('maxilla', {color: 'brown'})

step(4); say("The lower jaw called the mandible is the only part that moves, hinged at the sides to let you chew and speak.")
f.view({tilt: 14, turn: -28, zoom: 1.15})
const md = f.sphere({r: 0.02, at: [0, -0.85, 0.60], opacity: 0})
const hg = f.sphere({r: 0.02, at: [0.56, -0.32, 0.08], opacity: 0})
md.label('mandible', {color: 'red'})
hg.label('jaw joint', {color: 'orange'})
// hinge dots
dot(c.x + 80, c.y + 120, 6, {color: 'orange'})
text(c.x + 95, c.y + 120, 'hinge', {align: 'start', size: 20, color: 'orange'})

step(5); say("So the skull is a strong case for the brain with openings for eyes nose and teeth and a hinged jaw below.")
highlight(c.x + 70, c.y + c.h + 12, c.w - 140, 36, {color: 'yellow', opacity: 0.9})
text(c.x + c.w/2, c.y + c.h + 31, 'brain case on top, face in front, hinged jaw below', {size: 21, bold: true})