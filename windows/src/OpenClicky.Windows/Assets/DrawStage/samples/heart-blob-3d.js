// A sample answer with an organic 3D figure (blobs): "how does the heart pump blood?"
step(1); say("Your heart is a strong muscle about the size of your fist, with four rooms inside.")
const c = {x: W * 0.36, y: H * 0.05, w: W * 0.6, h: H * 0.86}
card(c.x, c.y, c.w, c.h, {title: 'Inside your heart'})
const f = figure(c.x + 40, c.y + 70, c.w - 80, c.h - 150, {tilt: 8, turn: -12})
const muscle = f.blob({balls: [[0.35, -0.35, 0, 0.85, 1.1, 0.8], [-0.35, -0.2, 0.15, 0.75, 0.95, 0.7], [0.2, -1.25, 0.05, 0.3]],
  smooth: 0.45, mat: 'tissue'}).pulse({amount: 0.035, period: 1})
const leftAtrium = f.blob({balls: [[0.6, 0.7, -0.25, 0.5]], sticks: [{from: [0.6, 0.8, -0.3], to: [1.3, 0.95, -0.45], r: 0.14}], smooth: 0.2, mat: 'tissue:#B8454E'})
const rightAtrium = f.blob({balls: [[-0.75, 0.55, 0.05, 0.55]], smooth: 0.2, mat: 'tissue:#9A5A78'})

step(2); say("Blood with no oxygen comes in on the right side, and the right side pushes it to your lungs.")
const venaCava = f.blob({paths: [{points: [[-0.85, 0.7, 0], [-0.88, 1.5, 0], [-0.9, 2.2, 0]], r: 0.24}], mat: 'tissue:#4A6FB5'})
const pulmonary = f.blob({paths: [{points: [[-0.15, 0.55, 0.45], [-0.15, 1.3, 0.35], [0.25, 1.65, 0.1], [-0.55, 1.75, 0]], r: 0.24, r2: 0.18}], mat: 'tissue:#5B7FC4'})
venaCava.label('from the body', {color: 'blue'})
pulmonary.label('to the lungs', {color: 'blue'})

step(3); say("The left side gets fresh blood back from the lungs and squeezes hard to send it all round your body.")
const aorta = f.blob({paths: [{points: [[0.1, 0.5, 0], [0.12, 1.5, -0.05], [0.55, 2.05, -0.1], [1.1, 1.75, -0.15], [1.2, 1.0, -0.2]], r: 0.3, r2: 0.26}],
  sticks: [{from: [0.45, 1.95, -0.1], to: [0.4, 2.6, -0.1], r: 0.1}, {from: [0.7, 2.0, -0.1], to: [0.75, 2.6, -0.1], r: 0.1}], mat: 'tissue:#C0392B'})
aorta.label('to the body', {color: 'red'})
muscle.label('strong muscle walls', {color: 'pink'})

step(4); say("Every beat squeezes both sides together, about seventy times a minute.")
f.view({turn: 25})
