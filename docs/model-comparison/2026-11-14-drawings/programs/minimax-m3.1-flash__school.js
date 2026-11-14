step(1); say("Let me build a three-dimensional school with two floors, a pitched roof, a central entrance, and green grounds.");
const c = {x: 280, y: 82, w: 720, h: 500};
const title = text(c.x + c.w / 2, 51, '3D school model', {size: 28, bold: true, color: 'white'});
const f = figure(c.x, c.y, c.w, c.h, {tilt: 18, turn: -30, zoom: 1, ground: false});

f.box({w: 9, h: 0.16, d: 4.7, at: [0, -0.08, 0], round: 0.15, mat: 'paint:#6FA65A'});
f.box({w: 5.2, h: 0.22, d: 2.35, at: [0, 0.11, 0], mat: 'stone'});
f.box({w: 5, h: 2.6, d: 2.2, at: [0, 1.32, 0], mat: 'paint:#F1E3C1'});
f.extrude({points: [[-2.65, 0], [0, 1.02], [2.65, 0]], depth: 2.4, at: [0, 2.62, -1.2], mat: 'paint:#B5463A'});
f.box({w: 0.12, h: 0.12, d: 2.42, at: [0, 3.64, 0], mat: 'paint:#9D3931'});

f.box({w: 1.45, h: 1.25, d: 1.9, at: [-3.225, 0.625, 0], mat: 'paint:#E8D5B2'});
f.extrude({points: [[-0.78, 0], [0, 0.5], [0.78, 0]], depth: 2.05, at: [-3.225, 1.25, -1.025], mat: 'paint:#B5463A'});
f.box({w: 1.45, h: 1.25, d: 1.9, at: [3.225, 0.625, 0], mat: 'paint:#E8D5B2'});
f.extrude({points: [[-0.78, 0], [0, 0.5], [0.78, 0]], depth: 2.05, at: [3.225, 1.25, -1.025], mat: 'paint:#B5463A'});

f.box({w: 1.22, h: 1.08, d: 0.1, at: [0, 0.56, 1.13], mat: 'paint:#6B3E2E'});
f.box({w: 0.96, h: 0.93, d: 0.12, at: [0, 0.49, 1.21], mat: 'paint:#A83D32'});
f.box({w: 1.55, h: 0.16, d: 0.55, at: [0, 0.08, 1.39], mat: 'stone'});
f.box({w: 1.9, h: 0.12, d: 0.7, at: [0, 0.02, 1.62], mat: 'stone'});
f.box({w: 1.75, h: 0.14, d: 0.72, at: [0, 1.18, 1.48], mat: 'paint:#B5463A'});
f.cyl({r: 0.035, h: 1.1, at: [-0.72, 0.55, 1.78], copies: {n: 2, step: [1.44, 0, 0]}, mat: 'white'});

step(2); say("Rows of windows brighten both floors, while a clock tower crowns the middle with a small bell.");
f.box({w: 0.64, h: 0.64, d: 0.08, at: [-1.85, 1.05, 1.14], copies: {n: 2, step: [0.85, 0, 0]}, mirror: 'x', mat: 'white'});
f.box({w: 0.5, h: 0.5, d: 0.1, at: [-1.85, 1.05, 1.21], copies: {n: 2, step: [0.85, 0, 0]}, mirror: 'x', mat: 'glass'});
f.box({w: 0.64, h: 0.64, d: 0.08, at: [-2, 2.08, 1.14], copies: {n: 5, step: [1, 0, 0]}, mat: 'white'});
f.box({w: 0.5, h: 0.5, d: 0.1, at: [-2, 2.08, 1.21], copies: {n: 5, step: [1, 0, 0]}, mat: 'glass'});
f.box({w: 0.7, h: 0.55, d: 0.08, at: [-3.225, 0.67, 1], copies: {n: 2, step: [6.45, 0, 0]}, mat: 'white'});
f.box({w: 0.56, h: 0.41, d: 0.1, at: [-3.225, 0.67, 1.07], copies: {n: 2, step: [6.45, 0, 0]}, mat: 'glass'});
f.box({w: 0.08, h: 0.6, d: 0.5, at: [-2.52, 2, -0.62], copies: {n: 3, step: [0, 0, 0.62]}, mirror: 'x', mat: 'white'});
f.box({w: 0.1, h: 0.46, d: 0.38, at: [-2.56, 2, -0.62], copies: {n: 3, step: [0, 0, 0.62]}, mirror: 'x', mat: 'glass'});

f.box({w: 1.08, h: 1.55, d: 1.08, at: [0, 3.32, 0.02], mat: 'paint:#F1E3C1'});
f.cone({r: 0.76, h: 0.72, at: [0, 4.43, 0.02], mat: 'paint:#B5463A'});
f.cyl({r: 0.3, h: 0.08, axis: 'z', at: [0, 3.62, 0.6], mat: 'white'});
f.box({w: 0.03, h: 0.22, d: 0.025, at: [0, 3.7, 0.67], mat: 'ink'});
f.box({w: 0.19, h: 0.03, d: 0.025, at: [-0.06, 3.63, 0.68], rot: [0, 0, -12], mat: 'ink'});
f.lathe({points: [[0.02, -0.15], [0.08, -0.12], [0.15, 0.08], [0.21, 0.15]], axis: 'y', at: [0, 2.95, 0.63], mat: 'brass'});

step(3); say("A playground, flagpole, trees, and shrubs turn the surrounding grounds into a welcoming schoolyard.");
f.box({w: 1.6, h: 0.08, d: 2.2, at: [0, 0.04, 2.05], mat: 'stone'});
f.cyl({r: 0.045, h: 1.45, at: [-3.15, 0.73, 2.15], copies: {n: 2, step: [1.3, 0, 0]}, mat: 'steel'});
f.box({w: 1.48, h: 0.08, d: 0.08, at: [-2.5, 1.45, 2.15], mat: 'steel'});
f.cyl({r: 0.012, h: 0.95, at: [-2.75, 0.96, 2.15], copies: {n: 2, step: [0.5, 0, 0]}, mat: 'ink'});
f.box({w: 0.68, h: 0.08, d: 0.2, at: [-2.5, 0.46, 2.15], mat: 'paint:#D65345'});

f.cyl({r: 0.035, h: 2.45, at: [1.85, 1.225, 2.05], mat: 'steel'});
f.sphere({r: 0.065, at: [1.85, 2.5, 2.05], mat: 'gold'});
f.box({w: 0.58, h: 0.32, d: 0.025, at: [2.15, 2.23, 2.05], mat: 'paint:#D65345'});

f.cyl({r: 0.09, h: 1.35, at: [-3.95, 0.68, 0.25], mat: 'wood'});
f.blob({balls: [[0, 0, 0, 0.48], [-0.28, 0.08, 0.06, 0.34], [0.25, 0.12, -0.08, 0.36]], smooth: 0.4, at: [-3.95, 1.65, 0.25], mat: 'leaf'});
f.cyl({r: 0.09, h: 1.35, at: [3.95, 0.68, -0.15], mat: 'wood'});
f.blob({balls: [[0, 0, 0, 0.48], [-0.25, 0.1, 0.05, 0.35], [0.28, 0.08, -0.06, 0.36]], smooth: 0.4, at: [3.95, 1.65, -0.15], mat: 'leaf'});
f.blob({balls: [[0, 0, 0, 0.28], [-0.15, 0.05, 0, 0.2], [0.16, 0.03, 0.05, 0.21]], smooth: 0.4, at: [-3.1, 0.28, 1.35], mat: 'leaf'});
f.blob({balls: [[0, 0, 0, 0.28], [-0.14, 0.04, 0.04, 0.2], [0.17, 0.03, 0, 0.21]], smooth: 0.4, at: [3.15, 0.28, 1.25], mat: 'leaf'});

step(4); say("A few pupils by the doors show the scale, so the finished model feels complete and easy to recognize.");
f.capsule({r: 0.075, h: 0.28, at: [-0.52, 0.24, 1.88], mat: 'paint:#2F6FB0'});
f.sphere({r: 0.09, at: [-0.52, 0.5, 1.88], mat: 'skin:#C98F63'});
f.capsule({r: 0.075, h: 0.28, at: [0.45, 0.22, 2.02], mat: 'paint:#E08B35'});
f.sphere({r: 0.09, at: [0.45, 0.48, 2.02], mat: 'skin:#C98F63'});
f.capsule({r: 0.075, h: 0.28, at: [0.95, 0.25, 1.85], mat: 'paint:#7653A6'});
f.sphere({r: 0.09, at: [0.95, 0.51, 1.85], mat: 'skin:#C98F63'});
erase(title);
highlight(c.x + c.w / 2 - 135, 33, 270, 40, {color: 'yellow', fillOpacity: 0.45});
text(c.x + c.w / 2, 53, 'complete school model', {size: 27, bold: true, color: 'white'});

suggest(['Show from above', 'Turn the school']);