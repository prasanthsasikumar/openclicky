step(1); say("Let me build a 3D model of a human skull. The rounded dome on top is the cranium, the bony case that protects the brain, with the eye sockets at the front.")
const c = {x: 330, y: 95, w: 620, h: 475}
text(640, 64, 'A human skull', {size: 28, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 12, turn: 26})
const cranium = f.blob({
  balls: [[−0.02, 0.5, 0, 1.02, 0.95, 0.9], [-0.28, 0.18, 0, 0.82, 0.75, 0.82], [0.3, 0.42, 0, 0.72, 0.7, 0.78]],
  sticks: [{from: [0.62, 0.44, 0.6], to: [0.74, 0.5, 0], r: 0.13}, {from: [0.74, 0.5, 0], to: [0.62, 0.44, -0.6], r: 0.13}],
  holes: [[0.7, 0.16, 0.35, 0.28], [0.7, 0.16, -0.35, 0.28]],
  smooth: 0.42, mat: 'bone'
})
cranium.label('cranium', {color: 'blue'})
const socketR = f.sphere({r: 0.16, at: [0.6, 0.16, 0.35], mat: 'plastic:#26262C'})
const socketL = f.sphere({r: 0.16, at: [0.6, 0.16, -0.35], mat: 'plastic:#26262C'})

step(2); say("Under the eyes sits the upper jaw, with the pear-shaped nasal opening in the middle and the cheekbones arching back toward the ears.")
const maxilla = f.blob({
  balls: [[0.56, -0.36, 0, 0.5, 0.45, 0.52], [0.7, -0.05, 0, 0.34, 0.28, 0.42]],
  holes: [[1.0, -0.26, 0, 0.2], [0.94, -0.46, 0, 0.12]],
  smooth: 0.4, mat: 'bone'
})
maxilla.label('upper jaw', {color: 'green'})
const nose = f.sphere({r: 0.14, at: [0.9, -0.3, 0], mat: 'plastic:#26262C'})
nose.label('nasal cavity', {color: 'purple'})
const cheek = f.blob({
  paths: [
    {points: [[0.5, -0.1, 0.55], [0.2, -0.04, 0.78], [-0.06, 0.04, 0.84]], r: 0.12, r2: 0.1},
    {points: [[0.5, -0.1, -0.55], [0.2, -0.04, -0.78], [-0.06, 0.04, -0.84]], r: 0.12, r2: 0.1}
  ], smooth: 0.4, mat: 'bone'
})
cheek.label('cheekbone', {color: 'teal'})
socketR.label('eye socket', {color: 'ink'})

step(3); say("The upper teeth are rooted in that jaw bone, right under the nasal opening they are the only part of the skull you can see on a living face.")
const teethTop = f.box({w: 0.09, h: 0.15, d: 0.1, at: [0.82, -0.76, 0], around: {n: 14, axis: 'y', center: [0.6, -0.76, 0]}, mat: 'ceramic'})
teethTop.label('teeth', {color: 'brown'})

step(4); say("The lower jaw, called the mandible, is the only bone of the skull that moves, swinging on two hinges just in front of the ears.")
const mandible = f.blob({
  balls: [[0.84, -1.0, 0, 0.22, 0.18, 0.3]],
  paths: [
    {points: [[-0.12, 0.02, 0.6], [0.0, -0.7, 0.62], [0.4, -1.08, 0.52], [0.8, -1.06, 0.28], [0.86, -0.98, 0]], r: 0.17, r2: 0.14},
    {points: [[-0.12, 0.02, -0.6], [0.0, -0.7, -0.62], [0.4, -1.08, -0.52], [0.8, -1.06, -0.28], [0.86, -0.98, 0]], r: 0.17, r2: 0.14}
  ], smooth: 0.4, mat: 'bone'
}).bob({axis: 'y', amount: 0.05, period: 3})
mandible.label('lower jaw', {color: 'red'})
const teethLow = f.box({w: 0.09, h: 0.14, d: 0.1, at: [0.78, -0.94, 0], around: {n: 14, axis: 'y', center: [0.58, -0.94, 0]}, mat: 'ceramic'})

step(5); say("So a skull is armour for the brain and a frame for the face, made of twenty-two bones locked together, with just one of them free to move.")
f.view({tilt: 6, turn: 46, zoom: 1})
highlight(390, 576, 500, 40, {color: 'yellow'})
text(640, 596, '22 bones, and only the jaw moves', {size: 24})