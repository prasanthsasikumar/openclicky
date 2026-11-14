step(1); say("Let me build a 3D model of a passenger airplane for you, starting with the long tube of the fuselage and the cockpit at the front.")
const c = {x: 260, y: 90, w: 760, h: 480}
text(c.x + c.w / 2, c.y - 24, 'How a passenger airplane is built', {size: 28, bold: true, color: 'white'})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 20, turn: -38, ground: false})

const fuselage = f.lathe({
  points: [
    [0.01, -2.75], [0.22, -2.55], [0.42, -2.2], [0.52, -1.7],
    [0.54, 1.1], [0.44, 1.85], [0.24, 2.45], [0.06, 2.8]
  ],
  axis: 'x',
  mat: 'paint:#F4F6F9'
})
const belly = f.cyl({r: 0.525, h: 2.7, axis: 'x', at: [-0.25, -0.05, 0], mat: 'paint:#2B5B9E'})
const radome = f.sphere({r: 0.16, at: [-2.68, -0.02, 0], mat: 'paint:#2C3038'})
const windshield = f.box({w: 0.42, h: 0.2, d: 0.62, round: 0.06, at: [-2.08, 0.23, 0], rot: [0, 0, -18], mat: 'glass'})
const windowRow = f.box({w: 0.09, h: 0.12, d: 1.07, round: 0.03, at: [-1.45, 0.14, 0], copies: {n: 14, step: [0.2, 0, 0]}, mat: 'paint:#1C2838'})
fuselage.label('fuselage', {color: 'blue'})

step(2); say("Swept-back wings reach out from each side to create lift, with little upturned winglets at the tips to cut drag.")
const wingBox = f.box({w: 1.15, h: 0.24, d: 1.2, round: 0.06, at: [-0.2, -0.22, 0], mat: 'paint:#2B5B9E'})
const wing = f.extrude({
  points: [[-0.65, 0.45], [0.35, 0.45], [0.85, 2.75], [0.45, 2.85]],
  depth: 0.1,
  bevel: 0.03,
  axis: 'y',
  at: [-0.1, -0.14, 0],
  mirror: 'z',
  mat: 'alu'
})
const winglet = f.extrude({
  points: [[0.45, 0], [0.85, 0], [0.95, 0.42], [0.7, 0.42]],
  depth: 0.05,
  bevel: 0.015,
  at: [-0.1, -0.1, 2.8],
  mirror: 'z',
  mat: 'paint:#E03C31'
})
const flapTrack = f.capsule({r: 0.04, h: 0.38, axis: 'x', at: [0.38, -0.2, 1.35], copies: {n: 2, step: [0.15, 0, 0.55]}, mirror: 'z', mat: 'alu'})
wing.label('swept wing', {color: 'teal'})

step(3); say("Under the wings hang two turbofan engines that suck in air and push the plane forward.")
const pylon = f.box({w: 0.55, h: 0.22, d: 0.1, round: 0.02, at: [-0.45, -0.3, 1.25], rot: [0, -10, 0], mirror: 'z', mat: 'alu'})
const nacelle = f.lathe({
  points: [[0.32, -0.45], [0.36, -0.2], [0.35, 0.2], [0.27, 0.48]],
  axis: 'x',
  at: [-0.62, -0.48, 1.25],
  mirror: 'z',
  mat: 'paint:#2B5B9E'
})
const intakeLip = f.torus({r: 0.31, tube: 0.045, axis: 'x', at: [-1.05, -0.48, 1.25], mirror: 'z', mat: 'chrome'})
const fan = f.cyl({r: 0.28, h: 0.08, axis: 'x', at: [-0.95, -0.48, 1.25], mirror: 'z', mat: 'titanium'}).spin({axis: 'x', period: 0.5})
const spinnerCone = f.cone({r: 0.1, h: 0.2, axis: '-x', at: [-1.02, -0.48, 1.25], mirror: 'z', mat: 'paint:#2C3038'})
const exhaustNozzle = f.cone({r: 0.2, h: 0.28, axis: 'x', at: [-0.08, -0.48, 1.25], mirror: 'z', mat: 'steel'})
nacelle.label('turbofan engine', {color: 'orange'})

step(4); say("At the back, a tall vertical fin and two small tailplanes keep the nose pointed steady and level in flight.")
const tailFin = f.extrude({
  points: [[-0.35, 0], [0.55, 0], [0.95, 1.35], [0.45, 1.38]],
  depth: 0.09,
  bevel: 0.025,
  at: [1.85, 0.22, 0],
  mat: 'paint:#2B5B9E'
})
const tailLogo = f.cyl({r: 0.24, h: 0.11, axis: 'z', at: [2.42, 0.92, 0], mat: 'paint:#E03C31'})
const tailplane = f.extrude({
  points: [[-0.2, 0.15], [0.35, 0.15], [0.65, 1.05], [0.3, 1.08]],
  depth: 0.06,
  bevel: 0.02,
  axis: 'y',
  at: [2.15, 0.12, 0],
  mirror: 'z',
  mat: 'alu'
})
tailFin.label('tail fin (rudder)', {color: 'red'})
tailplane.label('tailplane (elevator)', {color: 'purple'})
f.view({tilt: 18, turn: -135, zoom: 1.05})

suggest(['Cut open a jet engine', 'How do wings make lift?'])