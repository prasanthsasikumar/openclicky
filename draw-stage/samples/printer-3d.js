// A sample answer with a 3D figure: "how does a 3D printer work?"
step(1); say("A 3D printer is a metal frame with a flat bed, and a hot nozzle that moves above it.")
const c = {x: W * 0.46, y: H * 0.05, w: W * 0.5, h: H * 0.8}
card(c.x, c.y, c.w, c.h, {title: 'How a 3D printer works'})
const f = figure(c.x + 20, c.y + 50, c.w - 40, c.h - 130, {tilt: 20, turn: -32})
f.box({w: 3.6, h: 0.3, d: 2.6, at: [0, 0, 0], mat: 'paint:grey'})
f.box({w: 0.15, h: 3.2, d: 0.15, at: [-1.7, 1.75, 0], mat: 'alu'})
f.box({w: 0.15, h: 3.2, d: 0.15, at: [1.7, 1.75, 0], mat: 'alu'})
f.box({w: 3.55, h: 0.15, d: 0.15, at: [0, 3.3, 0], mat: 'alu'})
const bed = f.box({w: 2.6, h: 0.1, d: 2.2, at: [0, 0.25, 0], mat: 'paint:black'})

step(2); say("Plastic filament comes off a spool and is pushed into the hot end, which melts it like a glue gun.")
const spool = f.cyl({r: 0.6, h: 0.35, axis: 'z', at: [1.7, 4, 0], mat: 'plastic:orange'}).spin({axis: 'z', period: 6})
f.tube({points: [[1.2, 3.9, 0], [0.6, 3.6, 0], [0, 2.75, 0]], r: 0.03, mat: 'plastic:orange'})
const head = f.box({w: 0.5, h: 0.5, d: 0.5, at: [0, 2.4, 0], mat: 'steel'})
f.cone({r: 0.12, h: 0.3, at: [0, -0.4, 0], rot: [180, 0, 0], mat: 'brass', parent: head})
head.stroke({axis: 'x', amount: 1.2, period: 3})
f.label(head, 'hot end', {color: 'red'})
f.label(spool, 'filament')

step(3); say("The nozzle draws one thin layer of melted plastic on the bed, then moves up and draws the next layer on top.")
f.label(bed, 'print bed')
const print = f.box({w: 0.8, h: 0.6, d: 0.8, at: [0, 0.6, 0], mat: 'plastic:orange'}).pulse({amount: 0.03, period: 1.5})
f.label(print, 'layers stack up', {color: 'orange'})

step(4); say("Layer by layer, hundreds of thin slices stack up into a solid object you can hold.")
highlight(c.x + 30, c.y + c.h - 62, c.w - 60, 42, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 41, 'melt, draw a layer, move up, repeat', {align: 'start', size: 24})
