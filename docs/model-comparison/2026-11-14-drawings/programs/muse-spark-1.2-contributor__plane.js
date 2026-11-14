step(1); say("Let me build a 3D model of the airplane for you.")
text(W/2, 38, 'Airplane — 3D Model', {size: 28, bold: true})
const f = figure(320, 110, 640, 470, {tilt: 11, turn: -23, zoom: 1.05, ground: false})

// fuselage - long tube with nose and tail taper
const fuselage = f.lathe({points: [[0, 2.95],[0.24, 2.68],[0.38, 2.35],[0.44, 1.9],[0.45, 0.8],[0.45, -1.15],[0.38, -1.85],[0.18, -2.55],[0.04, -2.92]], axis: 'x', mat: 'paint:white'})
// cheatline stripe
const stripe = f.cyl({r: 0.452, h: 3.4, axis: 'x', at: [0.35, -0.06, 0], mat: 'paint:#2A7FFF', opacity: 0.95})
// cockpit windshield
const cockpit = f.box({w: 0.45, h: 0.12, d: 0.38, at: [2.05, 0.22, 0], rot: [0, -12, 0], mat: 'glass', opacity: 0.9})
// main wings - swept, thin
const leftWing = f.extrude({points: [[-0.55, 0],[0.55, 0],[0.95, 2.05],[-0.12, 2.05]], depth: 0.07, mat: 'paint:white', at: [0.18, -0.14, 0.44], rot: [90, 0, 0]})
const rightWing = f.extrude({points: [[-0.55, 0],[0.55, 0],[0.95, -2.05],[-0.12, -2.05]], depth: 0.07, mat: 'paint:white', at: [0.18, -0.14, -0.44], rot: [90, 0, 0]})
// wing tips winglets
const wingletL = f.box({w: 0.18, h: 0.32, d: 0.04, at: [0.48, 0.02, 2.52], rot: [0, 28, 28], mat: 'paint:white'})
const wingletR = f.box({w: 0.18, h: 0.32, d: 0.04, at: [0.48, 0.02, -2.52], rot: [0, -28, -28], mat: 'paint:white'})
// vertical tail
const vStab = f.extrude({points: [[-0.45, 0],[0.38, 0],[0.62, 1.05],[-0.05, 1.05]], depth: 0.08, mat: 'paint:white', at: [-2.18, 0.38, 0]})
// horizontal stabilizers
const leftHStab = f.extrude({points: [[-0.32, 0],[0.32, 0],[0.52, 0.95],[-0.12, 0.95]], depth: 0.05, mat: 'paint:white', at: [-2.25, 0.06, 0.14], rot: [90, 0, 0]})
const rightHStab = f.extrude({points: [[-0.32, 0],[0.32, 0],[0.52, -0.95],[-0.12, -0.95]], depth: 0.05, mat: 'paint:white', at: [-2.25, 0.06, -0.14], rot: [90, 0, 0]})
// engines under wings
const engL = f.cyl({r: 0.18, h: 0.72, axis: 'x', at: [0.22, -0.34, 1.05], mat: 'alu'})
const engR = f.cyl({r: 0.18, h: 0.72, axis: 'x', at: [0.22, -0.34, -1.05], mat: 'alu'})
const intakeL = f.cyl({r: 0.175, h: 0.04, axis: 'x', at: [-0.12, -0.34, 1.05], mat: 'iron'})
const intakeR = f.cyl({r: 0.175, h: 0.04, axis: 'x', at: [-0.12, -0.34, -1.05], mat: 'iron'})
const nozzleL = f.cyl({r: 0.155, h: 0.05, axis: 'x', at: [0.58, -0.34, 1.05], mat: 'iron'})
const nozzleR = f.cyl({r: 0.155, h: 0.05, axis: 'x', at: [0.58, -0.34, -1.05], mat: 'iron'})
// pylon
const pylonL = f.box({w: 0.22, h: 0.18, d: 0.06, at: [0.20, -0.24, 1.05], mat: 'alu'})
const pylonR = f.box({w: 0.22, h: 0.18, d: 0.06, at: [0.20, -0.24, -1.05], mat: 'alu'})
// passenger windows - rows of tiny dark dots along fuselage
for (let i = 0; i < 14; i++) {
  f.box({w: 0.04, h: 0.05, d: 0.012, at: [1.55 - i*0.21, 0.14, 0.445], mat: 'paint:#1A2A44'})
  f.box({w: 0.04, h: 0.05, d: 0.012, at: [1.55 - i*0.21, 0.14, -0.445], mat: 'paint:#1A2A44'})
}
// nose gear and main gear (tiny)
const noseGear = f.cyl({r: 0.03, h: 0.18, axis: 'y', at: [1.6, -0.48, 0], mat: 'iron'})
const wheelN = f.cyl({r: 0.06, h: 0.04, axis: 'z', at: [1.6, -0.58, 0], mat: 'rubber'})
const mainGearL = f.cyl({r: 0.03, h: 0.14, axis: 'y', at: [0.05, -0.48, 0.35], mat: 'iron'})
const wheelML = f.cyl({r: 0.055, h: 0.06, axis: 'z', at: [0.05, -0.57, 0.35], mat: 'rubber'})
const mainGearR = f.cyl({r: 0.03, h: 0.14, axis: 'y', at: [0.05, -0.48, -0.35], mat: 'iron'})
const wheelMR = f.cyl({r: 0.055, h: 0.06, axis: 'z', at: [0.05, -0.57, -0.35], mat: 'rubber'})

step(2); say("The long fuselage holds the cabin, the swept wings make lift, and the tail keeps it steady.")
fuselage.label('fuselage', {color: 'blue'})
leftWing.label('wing', {color: 'blue'})
vStab.label('tail fin', {color: 'blue'})
leftHStab.label('stabilizer', {color: 'ink'})

step(3); say("Twin jet engines hang under the wings, and rows of windows line the cabin to the cockpit.")
engL.label('jet engine', {color: 'purple'})
cockpit.label('cockpit', {color: 'teal'})
f.view({tilt: 8, turn: -36, zoom: 1.08})

step(4); say("In flight thrust pushes forward and the curved wings lift the plane up through the air.")
const thrustL = f.arrow({from: [0.9, -0.34, 1.05], to: [-0.9, -0.34, 1.05], color: 'orange'})
const thrustR = f.arrow({from: [0.9, -0.34, -1.05], to: [-0.9, -0.34, -1.05], color: 'orange'})
const liftL = f.arrow({from: [0.1, -0.15, 1.2], to: [0.1, 0.85, 1.2], color: 'teal'})
const liftR = f.arrow({from: [0.1, -0.15, -1.2], to: [0.1, 0.85, -1.2], color: 'teal'})
const flow = f.tube({points: [[2.9, 0.05, 0.7],[1.2, 0.05, 0.7],[0.2, 0.12, 0.7],[-0.8, 0.05, 0.7],[-2.9, 0.05, 0.7]], r: 0.012, mat: 'paint:#2A7FFF', opacity: 0.6})
highlight(390, 585, 500, 30, {color: 'yellow'})
text(640, 603, 'wings lift up, engines push forward', {size: 22, bold: true})