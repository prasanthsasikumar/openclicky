step(1); say("Let me build a 3D model of a twin-engine passenger airplane, beginning with its fuselage, wings, and tail.")
const c = {x: 260, y: 85, w: 760, h: 500}
text(c.x + c.w / 2, 42, '3D passenger airplane', {size: 30, bold: true, color: 'ink'})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 18, turn: -28, zoom: 0.92, ground: true})
const fuselage = f.lathe({
  points: [[0.10,-5.15],[0.30,-5.03],[0.55,-4.72],[0.70,-4.15],[0.73,-3.3],[0.73,2.45],[0.68,3.25],[0.52,3.9],[0.28,4.45],[0.08,4.7]],
  axis: 'x', at: [0,2,0], mat: 'paint:#F4F6F8'
})
const rightWing = f.extrude({
  points: [[-1.25,0.25],[-0.45,4.8],[1.2,4.8],[1.45,0.25]],
  depth: 0.16, bevel: 0.03, at: [0,1.25,0], rot: [90,0,0], mat: 'alu'
})
const leftWing = f.extrude({
  points: [[-1.25,-0.25],[-0.45,-4.8],[1.2,-4.8],[1.45,-0.25]],
  depth: 0.16, bevel: 0.03, at: [0,1.25,0], rot: [90,0,0], mat: 'alu'
})
f.extrude({
  points: [[2.95,0.15],[3.1,2.15],[4.05,2.2],[4.55,0.15]],
  depth: 0.13, bevel: 0.02, at: [0,1.95,0], rot: [90,0,0], mat: 'paint:#276FBF'
})
f.extrude({
  points: [[2.95,-0.15],[3.1,-2.15],[4.05,-2.2],[4.55,-0.15]],
  depth: 0.13, bevel: 0.02, at: [0,1.95,0], rot: [90,0,0], mat: 'paint:#276FBF'
})
const tailFin = f.extrude({
  points: [[2.9,2.0],[3.1,4.1],[4.0,4.25],[4.65,2.0]],
  depth: 0.18, bevel: 0.03, at: [0,0,0], mat: 'paint:#276FBF'
})
fuselage.label('fuselage', {color: 'blue'})
rightWing.label('main wing', {color: 'blue'})
tailFin.label('tail fin', {color: 'purple'})

step(2); say("The dark cockpit glass sits at the nose, while neat cabin windows and blue stripes make the airliner recognizable.")
f.box({w: 0.50, h: 0.24, d: 0.05, at: [-4.35,2.23,0.53], rot: [0,0,-8], mat: 'paint:#142536'})
f.box({w: 0.50, h: 0.24, d: 0.05, at: [-4.35,2.23,-0.53], rot: [0,0,-8], mat: 'paint:#142536'})
f.box({
  w: 0.11, h: 0.12, d: 0.035, at: [-3.15,2.14,0.71],
  copies: {n: 5, step: [0.92,0,0]}, mat: 'paint:#182536'
})
f.box({
  w: 0.11, h: 0.12, d: 0.035, at: [-3.15,2.14,-0.71],
  copies: {n: 5, step: [0.92,0,0]}, mat: 'paint:#182536'
})
f.box({w: 5.0, h: 0.11, d: 0.035, at: [-0.75,1.76,0.695], mat: 'paint:#276FBF'})
f.box({w: 5.0, h: 0.11, d: 0.035, at: [-0.75,1.76,-0.695], mat: 'paint:#276FBF'})

step(3); say("Two turbofan engines hang under the wings, and their spinning fan blades draw air into the front inlets.")
const rightEngine = f.lathe({
  points: [[0.34,-1.15],[0.46,-0.95],[0.49,-0.55],[0.49,0.65],[0.40,1.0]],
  axis: 'x', at: [-1.55,0.78,2.05], mat: 'paint:#EDF0F3'
})
const leftEngine = f.lathe({
  points: [[0.34,-1.15],[0.46,-0.95],[0.49,-0.55],[0.49,0.65],[0.40,1.0]],
  axis: 'x', at: [-1.55,0.78,-2.05], mat: 'paint:#EDF0F3'
})
f.torus({r: 0.35, tube: 0.08, axis: 'x', at: [-2.72,0.78,2.05], mat: 'steel'})
f.torus({r: 0.35, tube: 0.08, axis: 'x', at: [-2.72,0.78,-2.05], mat: 'steel'})
f.cyl({r: 0.36, h: 0.05, axis: 'x', at: [-2.62,0.78,2.05], mat: 'paint:#111820'})
f.cyl({r: 0.36, h: 0.05, axis: 'x', at: [-2.62,0.78,-2.05], mat: 'paint:#111820'})
const rightHub = f.cyl({r: 0.11, h: 0.18, axis: 'x', at: [-2.77,0.78,2.05], mat: 'paint:#26313B'}).spin({axis: 'x', period: 0.8})
const leftHub = f.cyl({r: 0.11, h: 0.18, axis: 'x', at: [-2.77,0.78,-2.05], mat: 'paint:#26313B'}).spin({axis: 'x', period: 0.8})
f.box({
  w: 0.035, h: 0.30, d: 0.10, at: [-2.77,0.96,2.05], rot: [0,0,18],
  around: {n: 5, axis: 'x', center: [-2.77,0.78,2.05]}, parent: rightHub, mat: 'steel'
})
f.box({
  w: 0.035, h: 0.30, d: 0.10, at: [-2.77,0.96,-2.05], rot: [0,0,18],
  around: {n: 5, axis: 'x', center: [-2.77,0.78,-2.05]}, parent: leftHub, mat: 'steel'
})
rightEngine.label('engine', {color: 'orange'})

step(4); say("The nose wheel and two main wheels support the plane, while blue winglets and tailplanes help it stay stable.")
const noseGear = f.cyl({r: 0.07, h: 1.05, axis: 'y', at: [-3.55,0.72,0], mat: 'steel'})
f.cyl({r: 0.07, h: 0.9, axis: 'y', at: [0.85,0.65,1.25], mat: 'steel'})
f.cyl({r: 0.07, h: 0.9, axis: 'y', at: [0.85,0.65,-1.25], mat: 'steel'})
f.cyl({r: 0.22, h: 0.34, axis: 'z', at: [-3.55,0.22,0], mat: 'rubber'})
f.cyl({r: 0.23, h: 0.48, axis: 'z', at: [0.85,0.23,1.25], mat: 'rubber'})
f.cyl({r: 0.23, h: 0.48, axis: 'z', at: [0.85,0.23,-1.25], mat: 'rubber'})
f.box({w: 0.14, h: 1.05, d: 0.72, at: [0.4,1.78,4.75], rot: [0,12,0], mat: 'paint:#276FBF'})
f.box({w: 0.14, h: 1.05, d: 0.72, at: [0.4,1.78,-4.75], rot: [0,-12,0], mat: 'paint:#276FBF'})
noseGear.label('landing gear', {color: 'grey'})

step(5); say("Takeaway: a passenger jet has a body, wings, two engines, a tail, and landing gear.")
highlight(400, 540, 480, 44, {color: 'yellow'})
text(640, 562, 'body, wings, two engines, tail, and gear', {size: 23, bold: true, color: 'ink'})

suggest(['Show from above','Make it take off'])