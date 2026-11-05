// A sample answer for the drawing stage's smoke test: "how does an engine turn a piston's push into turning?" (crank and piston)
step(1); say("Inside a car engine, a piston slides up and down in a cylinder. Let me build one for you.")
const c = {x: W * 0.42, y: H * 0.04, w: W * 0.54, h: H * 0.8}
card(c.x, c.y, c.w, c.h, {title: 'Piston, rod and crank'})
const m = mechanism(c.x + 20, c.y + 60, c.w - 40, c.h - 120, {speed: 40, parts: [
  {crank: 'crankshaft', r: 1.2, label: 'crankshaft'},
  {piston: 'piston', crank: 'crankshaft', rod: 3.6, label: 'piston'},
]})

step(2); say("The connecting rod joins the piston to a crank. As the crank goes round, the piston is pulled down and pushed back up.")

step(3); say("Watch the piston: it slows right down at the top and bottom and moves fastest in the middle, just like a real engine.")
highlight(c.x + 30, c.y + c.h - 50, c.w - 60, 34, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 33, 'up and down becomes round and round', {align: 'start', size: 22})
