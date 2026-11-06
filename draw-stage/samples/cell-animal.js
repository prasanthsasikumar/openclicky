// A sample answer for the drawing stage's smoke test: "what is inside a cell?" (an animal cell, cut open)
step(1); say("Your body is made of trillions of tiny cells. Here is one of them, cut open so we can look inside.")
const c = {x: W * 0.34, y: H * 0.04, w: W * 0.62, h: H * 0.8}
card(c.x, c.y, c.w, c.h, {title: 'Inside an animal cell'})
const a = cell(c.x + 20, c.y + 60, c.w - 40, c.h - 110, {type: 'animal'})

step(2); say("The nucleus is the control room. It holds the DNA, the instructions for everything the cell does.")
a.point('nucleus')

step(3); say("The orange mitochondria are power stations: they turn food and oxygen into energy the cell can use.")
a.point('mitochondria')

step(4); say("Around the nucleus, the ER and the Golgi body build proteins, pack them up and send them where they are needed.")
highlight(c.x + 30, c.y + c.h - 46, c.w - 60, 32, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 30, 'control room, power stations, factory and post office', {align: 'start', size: 21})
