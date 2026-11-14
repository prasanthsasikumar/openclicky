step(1); say("Let me build you a 3D model of a human skull, starting with the rounded case that holds your brain.")
text(640, 74, 'The human skull', {size: 28, bold: true})
const f = figure(320, 100, 640, 470, {tilt: 10, turn: -28, ground: false})
const cranium = f.blob({balls: [[0, 0.75, -0.05, 0.95, 1.0, 1.0], [0, 0.6, -0.7, 0.78], [0, 0.5, 0.6, 0.6, 0.62, 0.5], [-0.5, -0.05, 0.5, 0.42], [0.5, -0.05, 0.5, 0.42], [0, -0.4, 0.5, 0.48, 0.4, 0.36]], holes: [[-0.42, 0.2, 0.85, 0.3], [0.42, 0.2, 0.85, 0.3], [0, -0.2, 0.95, 0.17]], smooth: 0.45, mat: 'bone'})
const socketL = f.sphere({r: 0.22, at: [-0.42, 0.2, 0.55], mat: 'plastic:#171310'})
f.sphere({r: 0.22, at: [0.42, 0.2, 0.55], mat: 'plastic:#171310'})
const nose = f.sphere({r: 0.1, at: [0, -0.2, 0.72], mat: 'plastic:#171310'})

step(2); say("Now the cheekbones, two rows of teeth, and the lower jaw, which hangs below the rest of the skull.")
const cheek = f.tube({points: [[0.6, 0.0, 0.62], [0.92, 0.05, 0.1], [0.78, 0.1, -0.45]], r: 0.09, mirror: 'x', mat: 'bone'})
const upTeeth = f.box({w: 0.12, h: 0.15, d: 0.1, at: [-0.39, -0.74, 0.82], copies: {n: 7, step: [0.13, 0, 0]}, mat: 'ceramic'})
f.box({w: 0.12, h: 0.14, d: 0.12, at: [0.45, -0.72, 0.58], copies: {n: 3, step: [0, 0, -0.15]}, mirror: 'x', mat: 'ceramic'})
const jaw = f.blob({balls: [[0, -1.08, 0.72, 0.34, 0.26, 0.3], [-0.34, -1.1, 0.45, 0.24], [0.34, -1.1, 0.45, 0.24], [-