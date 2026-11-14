step(1); say("Let me build a 3D model of the school for you.")
text(W/2, 48, 'A 3D School Campus', {size: 28, bold: true})
const f = figure(280, 110, 720, 500, {tilt: 18, turn: -28, zoom: 1.05})
// ground and courtyard
const ground = f.box({w: 14, h: 0.2, d: 11, at: [0, -1.15, 0], mat: 'plastic:#7bb369'})
const yard = f.box({w: 6.2, h: 0.06, d: 5.2, at: [0, -1.02, -1.2], mat: 'plastic:#c8d0d6'})
// main buildings - U shape
const mainFront = f.box({w: 7.6, h: 2.0, d: 1.5, at: [0, -0.03, 2.2], mat: 'paint:#e8dcc3'})
const roofFront = f.box({w: 7.9, h: 0.22, d: 1.8, at: [0, 1.08, 2.2], mat: 'plastic:#5e5e5e'})
const wingLeft = f.box({w: 1.5, h: 2.0, d: 4.4, at: [-3.05, -0.03, -0.05], mat: 'paint:#e8dcc3'})
const roofLeft = f.box({w: 1.8, h: 0.22, d: 4.7, at: [-3.05, 1.08, -0.05], mat: 'plastic:#5e5e5e'})
const wingRight = f.box({w: 1.5, h: 2.0, d: 4.4, at: [3.05, -0.03, -0.05], mat: 'paint:#e8dcc3'})
const roofRight = f.box({w: 1.8, h: 0.22, d: 4.7, at: [3.05, 1.08, -0.05], mat: 'plastic:#5e5e5e'})
mainFront.label('main building', {color: 'ink'})

step(2); say("The front has big windows, a central entrance with a clock above, and a flag out front.")
// front windows two floors
const winFrontTop = f.box({w: 0.52, h: 0.45, d: 0.06, at: [-2.7, 0.35, 2.98], copies: {n: 6, step: [1.08, 0, 0]}, mat: 'glass', opacity: 0.6, color: '#bfe4ff'})
const winFrontBot = f.box({w: 0.52, h: 0.45, d: 0.06, at: [-2.7, -0.35, 2.98], copies: {n: 6, step: [1.08, 0, 0]}, mat: 'glass', opacity: 0.6, color: '#bfe4ff'})
// side wing windows facing courtyard
const winLeftTop = f.box({w: 0.06, h: 0.4, d: 0.5, at: [-2.27, 0.35, 1.0], copies: {n: 4, step: [0, 0, -0.9]}, mat: 'glass', opacity: 0.6})
const winLeftBot = f.box({w: 0.06, h: 0.4, d: 0.5, at: [-2.27, -0.35, 1.0], copies: {n: 4, step: [0, 0, -0.9]}, mat: 'glass', opacity: 0.6})
const winRightTop = f.box({w: 0.06, h: 0.4, d: 0.5, at: [2.27, 0.35, 1.0], copies: {n: 4, step: [0, 0, -0.9]}, mat: 'glass', opacity: 0.6})
const winRightBot = f.box({w: 0.06, h: 0.4, d: 0.5, at: [2.27, -0.35, 1.0], copies: {n: 4, step: [0, 0, -0.9]}, mat: 'glass', opacity: 0.6})
// entrance
const porch = f.box({w: 1.4, h: 1.35, d: 0.5, at: [0, -0.32, 3.02], mat: 'plastic:#f5f5f5'})
const colL = f.cyl({r: 0.07, h: 1.25, at: [-0.48, -0.37, 3.22], mat: 'plastic:white'})
const colR = f.cyl({r: 0.07, h: 1.25, at: [0.48, -0.37, 3.22], mat: 'plastic:white'})
const steps = f.box({w: 1.6, h: 0.12, d: 0.65, at: [0, -1.0, 3.35], mat: 'plastic:#d6d6d6'})
const door = f.box({w: 0.62, h: 0.82, d: 0.06, at: [0, -0.58, 3.3], mat: 'plastic:#4a2d13'})
const clock = f.cyl({r: 0.26, h: 0.05, axis: 'z', at: [0, 0.62, 3.0], mat: 'plastic:white'})
const clockFace = f.cyl({r: 0.22, h: 0.06, axis: 'z', at: [0, 0.62, 3.02], mat: 'plastic:#2b2b2b'})
// flag
const pole = f.cyl({r: 0.03, h: 2.5, at: [-1.5, 0.05, 3.2], mat: 'alu'})
const flag = f.box({w: 0.55, h: 0.32, d: 0.02, at: [-1.22, 1.15, 3.2], mat: 'plastic:#e53935'})
porch.label('entrance', {color: 'red'})

step(3); say("Behind the buildings is the playground with a basketball court, trees and a fence around the yard.")
// court markings white thin boxes
const line1 = f.box({w: 3.6, h: 0.02, d: 0.04, at: [0, -0.98, 0.4], mat: 'plastic:white'})
const line2 = f.box({w: 3.6, h: 0.02, d: 0.04, at: [0, -0.98, -2.6], mat: 'plastic:white'})
const line3 = f.box({w: 0.04, h: 0.02, d: 3.0, at: [-1.8, -0.98, -1.1], mat: 'plastic:white'})
const line4 = f.box({w: 0.04, h: 0.02, d: 3.0, at: [1.8, -0.98, -1.1], mat: 'plastic:white'})
const lineMid = f.box({w: 0.04, h: 0.02, d: 3.0, at: [0, -0.98, -1.1], mat: 'plastic:white'})
// hoop
const hoopPole = f.cyl({r: 0.04, h: 1.2, at: [0, -0.45, -3.2], mat: 'alu'})
const backboard = f.box({w: 0.6, h: 0.38, d: 0.04, at: [0, 0.22, -3.35], mat: 'plastic:white'})
const rim = f.torus({r: 0.13, tube: 0.018, at: [0, 0.02, -3.15], rot: [90, 0, 0], mat: 'plastic:#ff6f00'})
// trees
const trunk1 = f.cyl({r: 0.09, h: 1.0, at: [4.6, -0.6, 2.0], mat: 'wood'})
const leaf1 = f.sphere({r: 0.48, at: [4.6, 0.12, 2.0], mat: 'leaf'})
const trunk2 = f.cyl({r: 0.09, h: 1.0, at: [-4.8, -0.6, 1.2], mat: 'wood'})
const leaf2 = f.sphere({r: 0.48, at: [-4.8, 0.12, 1.2], mat: 'leaf'})
const trunk3 = f.cyl({r: 0.09, h: 0.9, at: [5.0, -0.65, -1.5], mat: 'wood'})
const leaf3 = f.sphere({r: 0.42, at: [5.0, 0.0, -1.5], mat: 'leaf'})
const trunk4 = f.cyl({r: 0.08, h: 0.85, at: [-5.0, -0.68, -2.0], mat: 'wood'})
const leaf4 = f.sphere({r: 0.4, at: [-5.0, -0.05, -2.0], mat: 'leaf'})
// fence posts around edge
const fenceN = f.cyl({r: 0.035, h: 0.6, at: [-6.2, -0.82, 5.0], copies: {n: 10, step: [1.38, 0, 0]}, mat: 'alu'})
const fenceS = f.cyl({r: 0.035, h: 0.6, at: [-6.2, -0.82, -5.3], copies: {n: 10, step: [1.38, 0, 0]}, mat: 'alu'})
const fenceW = f.cyl({r: 0.035, h: 0.6, at: [-6.8, -0.82, -4.2], copies: {n: 8, step: [0, 0, 1.32]}, mat: 'alu'})
const fenceE = f.cyl({r: 0.035, h: 0.6, at: [6.8, -0.82, -4.2], copies: {n: 8, step: [0, 0, 1.32]}, mat: 'alu'})
yard.label('playground', {color: 'teal'})

step(4); say("So the classrooms wrap around a safe inner yard where you play and learn every day.")
text(W/2, 650, 'U-shaped classrooms open to a central playground', {size: 22, color: 'teal'})