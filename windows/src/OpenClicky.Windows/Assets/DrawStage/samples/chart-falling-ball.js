// A sample answer for the drawing stage's smoke test: "how fast does a dropped ball fall?" (a chart with a line by value)
step(1); say("Let's draw a graph of a dropped ball's speed, second by second, starting from standing still.")
const c = {x: W * 0.45, y: H * 0.06, w: W * 0.5, h: H * 0.74}
card(c.x, c.y, c.w, c.h, {title: 'How fast a falling ball goes'})
const g = chart(c.x + 30, c.y + 70, c.w - 60, c.h - 140, {x: {from: 0, to: 5, step: 1, label: 'seconds'}, y: {from: 0, to: 50, label: 'speed (m/s)'}, grid: true})

step(2); say("Every second it falls, gravity adds about ten metres per second to its speed, so the line climbs straight up.")
g.line(t => 9.8 * t, {color: 'blue'})
g.dot(1, 9.8, {label: '10 after 1 s', color: 'blue'})
g.dot(2, 19.6, {label: '20 after 2 s', color: 'blue'})

step(3); say("Air pushes back harder as it speeds up, so a real ball stops gaining speed. That top speed is called terminal velocity.")
g.line([[0, 0], [1, 9.6], [2, 18], [3, 24.5], [4, 28.6], [5, 31]], {color: 'orange', dots: true})
g.hline(34, 'top speed with air', {color: 'red'})

step(4); say("So without air a ball keeps speeding up, but with air it levels off at its top speed.")
highlight(c.x + 30, c.y + c.h - 52, c.w - 60, 36, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 34, 'gravity adds speed, air pushes back', {align: 'start', size: 24})
