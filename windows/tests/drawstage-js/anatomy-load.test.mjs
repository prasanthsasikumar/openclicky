// What an anatomy part reads and leaves out: which atlas detail (full or light) a query is worth, the fascia and the
// atlas's empty markers it drops, how a structure is named and what credit the figure carries. Run with:
// node --test windows/tests/drawstage-js/

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  anatomyCredit,
  chooseDetail,
  dropMarkers,
  fasciaFilter,
  structureLabel,
} from "../../src/OpenClicky.Windows/Assets/DrawStage/anatomy-load.js";

const entry = (node, en, extra = {}) => ({
  node,
  en,
  side: null,
  licence: "CC BY-SA 4.0",
  system: "Skeletal system",
  ...extra,
});

const many = (count) => Array.from({ length: count }, (_, i) => entry("Bone" + i, "Bone " + i));

test("a handful of structures is read at full detail, a whole system at light", () => {
  assert.equal(chooseDetail(many(17), {}), "full");
  assert.equal(chooseDetail(many(400), {}), "full");
  assert.equal(chooseDetail(many(401), {}), "light");
  assert.equal(chooseDetail(many(401), { detail: "full" }), "full");
  assert.equal(chooseDetail(many(3), { detail: "light" }), "light");
  assert.equal(chooseDetail(undefined, undefined), "full");
});

test("the atlas's empty marker nodes are never shown", () => {
  const kept = dropMarkers([
    entry("Femur.i", "Femur"),
    entry("Femur.j", "Femur"),
    entry("SKELETAL SYSTEM.g", "SKELETAL SYSTEM"),
    entry("Femur.l", "Femur", { side: "left" }),
  ]);
  assert.deepEqual(kept.map((one) => one.node), ["Femur.l"]);
});

test("fascia and aponeuroses stay out unless the query asks for them", () => {
  const structures = [
    entry("Femur.l", "Femur"),
    entry("Antebrachial fascia.l", "Antebrachial fascia"),
    entry("Palmar aponeurosis.l", "Palmar aponeurosis"),
  ];
  assert.deepEqual(
    fasciaFilter(structures, "femur", {}).map((one) => one.en),
    ["Femur"],
  );
  assert.equal(fasciaFilter(structures, "fascia", {}).length, 3);
  assert.equal(fasciaFilter(structures, "muscles of the arm", { fascia: true }).length, 3);
  assert.equal(fasciaFilter(structures, "heart", undefined).length, 1);
});

test("a structure is named the way a person would say it", () => {
  assert.equal(structureLabel(entry("Femur.l", "Femur", { side: "left" })), "Femur (left)");
  assert.equal(structureLabel(entry("Left ventricle", "Left ventricle")), "Left ventricle");
  assert.equal(structureLabel(entry("Kidney.r", "Kidney", { side: "right" })), "Kidney (right)");
  assert.equal(structureLabel(entry("X", "")), "X");
});

test("the credit line names only the non-commercial parts that are shown", () => {
  const plain = [entry("Femur.l", "Femur")];
  assert.equal(anatomyCredit(plain), "Z-Anatomy · BodyParts3D · CC BY-SA");

  const innerEar = [entry("Cochlea", "Cochlea", { licence: "CC BY-NC-SA 4.0" })];
  assert.equal(
    anatomyCredit(innerEar),
    "Z-Anatomy · BodyParts3D · CC BY-SA · inner ear: Univ. of Dundee CC BY-NC-SA",
  );

  const kidney = [entry("Kidney.l", "Kidney", { licence: "CC BY-NC 4.0", side: "left" })];
  assert.equal(
    anatomyCredit(kidney),
    "Z-Anatomy · BodyParts3D · CC BY-SA · kidney: L. Cowley CC BY-NC",
  );

  assert.equal(
    anatomyCredit([...innerEar, ...kidney]),
    "Z-Anatomy · BodyParts3D · CC BY-SA · inner ear: Univ. of Dundee CC BY-NC-SA · kidney: L. Cowley CC BY-NC",
  );
  assert.equal(anatomyCredit([]), "Z-Anatomy · BodyParts3D · CC BY-SA");
});
