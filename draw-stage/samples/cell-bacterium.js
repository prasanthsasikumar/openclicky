// A sample answer for the drawing stage's smoke test: "what is a bacterium?" (a bacterial cell)
step(1); say("A bacterium is a whole living thing made of just one tiny cell, much simpler than ours.")
const c = {x: W * 0.36, y: H * 0.06, w: W * 0.6, h: H * 0.74}
card(c.x, c.y, c.w, c.h, {title: 'A bacterium'})
const b = cell(c.x + 20, c.y + 60, c.w - 40, c.h - 110, {type: 'bacterium'})

step(2); say("It has no nucleus. Its DNA just floats in a tangle in the middle, with a few little extra rings of DNA called plasmids.")
b.point('nucleoid')

step(3); say("The long whip at the end is a flagellum. It spins like a propeller to push the bacterium along.")
b.point('flagellum')
