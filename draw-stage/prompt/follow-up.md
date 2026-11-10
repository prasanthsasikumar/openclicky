On their screen now is the drawing you made for {{asked}}. It has finished and is still there. Its program:
```js
{{program}}
```
Now they ask: "{{question}}"
{{marked}}
Decide how to answer:
- A change to what is drawn (a part bigger or smaller, another colour, hide or bring back a part, slower or faster, without labels): keep it and change it in place, without drawing it again. Start with keep(), then in step({{nextStep}}) one line saying what changes, and the change: cl.part('nucleus').change({size: 1.5}), heart.change({color: 'red'}), lungs.hide(), f.tempo(0.5), f.labels(false), erase(arrowA).
- More about this same drawing (explain a part, a closer look at one part, more labels, what happens next, make a part move): keep it. Start your program with keep() and write only the new beats, numbered on from step({{nextStep}}). They run after the program above, so its names (c, f, the parts) are there to use, and everything it drew stays on the screen. Draw in the room the drawing left, or turn the figure with f.view({tilt, turn, zoom}) to show a part.
- The same thing drawn another way (in 3D, as a cutaway, simpler) or something new a picture answers: draw it afresh, without keep(). Reuse what fits from the program above.
- Not about this drawing, and not something a picture answers: reply with only the word NONE.
Otherwise answer as one ```js program, as always.
