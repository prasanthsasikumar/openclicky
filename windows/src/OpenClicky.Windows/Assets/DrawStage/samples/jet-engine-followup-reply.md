A follow-up to samples/jet-engine-cutaway-3d.js ("and what does the turbine do?"): it keeps the drawing and adds to it.

```js
keep()
step(5); say("Let's look closer at the back, where the hot gas rushes through the turbine.")
f.view({tilt: 14, turn: 30, zoom: 1.1})
const hotGas = f.arrow({from: [0.45, 0.42, 0.45], to: [1.6, 0.42, 0.45], color: 'orange'})
hotGas.label('hot gas', {color: 'orange'})

step(6); say("The turbine sits on the same shaft as the fan, so when the gas spins it, the fan at the front spins too.")
shaft.label('shaft', {color: 'teal'})
spinner.pulse({amount: 0.05, period: 0.8})
```
