// A sample answer for the drawing stage's smoke test: "how does a rocket fly?"
step(1); say("A rocket is a tall tube full of fuel, with an engine at the bottom.")
const x = W * 0.62, top = H * 0.16, w = 90, h = H * 0.5
const board = card(x - 190, top - 60, 620, h + 300, {title: 'How a rocket flies'})
poly([[x, top + 40], [x + w / 2, top - 20], [x + w, top + 40]], {color: 'red', fill: 'red'})
rect(x, top + 40, w, h, {color: 'ink', fill: 'white', fillOpacity: 0.9})
poly([[x, top + h - 10], [x - 40, top + h + 40], [x, top + h + 40]], {color: 'red', fill: 'red'})
poly([[x + w, top + h - 10], [x + w + 40, top + h + 40], [x + w, top + h + 40]], {color: 'red', fill: 'red'})
circle(x + w / 2, top + 110, 18, {color: 'blue', fill: 'teal'})

step(2); say("The engine burns that fuel and blasts hot gas out of the nozzle, very fast, downward.")
poly([[x + 25, top + h + 40], [x + w - 25, top + h + 40], [x + w - 10, top + h + 70], [x + 10, top + h + 70]], {color: 'grey', fill: 'grey'})
curve([[x + 20, top + h + 75], [x + w / 2 - 15, top + h + 140], [x + w / 2, top + h + 175], [x + w / 2 + 15, top + h + 140], [x + w - 20, top + h + 75]], {color: 'orange', width: 5})
arrow([[x + w / 2, top + h + 95], [x + w / 2, top + h + 155]], {color: 'orange'})
text(x + w + 70, top + h + 130, 'hot gas pushed down', {color: 'orange', align: 'start', size: 24})

step(3); say("Every push has an equal push back, so the gas pushes the rocket up. That is thrust.")
arrow([[x - 70, top + h * 0.75], [x - 70, top + h * 0.2]], {color: 'green', width: 5})
text(x - 85, top + h * 0.15, 'thrust', {color: 'green', align: 'end', size: 30, bold: true})
highlight(x - 140, top + h + 185, 470, 40, {color: 'yellow'})
text(x - 130, top + h + 205, 'action = reaction  (Newton’s third law)', {align: 'start', size: 24})
