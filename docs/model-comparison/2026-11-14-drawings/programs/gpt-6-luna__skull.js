step(1); say("Let me build a 3D model of a human skull, with its rounded braincase and sturdy face.")
const c = {x: 320, y: 75, w: 640, h: 480}
text(640, 48, 'Human skull', {size: 28, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 8, turn: 0, zoom: 1.05})
const braincase = f.blob({
  balls: [
    [0, 0.55, 0, 0.9, 1.05, 0.75],
    [0, -0.22, 0.28, 0.62, 0.78, 0.52],
    [-0.63, -0.12, 0.05, 0.3, 0.36, 0.3],
    [0.63, -0.12, 0.05, 0.3, 0.36, 0.3]
  ],
  holes: [[-0.42, 0.25, 0.66, 0.26], [0.42, 0.25, 0.66, 0.26], [0, -0.28, 0.72, 0.16]],
  smooth: 0.38,
  mat: 'bone'
})
braincase.label('braincase', {color: 'brown'})
const jaw = f.blob({
  balls: [[0, -1.12, 0.22, 0.55, 0.3, 0.4], [-0.56, -0.78, 0.12, 0.18, 0.42, 0.2], [0.56, -0.78, 0.12, 0.18, 0.42, 0.2]],
  smooth: 0.34,
  mat: 'bone'
})
jaw.label('lower jaw', {color: 'brown'})

step(2); say("The large openings are eye sockets, and the smaller opening below is the nose passage.")
for (const x of [-0.42, 0.42]) {
  f.sphere({r: 0.2, at: [x, 0.25, 0.57], mat: 'stone'})
  f.torus({r: 0.29, tube: 0.055, axis: 'z', at: [x, 0.25, 0.79], mat: 'bone'})
}
const nose = f.blob({balls: [[0, -0.28, 0.63, 0.12, 0.2, 0.08]], mat: 'stone', smooth: 0.2})
nose.label('nose opening', {color: 'blue'})
for (const side of [-1, 1]) {
  f.tube({points: [[side * 0.16, 0.55, 0.71], [side * 0.42, 0.59, 0.7], [side * 0.68, 0.48, 0.55]], r: 0.07, mat: 'bone'})
}

step(3); say("Cheekbones frame the face, while the upper and lower jaws hold rows of teeth.")
for (const side of [-1, 1]) {
  f.tube({points: [[side * 0.48, 0.03, 0.62], [side * 0.78, -0.18, 0.48], [side * 0.55, -0.42, 0.68]], r: 0.11, mat: 'bone'})
}
for (let i = 0; i < 7; i++) {
  const x = -0.42 + i * 0.14
  f.box({w: 0.12, h: 0.19, d: 0.13, round: 0.035, at: [x, -0.57, 0.77], mat: 'bone', color: '#F0E7D5'})
  f.box({w: 0.12, h: 0.18, d: 0.13, round: 0.035, at: [x, -0.91, 0.75], mat: 'bone', color: '#F0E7D5'})
}

step(4); say("The skull protects the brain and supports the structures of the face.")
highlight(380, 568, 520, 34, {color: 'yellow'})
text(640, 585, 'A strong case for the brain', {size: 24, bold: true})