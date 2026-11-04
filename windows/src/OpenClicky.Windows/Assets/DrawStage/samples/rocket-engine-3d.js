// A sample answer with a machine standing on its axis: "how does a rocket fly? show me in 3D". Most of its parts sit
// on the middle line, so their tags have to keep to one side while the figure sways.
step(1); say("Let me build a 3D model of a rocket engine for you. Up top are two big tanks: fuel and liquid oxygen.")
const c = {x: W * 0.3, y: H * 0.05, w: W * 0.66, h: H * 0.86}
card(c.x, c.y, c.w, c.h, {title: 'Inside a rocket engine'})
const f = figure(c.x + 30, c.y + 70, c.w - 60, c.h - 150, {tilt: 14, turn: -25})
const fuelTank = f.capsule({r: 0.55, h: 1.1, at: [0, 2.3, 0], mat: 'paint:#D9DEE6'})
const oxygenTank = f.capsule({r: 0.55, h: 0.9, at: [0, 0.95, 0], mat: 'paint:#9CC3F0'})
fuelTank.label('fuel', {color: 'orange'})
oxygenTank.label('liquid oxygen', {color: 'blue'})

step(2); say("Pumps push both into the combustion chamber, where they burn.")
const pipes = f.pipe({points: [[0.35, 0.3, 0], [0.35, -0.2, 0], [0.12, -0.45, 0]], r: 0.06, mat: 'copper', mirror: 'x'})
const pump = f.cyl({r: 0.22, h: 0.3, at: [0, 0.05, 0], mat: 'steel'}).spin({period: 0.5})
const chamber = f.sphere({r: 0.32, at: [0, -0.5, 0], mat: 'iron'})
pump.label('pump', {color: 'purple'})
chamber.label('combustion chamber', {color: 'red'})

step(3); say("The hot gas rushes out through the bell-shaped nozzle, faster and faster.")
const nozzle = f.lathe({points: [[0.16, -0.75], [0.22, -0.95], [0.42, -1.35], [0.62, -1.8]], mat: 'copper', cut: 'z'})
const flame = f.cone({r: 0.5, h: 1.2, axis: '-y', at: [0, -2.4, 0], mat: 'glow:#FF7A2F', opacity: 0.85}).pulse({amount: 0.06, period: 0.3})
nozzle.label('nozzle', {color: 'green'})
flame.label('exhaust', {color: 'orange'})

step(4); say("Gas pushed down pushes the rocket up: that is thrust.")
const thrust = f.arrow({from: [0.9, -0.5, 0], to: [0.9, 1.2, 0], color: 'red'})
thrust.label('thrust', {color: 'red'})
