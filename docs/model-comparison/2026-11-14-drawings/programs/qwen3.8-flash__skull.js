step(1); say("Let me build a 3D model of the human skull for you.")
const c = {x: 340, y: 105, w: 600, h: 450}
text(c.x + c.w / 2, c.y - 24, 'Human skull', {size: 26, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 18, turn: -32, zoom: 1.15, ground: false})
const skull = f.blob({
  balls: [
    [0, 0.7, 0, 0.9, 0.8, 1.0],
    [0.75, 0.4, 0, 0.55, 0.6, 0.7],
    [1.05, -0.15, 0, 0.35, 0.5, 0.6],
    [0.85, 0.05, 0.55, 0.25],
    [0.85, 0.05, -0.55, 0.25],
    [-0.75, 0.2, 0, 0.4, 0.55, 0.6],
    [0.15, -0.35, 0, 0.65, 0.35, 0.7],
    [1.2, 0.05, 0, 0.18, 0.25, 0.2]
  ],
  holes: [
    [1.12, 0.3, 0.45, 0.24],
    [1.12, 0.3, -0.45, 0.24],
    [1.25, -0.12, 0, 0.16]
  ],
  smooth: 0.45,
  mat: 'bone',
  name: 'skull'
})
skull.label('cranium', {color: 'ink'})

step(2); say("The rounded cranium protects the brain, while the face has eye sockets and a nose opening.")
const eyeR = f.sphere({r: 0.2, at: [0.98, 0.3, 0.45], mat: 'plastic:#2B2B2B'})
const eyeL = f.sphere({r: 0.2, at: [0.98, 0.3, -0.45], mat: 'plastic:#2B2B2B'})
const nose = f.sphere({r: 0.14, at: [1.1, -0.12, 0], mat: 'plastic:#2B2B2B'})
eyeR.label('eye socket', {color: 'ink'})
nose.label('nose opening', {color: 'ink'})

step(3); say("The jaw is the only movable part, and it carries the teeth for biting and chewing.")
const jaw = f.tube({
  points: [
    [0.35, 0.0, 0.8],
    [0.8, -0.55, 0.55],
    [1.1, -0.8, 0],
    [0.8, -0.55, -0.55],
    [0.35, 0.0, -0.8]
  ],
  r: 0.17,
  mat: 'bone',
  name: 'jaw'
})
jaw.label('jaw', {color: 'ink'})

const teeth = []
for (let i = 0; i < 7; i++) {
  const a = (-60 + i * 20) * PI / 180
  const x = 0.65 + 0.72 * cos(a)
  const z = 0.72 * sin(a)
  const t = f.box({w: 0.1, h: 0.18, d: 0.07, at: [x, -0.5, z], rot: [0, -a * 180 / PI, 0], mat: 'white'})
  teeth.push(t)
}
for (let i = 0; i < 7; i++) {
  const a = (-55 + i * 18.333) * PI / 180
  const x = 0.55 + 0.62 * cos(a)
  const z = 0.62 * sin(a)
  f.box({w: 0.09, h: 0.16, d: 0.065, at: [x, -0.72, z], rot: [0, -a * 180 / PI, 0], mat: 'white'})
}
teeth[3].label('teeth', {color: 'ink'})

step(4); say("So the skull joins the cranium, face, jaw, and teeth.")
highlight(c.x + 90, 560, 420, 34, {color: 'yellow'})
text(c.x + c.w / 2, 578, 'cranium, face, jaw, teeth', {size: 24})