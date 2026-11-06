// A sample answer for the drawing stage's smoke test: "how does a gene make a protein?" (transcription into mRNA)
step(1); say("A gene is a stretch of DNA. To use it, the cell first makes a copy of it called messenger RNA.")
const c = {x: W * 0.32, y: H * 0.04, w: W * 0.64, h: H * 0.8}
card(c.x, c.y, c.w, c.h, {title: 'Reading a gene'})
const d = dna(c.x + 20, c.y + 60, c.w - 40, c.h - 120, {sequence: 'ATGCGTACCTGAATGC', form: 'ladder'})

step(2); say("The DNA opens over the gene, and RNA letters pair up along one strand. RNA uses U where DNA uses T.")
d.transcribe({from: 3, to: 11})
d.label('mrna', 'messenger RNA')

step(3); say("The finished messenger RNA reads " + d.mrna(3, 11).split('').join(' ') + ". It leaves to be turned into a protein, and the DNA closes up again.")
highlight(c.x + 30, c.y + c.h - 46, c.w - 60, 32, {color: 'yellow'})
text(c.x + 45, c.y + c.h - 30, 'DNA to messenger RNA: ' + d.mrna(3, 11), {align: 'start', size: 22})
