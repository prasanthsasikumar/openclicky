step(1); say("Let me build a 3D model of a passenger airplane, starting with its long body, wings, and tail.")
const f = figure(250, 80, 780, 510, {tilt: 18, turn: -28, zoom: 1.05, ground: false})
text(640, 48, 'Passenger airplane', {size: 28, bold: true, color: 'ink'})
const fuselage = f.lathe({
  points: [[0.08, -3.1], [0.42, -2.8], [0.72, -2.35], [0.78, -1.5], [0.8, 0.8], [0.68, 1.9], [0.5, 2.65], [0.18, 3.05]],
  axis: 'x',
  mat: 'paint:#E7EDF2'
})
fuselage.label('fuselage', {color: 'blue'})
const mainWing = f.box({w: 2.8, h: 0.12, d: 5.0, at: [0.35, -0.02, 0], rot: [0, -8, 0], mat: 'alu'})
mainWing.label('main wing', {color: 'blue'})
const tailWing = f.box({w: 1.15, h: 0.1, d: 2.1, at: [2.3, 0.12, 0], rot: [0, -5, 0], mat: 'alu'})
tailWing.label('tail wings', {color: 'blue'})
const fin = f.extrude({
  points: [[1.75, 0], [2.65, 0], [2.35, 1.35], [2.0, 1.2]],
  depth: 0.16,
  bevel: 0.05,
  mat: 'paint:#D9E1E8'
})
fin.label('tail fin', {color: 'blue'})
const nose = f.cone({r: 0.38, h: 0.16, axis: '-x', at: [-3.0, 0, 0], mat: 'plastic:#202A33'})
const cockpit = f.box({w: 0.45, h: 0.22, d: 0.9, at: [-2.38, 0.42, 0], rot: [0, -18, 0], mat: 'glass'})
cockpit.label('cockpit', {color: 'purple'})
const wingTipL = f.box({w: 0.3, h: 0.32, d: 0.12, at: [0.9, 0.18, -2.55], rot: [0, 0, -18], mat: 'paint:#D9E1E8'})
const wingTipR = f.box({w: 0.3, h: 0.32, d: 0.12, at: [0.9, 0.18, 2.55], rot: [0, 0, 18], mat: 'paint:#D9E1E8'})

step(2); say("Two engines hang beneath the wings, where fans pull air in and push it backward to make thrust.")
const engineL = f.cyl({r: 0.42, h: 1.35, axis: 'x', at: [0.15, -0.48, -1.2], mat: 'alu'})
const engineR = f.cyl({r: 0.42, h: 1.35, axis: 'x', at: [0.15, -0.48, 1.2], mat: 'alu'})
const intakeL = f.torus({r: 0.33, tube: 0.09, axis: 'x', at: [-0.54, -0.48, -1.2], mat: 'steel'})
const intakeR = f.torus({r: 0.33, tube: 0.09, axis: 'x', at: [-0.54, -0.48, 1.2], mat: 'steel'})
const fanL = f.cyl({r: 0.28, h: 0.05, axis: 'x', at: [-0.59, -0.48, -1.2], mat: 'steel'}).spin({axis: 'x', period: 0.7})
const fanR = f.cyl({r: 0.28, h: 0.05, axis: 'x', at: [-0.59, -0.48, 1.2], mat: 'steel'}).spin({axis: 'x', period: 0.7})
intakeL.label('fan', {color: 'orange'})
intakeR.label('fan', {color: 'orange'})
const exhaustL = f.cone({r: 0.3, h: 0.18, axis: 'x', at: [0.9, -0.48, -1.2], mat: 'iron'})
const exhaustR = f.cone({r: 0.3, h: 0.18, axis: 'x', at: [0.9, -0.48, 1.2], mat: 'iron'})
f.arrow({from: [1.35, -0.48, -1.2], to: [2.1, -0.48, -1.2], color: 'red'})
f.arrow({from: [1.35, -0.48, 1.2], to: [2.1, -0.48, 1.2], color: 'red'})

step(3); say("Rows of dark windows run along the cabin, while the landing gear supports the airplane when it is on the ground.")
for (const z of [-0.36, 0.36]) {
  for (const i of range(8)) {
    const window = f.sphere({r: 0.075, at: [-1.75 + i * 0.38, 0.43, z], mat: 'glass'})
  }
}
const gearFront = f.cyl({r: 0.06, h: 0.48, at: [-1.25, -0.58, 0], mat: 'steel'})
const gearMainL = f.cyl({r: 0.08, h: 0.62, at: [0.65, -0.65, -0.85], mat: 'steel'})
const gearMainR = f.cyl({r: 0.08, h: 0.62, at: [0.65, -0.65, 0.85], mat: 'steel'})
const wheelFront = f.torus({r: 0.12, tube: 0.045, axis: 'x', at: [-1.25, -0.88, 0], mat: 'rubber'})
const wheelL = f.torus({r: 0.16, tube: 0.055, axis: 'x', at: [0.65, -1.02, -0.85], mat: 'rubber'})
const wheelR = f.torus({r: 0.16, tube: 0.055, axis: 'x', at: [0.65, -1.02, 0.85], mat: 'rubber'})
f.label(mainWing, 'wings', {color: 'blue'})
f.label(fuselage, 'cabin body', {color: 'blue'})

step(4); say("The wings lift the airplane, the engines provide forward thrust, and the tail helps keep it steady in flight.")
f.arrow({from: [2.2, 0.9, 0], to: [2.2, 1.7, 0], color: 'green'})
f.arrow({from: [0.1, -0.05, 0], to: [-0.1, 0.8, 0], color: 'green'})
text(640, 550, 'Key idea: wings lift, engines thrust, tail stabilizes', {size: 23, bold: true, color: 'ink'})