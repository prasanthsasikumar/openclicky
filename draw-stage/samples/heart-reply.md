Here is how it works:

```js
step(1); say("Your heart is a pump with two sides, and each side has two rooms, a small one on top and a big one below.")
const c = {x: W * 0.5, y: H * 0.06, w: W * 0.46, h: H * 0.78}
card(c.x, c.y, c.w, c.h, {title: 'How your heart pumps blood'})
const mx = c.x + c.w / 2, top = c.y + c.h * 0.2, rw = c.w * 0.3, rh = c.h * 0.14
rect(mx - rw - 10, top, rw, rh, {color: 'blue', fill: 'blue', fillOpacity: 0.15})
rect(mx + 10, top, rw, rh, {color: 'red', fill: 'red', fillOpacity: 0.15})
rect(mx - rw - 10, top + rh + 20, rw, rh * 1.6, {color: 'blue', fill: 'blue', fillOpacity: 0.25})
rect(mx + 10, top + rh + 20, rw, rh * 1.6, {color: 'red', fill: 'red', fillOpacity: 0.25})
text(mx - rw / 2 - 10, top + rh / 2, 'right atrium', {size: 20})
text(mx + rw / 2 + 10, top + rh / 2, 'left atrium', {size: 20})
text(mx - rw / 2 - 10, top + rh * 1.8 + 20, 'right ventricle', {size: 20})
text(mx + rw / 2 + 10, top + rh * 1.8 + 20, 'left ventricle', {size: 20})

step(2); say("Blue blood that has used up its oxygen comes back from the body into the right side.")
arrow([[c.x + 30, top - 40], [mx - rw / 2 - 10, top - 4]], {color: 'blue', width: 4})
text(c.x + 40, top - 60, 'from the body', {align: 'start', size: 20, color: 'blue'})
arrow([[mx - rw / 2 - 10, top + rh + 2], [mx - rw / 2 - 10, top + rh + 50]], {color: 'blue'})

step(3); say("The right side squeezes it to the lungs, where it picks up fresh oxygen and turns bright red.")
arrow([[mx - rw / 2 - 10, top + rh * 2.6 + 30], [c.x + 40, top + rh * 2.6 + 90]], {color: 'blue', width: 4})
text(c.x + 40, top + rh * 2.6 + 120, 'to the lungs', {align: 'start', size: 20, color: 'blue'})
arrow([[c.x + c.w - 40, top - 40], [mx + rw / 2 + 10, top - 4]], {color: 'red', width: 4})
text(c.x + c.w - 40, top - 60, 'from the lungs', {align: 'end', size: 20, color: 'red'})

step(4); say("Then the strong left side squeezes that red blood out to your whole body, about once every second.")
arrow([[mx + rw / 2 + 10, top + rh * 2.6 + 30], [c.x + c.w - 40, top + rh * 2.6 + 90]], {color: 'red', width: 5})
text(c.x + c.w - 40, top + rh * 2.6 + 120, 'to the body', {align: 'end', size: 20, color: 'red'})
highlight(c.x + 30, c.y + c.h - 70, c.w - 60, 44, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 48, 'right side to the lungs, left side to the body', {align: 'start', size: 22})
```
