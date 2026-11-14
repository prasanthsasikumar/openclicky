step(1); say("Let me build a 3D model of a human skull for you, with the rounded brain case and the face bones.")
const c = {x: 300, y: 90, w: 680, h: 480}
text(c.x + c.w / 2, c.y - 26, 'Human skull', {size: 26, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 12, turn: -28})
const cranium = f.blob({
  balls: [
    [0, 0.9, -0.1, 1.15, 1.25, 1.1],
    [0, -0.1, 0.0, 1.05, 0.9, 1.05],
    [0.75, -0.35, 0.35, 0.45, 0.55, 0.45],
    [-0.75, -0.35, 0.35, 0.45, 0.55, 0.45],
    [0, -0.65, 0.55, 0.55, 0.45, 0.55],
    [0.95, 0.1, 0.0, 0.35, 0.6, 0.35],
    [-0.95, 0.1, 0.0, 0.35, 0.6, 0.35],
  ],
  holes: [
    [0.48, -0.25, 0.95, 0.38],
    [-0.48, -0.25, 0.95, 0.38],
    [0, -0.55, 1.05, 0.28],
  ],
  smooth: 0.45,
  mat: 'bone'
})
const mandible = f.blob({
  balls: [
    [0, -1.15, 0.35, 0.55, 0.35, 0.55],
    [0.55, -0.95, 0.2, 0.28, 0.5, 0.28],
    [-0.55, -0.95, 0.2, 0.28, 0.5, 0.28],
  ],
  smooth: 0.4,
  mat: 'bone'
})

step(2); say("Now I add the row of teeth in the upper jaw and the lower jaw, so the skull can bite and chew.")
const upTeeth = f.box({w: 0.1, h: 0.16, d: 0.1, at: [-0.54, -0.85, 0.82], copies: {n: 7, step: [0.18, 0, 0]}, mat: 'plastic: