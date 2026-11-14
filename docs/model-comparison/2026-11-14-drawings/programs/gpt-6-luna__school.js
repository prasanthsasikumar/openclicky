step(1); say("Let me build a 3D model of a school, starting with its main classroom block and roof.")
const c = {x: 280, y: 100, w: 720, h: 480}
text(640, 60, 'A School', {size: 30, bold: true, color: 'ink'})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 20, turn: -32})
const base = f.box({w: 6, h: 0.16, d: 3.8, at: [0, -0.08, 0], mat: 'stone'})
const body = f.box({w: 5.2, h: 2.5, d: 2.8, at: [0, 1.25, 0], mat: 'paint:#E8D6A8'})
body.label('school', {color: 'blue'})
const roof = f.box({w: 5.5, h: 0.2, d: 3.05, at: [0, 2.6, 0], mat: 'paint:#8E5142'})
const roofEdge = f.box({w: 5.35, h: 0.08, d: 2.9, at: [0, 2.47, 0], mat: 'paint:#B76A50'})

step(2); say("Blue windows bring light into the classrooms, and the central door gives students a clear way inside.")
for (const x of [-1.8, -0.6, 0.6, 1.8]) {
  f.box({w: 0.66, h: 0.62, d: 0.1, at: [x, 1.87, 1.46], mat: 'paint:#F6E9C8'})
  f.box({w: 0.5, h: 0.46, d: 0.05, at: [x, 1.87, 1.54], mat: 'glass', color: '#69B9D0'})
}
for (const x of [-1.75, 1.75]) {
  f.box({w: 0.66, h: 0.62, d: 0.1, at: [x, 0.78, 1.46], mat: 'paint:#F6E9C8'})
  f.box({w: 0.5, h: 0.46, d: 0.05, at: [x, 0.78, 1.54], mat: 'glass', color: '#69B9D0'})
}
const doorway = f.box({w: 0.92, h: 1.36, d: 0.1, at: [0, 0.7, 1.47], mat: 'paint:#F6E9C8'})
const door = f.box({w: 0.7, h: 1.18, d: 0.08, at: [0, 0.65, 1.56], mat: 'paint:#397A76'})
door.label('front door', {color: 'teal'})
const knob = f.sphere({r: 0.055, at: [0.23, 0.65, 1.62], mat: 'brass'})

step(3); say("A sign, canopy, steps, and path make the entrance easy to find. The takeaway is bright classrooms and a welcoming way in.")
const sign = f.box({w: 1.45, h: 0.28, d: 0.12, at: [0, 2.28, 1.48], mat: 'paint:#397A76'})
sign.label('SCHOOL', {color: 'teal'})
const canopy = f.box({w: 1.45, h: 0.14, d: 0.72, at: [0, 1.43, 1.72], mat: 'paint:#D59A43'})
for (const x of [-0.58, 0.58]) {
  f.cyl({r: 0.045, h: 1.25, at: [x, 0.72, 1.88], mat: 'steel'})
}
f.box({w: 1.15, h: 0.12, d: 0.28, at: [0, 0.06, 1.57], mat: 'stone'})
f.box({w: 1.45, h: 0.1, d: 0.3, at: [0, 0.05, 1.88], mat: 'stone'})
f.box({w: 1.6, h: 0.08, d: 1.4, at: [0, 0.02, 2.6], mat: 'stone'})
highlight(400, 558, 480, 40, {color: 'yellow'})
text(640, 578, 'Bright classrooms, welcoming entrance', {size: 23, bold: true, color: 'ink'})