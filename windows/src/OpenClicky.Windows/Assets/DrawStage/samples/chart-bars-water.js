// A sample answer for the drawing stage's smoke test: "where does the water in a house go?" (bars over named columns)
step(1); say("Here is how many litres of water one person uses at home on a normal day.")
const c = {x: W * 0.42, y: H * 0.06, w: W * 0.54, h: H * 0.74}
card(c.x, c.y, c.w, c.h, {title: 'Water one person uses a day'})
const g = chart(c.x + 30, c.y + 70, c.w - 60, c.h - 110, {x: {labels: ['toilet', 'shower', 'taps', 'washing', 'drinking']}, y: {from: 0, to: 50, step: 10, label: 'litres'}})

step(2); say("Flushing the toilet and taking showers use the most, around forty litres each.")
g.bars([42, 38, 30, 20, 3], {colors: ['blue', 'teal', 'purple', 'green', 'orange'], values: true})

step(3); say("Drinking is only about three litres, a tiny part of all the water we use.")
const p = g.at(4, 3)
arrow([[p[0] - 40, p[1] - 150], [p[0], p[1] - 40]], {color: 'red'})
text(p[0] - 40, p[1] - 170, 'just this much', {color: 'red', size: 22})
