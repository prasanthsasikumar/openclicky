// A sample answer for the drawing stage's smoke test: "how does DNA copy itself?" (unzipping and new partners)
step(1); say("Before a cell divides, it copies all of its DNA. Here is a short piece of it.")
const c = {x: W * 0.32, y: H * 0.04, w: W * 0.64, h: H * 0.8}
card(c.x, c.y, c.w, c.h, {title: 'How DNA copies itself'})
const d = dna(c.x + 20, c.y + 60, c.w - 40, c.h - 120, {sequence: 'ATGCGTACCTGA'})

step(2); say("First the cell untwists it and unzips the two strands, breaking the pairs apart like a zip.")
d.unzip()

step(3); say("Then free bases float in and pair up with each old strand: A with T, G with C, one by one.")
d.copy()
d.label('new', 'new strand')

step(4); say("Now there are two identical pieces of DNA, and each one keeps one old strand and gets one new strand.")
highlight(c.x + 30, c.y + c.h - 46, c.w - 60, 32, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 30, 'unzip, pair up, two copies', {align: 'start', size: 22})
