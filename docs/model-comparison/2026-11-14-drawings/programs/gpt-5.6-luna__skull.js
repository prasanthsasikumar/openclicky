step(1); say("Let me build a 3D model of a human skull, with its rounded brain case, face, jaw, and cheekbones.")
const c = {x: 360, y: 70, w: 560, h: 500}
text(c.x + c.w / 2, c.y - 25, 'Human skull', {size: 30, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 12, turn: 0, zoom: 1.05, ground: false})
const cranium = f.sphere({r: 1, at: [0, 1.0, 0], scale: [1.75, 1.95, 1.35], mat: 'bone', cut: 'z'})
cranium.label('brain case', {color: 'brown'})
const forehead = f.sphere({r: 0.8, at: [0, 0.15, -0.75], scale: [1.25, 1.15, 0.55], mat: 'bone'})
const cheekL = f.box({w: 0.72, h: 0.65, d: 0.62, at: [-1.05, -0.15, -0.78], round: 0.18, mat: 'bone'})
const cheekR = f.box({w: 0.72, h: 0.65, d: 0.62, at: [1.05, -0.15, -0.78], round: 0.18, mat: 'bone'})
const face = f.sphere({r: 0.8, at: [0, -0.45, -0.7], scale: [1.22, 1.35, 0.62], mat: 'bone'})
const jaw = f.capsule({r: 0.62, h: 1.45, at: [0, -1.55, -0.2], axis: 'y', scale: [1.25, 1, 0.78], mat: 'bone'})
jaw.label('lower jaw', {color: 'brown'})

step(2); say("The large dark openings are eye sockets, while the smaller opening in the center is the nasal cavity.")
const eyeL = f.sphere({r: 0.5, at: [-0.62, 0.0, -1.13], scale: [1, 1.08, 0.35], mat: 'stone'})
const eyeR = f.sphere({r: 0.5, at: [0.62, 0.0, -1.13], scale: [1, 1.08, 0.35], mat: 'stone'})
const rimL = f.torus({r: 0.55, tube: 0.13, at: [-0.62, 0.0, -1.22], mat: 'bone'})
const rimR = f.torus({r: 0.55, tube: 0.13, at: [0.62, 0.0, -1.22], mat: 'bone'})
eyeL.label('eye socket', {color: 'purple'})
const nose = f.cone({r: 0.38, h: 0.72, at: [0, -0.72, -1.18], axis: 'y', mat: 'bone'})
const nostrilL = f.sphere({r: 0.18, at: [-0.19, -1.0, -1.35], scale: [1, 0.7, 0.4], mat: 'stone'})
const nostrilR = f.sphere({r: 0.18, at: [0.19, -1.0, -1.35], scale: [1, 0.7, 0.4], mat: 'stone'})
nose.label('nasal cavity', {color: 'purple'})

step(3); say("Inside the open mouth, the upper and lower teeth meet like two pale rows of small blocks.")
const mouth = f.box({w: 1.25, h: 0.38, d: 0.18, at: [0, -1.35, -1.0], round: 0.08, mat: 'stone'})
for (const side of [-1, 1]) {
  for (const k of range(5)) {
    const x = side * (0.12 + k * 0.2)
    f.box({w: 0.14, h: 0.28, d: 0.2, at: [x, -1.19, -1.18], round: 0.03, mat: 'ceramic'})
    f.box({w: 0.14, h: 0.28, d: 0.2, at: [x, -1.51, -1.18], round: 0.03, mat: 'ceramic'})
  }
}
const templeL = f.torus({r: 0.7, tube: 0.08, at: [-1.28, 0.35, -0.45], rot: [0, 20, 0], mat: 'bone'})
const templeR = f.torus({r: 0.7, tube: 0.08, at: [1.28, 0.35, -0.45], rot: [0, -20, 0], mat: 'bone'})
const jawJointL = f.sphere({r: 0.18, at: [-0.88, -0.72, -0.35], mat: 'bone'})
const jawJointR = f.sphere({r: 0.18, at: [0.88, -0.72, -0.35], mat: 'bone'})

step(4); say("A skull protects the brain and supports the face; its hollow spaces make it lighter than a solid bone shell.")
highlight(c.x + 65, c.y + c.h - 45, c.w - 130, 34, {color: 'yellow'})
text(c.x + c.w / 2, c.y + c.h - 28, 'strong protection, lighter structure', {size: 24, bold: true})
suggest(['Show the side view', 'Label more bones'])