Draco decoder for the drawing stage's GLB models (Google Draco, Apache License 2.0).

- `draco_decoder.js` and `draco_decoder.wasm` are the `examples/jsm/libs/draco/gltf/` build from three.js r172
  (https://github.com/mrdoob/three.js/tree/r172/examples/jsm/libs/draco/gltf), the same three.js version as
  `vendor/three`.
- They are loaded only when a model turns out to use `KHR_draco_mesh_compression` (NASA's bundled GLBs do), so an
  ordinary drawing never pays for them. Draco's licence: https://github.com/google/draco/blob/main/LICENSE
