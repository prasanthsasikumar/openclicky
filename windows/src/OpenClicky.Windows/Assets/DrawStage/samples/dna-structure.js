// A sample answer for the drawing stage's smoke test: "what is DNA?" (the double helix and its base pairs)
step(1); say("DNA is the instruction book inside almost every cell of your body. Let me build a little piece of it.")
const c = {x: W * 0.36, y: H * 0.05, w: W * 0.6, h: H * 0.76}
card(c.x, c.y, c.w, c.h, {title: 'A piece of DNA'})
const d = dna(c.x + 20, c.y + 60, c.w - 40, c.h - 130, {sequence: 'ATGCGTACCTGAATGC'})

step(2); say("It is two long strands twisted into a spiral called a double helix, like a ladder that has been twisted.")
d.label(7, 'one base pair')

step(3); say("The rungs are pairs of bases. A always joins T, and G always joins C, so each strand tells you the other.")
highlight(c.x + 30, c.y + c.h - 52, c.w - 60, 34, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 35, 'A with T, G with C: the other strand reads ' + d.complement, {align: 'start', size: 20})
