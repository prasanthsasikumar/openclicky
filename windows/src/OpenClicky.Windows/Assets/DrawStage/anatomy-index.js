// Anatomy index: find the atlas structures a drawing asked for.
//
// The bundled Z-Anatomy atlas ships index.json with one entry per structure
// ({ node, file, full, system, en, la, side, ta, licence, groups }) and the
// per-system glTF files beside it. This module is deliberately plain ES: no
// DOM, no three.js, so figure3d.js and the Node tests share it.
//
// createAnatomyIndex() builds the lookup maps once; find() is pure and cheap
// afterwards. Lookup order: exact name (node/en/la), topic group ("Heart",
// "Cranium"), synonym ("skull", "LAD"), then every word of the query.

const DEFAULT_LIMIT = 400;

const SIDE_WORDS = new Map([
  ["left", "left"],
  ["l", "left"],
  ["right", "right"],
  ["r", "right"],
]);

// Short, medical synonym table. Targets are atlas group names or exact English
// names; a target that is not in the bundled index is ignored by
// createAnatomyIndex. (SA node / AV node are not in the atlas, and the atlas
// calls the LAD the "Anterior interventricular artery".)
const SYNONYMS = {
  skull: { label: "Skull", groups: ["Cranium"], names: ["Mandible"] },
  brain: { label: "Brain", groups: ["Brain", "Encephalon"] },
  encephalon: { label: "Brain", groups: ["Brain"] },
  lad: { label: "Anterior interventricular artery", names: ["Anterior interventricular artery"] },
  "left anterior descending artery": {
    label: "Anterior interventricular artery",
    names: ["Anterior interventricular artery"],
  },
  rca: { label: "Right coronary artery", names: ["Right coronary artery"] },
  lca: { label: "Left coronary artery", names: ["Left coronary artery"] },
  acl: { label: "Anterior cruciate ligament", names: ["Anterior cruciate ligament"] },
  pcl: { label: "Posterior cruciate ligament", names: ["Posterior cruciate ligament"] },
  mcl: { label: "Tibial collateral ligament", groups: ["Tibial collateral ligament"] },
  lcl: { label: "Fibular collateral ligament", names: ["Fibular collateral ligament"] },
  "achilles tendon": { label: "Calcaneal tendon", names: ["Calcaneal tendon"] },
  achilles: { label: "Calcaneal tendon", names: ["Calcaneal tendon"] },
  "funny bone": { label: "Ulnar nerve", names: ["Ulnar nerve"] },
  collarbone: { label: "Clavicle", names: ["Clavicle"] },
  kneecap: { label: "Patella", names: ["Patella"] },
  "shin bone": { label: "Tibia", names: ["Tibia"] },
  shinbone: { label: "Tibia", names: ["Tibia"] },
  windpipe: { label: "Trachea", names: ["Trachea"] },
  "voice box": { label: "Larynx", names: ["Larynx"] },
  voicebox: { label: "Larynx", names: ["Larynx"] },
  gullet: { label: "Oesophagus", names: ["Oesophagus"] },
  // Heart valves: the atlas keeps only an empty marker for each valve ("Aortic valve.j") and names the leaflets
  // without the valve's everyday name ("Left coronary leaflet"), so a valve is its leaflets.
  "aortic valve": { label: "Aortic valve", names: ["Left coronary leaflet", "Right coronary leaflet", "Non-coronary leaflet"] },
  "mitral valve": { label: "Mitral valve", names: ["Posterior leaflet of left atrioventricular valve", "Anterior leaflet of left atrioventricular valve"] },
  "left atrioventricular valve": { label: "Mitral valve", names: ["Posterior leaflet of left atrioventricular valve", "Anterior leaflet of left atrioventricular valve"] },
  "bicuspid valve": { label: "Mitral valve", names: ["Posterior leaflet of left atrioventricular valve", "Anterior leaflet of left atrioventricular valve"] },
  "tricuspid valve": { label: "Tricuspid valve", names: ["Anterior leaflet of right atrioventricular valve", "Inferior leaflet of right atrioventricular valve", "Septal leaflet of right atrioventricular valve"] },
  "right atrioventricular valve": { label: "Tricuspid valve", names: ["Anterior leaflet of right atrioventricular valve", "Inferior leaflet of right atrioventricular valve", "Septal leaflet of right atrioventricular valve"] },
  "pulmonary valve": { label: "Pulmonary valve", names: ["Anterior semilunar leaflet of pulmonary valve", "Left semilunar leaflet of pulmonary valve", "Right semilunar leaflet of pulmonary valve"] },
  // Common lay names that are not exact atlas terms.
  "shoulder blade": { label: "Scapula", names: ["Scapula"] },
  breastbone: { label: "Sternum", groups: ["Sternum"], names: ["Sternum"] },
  tailbone: { label: "Coccyx", names: ["Coccyx"] },
  cheekbone: { label: "Zygomatic bone", names: ["Zygomatic bone"] },
  jawbone: { label: "Mandible", names: ["Mandible"] },
  "thigh bone": { label: "Femur", names: ["Femur"] },
  "calf bone": { label: "Fibula", names: ["Fibula"] },
  "heel bone": { label: "Calcaneus", names: ["Calcaneus"] },
  "hip bone": { label: "Hip bone", names: ["Hip bone"] },
  "belly button": { label: "Umbilicus", names: ["Umbilicus"] },
  "adam's apple": { label: "Laryngeal prominence", names: ["Laryngeal prominence"] },
  backbone: { label: "Vertebral column", groups: ["Vertebral column"] },
  spine: { label: "Vertebral column", groups: ["Vertebral column"] },
};

function normalise(value) {
  return String(value ?? "")
    .normalize("NFKD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .replace(/\s+/g, " ")
    .trim()
    .replace(/^the\s+/, "");
}

function wordsOf(value) {
  return normalise(value).match(/[a-z0-9]+/g) || [];
}

function titleCase(text) {
  return text ? text.charAt(0).toUpperCase() + text.slice(1) : "";
}

function normaliseSide(value) {
  const side = normalise(value);
  return side === "left" || side === "l" ? "left" : side === "right" || side === "r" ? "right" : null;
}

// "left femur" -> { text: "femur", side: "left" }; opts.side wins when given.
function splitSide(query, optsSide) {
  const words = normalise(query).split(" ").filter(Boolean);
  let side = normaliseSide(optsSide);
  const kept = [];
  for (const word of words) {
    const wordSide = SIDE_WORDS.get(word);
    if (wordSide) {
      if (!side) side = wordSide;
      continue;
    }
    kept.push(word);
  }
  return { text: kept.join(" "), side };
}

function isMarker(entry) {
  return /\.(?:j|i|g)$/.test(entry.node || "");
}

function containsAll(entryWords, queryWords) {
  if (!queryWords.length) return false;
  const present = new Set(entryWords);
  return queryWords.every((word) => present.has(word));
}

function withSide(entries, side) {
  if (!side) return entries;
  const sided = entries.filter((entry) => entry.side === side);
  return sided.length ? sided : entries.filter((entry) => !entry.side);
}

// Group and synonym answers should name real geometry when the atlas has it;
// the atlas's empty `.j` topic markers would otherwise crowd the result.
function preferMeshes(entries) {
  const meshes = entries.filter((entry) => !isMarker(entry));
  return meshes.length ? meshes : entries;
}

function sideLabel(side, name) {
  const prefix = side === "left" ? "Left" : side === "right" ? "Right" : "";
  if (!name) return prefix;
  if (!prefix) return name;
  // "Femur" -> "Left femur", but leave proper nouns like "LAD" alone.
  const lowered = /^[A-Z][a-z]/.test(name) ? name.charAt(0).toLowerCase() + name.slice(1) : name;
  return prefix + " " + lowered;
}

function makeSorter(systemOrder) {
  return (a, b) => {
    const rankA = systemOrder.get(a.system) ?? Number.MAX_SAFE_INTEGER;
    const rankB = systemOrder.get(b.system) ?? Number.MAX_SAFE_INTEGER;
    if (rankA !== rankB) return rankA - rankB;
    const enA = normalise(a.en);
    const enB = normalise(b.en);
    if (enA !== enB) return enA < enB ? -1 : 1;
    return a.node < b.node ? -1 : a.node > b.node ? 1 : 0;
  };
}

function makeResult(entries, matched, label, limit, sort) {
  const seen = new Set();
  const structures = [];
  for (const entry of entries) {
    const key = (entry.system || "") + "\u0000" + entry.node;
    if (seen.has(key)) continue;
    seen.add(key);
    structures.push(entry);
  }
  structures.sort(sort);
  const cap = Number.isFinite(limit) ? Math.max(0, Math.floor(limit)) : DEFAULT_LIMIT;
  return { structures: structures.slice(0, cap), matched, label };
}

function groupLabel(text, entries, side) {
  const match = entries.find((entry) => entry.groups.some((group) => normalise(group) === text));
  const name = match ? match.groups.find((group) => normalise(group) === text) : titleCase(text);
  return side ? sideLabel(side, name) : name;
}

function exactLabel(entries, side) {
  const name = (entries[0] && (entries[0].en || entries[0].node)) || "";
  return side ? sideLabel(side, name) : name;
}

export function createAnatomyIndex(indexJson) {
  const source = Array.isArray(indexJson) ? indexJson : (indexJson && indexJson.structures) || [];
  const exact = new Map();
  const groupMap = new Map();
  const worded = [];
  const systemOrder = new Map();

  const add = (map, key, entry) => {
    if (!key) return;
    let list = map.get(key);
    if (!list) map.set(key, (list = []));
    if (!list.includes(entry)) list.push(entry);
  };

  for (const raw of source) {
    const entry = {
      ...raw,
      groups: Array.isArray(raw.groups) ? raw.groups : [],
      enWords: wordsOf(raw.en),
      laWords: wordsOf(raw.la),
    };
    if (!systemOrder.has(entry.system)) systemOrder.set(entry.system, systemOrder.size);
    add(exact, normalise(entry.en), entry);
    add(exact, normalise(entry.la), entry);
    add(exact, normalise(entry.node), entry);
    for (const group of entry.groups) add(groupMap, normalise(group), entry);
    worded.push(entry);
  }

  const sort = makeSorter(systemOrder);

  const synonyms = new Map();
  for (const [phrase, spec] of Object.entries(SYNONYMS)) {
    const groups = (spec.groups || []).map(normalise).filter((name) => groupMap.has(name));
    const names = (spec.names || []).map(normalise).filter((name) => exact.has(name));
    if (!groups.length && !names.length) continue;
    synonyms.set(normalise(phrase), { label: spec.label, groups, names });
  }

  function lookupNamed(text, side, limit) {
    const exactHits = exact.get(text);
    if (exactHits && exactHits.length) {
      const sided = withSide(exactHits, side);
      const grouped = groupMap.get(text);
      // A bare group marker ("Brain.j", "Cranium.j") names a topic, not the
      // topic: when the atlas has a group of that name, answer with the group.
      if (grouped && sided.length && sided.every(isMarker)) {
        return makeResult(
          preferMeshes(withSide(grouped, side)),
          "group",
          groupLabel(text, grouped, side),
          limit,
          sort,
        );
      }
      // Only empty markers ("Aortic valve.j", "Pulmonary valve.j"): there is nothing to show, so a synonym or the
      // words of the name (a valve's leaflets) answer instead; the markers are the answer only when nothing else is.
      const hasShapes = (result) => !!result && result.structures.some((entry) => !isMarker(entry));
      if (sided.length && !(sided.every(isMarker) && (synonyms.has(text) || hasShapes(lookupWords(text, side, limit))))) {
        return makeResult(sided, "exact", exactLabel(sided, side), limit, sort);
      }
    }

    const grouped = groupMap.get(text);
    if (grouped && grouped.length) {
      return makeResult(
        preferMeshes(withSide(grouped, side)),
        "group",
        groupLabel(text, grouped, side),
        limit,
        sort,
      );
    }

    const synonym = synonyms.get(text);
    if (synonym) {
      const found = [];
      for (const name of synonym.groups) found.push(...groupMap.get(name));
      for (const name of synonym.names) found.push(...exact.get(name));
      if (found.length) {
        const label = side ? sideLabel(side, synonym.label) : synonym.label;
        return makeResult(preferMeshes(withSide(found, side)), "synonym", label, limit, sort);
      }
    }

    return null;
  }

  function lookupWords(text, side, limit) {
    const queryWords = wordsOf(text);
    if (!queryWords.length) return null;
    const matches = [];
    for (const entry of worded) {
      const inEn = containsAll(entry.enWords, queryWords);
      const inLa = containsAll(entry.laWords, queryWords);
      if (!inEn && !inLa) continue;
      const extra = Math.min(
        inEn ? entry.enWords.length - queryWords.length : Number.POSITIVE_INFINITY,
        inLa ? entry.laWords.length - queryWords.length : Number.POSITIVE_INFINITY,
      );
      matches.push({ extra, entry });
    }
    if (!matches.length) return null;
    matches.sort((a, b) => a.extra - b.extra || sort(a.entry, b.entry));
    const label = side ? sideLabel(side, titleCase(text)) : titleCase(text);
    return makeResult(withSide(matches.map((match) => match.entry), side), "words", label, limit, sort);
  }

  function find(query, opts) {
    const options = opts || {};
    const text = normalise(query);
    if (!text) return { structures: [], matched: "none", label: "" };

    const optsSide = normaliseSide(options.side);
    const split = splitSide(query, options.side);

    // Try the query as written first: "Left coronary artery" and "Right atrium"
    // are structure names, not a side plus a shorter name. Then try it with the
    // side word removed ("left femur" -> "femur" + left), before falling back to
    // loose word matching, which must not beat "right kidney" -> Kidney.r.
    const named = lookupNamed(text, optsSide, options.limit);
    if (named) return named;
    if (split.text && split.text !== text) {
      const sided = lookupNamed(split.text, split.side, options.limit);
      if (sided) return sided;
    }

    const words = lookupWords(text, optsSide, options.limit);
    if (words) return words;
    if (split.text && split.text !== text) {
      const sided = lookupWords(split.text, split.side, options.limit);
      if (sided) return sided;
    }

    return { structures: [], matched: "none", label: "" };
  }

  return { find };
}
