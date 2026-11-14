step(1); say("Let me build a 3D model of an airplane for you, piece by piece.")
const c = {x: 340, y: 110, w: 600, h: 480}
text(c.x + c.w / 2, c.y - 30, 'Airliner', {size: 28, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 16, turn: -35})
const fuselage = f.lathe({points: [[0.28, -2.4], [0.75, -2.0], [0.85, -0.8], [0.85, 0.8], [0.7, 1.8], [0.35, 2.6]], axis: 'x', mat: 'paint:#E8ECF2'})
const cockpit = f.sphere({r: 0.5, at: [-2.15, 0.15, 0], scale: [0.8, 0.6, 0.9], mat: 'glass'})
const winRow = f.sphere({r: 0.04, at: [-1.8, 0.35, 0.82], copies: {n: 12, step: [0.28, 0, 0]}, mat: 'paint:#2A3542'})
f.cyl({r: 0.04, at: [-1.8, 0.35, -0.82], copies: {n: 12, step: [0.28, 0, 0]}, mat: 'paint:#2A3542'})

step(2); say("The wings stick out from the middle and give the plane its lift as it flies forward.")
const wing = f.extrude({points: [[-0.3, 0], [2.6, 0.55], [3.1, 0.75], [2.9, 0.95], [0.1, 0.55]], depth: 0.12, at: [-0.5, -0.15, 0], mat: 'paint:#DDE3EC', mirror: 'x'})
wing.label('wing', {color: 'blue'})

step(3); say("Under each wing hangs a jet engine, which pulls the plane forward through the air.")
const nacelle = f.lathe({points: [[0.42, -0.7], [0.45, 0], [0.36, 0.9]], axis: 'x', at: [0.4, -0.7, 1.35], mat: 'alu', mirror: 'x'})
const fan = f.cone({r: 0.34, h: 0.2, axis: '-x', at: [-0.35, -0.7, 1.35], mat: 'steel', mirror: 'x'}).spin({axis: 'x', period: 1})
nacelle.label('jet engine', {color: 'purple'})

step(4); say("The tail at the back has a tall fin to keep it flying straight, and small wings to steer.")
const fin = f.extrude({points: [[-0.4, 0], [1.1, 1.35], [1.3, 1.35], [0.6, 0]], depth: 0.1, at: [1.6, 0.3, 0], mat: 'paint:#DDE3EC'})
fin.label('tail fin', {color: 'green'})
f.extrude({points: [[-0.5, 0], [1.3, 0.3], [1.5, 0.45], [1.2, 0.6], [0.1, 0.3]], depth: 0.09, at: [1.9, 0.35, 0], mat: 'paint:#DDE3EC', mirror: 'x'})

step(5); say("So that is your 3D airplane: a smooth body, lifting wings, jet engines, and a tail to steer by.")
highlight(c.x + 40, c.y + c.h - 55, c.w - 80, 40, {color: 'yellow'})
text(c.x + c.w / 2, c.y + c.h - 35, 'body, wings, engines, tail', {size: 24})
suggest(['Spin it around', 'Show how it lifts'])