step(1); say("Let me build a 3D model of a human skull: a rounded braincase sitting above the bones of the face and jaw.")
const f = figure(290, 70, 700, 480, {tilt: 14, turn: -28, ground: false})
text(640, 46, 'Human skull', {size: 26, bold: true})
const cranium = f.blob({balls: [[0, 0.85, -0.15, 1.12, 0.95, 1.25], [0, 0.45, 0.5, 0.78, 0.7, 0.72]], smooth: 0.45, mat: 'bone'})
cranium.label('cranium')
const face = f.blob({balls: [[0, 0.02, 0.72, 0.72, 0.5, 0.46], [0, -0.45, 0.68, 0.55, 0.45, 0.4]], holes: [[-0.38, 0.05, 1.05, 0.26], [0.38, 0.05, 1.05, 0.26], [0, -0.32, 1.05, 0.2]], smooth: 0.4, mat: 'bone'})
const cheek = f.blob({balls: [[0.62, -0.22, 0.62, 0.16]], sticks: [{from: [0.62, -0.22, 0.62], to: [0.98, -0.12, 0.05], r: 0.09}], smooth: 0.3, mat: 'bone', mirror: 'x'})
const mandible = f.blob({balls: [[0, -1.08, 0.72, 0.34, 0.28, 0.3]], sticks: [{from: [0, -1.05, 0.68], to: [0.6, -0.98, 0.3], r: 0.15}, {from: [0.6, -0.98, 0.3], to: [0.72, -0.35, -0.1], r: 0.13}], smooth: 0.35, mat: 'bone', mirror: 'x'})
mandible.label('lower jaw')

step(2); say("Two dark openings are the eye sockets, with the nasal cavity between them, and two rows of teeth meet below.")
face.label('eye sockets')
let firstTooth = null
for (const i of range(8)) {
  const a = -1.0 + i * (2.0 / 7)
  const tu = f.box({w: 0.1, h: 0.17, d: 0.1, at: [0.45 * sin(a), -0.86, 0.55 + 0.42 * cos(a)], rot: [0, a * 57.3, 0], mat: 'ceramic'})
  if (i === 0) firstTooth = tu
  f.box({w: 0.09, h: 0.16, d: 0.09, at: [0.4 * sin(a), -1.0, 0.5 + 0.38 * cos(a)], rot: [0, a * 57.3, 0], mat: 'ceramic'})
}
firstTooth.label('teeth')

step(3); say("From the side you see the cheekbone arch and the hinge where the lower jaw swings down to open.")
f.view({tilt: 10, turn: -78})
cheek.label('cheekbone')
f.arrow({from: [0.75, -0.45, 0.0], to: [0.8, -1.05, 0.35], color: 'red'})

step(4); say("So the skull is a helmet for the brain, a frame for the face, and a pair of chewing jaws.")
f.view({tilt: 14, turn: -28})
highlight(360, 575, 560, 36, {color: 'yellow'})
text(640, 593, 'helmet for the brain, frame for the face, jaws for chewing', {size: 22, bold: true})
suggest(['Show the skull from behind', 'Label every bone'])