step(1); say("Let me build a detailed 3D model of the human skull, including the cranium, facial bones and lower jaw.")
const c = {x: 300, y: 95, w: 680, h: 455}
text(c.x + c.w / 2, c.y - 42, 'Human skull', {size: 30, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 10, turn: -16, zoom: 1.03, ground: false})
const skull = f.blob({
  name: 'cranium',
  balls: [
    [0, 0.95, -0.15, 1.18, 1.22, 1.02],
    [0, 0.42, 0.35, 1.03, 0.65, 0.68],
    [-0.83, -0.12, 0.35, 0.34, 0.43, 0.42],
    [0.83, -0.12, 0.35, 0.34, 0.43, 0.42],
    [0, -0.57, 0.48, 0.63, 0.55, 0.50],
    [0, -0.85, 0.54, 0.60, 0.22, 0.42]
  ],
  holes: [
    [-0.48, 0.15, 0.77, 0.35], [-0.55, -0.04, 0.72, 0.30],
    [0.48, 0.15, 0.77, 0.35], [0.55, -0.04, 0.72, 0.30],
    [0, -0.42, 0.85, 0.19], [0, -0.62, 0.80, 0.24]
  ],
  smooth: 0.22,
  mat: 'bone'
})
const cheekArch = f.blob({
  paths: [{points: [[-0.88, -0.12, 0.48], [-1.10, 0, 0.14], [-1.10, 0.08, -0.25], [-0.96, 0.12, -0.56]], r: 0.095, r2: 0.075}],
  smooth: 0.25,
  mat: 'bone',
  mirror: 'x'
})
const browRidge = f.blob({
  paths: [{points: [[-0.90, 0.25, 0.55], [-0.75, 0.48, 0.72], [-0.48, 0.53, 0.82], [-0.20, 0.43, 0.82], [-0.10, 0.32, 0.76]], r: 0.08, r2: 0.065}],
  smooth: 0.25,
  mat: 'bone',
  mirror: 'x'
})
const nasalBone = f.blob({
  balls: [[0, 0.23, 0.83, 0.12, 0.32, 0.16], [0, -0.15, 0.84, 0.08, 0.18, 0.12]],
  smooth: 0.25,
  mat: 'bone'
})
const mandible = f.blob({
  name: 'mandible',
  paths: [
    {points: [[-0.78, -1.20, -0.15], [-0.73, -1.48, 0.12], [-0.45, -1.60, 0.50], [0, -1.63, 0.73], [0.45, -1.60, 0.50], [0.73, -1.48, 0.12], [0.78, -1.20, -0.15]], r: 0.15},
    {points: [[-0.78, -1.25, -0.12], [-0.82, -0.82, -0.20], [-0.78, -0.43, -0.30]], r: 0.13, r2: 0.10},
    {points: [[0.78, -1.25, -0.12], [0.82, -0.82, -0.20], [0.78, -0.43, -0.30]], r: 0.13, r2: 0.10}
  ],
  balls: [[0, -1.62, 0.68, 0.25, 0.18, 0.20]],
  smooth: 0.25,
  mat: 'bone'
})

step(2); say("The cranium protects the brain, while the deep eye sockets and nasal opening make the face light but strong.")
const leftOrbit = f.blob({balls: [[-0.48, 0.10, 0.44, 0.28, 0.29, 0.12]], smooth: 0.15, mat: 'paint:#302922'})
const rightOrbit = f.blob({balls: [[0.48, 0.10, 0.44, 0.28, 0.29, 0.12]], smooth: 0.15, mat: 'paint:#302922'})
const nasalCavity = f.blob({balls: [[0, -0.52, 0.50, 0.15, 0.23, 0.11]], smooth: 0.15, mat: 'paint:#302922'})
skull.label('cranium', {color: 'purple'})
leftOrbit.label('eye socket', {color: 'blue'})
nasalCavity.label('nasal opening', {color: 'teal'})

step(3); say("The upper teeth sit in the skull; the lower teeth sit in the mandible, which hinges near each ear.")
let upperIncisor
for (const i of range(14)) {
  const a = -78 + i * 12
  const th = a * PI / 180
  const back = abs(a) >= 42
  const tooth = f.box({
    w: back ? 0.19 : 0.15,
    h: abs(a) === 30 ? 0.27 : 0.22,
    d: back ? 0.23 : 0.18,
    round: 0.045,
    at: [0.64 * sin(th), -1.04, 0.46 + 0.50 * cos(th)],
    rot: [0, a, 0],
    mat: 'ceramic:#F2E8D2'
  })
  if (i === 6) upperIncisor = tooth
}
for (const i of range(14)) {
  const a = -78 + i * 12
  const th = a * PI / 180
  const back = abs(a) >= 42
  f.box({
    w: back ? 0.17 : 0.13,
    h: abs(a) === 30 ? 0.22 : 0.19,
    d: back ? 0.21 : 0.16,
    round: 0.04,
    at: [0.58 * sin(th), -1.38, 0.45 + 0.44 * cos(th)],
    rot: [0, a, 0],
    mat: 'ceramic:#EEE2C8'
  })
}
const jawJoint = f.sphere({r: 0.13, at: [-0.78, -0.40, -0.30], mat: 'bone'})
f.sphere({r: 0.13, at: [0.78, -0.40, -0.30], mat: 'bone'})
upperIncisor.label('upper teeth', {color: 'orange'})
mandible.label('mandible', {color: 'red'})
jawJoint.label('jaw joint', {color: 'green'})

step(4); say("From the side, zigzag sutures join cranial bones, and each cheek arch links the face to the skull.")
const coronalSuture = f.tube({
  points: [[-1.04, 1.39, 0.18], [-0.91, 1.52, 0.26], [-0.78, 1.68, 0.29], [-0.65, 1.80, 0.28], [-0.52, 1.92, 0.33], [-0.39, 1.99, 0.26], [-0.26, 2.03, 0.34], [-0.13, 2.09, 0.26], [0, 2.07, 0.32], [0.13, 2.09, 0.26], [0.26, 2.03, 0.34], [0.39, 1.99, 0.26], [0.52, 1.92, 0.33], [0.65, 1.80, 0.28], [0.78, 1.68, 0.29], [0.91, 1.52, 0.26], [1.04, 1.39, 0.18]],
  r: 0.012,
  mat: 'paint:#8B7256'
})
const sagittalSuture = f.tube({
  points: [[0, 1.90, 0.50], [0.015, 2.03, 0.35], [-0.012, 2.12, 0.20], [0.012, 2.18, 0.02], [-0.01, 2.19, -0.18], [0.012, 2.13, -0.38], [-0.01, 2.02, -0.58], [0, 1.84, -0.78]],
  r: 0.012,
  mat: 'paint:#8B7256'
})
sagittalSuture.label('bone sutures', {color: 'brown'})
cheekArch.label('cheek arch', {color: 'blue'})
f.view({tilt: 18, turn: -42, zoom: 1.02})

step(5); say("So the skull is a protective brain case, a light facial framework, and a separate hinged jaw.")
f.view({tilt: 12, turn: 18, zoom: 1})
highlight(c.x + 120, c.y + c.h + 12, c.w - 240, 40, {color: 'yellow'})
text(c.x + c.w / 2, c.y + c.h + 38, 'brain case, facial frame, hinged jaw', {size: 24, bold: true})
suggest(['Show the jaw hinge', 'View from the side'])