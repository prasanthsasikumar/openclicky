step(1); say("Let me build a 3D model of a human skull for you, starting with the rounded cranium that protects the brain.")
const c = {x: 270, y: 80, w: 740, h: 500}
text(c.x + c.w / 2, c.y - 24, 'The Human Skull', {size: 28, bold: true, color: 'white'})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 12, turn: -28, zoom: 1.05})

const cranium = f.blob({
  balls: [
    [0, 0.55, -0.15, 1.08, 1.02, 1.28],
    [0, 0.72, 0.45, 0.88, 0.75, 0.75],
    [0, 0.25, -0.75, 0.82, 0.68, 0.65],
    [-0.58, 0.22, 0.32, 0.35, 0.45, 0.48],
    [0.58, 0.22, 0.32, 0.35, 0.45, 0.48]
  ],
  holes: [
    [-0.88, 0.35, 0.35, 0.28],
    [0.88, 0.35, 0.35, 0.28],
    [0, -0.35, -0.25, 0.35]
  ],
  smooth: 0.35,
  mat: 'bone'
})
cranium.label('cranium (brain case)', {color: 'yellow'})

const sutureCoronal = f.tube({
  points: [[-0.85, 0.35, 0.25], [-0.55, 1.15, 0.28], [0, 1.48, 0.25], [0.55, 1.15, 0.28], [0.85, 0.35, 0.25]],
  r: 0.018,
  mat: 'bone:#B8AA92'
})

step(2); say("At the front are the cheekbones, the upper jaw, and deep sockets for the eyes and nose.")
const face = f.blob({
  balls: [
    [0, -0.02, 0.78, 0.68, 0.55, 0.52],
    [-0.56, 0.04, 0.74, 0.36, 0.28, 0.34],
    [0.56, 0.04, 0.74, 0.36, 0.28, 0.34],
    [-0.38, 0.38, 0.85, 0.28, 0.16, 0.22],
    [0.38, 0.38, 0.85, 0.28, 0.16, 0.22],
    [0, 0.26, 0.96, 0.16, 0.24, 0.22],
    [0, -0.34, 0.86, 0.46, 0.28, 0.42]
  ],
  sticks: [
    {from: [-0.74, -0.02, 0.65], to: [-0.84, -0.05, 0.05], r: 0.1, r2: 0.09},
    {from: [0.74, -0.02, 0.65], to: [0.84, -0.05, 0.05], r: 0.1, r2: 0.09}
  ],
  holes: [
    [-0.35, 0.14, 1.05, 0.27],
    [0.35, 0.14, 1.05, 0.27],
    [0, -0.06, 1.15, 0.19]
  ],
  smooth: 0.22,
  mat: 'bone'
})
face.label('eye orbit & maxilla', {color: 'orange'})

const eyeSocketL = f.sphere({r: 0.22, at: [-0.35, 0.14, 0.86], scale: [1, 0.9, 0.7], mat: 'paint:#221E1B'})
const eyeSocketR = f.sphere({r: 0.22, at: [0.35, 0.14, 0.86], scale: [1, 0.9, 0.7], mat: 'paint:#221E1B'})
const nasalCavity = f.cone({r: 0.14, h: 0.34, at: [0, -0.06, 0.96], scale: [0.8, 1.1, 0.7], mat: 'paint:#221E1B'})

step(3); say("Underneath sits the mandible, or lower jaw, which is the only skull bone that hinges open to chew and speak.")
const mandible = f.blob({
  sticks: [
    {from: [0, -0.92, 0.92], to: [-0.52, -0.86, 0.48], r: 0.19, r2: 0.17},
    {from: [0, -0.92, 0.92], to: [0.52, -0.86, 0.48], r: 0.19, r2: 0.17},
    {from: [-0.52, -0.86, 0.48], to: [-0.64, -0.15, 0.18], r: 0.16, r2: 0.12},
    {from: [0.52, -0.86, 0.48], to: [0.64, -0.15, 0.18], r: 0.16, r2: 0.12}
  ],
  balls: [
    [0, -0.94, 0.98, 0.24, 0.21, 0.18]
  ],
  smooth: 0.2,
  mat: 'bone:#E2D7C3'
}).bob({axis: 'y', amount: 0.04, period: 2.4})
mandible.label('mandible (lower jaw)', {color: 'teal'})

const upperTeeth = f.capsule({
  r: 0.048, h: 0.12,
  at: [0, -0.54, 1.15],
  around: {n: 10, axis: 'y', center: [0, -0.54, 0.78]},
  mat: 'ceramic'
})

const lowerTeeth = f.capsule({
  r: 0.045, h: 0.11,
  at: [0, -0.70, 1.12],
  around: {n: 10, axis: 'y', center: [0, -0.70, 0.76]},
  parent: mandible,
  mat: 'ceramic'
})

step(4); say("Let's turn it around so you can see how the twenty-two bones lock together like a helmet around the head.")
f.view({tilt: 16, turn: 38, zoom: 1.08})

suggest(['Quiz me on the skull', 'Show the brain inside'])