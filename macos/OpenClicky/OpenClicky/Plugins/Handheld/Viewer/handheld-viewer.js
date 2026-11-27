// The handheld's 3D picture on its settings page (Plugins/Handheld). It draws the device's own
// model, which the person supplies (the app ships none), lets it be turned by dragging, and
// tells the app which button was clicked so the page can show that button's choices.
//
// The app writes `window.handheldViewerConfiguration` before this runs:
//   { controls: { keyA: { name: "Key A", part: "<the model's node name>",
//                         gestures: [{ mark: "•", action: "Dictate (hold)" }, …] }, … },
//     selected: "keyA", reduceMotion: false }
// and afterwards calls `window.handheldViewer.select(id)`, `.press(id)` and
// `.setGestures({ keyA: [{ mark, action }, …], … })` when a binding changes.
// A click on a button posts { type: "select", control: id } to the app.

import * as THREE from './three/three.module.min.js';

const configuration = window.handheldViewerConfiguration || { controls: {}, selected: null, reduceMotion: false };
const canvas = document.getElementById('device');
const calloutLayer = document.getElementById('callouts');
const lineLayer = document.getElementById('lines');
const ground = document.getElementById('ground');
const statusLine = document.getElementById('status');

// Colours by the role a part plays, matched on what its name contains. The device itself is
// orange with a dark screen; a part that matches nothing is drawn like the shell.
const partLooks = [
  { nameContains: '屏幕', colour: 0x0c0e0d, roughness: 0.25, metalness: 0.1 },
  { nameContains: '玻璃', colour: 0x101312, roughness: 0.2, metalness: 0.1 },
  { nameContains: '触控', colour: 0xf79a4d, roughness: 0.55, metalness: 0 },
  { nameContains: '按键', colour: 0xffa552, roughness: 0.5, metalness: 0 },
  { nameContains: '滑轮', colour: 0x343a36, roughness: 0.4, metalness: 0.6 },
  { nameContains: '声学', colour: 0x1b1d1b, roughness: 0.8, metalness: 0 },
  { nameContains: 'typec', colour: 0x5b605c, roughness: 0.4, metalness: 0.7 },
];
const shellLook = { colour: 0xf2731d, roughness: 0.6, metalness: 0 };
const highlightColour = new THREE.Color(0xff6a00);

// How far a button moves, as a share of the model's size.
const hoverLift = 0.035;
const pressDip = 0.03;
const pressSeconds = 0.16;

// `?capture` keeps each finished frame in the canvas, which a screenshot tool needs to read it
// back; on screen it is not needed and costs a copy per frame.
const renderer = new THREE.WebGLRenderer({
  canvas, antialias: true, alpha: true, preserveDrawingBuffer: location.search.includes('capture'),
});
renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(28, 1, 0.1, 50);
camera.position.set(0, 0, 5.2);
scene.add(new THREE.HemisphereLight(0xffffff, 0x8a8a8a, 1.9));
const keyLight = new THREE.DirectionalLight(0xffffff, 2.2);
keyLight.position.set(2.5, 3, 4);
scene.add(keyLight);

// The device turns inside this group; dragging changes its two angles.
const turntable = new THREE.Group();
scene.add(turntable);
const restingYaw = 3.76;
const restingPitch = 0.2;
// The device floats: a slow rise and fall, a share of its size, with the faintest sway.
const floatSeconds = 5.2;
const floatHeight = 0.022;
const floatSway = 0.018;
let yaw = restingYaw;
let pitch = restingPitch;

/** Control id → { mesh, restPosition, outward, lift, dip, pressedUntil }. */
const controlParts = new Map();
let hoveredControlID = null;
let selectedControlID = configuration.selected;
let frameIsWanted = false;

function wantFrame() {
  if (frameIsWanted) return;
  frameIsWanted = true;
  requestAnimationFrame(drawFrame);
}

function resize() {
  const width = canvas.clientWidth || 1;
  const height = canvas.clientHeight || 1;
  renderer.setSize(width, height, false);
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
  wantFrame();
}

// A binary glTF file: a header, a JSON chunk describing nodes, meshes and where their numbers
// are, then one binary chunk holding the numbers. Only what such a plain model needs is read:
// positions, normals and optional indices of triangle meshes.
async function loadModel(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error('the model could not be read');
  const fileBytes = await response.arrayBuffer();
  const header = new DataView(fileBytes);
  if (header.getUint32(0, true) !== 0x46546c67) throw new Error('not a glTF binary file');
  const jsonLength = header.getUint32(12, true);
  const description = JSON.parse(new TextDecoder().decode(new Uint8Array(fileBytes, 20, jsonLength)));
  const binaryStart = 20 + jsonLength + 8;

  function numbers(accessorIndex) {
    const accessor = description.accessors[accessorIndex];
    const view = description.bufferViews[accessor.bufferView];
    const start = binaryStart + (view.byteOffset || 0) + (accessor.byteOffset || 0);
    if (accessor.componentType === 5126 && accessor.type === 'VEC3') {
      return new Float32Array(fileBytes.slice(start, start + accessor.count * 12));
    }
    if (accessor.componentType === 5125 && accessor.type === 'SCALAR') {
      return new Uint32Array(fileBytes.slice(start, start + accessor.count * 4));
    }
    if (accessor.componentType === 5123 && accessor.type === 'SCALAR') {
      return new Uint16Array(fileBytes.slice(start, start + accessor.count * 2));
    }
    throw new Error('a kind of number this viewer does not read');
  }

  const device = new THREE.Group();
  for (const node of description.nodes || []) {
    if (node.mesh === undefined) continue;
    const primitive = description.meshes[node.mesh].primitives[0];
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(numbers(primitive.attributes.POSITION), 3));
    if (primitive.attributes.NORMAL !== undefined) {
      geometry.setAttribute('normal', new THREE.BufferAttribute(numbers(primitive.attributes.NORMAL), 3));
    } else {
      geometry.computeVertexNormals();
    }
    if (primitive.indices !== undefined) geometry.setIndex(new THREE.BufferAttribute(numbers(primitive.indices), 1));
    const partName = node.name || '';
    const look = partLooks.find((candidate) => partName.toLowerCase().includes(candidate.nameContains)) || shellLook;
    const material = new THREE.MeshStandardMaterial({ color: look.colour, roughness: look.roughness, metalness: look.metalness });
    const mesh = new THREE.Mesh(geometry, material);
    mesh.name = partName;
    if (node.scale) mesh.scale.fromArray(node.scale);
    if (node.translation) mesh.position.fromArray(node.translation);
    if (node.rotation) mesh.quaternion.fromArray(node.rotation);
    device.add(mesh);
  }
  return device;
}

function placeDevice(device) {
  // Centred on the turntable and sized to fit the view whatever units the model is in.
  const bounds = new THREE.Box3().setFromObject(device);
  const size = bounds.getSize(new THREE.Vector3());
  const centre = bounds.getCenter(new THREE.Vector3());
  const fitScale = 1.75 / Math.max(size.x, size.y, size.z);
  const holder = new THREE.Group();
  holder.scale.setScalar(fitScale);
  device.position.sub(centre);
  holder.add(device);
  turntable.add(holder);

  const deviceBounds = new THREE.Box3().setFromObject(device);
  for (const [controlID, control] of Object.entries(configuration.controls)) {
    const mesh = device.children.find((child) => child.name === control.part);
    if (!mesh) continue;
    mesh.geometry.computeBoundingBox();
    // A button moves along the axis it sticks out on: the largest part of the line from the
    // device's middle to the button's middle.
    const partCentre = new THREE.Box3().setFromObject(mesh).getCenter(new THREE.Vector3());
    const fromDeviceCentre = partCentre.clone().sub(new THREE.Box3().setFromObject(device).getCenter(new THREE.Vector3()));
    // Measured against the device's size on each axis; a device with no depth on an axis has no
    // direction along it.
    const share = (distance, extent) => (extent > 0 ? distance / extent : 0);
    const relative = new THREE.Vector3(share(fromDeviceCentre.x, size.x), share(fromDeviceCentre.y, size.y), share(fromDeviceCentre.z, size.z));
    const outward = new THREE.Vector3();
    const largest = Math.max(Math.abs(relative.x), Math.abs(relative.y), Math.abs(relative.z));
    if (largest === Math.abs(relative.x)) outward.set(Math.sign(relative.x), 0, 0);
    else if (largest === Math.abs(relative.y)) outward.set(0, Math.sign(relative.y), 0);
    else outward.set(0, 0, Math.sign(relative.z));
    mesh.userData.controlID = controlID;
    controlParts.set(controlID, {
      mesh, restPosition: mesh.position.clone(), outward,
      // The middle of the part in its own space: where its label's line ends.
      middle: mesh.geometry.boundingBox.getCenter(new THREE.Vector3()),
      travel: Math.max(size.x, size.y, size.z), lift: 0, dip: 0, pressedUntil: 0,
    });
  }
  void deviceBounds;
}

// Where a part's middle is on the page, in pixels.
function pagePoint(part) {
  const point = part.mesh.localToWorld(part.middle.clone()).project(camera);
  return { x: (point.x + 1) / 2 * canvas.clientWidth, y: (1 - point.y) / 2 * canvas.clientHeight };
}

/** Control id → { box, line, dot, side }. */
const callouts = new Map();

function gestureLines(gestures) {
  return (gestures || []).map((gesture) => {
    const line = document.createElement('div');
    line.className = 'gesture';
    const mark = document.createElement('b');
    mark.textContent = gesture.mark;
    line.append(mark, document.createTextNode(gesture.action));
    line.title = gesture.action;
    return line;
  });
}

// One label per button, on the side of the device its button is on when the device rests, top to
// bottom in the order the buttons are. They stay put while the device is turned; their lines follow.
function buildCallouts() {
  turntable.rotation.set(restingPitch, restingYaw, 0);
  turntable.updateMatrixWorld(true);
  // The camera works its own matrices out when a frame is drawn, and none has been yet: without
  // this every button reads as being on the right.
  camera.updateMatrixWorld(true);
  camera.updateProjectionMatrix();
  // Where each button is in the view itself (-1 at its left edge to 1 at its right, 1 at its
  // top), not in pixels: the page may not have its size yet when the model arrives.
  const placed = [...controlParts].map(([controlID, part]) => (
    { controlID, point: part.mesh.localToWorld(part.middle.clone()).project(camera) }));
  for (const side of ['left', 'right']) {
    const onThisSide = placed
      .filter((entry) => (entry.point.x < 0) === (side === 'left'))
      .sort((first, second) => second.point.y - first.point.y);
    onThisSide.forEach((entry, index) => {
      const control = configuration.controls[entry.controlID];
      const box = document.createElement('div');
      box.className = `callout ${side}`;
      box.style[side] = '6px';
      box.style.top = `${Math.round((index + 0.5) / onThisSide.length * 100)}%`;
      box.style.transform = 'translateY(-50%)';
      const name = document.createElement('div');
      name.className = 'name';
      name.textContent = control.name;
      box.append(name, ...gestureLines(control.gestures));
      box.addEventListener('click', () => choose(entry.controlID));
      box.addEventListener('pointerenter', () => { hoveredControlID = entry.controlID; showLooks(); wantFrame(); });
      box.addEventListener('pointerleave', () => { hoveredControlID = null; showLooks(); wantFrame(); });
      calloutLayer.append(box);
      const line = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
      const dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
      dot.setAttribute('r', '3.5');
      lineLayer.append(line, dot);
      callouts.set(entry.controlID, { box, line, dot, side });
    });
  }
}

function drawLines() {
  const pageBox = canvas.getBoundingClientRect();
  for (const [controlID, callout] of callouts) {
    const part = controlParts.get(controlID);
    const end = pagePoint(part);
    const box = callout.box.getBoundingClientRect();
    const startX = (callout.side === 'left' ? box.right : box.left) - pageBox.left;
    const startY = box.top - pageBox.top + 15;
    const elbowX = startX + (callout.side === 'left' ? 22 : -22);
    callout.line.setAttribute('points', `${startX},${startY} ${elbowX},${startY} ${end.x},${end.y}`);
    callout.dot.setAttribute('cx', end.x);
    callout.dot.setAttribute('cy', end.y);
  }
}

function choose(controlID) {
  window.handheldViewer.select(controlID);
  window.handheldViewer.press(controlID);
  window.webkit?.messageHandlers?.handheld?.postMessage({ type: 'select', control: controlID });
}

function showLooks() {
  for (const [controlID, part] of controlParts) {
    const isSelected = controlID === selectedControlID;
    const isHovered = controlID === hoveredControlID;
    part.mesh.material.emissive.copy(highlightColour);
    part.mesh.material.emissiveIntensity = isSelected ? 0.55 : (isHovered ? 0.28 : 0);
    const callout = callouts.get(controlID);
    if (callout) {
      callout.box.classList.toggle('chosen', isSelected);
      callout.line.classList.toggle('chosen', isSelected);
    }
  }
}

function drawFrame(now) {
  frameIsWanted = false;
  let somethingIsStillMoving = false;
  for (const [controlID, part] of controlParts) {
    const wantsLift = (controlID === hoveredControlID || controlID === selectedControlID) ? 1 : 0;
    const wantsDip = now < part.pressedUntil ? 1 : 0;
    if (configuration.reduceMotion) {
      part.lift = wantsLift;
      part.dip = wantsDip;
    } else {
      part.lift += (wantsLift - part.lift) * 0.22;
      part.dip += (wantsDip - part.dip) * 0.35;
    }
    if (Math.abs(wantsLift - part.lift) > 0.002 || Math.abs(wantsDip - part.dip) > 0.002 || wantsDip === 1) somethingIsStillMoving = true;
    // A part's position is in the device's own space, the same space its size was measured in,
    // whatever scale the part's mesh carries (the model's parts are in millimetres, scaled down).
    const distance = (part.lift * hoverLift - part.dip * pressDip) * part.travel;
    part.mesh.position.copy(part.restPosition).addScaledVector(part.outward, distance);
  }
  // The float: up and down and the faintest sway, still when motion is reduced. Its shadow on
  // the ground shrinks a little as the device rises.
  const floatPhase = configuration.reduceMotion ? 0 : Math.sin(now / 1000 * (2 * Math.PI / floatSeconds));
  turntable.position.y = floatPhase * floatHeight * 1.75;
  turntable.rotation.set(pitch, yaw + floatPhase * floatSway, 0);
  turntable.updateMatrixWorld(true);
  ground.style.transform = `scale(${1 - floatPhase * 0.06})`;
  ground.style.opacity = String(0.9 - floatPhase * 0.12);
  renderer.render(scene, camera);
  drawLines();
  // The float never ends, so while the page can be seen there is always a next frame.
  if (somethingIsStillMoving || (!configuration.reduceMotion && !document.hidden)) wantFrame();
}

document.addEventListener('visibilitychange', wantFrame);

// Which button is under the pointer, if any.
const raycaster = new THREE.Raycaster();
function controlUnder(pointerEvent) {
  const box = canvas.getBoundingClientRect();
  const pointer = new THREE.Vector2(
    ((pointerEvent.clientX - box.left) / box.width) * 2 - 1,
    -((pointerEvent.clientY - box.top) / box.height) * 2 + 1);
  raycaster.setFromCamera(pointer, camera);
  const hit = raycaster.intersectObjects(turntable.children, true)[0];
  return hit && hit.object.userData.controlID ? hit.object.userData.controlID : null;
}

let dragStart = null;
let pointerMovedWhileDown = false;

canvas.addEventListener('pointerdown', (pointerEvent) => {
  dragStart = { x: pointerEvent.clientX, y: pointerEvent.clientY, yaw, pitch };
  pointerMovedWhileDown = false;
  canvas.setPointerCapture(pointerEvent.pointerId);
});

canvas.addEventListener('pointermove', (pointerEvent) => {
  if (dragStart) {
    const movedX = pointerEvent.clientX - dragStart.x;
    const movedY = pointerEvent.clientY - dragStart.y;
    if (Math.abs(movedX) + Math.abs(movedY) > 4) pointerMovedWhileDown = true;
    yaw = dragStart.yaw + movedX * 0.01;
    pitch = Math.max(-1.1, Math.min(1.1, dragStart.pitch + movedY * 0.01));
    wantFrame();
    return;
  }
  const controlID = controlUnder(pointerEvent);
  if (controlID !== hoveredControlID) {
    hoveredControlID = controlID;
    showLooks();
    wantFrame();
  }
  canvas.style.cursor = controlID ? 'pointer' : 'grab';
});

canvas.addEventListener('pointerup', (pointerEvent) => {
  const wasAClick = dragStart && !pointerMovedWhileDown;
  dragStart = null;
  if (!wasAClick) return;
  const controlID = controlUnder(pointerEvent);
  if (controlID) choose(controlID);
});

canvas.addEventListener('pointerleave', () => {
  if (hoveredControlID !== null) {
    hoveredControlID = null;
    showLooks();
    wantFrame();
  }
});

// A double click puts the device back the way it rests.
canvas.addEventListener('dblclick', (pointerEvent) => {
  if (controlUnder(pointerEvent)) return;
  yaw = restingYaw;
  pitch = restingPitch;
  wantFrame();
});

window.handheldViewer = {
  select(controlID) {
    selectedControlID = controlID;
    showLooks();
    wantFrame();
  },
  /** A press on the real device, or a click on the picture: the button dips and comes back. */
  press(controlID) {
    const part = controlParts.get(controlID);
    if (!part) return;
    part.pressedUntil = performance.now() + pressSeconds * 1000;
    wantFrame();
    setTimeout(wantFrame, pressSeconds * 1000 + 20);
  },
  /** How far a button is from where it rests, as a share of the device's size. For tests. */
  distanceFromRest(controlID) {
    const part = controlParts.get(controlID);
    if (!part) return null;
    return part.mesh.position.distanceTo(part.restPosition) / part.travel;
  },
  /** A binding changed: the labels say what each gesture does now. */
  setGestures(gesturesByControlID) {
    for (const [controlID, gestures] of Object.entries(gesturesByControlID)) {
      const callout = callouts.get(controlID);
      if (!callout) continue;
      callout.box.querySelectorAll('.gesture').forEach((line) => line.remove());
      callout.box.append(...gestureLines(gestures));
    }
    wantFrame();
  },
  /** Draws one frame at once. For tests: a page that is not on screen is given no frames. */
  drawNow() {
    drawFrame(performance.now());
  },
  /** Which side of the device each button's label is on. For tests. */
  calloutSides() {
    return Object.fromEntries([...callouts].map(([controlID, callout]) => [controlID, callout.side]));
  },
  /** What the last frame drew and onto what. For tests and for finding a blank picture's cause. */
  drawnState() {
    return {
      triangles: renderer.info.render.triangles, canvasWidth: canvas.width, canvasHeight: canvas.height,
      cssWidth: canvas.clientWidth, cssHeight: canvas.clientHeight, turntableY: turntable.position.y,
    };
  },
  /** For looking at it from outside the app: the angles, so a good resting view can be found. */
  turnTo(newYaw, newPitch) {
    yaw = newYaw;
    pitch = newPitch;
    wantFrame();
  },
};

new ResizeObserver(resize).observe(canvas);
resize();

loadModel('model.glb').then((device) => {
  placeDevice(device);
  buildCallouts();
  showLooks();
  statusLine.remove();
  wantFrame();
  window.handheldViewerIsReady = true;
}).catch((error) => {
  statusLine.textContent = 'The picture of the device could not be shown.';
  window.webkit?.messageHandlers?.handheld?.postMessage({ type: 'failed', reason: String(error && error.message || error) });
});
