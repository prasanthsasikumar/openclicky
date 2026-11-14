step(1); say("Here is a 3D model of an airplane. The fuselage carries the people, the wings give the lift, and the tail keeps it steady.")
text(640, 46, 'A 3D model of an airplane', {size: 30, bold: true})
const f = figure(300, 95, 680, 450, {tilt: 16, turn: -32})
const fuselage = f.lathe({points: [[0,-2.7],[0.18,-2.5],[0.42,-2.2],[0.5,-1.6],[0.52,-0.6],[0.52,1.0],[0.46,1.6],[0.32,2.0],[0.16,2.3],[0,2.6]], axis: 'x', mat: 'paint:#F0F2F5', name: 'fuselage'})
const wing = f.extrude({points: [[-0.95,0.42],[0.62,0.42],[0.52,2.95],[0.02,2.95],[0.02,-2.95],[0.52,-2.95],[0.62,-0.42],[-0.95,-0.42]], depth: 0.14, at: [0,-0.08,0], rot: [88,0,0], mat: 'paint:#DCE1E8', name: 'wing'})
const hstab = f.extrude({points: [[1.85,0.22],[2.62,0.22],[2.55,1.15],[2.1,1.15],[2.1,-1.15],[2.55,-1.15],[2.62,-0.22],[1.85,-0.22]], depth: 0.08, at: [0,0.02,0], rot: [88,0,0], mat: 'paint:#DCE1E8'})
const vfin = f.extrude({points: [[1.75,0.05],[2.55,0.05],[2.72,1.05],[2.15,1.05]], depth: 0.09, mat: 'paint:#DCE1E8', name: 'tail'})
f.extrude({points: [[0.12,0.0],[0.56,0.0],[0.64,0.34],[0.22,0.34]], depth: 0.06, at: [0,0.0,2.9], mat: 'paint:#DCE1E8'})
f.extrude({points: [[0.12,0.0],[0.56,0.0],[0.64,0.34],[0.22,0.34]], depth: 0.06, at: [0,0.0,-2.9], mat: 'paint:#DCE1E8'})
fuselage.label('fuselage', {color: 'blue'})
wing.label('wing', {color: 'green'})
vfin.label('tail', {color: 'purple'})

step(2); say("Two jet engines hang under the wings. A big fan pulls air in, and hot gas blasts out the back to push the plane forward.")
for (const s of [1, -1]) {
  const nacelle = f.lathe({points: [[0,-0.78],[0.28,-0.75],[0.35,-0.6],[0.37,-0.3],[0.37,0.12],[0.33,0.3],[0.24,0.42],[0.12,0.48],[0,0.5]], axis: 'x', at: [-0.15,-0.55,s*1.5], mat: 'paint:#D9DEE5'})
  f.torus({r: 0.33, tube: 0.05, axis: 'x', at: [-0.92,-0.55,s*1.5], mat: 'titanium'})
  f.cyl({r: 0.3, h: 0.06, axis: 'x', at: [-0.88,-0.55,s*1.5], mat: 'paint:#1B1F27'})
  f.box({w: 0.05, h: 0.3, d: 0.12, at: [-0.87,-0.37,s*1.5], around: {n: 10, axis: 'x', center: [-0.87,-0.55,s*1.5]}, mat: 'titanium'}).spin({axis: 'x', period: 0.45})
  f.cone({r: 0.09, h: 0.2, axis: '-x', at: [-0.82,-0.55,s*1.5], mat: 'chrome'})
  f.box({w: 0.9, h: 0.4, d: 0.16, at: [-0.15,-0.3,s*1.5], mat: 'paint:#C6CCD5'})
  f.cyl({r: 0.22, r2: 0.17, h: 0.22, axis: 'x', at: [0.42,-0.55,s*1.5], mat: 'iron'})
  if (s === 1) nacelle.label('jet engine', {color: 'orange'})
}

step(3); say("Up front is the cockpit where the pilots sit. Windows run along the cabin, and the wheels fold down for landing.")
f.box({w: 0.45, h: 0.22, d: 0.05, at: [-2.15,0.22,0.36], rot: [0,-18,0], round: 0.04, mat: 'paint:#151A22'})
f.box({w: 0.45, h: 0.22, d: 0.05, at: [-2.15,0.22,-0.36], rot: [0,18,0], round: 0.04, mat: 'paint:#151A22'})
f.box({w: 0.06, h: 0.16, d: 0.34, at: [-2.36,0.26,0], rot: [24,0,0], round: 0.03, mat: 'paint:#151A22'})
for (const s of [1, -1]) {
  for (let k = 0; k < 5; k++) f.box({w: 0.14, h: 0.16, d: 0.05, at: [-1.5 + k*0.32, 0.24, s*0.47], round: 0.05, mat: 'paint:#151A22'})
  f.box({w: 0.18, h: 0.55, d: 0.06, at: [-1.02,-0.02,s*0.5], round: 0.05, mat: 'paint:#AEB6C0'})
}
const gear = f.cyl({r: 0.05, h: 0.8, axis: 'y', at: [-1.65,-0.85,0], mat: 'steel', name: 'gear'})
f.cyl({r: 0.17, h: 0.1, axis: 'z', at: [-1.65,-1.25,0.13], mat: 'rubber'})
f.cyl({r: 0.17, h: 0.1, axis: 'z', at: [-1.65,-1.25,-0.13], mat: 'rubber'})
for (const s of [1, -1]) {
  f.cyl({r: 0.06, h: 1.05, axis: 'y', at: [0.5,-0.6,s*0.62], mat: 'steel'})
  f.cyl({r: 0.22, h: 0.13, axis: 'z', at: [0.34,-1.2,s*0.62], mat: 'rubber'})
  f.cyl({r: 0.22, h: 0.13, axis: 'z', at: [0.66,-1.2,s*0.62], mat: 'rubber'})
}
gear.label('landing gear', {color: 'brown'})

step(4); say("Air rushes over the curved wings and pushes them upward. That lift, with the thrust from the engines, holds the plane in the sky.")
f.arrow({from: [-2.0,0.5,2.0], to: [-0.8,0.5,2.0], color: 'teal'}).label('air flowing back', {color: 'teal'})
f.arrow({from: [-1.4,-0.1,1.1], to: [-1.4,0.85,1.1], color: 'green'}).label('lift', {color: 'green'})
f.arrow({from: [0.8,-0.55,1.5], to: [1.9,-0.55,1.5], color: 'red'}).label('hot gas out', {color: 'red'})

step(5); say("So that is the whole airplane: a fuselage, wings for lift, engines for thrust, and a tail to steer.")
highlight(340, 566, 600, 38, {color: 'yellow'})
text(640, 585, 'fuselage, wings, engines and tail: one flying machine', {size: 24})

suggest(['Turn it side on', 'Peek inside the cabin'])