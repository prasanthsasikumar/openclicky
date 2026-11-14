step(1); say("Let me build a 3D model of a human skull. The rounded case on top is the cranium, and the deep hollows are the eye sockets and the nose opening.")
const c = {x: 290, y: 80, w: 700, h: 510}
text(c.x + c.w / 2, c.y - 24, 'A human skull, in 3D', {size: 28, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 10, turn: -20, zoom: 1, spin: 0.3, ground: false})
const cranium = f.blob({
  balls: [[0, 0.8, 0.25, 1.45, 1.3, 1.65], [0, 0.5, 1.35, 1.15, 1.0, 0.7], [-1.0, 0.45, 0.35, 0.75], [1.0, 0.45, 0.35, 0.75]],
  holes: [[-0.6, 0.95, -1.35, 0.4], [0.6, 0.95, -1.35, 0.4], [0, 0.4, -1.25, 0.32]],
  smooth: 0.5, mat: 'bone', name: 'cranium'
})
cranium.label('cranium', {color: 'blue'})

step(2); say("A heavy brow ridge juts out over the eyes, and a thin cheekbone sweeps back on each side toward the ear.")
const brow = f.tube({points: [[-1.0, 1.2, -0.8], [-0.5, 1.33, -1.22], [0, 1.36, -1.33], [0.5, 1.33, -1.22], [1.0, 1.2, -0.8]], r: 0.17, mat: 'bone', name: 'brow ridge'})
brow.label('brow ridge', {color: 'brown'})
const arch = f.tube({points: [[-1.15, -0.25, -0.85], [-1.45, -0.15, -0.25], [-1.45, 0.05, 0.35]], r: 0.15, mat: 'bone', mirror: 'x', name: 'cheekbone'})
arch.label('cheekbone', {color: 'brown'})

step(3); say("Below the nose sits the upper jaw, grown forward from the skull, with a full row of teeth rooted in the bone.")
const maxilla = f.blob({
  balls: [[0, -0.35, -1.05, 0.8, 0.5, 0.55], [-0.55, -0.45, -0.85, 0.5, 0.42, 0.45], [0.55, -0.45, -0.85, 0.5, 0.42, 0.45], [-0.95, -0.3, -0.7, 0.32], [0.95, -0.3, -0.7, 0.32]],
  smooth: 0.5, mat: 'bone', name: 'maxilla'
})
maxilla.label('upper jaw', {color: 'blue'})
const teethU = []
for (const i of range(9)) {
  const t = i / 8 * 2 - 1
  teethU.push(f.box({w: 0.16, h: 0.26, d: 0.15, at: [t * 0.68, -0.98, -1.2 - 0.35 * (1 - t * t)], rot: [0, -t * 18, 0], mat: 'ceramic', name: 'upper teeth'}))
}
teethU[4].label('teeth', {color: 'grey'})

step(4); say("The lower jaw is a separate horseshoe-shaped bone, hinged at each side near the ear, so it swings when you chew.")
const jaw = f.blob({
  balls: [[0, -1.55, -1.4, 0.32, 0.3, 0.3]],
  paths: [
    {points: [[0, -1.55, -1.4], [-0.65, -1.45, -1.25], [-1.1, -1.2, -0.75], [-1.3, -0.6, -0.2], [-1.28, -0.05, 0.05], [-1.2, 0.45, 0.1]], r: 0.2},
    {points: [[0, -1.55, -1.4], [0.65, -1.45, -1.25], [1.1, -1.2, -0.75], [1.3, -0.6, -0.2], [1.28, -0.05, 0.05], [1.2, 0.45, 0.1]], r: 0.2}
  ],
  smooth: 0.4, mat: 'bone', name: 'mandible'
})
jaw.label('lower jaw', {color: 'blue'})
for (const i of range(9)) {
  const t = i / 8 * 2 - 1
  f.box({w: 0.15, h: 0.24, d: 0.14, at: [t * 0.6, -1.06, -1.15 - 0.3 * (1 - t * t)], rot: [0, -t * 18, 0], mat: 'ceramic', name: 'lower teeth'})
}

step(5); say("So the skull is a rigid helmet for the brain, plus one movable lower jaw for biting and chewing.")
highlight(c.x + 60, c.y + c.h - 50, c.w - 120, 36, {color: 'yellow'})
text(c.x + 80, c.y + c.h - 32, 'a helmet for the brain + a hinged jaw', {align: 'start', size: 22})
suggest(['Cut the skull open', 'Make the jaw chew'])