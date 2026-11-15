// Anatomy parts of a figure: which atlas detail to load, which structures to leave out, and the lines they carry.
//
// The atlas index (library/anatomy/index.json) answers a drawing's query with structures; figure3d.js then decides
// how much of the atlas to read and what to drop. These decisions are plain data, so this module has no DOM and no
// three.js, and the Node tests (windows/tests/drawstage-js/) check it directly.

const DEFAULT_LIMIT = 400;

// The atlas's empty group markers (".j"), reference points (".i") and floating titles (".g") have no geometry:
// a query can match them exactly ("Aortic valve", "Femur"), and they are never worth showing.
const MARKER = /\.(?:j|i|g)$/;

// Sheets of connective tissue: they wrap the muscles a drawing of "the muscles" would rather show.
const FASCIA = /fascia|aponeurosis/i;

const list = (structures) => (Array.isArray(structures) ? structures : []);

/** The structures that have real geometry, in the order they came. */
export function dropMarkers(structures) {
  return list(structures).filter((entry) => entry && !MARKER.test(String(entry.node || "")));
}

/**
 * Which atlas files to read: the full-detail ones for a handful of structures (a close look), the light ones for a
 * whole system (an overview). opts.detail ('auto' | 'full' | 'light') wins over the count.
 */
export function chooseDetail(structures, opts) {
  const wanted = String((opts && opts.detail) || "auto").toLowerCase();
  if (wanted === "light") return "light";
  if (wanted === "full") return "full";
  return list(structures).length <= DEFAULT_LIMIT ? "full" : "light";
}

/** Drops the fascia and aponeuroses a query did not ask for (opts.fascia: true keeps them). */
export function fasciaFilter(structures, query, opts) {
  const kept = list(structures);
  if (opts && opts.fascia === true) return kept.slice();
  if (FASCIA.test(String(query == null ? "" : query))) return kept.slice();
  return kept.filter((entry) => !FASCIA.test(String((entry && entry.en) || "")));
}

/** A structure as it is named to the person: "Left ventricle", "Femur (left)". */
export function structureLabel(entry) {
  const name = String((entry && (entry.en || entry.node)) || "").trim();
  const side = entry && entry.side;
  return side === "left" || side === "right" ? name + " (" + side + ")" : name;
}

/** The figure's credit line: the atlas's own attribution, plus whoever made the parts under another licence. */
export function anatomyCredit(structures) {
  const licences = new Set(list(structures).map((entry) => entry && entry.licence));
  let text = "Z-Anatomy · BodyParts3D · CC BY-SA";
  if (licences.has("CC BY-NC-SA 4.0")) text += " · inner ear: Univ. of Dundee CC BY-NC-SA";
  if (licences.has("CC BY-NC 4.0")) text += " · kidney: L. Cowley CC BY-NC";
  return text;
}
