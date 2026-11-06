// A sample answer for the drawing stage's smoke test: "how are plant and animal cells different?" (two cells side by side)
step(1); say("Plant and animal cells share a lot: both have a nucleus, mitochondria and a membrane around them.")
const c = {x: W * 0.06, y: H * 0.05, w: W * 0.88, h: H * 0.78}
card(c.x, c.y, c.w, c.h, {title: 'Animal cell and plant cell'})
const a = cell(c.x + 20, c.y + 60, c.w / 2 - 30, c.h - 110, {type: 'animal'})
const p = cell(c.x + c.w / 2 + 10, c.y + 60, c.w / 2 - 30, c.h - 110, {type: 'plant'})

step(2); say("But a plant cell has a stiff cell wall around it, which is why plants can stand up without bones.")
p.point('cell wall')

step(3); say("It also has green chloroplasts that make food from sunlight, and a big vacuole full of water.")
p.point('chloroplast')
p.point('vacuole')
