step(1); say("Let me build a 3D model of a human skull for you, starting with the rounded brain case and the bones of the face.")
text(640, 40, 'The human skull', {size: 30, bold: true})
const f = figure(320, 60, 640, 545, {tilt: 6, turn: -25, ground: false})
const braincase = f.blob({
  balls: [
    [0, 1.15, 0, 1.25, 1.35, 1.25],
    [0, 1.75, 0.35, 1.05, 0.95, 1.05],
    [0, 1.05, -0.55, 1.15, 1.15, 1.05],
    [-0.98, 1.0, 0.15, 0.62, 0.85, 0.75],
    [0.98, 1.0, 0.15, 0.62, 0.85, 0.75],
    [0, 0.72, 0.72, 1.05, 0.38, 0.62],
    [-0.85, 0.3, 0.55, 0.55, 0.5, 0.55],
    [0.85, 0.3, 0.55, 0.55, 0.5, 0.55],
    [0, -0.15, 0.6, 0.85, 0.55, 0.7],
    [0, 0.45, 1.0, 0.26, 0.5, 0.35]
  ],
  holes: [[-0.58, 0.33, 1.15, 0.35], [0.58, 0.33, 1.15, 0.35], [0, -0.02, 1.35, 0.22]],
  smooth: 0.34,
  mat: 'bone'
})
braincase.label('brain case', {color: 'blue'})

step(2); say("The two eye sockets and the nose opening are hollows in the bone, so the eyes and nose sit safely inside.")
const socketL = f.sphere({r: 0.28, at: [-0.58, 0.31, 0.88], mat: 'plastic:#242424'})
const socketR = f.sphere({r: 0.28, at: [0.58, 0.31, 0.88], mat: 'plastic:#242424'})
socketL.label('eye socket', {color: 'purple'})
const nasal = f.sphere({r: 0.19, at: [0, -0.04, 1.12], mat: 'plastic:#242424'})
nasal.label('nose opening', {color: 'teal'})

step(3); say("The cheekbones sweep back like thin bridges toward the ears, and the lower jaw hangs below on a hinge.")
const archL = f.tube({points: [[-0.9, 0.3, 0.9], [-1.2, 0.35, 0.35], [-1.05, 0.55, -0.15]], r: 0.13, mat: 'bone'})
const archR = f.tube({points: [[0.9, 0.3, 0.9], [1.2, 0.35, 0.35], [1.05, 0.55, -0.15]], r: 0.13, mat: 'bone'})
archL.label('cheekbone', {color: 'orange'})
const jaw = f.blob({
  balls: [[0, -1.25, 0.88, 0.62, 0.45, 0.5], [-0.6, -1.15, 0.55, 0.45, 0.38, 0.5], [0.6, -1.15, 0.55, 0.45, 0.38, 0.5], [-0.92, -0.5, -0.12, 0.34, 0.72, 0.45], [0.92, -0.5, -0.12, 0.34, 0.72, 0.45]],
  smooth: 0.42,
  mat: 'bone'
})
jaw.label('lower jaw', {color: 'red'})
const hingeL = f.sphere({r: 0.22, at: [-0.95, 0.32, -0.16], mat: 'bone'})
const hingeR = f.sphere({r: 0.22, at: [0.95, 0.32, -0.16], mat: 'bone'})
hingeL.label('jaw hinge', {color: 'green'})

step(4); say("Two arcs of teeth sit in the bones, and the lower jaw opens and closes on its hinge when you chew.")
for (const k of range(9)) {
  const t = (k - 4) / 4
  const x = 0.68 * t
  const z = 1.30 - 0.32 * t * t
  const r = 0.095 + 0.035 * abs(t)
  f.capsule({r, h: 0.20, at: [x, -0.70, z], mat: 'ceramic'})
  f.capsule({r, h: 0.20, at: [x, -0.92, z], mat: 'ceramic', parent: jaw})
}
jaw.bob({axis: 'y', amount: 0.07, period: 3})

step(5); say("So the skull is one rounded case for the brain, a face of paired bones, and a single moving jaw.")
f.view({turn: -8, tilt: 10})
highlight(70, 300, 250, 48, {color: 'yellow'})
text(195, 331, 'one case, one jaw', {size: 24, bold: true})