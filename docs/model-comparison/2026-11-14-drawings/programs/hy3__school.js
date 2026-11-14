step(1); say("Let me build a 3D model of a school for you.")
const f = figure(290, 120, 700, 480, {tilt: 18, turn: -35, ground: true})
text(640, 90, '3D model of a school', {size: 26, bold: true})
const mainB = f.box({w: 4, h: 2, d: 2, at: [0, 1, 0], mat: 'paint:#F9E79F', round: 0.05, name: 'main'})
const roof = f.box({w: 4.2, h: 0.2, d: 2.2, at: [0, 2.1, 0], mat: 'paint:#34495E', name: 'roof'})

step(2); say("The entrance sticks out at the front, with a door and a few steps leading up.")
const entrance = f.box({w: 1.2, h: 1.2, d: 0.5, at: [0, 0.6, 1.25], mat: 'paint:#F9E79F'})
const steps = f.box({w: 1.6, h: 0.2, d: 0.8, at: [0, 0.1, 1.6], mat: 'stone'})
const door = f.box({w: 0.8, h: 1.0, d: 0.1, at: [0, 0.5, 1.51], mat: 'paint:#8B4513'})

step(3); say("Two rows of windows light the classrooms on both floors.")
const win1 = f.box({w: 0.5, h: 0.5, d: 0.05, at: [-1.35, 0.6, 1.01], mat: 'glass', copies: {n: 4, step: [0.9, 0, 0]}})
const win2 = f.box({w: 0.5, h: 0.5, d: 0.05, at: [-1.35, 1.6, 1.01], mat: 'glass', copies: {n: 4, step: [0.9, 0, 0]}})

step(4); say("A flagpole with a red flag stands at the side, and a round clock sits above the door.")
const pole = f.cyl({r: 0.04, h: 1.6, at: [1.9, 2.8, 0], axis: 'y', mat: 'steel'})
const flag = f.box({w: 0.5, h: 0.3, d: 0.02, at: [2.15, 3.3, 0], mat: 'paint:#E74C3C'})
const clock = f.cyl({r: 0.35, h: 0.05, at: [0, 1.7, 1.02], axis: 'z', mat: 'paint:#FFFFFF'})

step(5); say("So a school is a bright block with windows, a door, and a flag to welcome students.")
mainB.label('school building', {color: 'blue'})
roof.label('roof', {color: 'grey'})