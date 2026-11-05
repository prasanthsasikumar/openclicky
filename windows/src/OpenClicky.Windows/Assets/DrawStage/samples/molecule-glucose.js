// A sample answer for the drawing stage's smoke test: "what does sugar look like up close?" (a bigger molecule, labelled)
step(1); say("This is glucose, the sugar your body runs on. Watch it build, atom by atom.")
const c = {x: W * 0.4, y: H * 0.06, w: W * 0.56, h: H * 0.74}
card(c.x, c.y, c.w, c.h, {title: 'Glucose, up close'})
const g = molecule(c.x + 20, c.y + 60, c.w - 40, c.h - 120, {name: 'glucose'})

step(2); say("Five carbons and an oxygen make a ring, the sixth carbon hangs off it, and little OH groups stick out all round. Those make sugar dissolve so easily in water.")
g.label(5, 'the ring oxygen', {color: 'red'})

step(3); say("Your cells break these bonds apart, and the energy that holds them together is what keeps you going.")
highlight(c.x + 30, c.y + c.h - 50, c.w - 60, 36, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 32, 'six carbons, a ring, and energy in its bonds', {align: 'start', size: 22})
