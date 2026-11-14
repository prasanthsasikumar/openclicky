step(1); say("Let me build a 3D model of a human skull, starting with the rounded braincase and bony face.")
const c = {x: 260,y: 88,w:760,h:520}
text(640,42,'Human skull',{size:30,bold:true})
const f = figure(c.x,c.y,c.w,c.h,{tilt:10,turn:-28,zoom:0.96,ground:false})
const cranium = f.blob({...})
const forehead = f.blob(...)
const face = f.blob(...)
const leftCheek = f.blob(...)
const rightCheek = f.blob(...)
const upperJaw = f.blob(...)
const jawLeft = f.blob(...)
const jawRight = ...
const jawBody = ...
const chin=...