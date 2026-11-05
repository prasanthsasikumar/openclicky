// A sample answer for the drawing stage's smoke test: "how does an oil pump jack work?" (a crank rocking a beam)
step(1); say("A pump jack lifts oil from deep underground. A motor turns a heavy crank round and round.")
const c = {x: W * 0.38, y: H * 0.05, w: W * 0.58, h: H * 0.78}
card(c.x, c.y, c.w, c.h, {title: 'How a pump jack nods'})
const m = mechanism(c.x + 20, c.y + 60, c.w - 40, c.h - 120, {speed: 12, parts: [
  {crank: 'motor', r: 1.2, label: 'crank'},
  {rocker: 'beam', crank: 'motor', rod: 5, length: 3, pivot: [3, 5], reach: 4, head: true, label: 'walking beam'},
]})

step(2); say("A rod from the crank pulls the back of the walking beam down and pushes it up, so the beam rocks on its post.")

step(3); say("The horse head on the front nods up and down, pulling a long rod in the well that lifts a little oil with every stroke.")
highlight(c.x + 30, c.y + c.h - 50, c.w - 60, 34, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 33, 'round and round becomes up and down', {align: 'start', size: 22})
