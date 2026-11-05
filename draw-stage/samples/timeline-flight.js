// A sample answer for the drawing stage's smoke test: "how did people learn to fly?" (a timeline with crowded events)
step(1); say("People have only been flying for about a hundred and twenty years. Here is that story on a line.")
const c = {x: W * 0.06, y: H * 0.12, w: W * 0.88, h: H * 0.6}
card(c.x, c.y, c.w, c.h, {title: 'How we learned to fly'})
const t = timeline(c.x + 40, c.y + c.h * 0.55, c.w - 80, {from: 1900, to: 2030, step: 10})

step(2); say("In 1903 the Wright brothers made the first powered flight, just twelve seconds long.")
t.event(1903, 'first flight', {color: 'red'})
t.event(1909, 'across the Channel', {color: 'red'})
t.span(1914, 1918, 'war planes', {color: 'grey'})

step(3); say("Then came the first flight across the Atlantic, jet engines, and planes faster than sound.")
t.event(1927, 'across the Atlantic')
t.event(1939, 'first jet', {color: 'orange'})
t.event(1947, 'faster than sound', {color: 'orange'})
t.event(1952, 'jet airliner', {color: 'orange'})

step(4); say("Rockets took us to space and to the Moon, and now drones and electric planes are the new chapter.")
t.span(1957, 1975, 'the space race', {color: 'purple'})
t.event(1969, 'Moon landing', {color: 'purple'})
t.event(1976, 'Concorde')
t.event(2021, 'helicopter on Mars', {color: 'teal'})
