// A sample answer with a photo card beside a 3D figure: "what does a heart look like inside?"
step(1); say("Here is a real human heart, about the size of your fist.")
const c = {x: W * 0.32, y: H * 0.06, w: W * 0.64, h: H * 0.78}
card(c.x, c.y, c.w, c.h, {title: 'Your heart, outside and inside'})
photo(c.x + 24, c.y + 60, c.w * 0.4, c.h * 0.62, {query: 'human heart anatomy', caption: 'a real heart'})

step(2); say("Inside, it is a pump with two sides that squeeze together, about once every second.")
const f = figure(c.x + c.w * 0.45, c.y + 50, c.w * 0.52, c.h - 120, {tilt: 15})
const left = f.sphere({r: 1, at: [0.7, 0, 0], scale: [1, 1.25, 0.9], mat: 'paint:red'}).pulse({amount: 0.06, period: 1})
const right = f.sphere({r: 0.9, at: [-0.7, 0.1, 0], scale: [1, 1.2, 0.9], mat: 'paint:#B04A6A'}).pulse({amount: 0.06, period: 1})
f.tube({points: [[0.4, 1, 0], [0.3, 1.9, 0], [1.3, 2.3, 0], [1.9, 1.9, 0]], r: 0.22, mat: 'paint:red'})
f.label(left, 'left side: to the body', {color: 'red'})
f.label(right, 'right side: to the lungs', {color: 'blue'})

step(3); say("Every squeeze pushes blood out through big tubes called arteries.")
highlight(c.x + 24, c.y + c.h - 52, c.w - 48, 38, {color: 'yellow'})
text(c.x + 36, c.y + c.h - 33, 'squeeze, push, relax, refill', {align: 'start', size: 24})
