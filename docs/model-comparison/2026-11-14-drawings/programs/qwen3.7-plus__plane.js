step(1); say("Let me build a 3D model of a commercial jet airplane for you, starting with the main body and wings.")
const c = {x: 200, y: 80, w: 880, h: 520}
text(c.x + c.w / 2, c.y - 20, 'Commercial Jet Airplane', {size: 28, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 18, turn: -25, zoom: 1.1})

const fuselage = f.lathe({points: [[0.35, -2.2], [0.42, -1.8], [0.44, -1.0], [0.44, 1.0], [0.42, 1.6], [0.30, 2.0], [0.18, 2.2]], axis: 'x', mat: 'alu', round: 0.05})
fuselage.label('fuselage', {color: 'blue'})

const nose = f.lathe({points: [[0.18, 2.0], [0.22, 2.15], [0.15, 2.35], [0.05, 2.45], [0, 2.5]], axis: 'x', mat: 'alu'})

const wing = f.extrude({points: [[0, 0], [0.15, 0], [0.12, 1.8], [-0.05, 1.8]], depth: 0.04, bevel: 0.02, at: [0.2, 0, 0], rot: [0, 0, 0], mat: 'alu'})
wing.label('wing', {color: 'teal'})
const wingR = f.extrude({points: [[0, 0], [0.15, 0], [0.12, 1.8], [-0.05, 1.8]], depth: 0.04, bevel: 0.02, at: [0.2, 0, 0], rot: [0, 0, 180], mat: 'alu'})

const vTail = f.extrude({points: [[0, 0], [0.08, 0], [0.06, 0.9], [-0.04, 0.9]], depth: 0.04, bevel: 0.01, at: [-1.9, 0.44, 0], mat: 'alu'})
vTail.label('tail fin', {color: 'purple'})

const hTail = f.extrude({points: [[0, 0], [0.1, 0], [0.08, 0.7], [-0.04, 0.7]], depth: 0.03, bevel: 0.01, at: [-1.9, 0, 0], mat: 'alu'})
const hTailR = f.extrude({points: [[0, 0], [0.1, 0], [0.08, 0.7], [-0.04, 0.7]], depth: 0.03, bevel: 0.01, at: [-1.9, 0, 0], rot: [0, 0, 180], mat: 'alu'})

step(2); say("Now I'll add the jet engines under the wings and the cockpit windows at the front.")
const engineL = f.cyl({r: 0.18, h: 0.55, axis: 'x', at: [0.1, -0.15, 0.6], mat: 'steel'})
engineL.label('engine', {color: 'orange'})
const engineR = f.cyl({r: 0.18, h: 0.55, axis: 'x', at: [0.1, -0.15, -0.6], mat: 'steel'})

const engineInletL = f.cyl({r: 0.20, h: 0.08, axis: 'x', at: [0.38, -0.15, 0.6], mat: 'steel', open: true})
const engineInletR = f.cyl({r: 0.20, h: 0.08, axis: 'x', at: [0.38, -0.15, -0.6], mat: 'steel', open: true})

const cockpit = f.box({w: 0.12, h: 0.15, d: 0.22, round: 0.04, at: [1.95, 0.28, 0], mat: 'glass'})
cockpit.label('cockpit', {color: 'red'})

const noseGear = f.cyl({r: 0.04, h: 0.25, axis: 'y', at: [1.7, -0.55, 0], mat: 'steel'})
const mainGearL = f.cyl({r: 0.05, h: 0.22, axis: 'y', at: [0.0, -0.55, 0.35], mat: 'steel'})
const mainGearR = f.cyl({r: 0.05, h: 0.22, axis: 'y', at: [0.0, -0.55, -0.35], mat: 'steel'})

step(3); say("A jet airplane has a long tube body, swept-back wings with engines underneath, and a tail that keeps it steady in flight.")
f.view({tilt: 12, turn: -15, zoom: 1.0})
highlight(c.x + 30, c.y + c.h - 55, c.w - 60, 38, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 32, 'fuselage + wings + engines + tail = flight', {align: 'start', size: 22})