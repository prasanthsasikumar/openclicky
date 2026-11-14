step(1); say("Let me build a 3D model of a human skull for you, starting with the cranium that protects the brain.")
const c = {x: 280, y: 60, w: 720, h: 560}
text(c.x + c.w / 2, c.y - 20, 'Human Skull', {size: 28, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 15, turn: -25, zoom: 1.1})

const cranium = f.blob({balls: [
  [0, 1.6, 0, 1.7, 1.9, 1.7],
  [-0.6, 1.8, 0.3, 1.3, 1.5, 1.3],
  [0.6, 1.8, 0.3, 1.3, 1.5, 1.3],
  [0, 2.2, -0.2, 1.4, 1.2, 1.4],
  [0, 1.0, -0.5, 1.5, 1.3, 1.5],
  [-0.8, 1.4, 0, 1.1, 1.4, 1.2],
  [0.8, 1.4, 0, 1.1, 1.4, 1.2]
], smooth: 0.5, mat: 'bone'})
cranium.label('cranium', {color: 'brown'})

step(2); say("The face has two deep eye sockets called orbits, a nasal opening, and the upper jaw with teeth.")
const face = f.blob({balls: [
  [0, 0.2, 1.2, 1.4, 1.2, 0.9],
  [-0.5, 0.3, 1.5, 0.9, 1.0, 0.7],
  [0.5, 0.3, 1.5, 0.9, 1.0, 0.7],
  [0, -0.3, 1.4, 1.1, 0.8, 0.7],
  [-0.35, 0.1, 1.8, 0.55, 0.5, 0.45],
  [0.35, 0.1, 1.8, 0.55, 0.5, 0.45],
  [0, -0.1, 1.9, 0.35, 0.4, 0.3]
], smooth: 0.4, mat: 'bone'})
face.label('face', {color: 'brown'})

const leftOrbit = f.sphere({r: 0.48, at: [-0.55, 0.25, 1.75], mat: 'paint:#1a1a1a'})
leftOrbit.label('orbit', {color: 'blue'})
const rightOrbit = f.sphere({r: 0.48, at: [0.55, 0.25, 1.75], mat: 'paint:#1a1a1a'})

const nasalCavity = f.sphere({r: 0.32, at: [0, -0.15, 1.95], mat: 'paint:#2a1a1a'})
nasalCavity.label('nasal opening', {color: 'purple'})

const upperTeeth = f.box({w: 1.3, h: 0.18, d: 0.35, at: [0, -0.55, 1.55], round: 0.04, mat: 'bone'})
upperTeeth.label('upper teeth', {color: 'teal'})

step(3); say("The lower jaw, or mandible, is the only skull bone that moves, letting you chew and speak.")
const mandible = f.blob({balls: [
  [0, -1.0, 1.1, 1.2, 0.5, 0.9],
  [-0.7, -0.85, 1.3, 0.5, 0.45, 0.5],
  [0.7, -0.85, 1.3, 0.5, 0.45, 0.5],
  [-0.9, -0.5, 0.9, 0.35, 0.55, 0.4],
  [0.9, -0.5, 0.9, 0.35, 0.55, 0.4],
  [0, -1.2, 1.3, 0.8, 0.35, 0.6]
], smooth: 0.4, mat: 'bone'})
mandible.label('mandible', {color: 'green'})

const lowerTeeth = f.box({w: 1.1, h: 0.16, d: 0.3, at: [0, -0.72, 1.5], round: 0.04, mat: 'bone'})
lowerTeeth.label('lower teeth', {color: 'teal'})

step(4); say("The skull is made of 22 bones fused together, with the mandible being the only one that moves.")
const sutureLine = f.tube({points: [[-1.2, 1.8, 0.5], [-0.6, 2.1, 0.8], [0, 2.2, 0.9], [0.6, 2.1, 0.8], [1.2, 1.8, 0.5]], r: 0.04})
sutureLine.label('sutures', {color: 'orange'})

highlight(c.x + 30, c.y + c.h - 55, c.w - 60, 40, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 35, '22 bones, one moving jaw, protects the brain', {align: 'start', size: 22})