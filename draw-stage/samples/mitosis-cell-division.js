// A sample answer for the drawing stage's smoke test: "how does a cell divide?" (mitosis, phase by phase)
step(1); say("One cell becomes two by mitosis. Here is a cell getting ready: its DNA is loose inside the nucleus.")
const c = {x: W * 0.3, y: H * 0.03, w: W * 0.66, h: H * 0.82}
card(c.x, c.y, c.w, c.h, {title: 'How a cell divides'})
const m = mitosis(c.x + 20, c.y + 55, c.w - 40, c.h - 100, {pairs: 2})

step(2); say("First, each chromosome has been copied, and the two copies coil up, joined in the middle. The nucleus breaks down.")
m.next()

step(3); say("Then the chromosomes line up across the middle, each held by spindle fibres from both ends of the cell.")
m.next()

step(4); say("The fibres pull the two copies of every chromosome apart, one to each end, and the cell stretches.")
m.next()

step(5); say("A new nucleus forms round each set, and the cell starts to pinch in the middle.")
m.next()

step(6); say("Finally it pinches in two: two new cells, each with exactly the same chromosomes as the first.")
m.next()
