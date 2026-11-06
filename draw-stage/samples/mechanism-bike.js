// A sample answer for the drawing stage's smoke test: "how do bike gears make you go faster?" (sprockets and a chain)
step(1); say("On a bike, your pedals turn a big front sprocket, and a chain carries that turning to a small sprocket on the back wheel.")
const c = {x: W * 0.36, y: H * 0.06, w: W * 0.6, h: H * 0.74}
card(c.x, c.y, c.w, c.h, {title: 'Bike chain and sprockets'})
const m = mechanism(c.x + 20, c.y + 60, c.w - 40, c.h - 130, {speed: 15, parts: [
  {sprocket: 'pedals', teeth: 40, label: 'front: 40 teeth'},
  {sprocket: 'wheel', teeth: 16, chain: 'pedals', angle: 0, distance: 9, label: 'back: 16 teeth'},
]})

step(2); say("Every link of the chain that leaves the front sprocket pulls one tooth of the back one, so the back wheel turns faster.")
text(c.x + c.w / 2, c.y + c.h - 52, 'pedals ' + m.speed('pedals') + ' turns a minute, wheel ' + m.speed('wheel'), {size: 22, color: 'blue'})

step(3); say("Forty teeth to sixteen means the wheel turns two and a half times for every turn of the pedals.")
highlight(c.x + 30, c.y + c.h - 34, c.w - 60, 28, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 20, 'big front, small back: faster wheel', {align: 'start', size: 20})
