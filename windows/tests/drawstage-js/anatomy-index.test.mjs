// The anatomy finder against the real bundled atlas index: the queries a drawing
// makes ("the heart", "left femur", "LAD", "gullet") must land on the right
// structures. Run with: node --test windows/tests/drawstage-js/

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createAnatomyIndex } from "../../src/OpenClicky.Windows/Assets/DrawStage/anatomy-index.js";

const indexPath = new URL(
  "../../src/OpenClicky.Windows/Assets/DrawStage/library/anatomy/index.json",
  import.meta.url,
);
const index = JSON.parse(readFileSync(indexPath, "utf8"));
const anatomy = createAnatomyIndex(index);

const nodes = (result) => result.structures.map((structure) => structure.node);

test("heart is found as its topic group", () => {
  const result = anatomy.find("heart");
  assert.equal(result.matched, "group");
  assert.ok(result.structures.length > 0);
  assert.ok(nodes(result).includes("Right atrium"), "Right atrium");
  assert.ok(nodes(result).includes("Left ventricle"), "Left ventricle");
});

test("the skull finds the cranium and the mandible", () => {
  const result = anatomy.find("the skull");
  assert.equal(result.matched, "synonym");
  assert.ok(nodes(result).some((node) => node.startsWith("Parietal bone")), "a parietal bone");
  assert.ok(nodes(result).includes("Mandible"), "Mandible");
});

test("left femur is only the left bone", () => {
  const result = anatomy.find("left femur");
  assert.equal(result.matched, "exact");
  assert.deepEqual(nodes(result), ["Femur.l"]);
});

test("femur finds both sides", () => {
  const result = anatomy.find("femur");
  assert.equal(result.matched, "exact");
  assert.ok(nodes(result).includes("Femur.l"), "Femur.l");
  assert.ok(nodes(result).includes("Femur.r"), "Femur.r");
});

test("brachial plexus finds the plexus group", () => {
  const result = anatomy.find("brachial plexus");
  assert.equal(result.matched, "group");
  assert.ok(result.structures.length > 10);
  assert.ok(nodes(result).includes("Superior trunk of brachial plexus.r"), "a brachial plexus part");
});

test("LAD is the anterior interventricular artery", () => {
  const result = anatomy.find("LAD");
  assert.equal(result.matched, "synonym");
  assert.ok(nodes(result).includes("Anterior interventricular artery"));
});

test("a heart valve is its leaflets (the atlas keeps only an empty marker for the valve)", () => {
  const aortic = anatomy.find("aortic valve");
  assert.equal(aortic.matched, "synonym");
  assert.deepEqual(new Set(nodes(aortic)), new Set(["Left coronary leaflet", "Right coronary leaflet", "Non-coronary leaflet"]));
  assert.ok(nodes(anatomy.find("mitral valve")).includes("Posterior leaflet of left atrioventricular valve"), "mitral");
  assert.ok(nodes(anatomy.find("tricuspid valve")).includes("Septal leaflet of right atrioventricular valve"), "tricuspid");
  assert.equal(anatomy.find("pulmonary valve").structures.length, 3, "pulmonary");
});

test("kneecap is the patella", () => {
  const result = anatomy.find("kneecap");
  assert.equal(result.matched, "synonym");
  assert.ok(nodes(result).includes("Patella.l"), "Patella.l");
  assert.ok(nodes(result).includes("Patella.r"), "Patella.r");
});

test("kidney finds both kidneys", () => {
  const result = anatomy.find("kidney");
  assert.equal(result.matched, "exact");
  assert.ok(nodes(result).includes("Kidney.l"), "Kidney.l");
  assert.ok(nodes(result).includes("Kidney.r"), "Kidney.r");
});

test("a Latin name finds its English structure", () => {
  const result = anatomy.find("Os frontale");
  assert.equal(result.matched, "exact");
  assert.ok(nodes(result).includes("Frontal bone"));
});

test("gullet is the oesophagus", () => {
  const result = anatomy.find("gullet");
  assert.equal(result.matched, "synonym");
  assert.ok(nodes(result).includes("Oesophagus"));
});

test("a nonsense word matches nothing", () => {
  const result = anatomy.find("zygomorphic quux");
  assert.equal(result.matched, "none");
  assert.deepEqual(result.structures, []);
});

test("opts.side picks a side and limit caps the result", () => {
  assert.deepEqual(nodes(anatomy.find("femur", { side: "right" })), ["Femur.r"]);
  const limited = anatomy.find("brain", { limit: 5 });
  assert.equal(limited.structures.length, 5);
});
