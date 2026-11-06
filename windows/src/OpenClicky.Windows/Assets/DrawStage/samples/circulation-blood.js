// A sample answer for the drawing stage's smoke test: "how does blood go round my body?" (the double circuit)
step(1); say("Your heart is really two pumps side by side. Let me show you the path your blood takes, round and round.")
const c = {x: W * 0.3, y: H * 0.03, w: W * 0.66, h: H * 0.82}
card(c.x, c.y, c.w, c.h, {title: 'How blood goes round'})
const b = circulation(c.x + 20, c.y + 55, c.w - 40, c.h - 100, {})

step(2); say("Blue blood, with little oxygen left, comes back from your body into the right side of the heart.")
b.point('vena cava')

step(3); say("The right side pumps it to your lungs, where it picks up oxygen from the air you breathe and turns bright red.")
b.point('lungs')

step(4); say("The left side then pumps that red blood out through the aorta, to every part of your body, and the loop starts again.")
b.point('aorta')
highlight(c.x + 30, c.y + c.h - 40, c.w - 60, 30, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 25, 'body, heart, lungs, heart, body: about once a minute', {align: 'start', size: 20})
