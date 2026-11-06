// A sample answer for the drawing stage's smoke test: "why does a small gear spin faster than a big one?" (meshed gears)
step(1); say("Let me build a little gear train. The small brass gear on the left is the one being turned.")
const c = {x: W * 0.4, y: H * 0.06, w: W * 0.56, h: H * 0.74}
card(c.x, c.y, c.w, c.h, {title: 'Small gear, big gear'})
const m = mechanism(c.x + 20, c.y + 60, c.w - 40, c.h - 130, {speed: 30, parts: [
  {gear: 'small', teeth: 12, label: '12 teeth'},
  {gear: 'big', teeth: 36, meshes: 'small', label: '36 teeth'},
]})

step(2); say("Their teeth lock together, so every tooth the small gear turns pushes one tooth of the big gear. That makes them turn opposite ways.")
text(c.x + c.w / 2, c.y + c.h - 52, 'small: ' + m.speed('small') + ' turns a minute, big: ' + m.speed('big'), {size: 22, color: 'blue'})

step(3); say("The big gear has three times the teeth, so it turns three times slower, but with three times the push.")
highlight(c.x + 30, c.y + c.h - 34, c.w - 60, 28, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 20, 'three times the teeth, a third of the speed', {align: 'start', size: 20})
