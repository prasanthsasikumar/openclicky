step(1); say("Here is a human skull. The rounded cranium at the back and top protects the brain.")
const c = {x: 340, y: 90, w: 600, h: 450}
text(c.x + c.w / 2, c.y - 28, 'Human skull', {size: 28, bold: true})
const f = figure(c.x, c.y, c.w, c.h, {tilt: 12, turn: -25, ground: false})
const skull = f.blob({
  balls: [
    [0, 1.1, -0.2, 1.35, 1.2, 1.0, 1.0],
    [0, 0.5, 0.75, 0.85, 0.9, 0.8, 0.75],
    [-1.0, -0.25, 0.35, 0.55, 0.5, 0.45, 0.5],
    [1.0, -0.25, 0.35, 0.55, 0.5, 0.45, 0.5],
    [-0.75, -1.15, 0.4, 0.5, 0.45, 0.55, 0.45],
    [0.75, -1.15, 0.4, 0.5, 0.45, 0.55, 0.45],
    [0, -1.55, 0.5, 0.45, 0.42, 0.5, 0.4]
  ],
  holes: [
    [-0.45, 0.05, 0.95, 0.34],
    [0.45, 0.05, 0.95, 0.34],
    [0, -0.42, 1.0, 0.17]
  ],
  smooth: 0.42,
  mat: 'bone'
})
skull.label('cranium', {color: 'brown'})

step(2); say("The large holes are the eye sockets, and the smaller hole below them is the nasal cavity.")
f.label(skull, 'eye sockets', {color: 'blue'})
f.label(skull, 'nasal cavity', {color: 'purple'})
const upperTeeth = []
for (const k of range(8)) {
  const x = -0.52 + k * 0.15
  upperTeeth.push(f.capsule({r: 0.055, h: 0.16, at: [x, -0.92, 0.92], rot: [0, 0, 0], axis: 'y', mat: 'bone'}))
}
const lowerTeeth = []
for (const k of range(6)) {
  const x = -0.38 + k * 0.15
  lowerTeeth.push(f.capsule({r: 0.05, h: 0.14, at: [x, -1.32, 0.9], rot: [0, 0, 0], axis: 'y', mat: 'bone'}))
}

step(3); say("The lower jaw, called the mandible, hinges at the sides so you can open and close your mouth.")
const jaw = f.blob({
  balls: [
    [-0.8, -1.15, 0.42, 0.38, 0.35, 0.45, 0.35],
    [0.8, -1.15, 0.42, 0.38, 0.35, 0.45, 0.35],
    [0, -1.65, 0.52, 0.42, 0.4, 0.48, 0.38]
  ],
  smooth: 0.4,
  mat: 'bone'
})
jaw.label('mandible', {color: 'orange'})

step(4); say("So the skull is a strong bone helmet that shields the brain, eyes, and nose, and holds the teeth.")
highlight(c.x + 30, c.y + c.h - 55, c.w - 60, 36, {color: 'yellow'})
text(c.x + c.w / 2, c.y + c.h - 34, 'skull: brain case + face + jaw', {size: 22})
suggest(['Spin it around', 'Show the inside'])