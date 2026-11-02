// A sample answer with a machine cut open (cut, around, copies): "how does a jet engine work?"
step(1); say("A jet engine sucks air in at the front with a big fan.")
const c = {x: W * 0.3, y: H * 0.05, w: W * 0.66, h: H * 0.86}
card(c.x, c.y, c.w, c.h, {title: 'Inside a jet engine'})
const f = figure(c.x + 30, c.y + 70, c.w - 60, c.h - 150, {tilt: 22, turn: -35})
const casing = f.lathe({points: [[1.05, -2.3], [1.12, -1.9], [1.1, -0.6], [0.95, 0.6], [0.95, 1.4], [0.7, 2.2]], axis: 'x', mat: 'alu', cut: 'z'})
const shaft = f.cyl({r: 0.09, h: 4.2, axis: 'x', mat: 'steel'})
const spinner = f.cone({r: 0.32, h: 0.55, axis: '-x', at: [-2.15, 0, 0], mat: 'paint:#30343B'})
const fan = f.box({w: 0.05, h: 0.78, d: 0.3, at: [-1.75, 0.5, 0], rot: [0, 30, 0], around: {n: 18, axis: 'x', center: [-1.75, 0, 0]}, mat: 'titanium:#8C939C'}).spin({axis: 'x', period: 0.8})
fan.label('fan', {color: 'blue'})

step(2); say("Rows of smaller blades squeeze that air tighter and tighter: that is the compressor.")
const compressor = f.cyl({r: 0.62, r2: 0.48, h: 0.06, axis: 'x', at: [-1.2, 0, 0], copies: {n: 7, step: [0.16, 0, 0]}, mat: 'steel'}).spin({axis: 'x', period: 0.6})
const vanes = f.box({w: 0.03, h: 0.35, d: 0.12, at: [-0.68, 0.42, 0], around: {n: 24, axis: 'x', center: [-0.68, 0, 0]}, mat: 'steel'})
compressor.label('compressor', {color: 'purple'})

step(3); say("Fuel burns in the squeezed air, and the hot gas rushes backwards.")
const burner = f.torus({r: 0.55, tube: 0.16, axis: 'x', at: [0.25, 0, 0], mat: 'glow:orange'})
const flame = f.cone({r: 0.32, h: 1.0, axis: 'x', at: [0.6, 0.3, 0], around: {n: 8, axis: 'x', center: [0.6, 0, 0]}, mat: 'glow:#FF7A2F', opacity: 0.85})
burner.label('burn fuel', {color: 'orange'})

step(4); say("The gas spins a turbine that turns the fan, then shoots out of the nozzle and pushes the plane forward.")
const turbine = f.gear({r: 0.6, teeth: 30, depth: 0.12, axis: 'x', at: [1.05, 0, 0], copies: {n: 2, step: [0.22, 0, 0]}, mat: 'iron'}).spin({axis: 'x', period: 0.6})
const nozzle = f.cone({r: 0.35, h: 0.8, axis: 'x', at: [2.0, 0, 0], mat: 'iron'})
const thrust = f.arrow({from: [2.4, 0, 0], to: [3.6, 0, 0], color: 'red'})
turbine.label('turbine', {color: 'green'})
thrust.label('thrust', {color: 'red'})
