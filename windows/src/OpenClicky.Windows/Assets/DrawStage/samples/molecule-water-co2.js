// A sample answer for the drawing stage's smoke test: "what are water and carbon dioxide made of?" (two molecules)
step(1); say("Everything around you is made of tiny molecules. Let me build two of them for you: water and carbon dioxide.")
const c = {x: W * 0.36, y: H * 0.06, w: W * 0.6, h: H * 0.74}
card(c.x, c.y, c.w, c.h, {title: 'Two tiny molecules'})
const water = molecule(c.x + 20, c.y + 70, c.w / 2 - 30, c.h - 160, {name: 'water'})
text(c.x + c.w / 4, c.y + c.h - 70, 'water, H2O', {color: 'blue', size: 24})

step(2); say("Water is one oxygen atom holding two hydrogens. They sit at an angle, so the molecule is bent like a boomerang.")
const co2 = molecule(c.x + c.w / 2 + 10, c.y + 70, c.w / 2 - 30, c.h - 160, {name: 'carbon dioxide'})
text(c.x + c.w * 3 / 4, c.y + c.h - 70, 'carbon dioxide, CO2', {color: 'grey', size: 24})

step(3); say("Carbon dioxide is a carbon with an oxygen on each side, each held by a double bond, in a perfectly straight line.")
highlight(c.x + 30, c.y + c.h - 46, c.w - 60, 34, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 29, 'the shape comes from how the atoms share electrons', {align: 'start', size: 22})
