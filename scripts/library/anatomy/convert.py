"""Convert the Z-Anatomy Blender atlas into per-system glTF binaries.

Run headless (Blender 5.x):

    blender -b Startup.blend --python scripts/library/anatomy/convert.py -- \
        <outDir> <TA2.csv path> [<bundled anatomy dir>]

Outputs:
    <bundled>/full/<slug>.glb             CC BY-SA geometry, the atlas's original detail (close-ups)
    <bundled>/nc/full/<slug>.glb          non-commercial geometry, original detail
    <bundled>/light/<slug>.glb            CC BY-SA geometry, light detail (overviews)
    <bundled>/nc/light/<slug>.glb         non-commercial geometry, light detail (bundled: OpenClicky is
                                          free and local only; kept in their own files with their own licence)
    <bundled>/index.json                  one entry per structure
    <bundled>/LICENSE.md                  attribution notice
    <outDir>/summary.json                 machine-readable run report

One glTF node per anatomical structure, named after the Blender object (name
trimmed). Node extras carry the TA2 metadata and the structure's topic groups
(the collection path inside its system, e.g. ["Vessels", "Heart"]). The atlas file is opened by
Blender and is never written back.

Blender's Python only: no third-party packages.
"""

import bpy
import json
import os
import re
import struct
import sys
import time

T0 = time.time()


def log(*args):
    print("[anatomy]", *args, flush=True)


# --------------------------------------------------------------------------
# Arguments
# --------------------------------------------------------------------------

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(argv) < 2:
    raise SystemExit("usage: convert.py -- <outDir> <TA2.csv> [bundledAnatomyDir]")

OUT_DIR = os.path.abspath(argv[0])
TA2_CSV = os.path.abspath(argv[1])
HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
BUNDLE_DIR = os.path.abspath(argv[2]) if len(argv) > 2 else os.path.join(
    REPO_ROOT, "windows", "src", "OpenClicky.Windows", "Assets", "DrawStage", "library", "anatomy"
)

# Development aid: ANATOMY_ONLY=regions,joints converts a subset only.
ONLY = {s.strip() for s in os.environ.get("ANATOMY_ONLY", "").split(",") if s.strip()}

OUTPUT_DIRS = [
    os.path.join(BUNDLE_DIR, "full"),
    os.path.join(BUNDLE_DIR, "nc", "full"),
    os.path.join(BUNDLE_DIR, "light"),
    os.path.join(BUNDLE_DIR, "nc", "light"),
]
for _dir in OUTPUT_DIRS:
    os.makedirs(_dir, exist_ok=True)
    for _name in os.listdir(_dir):
        if _name.endswith(".glb"):
            os.remove(os.path.join(_dir, _name))

CC_BY_SA = "CC BY-SA 4.0"
CC_BY_NC_SA = "CC BY-NC-SA 4.0"
CC_BY_NC = "CC BY-NC 4.0"

CREDIT = "Z-Anatomy (CC BY-SA 4.0), BodyParts3D (CC BY-SA 2.1 JP)"

# Top-level collections of the atlas, in atlas order.
SYSTEM_SLUGS = {
    "Skeletal system": "skeleton",
    "Muscular insertions": "muscle-insertions",
    "Joints": "joints",
    "Muscular system": "muscles",
    "Cardiovascular system": "cardiovascular",
    "Lymphoid organs": "lymphoid",
    "Nervous system & Sense organs": "nervous",
    "Visceral systems": "visceral",
    "Regions of human body": "regions",
}

# Objects that belong to the two non-commercial models (see License.txt).
NC_EAR = [
    r"^cochlea\b",
    r"^vestibule\b",
    r"\bsemicircular (canal|duct)",
    r"\butricle\b",
    r"\bsaccule\b",
    r"\blabyrinth\b",
    r"spiral organ",
    r"cochlear duct",
    r"helicotrema",
    r"modiolus",
    r"cochlear cupula",
    r"spiral canal of cochlea",
    r"base of cochlea",
    r"\bendolymph\b",
    r"\bperilymph\b",
]
NC_KIDNEY = [
    r"^kidney\b",
    r"\bkidney\b",
    r"^renal pelvis\b",
    r"\brenal (cortex|medulla|pyramid|papilla|calyx|calix|column)",
]

LIGHT_BUDGET = 25_000_000  # bytes of .glb for the whole light set
LIGHT_TARGET = 22_000_000  # aim slightly under the budget
MIN_DECIMATE_TRIS = 500    # never decimate meshes smaller than this
CURVE_BEVEL = 0.0008       # metres; gives unbevelled curves a visible tube

# --------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------

SIDE_RE = re.compile(r"\.(?:(?P<qual>o|e)(?P<num>\d*))?(?P<side>[lr])$")
MARKER_RE = re.compile(r"\.(j|i|g)$")


def parse_name(name):
    """Split an atlas object name into (node name, English base, side, qualifier)."""
    node = name.strip()
    side = None
    qual = None
    m = SIDE_RE.search(node)
    if m:
        side = "left" if m.group("side") == "l" else "right"
        if m.group("qual"):
            qual = m.group("qual") + (m.group("num") or "")
        base = node[: m.start()]
    else:
        base = node
        strip = MARKER_RE.search(node)
        if strip:
            base = node[: strip.start()]
    return node, base.strip(), side, qual


def ta2_key(text):
    return re.sub(r"\s+", " ", text.strip()).casefold()


def parse_ta2(path):
    table = {}
    with open(path, encoding="utf-8-sig") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            if line.startswith('"') and line.endswith('"'):
                line = line[1:-1]
            parts = line.split(";")
            if len(parts) < 3 or parts[0] == "TA2ID" or not parts[1].strip():
                continue
            table[ta2_key(parts[1])] = {"la": parts[2].strip() or None, "ta": parts[0].strip() or None}
    return table


def tri_count(mesh):
    mesh.calc_loop_triangles()
    return len(mesh.loop_triangles)


def palette_for(name):
    n = (name or "").lower()
    if n.startswith(("bone", "dentine", "teeth")):
        return (0.86, 0.81, 0.71)
    if n.startswith("cartilage"):
        return (0.70, 0.80, 0.78)
    if n.startswith(("lung", "bronchi")):
        return (0.79, 0.45, 0.45)
    if n.startswith("nerve"):
        return (0.93, 0.90, 0.55)
    if n.startswith("lymph"):
        return (0.55, 0.75, 0.45)
    if n.startswith("skin"):
        return (0.80, 0.60, 0.47)
    if n.startswith("suture"):
        return (0.72, 0.70, 0.66)
    if n.startswith("organ"):
        return (0.62, 0.35, 0.30)
    return (0.75, 0.73, 0.70)


def material_colour(mat):
    """A single flat base colour for a source material.

    The atlas shades with node groups; the top-level Principled base colour or
    the top-level Gamma/RGB node is the flat colour the atlas assigns. The
    numbered material variants (Bone-1..8, Lung-1..9, ...) carry no colour
    node at all, so they fall back to a per-family palette.
    """
    if mat is not None and mat.use_nodes and mat.node_tree:
        for node in mat.node_tree.nodes:
            if node.type == "BSDF_PRINCIPLED":
                return tuple(node.inputs["Base Color"].default_value)[:3]
        for node in mat.node_tree.nodes:
            if node.type == "GAMMA":
                return tuple(node.inputs["Color"].default_value)[:3]
            if node.type == "RGB":
                return tuple(node.outputs[0].default_value)[:3]
    return palette_for(mat.name if mat else "")


def licence_for(base):
    low = base.lower()
    if any(re.search(p, low) for p in NC_EAR):
        return CC_BY_NC_SA
    if any(re.search(p, low) for p in NC_KIDNEY):
        return CC_BY_NC
    return CC_BY_SA


# --------------------------------------------------------------------------
# Read the atlas structure
# --------------------------------------------------------------------------

ta2 = parse_ta2(TA2_CSV)
log("TA2 rows:", len(ta2))

scene = bpy.context.scene

systems = []  # ordered list of dicts
coll_system = {}
for top in scene.collection.children:
    m = re.match(r"^([1-9]):\s*(.+)$", top.name)
    if not m:
        continue
    name = m.group(2).strip()
    slug = SYSTEM_SLUGS.get(name)
    if slug is None:
        raise SystemExit("unmapped top-level system: %r" % top.name)
    systems.append({"name": name, "slug": slug, "coll": top})
    stack = [top]
    while stack:
        c = stack.pop()
        coll_system[c.name] = slug
        stack.extend(c.children)

if ONLY:
    systems = [s for s in systems if s["slug"] in ONLY]
    coll_system = {k: v for k, v in coll_system.items() if v in ONLY}

SYSTEM_NAMES = {s["slug"]: s["name"] for s in systems}
log("systems:", ", ".join("%s=%s" % (s["slug"], s["name"]) for s in systems))


def system_of(obj):
    for c in obj.users_collection:
        slug = coll_system.get(c.name)
        if slug:
            return slug
    return None


# --------------------------------------------------------------------------
# Topic groups: the atlas also files every object under "Bonus collection" in
# a per-system topic tree ("Cardiovascular system" > "Heart", "Skeletal system"
# > "Cranium" > "Bones of cranium", ...). The export flattens that tree, so a
# lookup for "heart" or "cranium" would otherwise have nothing to go on: record
# the path from the system collection down to the collection holding the object.
# --------------------------------------------------------------------------

BONUS = bpy.data.collections.get("Bonus collection")

# First-level collections of the topic tree, mapped to the numbered systems.
TOPIC_SLUGS = {
    "Skeletal system": "skeleton",
    "Muscular insertions": "muscle-insertions",
    "Arthrology": "joints",
    "Muscular system": "muscles",
    "Cardiovascular system": "cardiovascular",
    "Lymphoid organs": "lymphoid",
    "Nervous system": "nervous",
    "Visceral systems": "visceral",
    "Regions of human body": "regions",
}

TOPIC_PATHS = {}  # object name -> [collection paths below "Bonus collection"]
if BONUS is not None:
    stack = [(BONUS, [])]
    while stack:
        coll, path = stack.pop()
        for obj in coll.objects:
            TOPIC_PATHS.setdefault(obj.name, []).append(path)
        for child in coll.children:
            stack.append((child, path + [child.name]))
    log("topic collections:", sum(1 for _ in TOPIC_PATHS), "objects with a topic path")

COLLECTION_OBJECTS = {c.name: len(c.objects) for c in bpy.data.collections}
NUMBER_PREFIX_RE = re.compile(r"^\s*\d+(?:\.\d+)*[.):]?\s*")


def groups_for(obj, slug):
    """The deepest topic path of *obj* inside its own system, system name out.

    An object can be filed in several places (e.g. a parietal bone is under both
    "Axial skeleton" and "Cranium"); the deepest path wins, and equal depths go
    to the smallest terminal collection, i.e. the most specific group.
    """
    paths = []
    for path in TOPIC_PATHS.get(obj.name, []):
        if not path or TOPIC_SLUGS.get(path[0]) != slug:
            continue
        groups = [NUMBER_PREFIX_RE.sub("", name).strip() for name in path[1:]]
        paths.append([name for name in groups if name])
    if not paths:
        return []
    paths.sort(key=lambda p: (-len(p), COLLECTION_OBJECTS.get(p[-1], 0) if p else 0, p))
    return paths[0]


# --------------------------------------------------------------------------
# Curves without a bevel get a thin round profile so they are visible
# --------------------------------------------------------------------------

unbevelled = 0
# A curve that shapes another curve (its bevel profile or taper, e.g. "Oesophagus-profile") must stay a flat line:
# giving it a tube of its own breaks the curve it shapes (the oesophagus then evaluates to no faces at all).
shaping = set()
for obj in bpy.data.objects:
    if obj.type == "CURVE" and obj.data is not None:
        for helper in (obj.data.bevel_object, obj.data.taper_object):
            if helper is not None:
                shaping.add(helper.name)
for obj in bpy.data.objects:
    if obj.type != "CURVE" or obj.name in shaping:
        continue
    cd = obj.data
    if cd is None or not cd.splines:
        continue
    if cd.bevel_object is None and cd.bevel_depth <= 0.0 and cd.extrude <= 0.0:
        cd.bevel_depth = CURVE_BEVEL
        cd.bevel_resolution = max(cd.bevel_resolution, 2)
        unbevelled += 1
log("curves given a fallback bevel:", unbevelled)

# --------------------------------------------------------------------------
# Collect the objects that go into the export
# --------------------------------------------------------------------------

entries = []
skipped = {"no_system": 0, "empty": 0, "no_splines": 0}
for obj in list(bpy.data.objects):
    if obj.type not in ("MESH", "CURVE"):
        continue
    slug = system_of(obj)
    if not slug:
        skipped["no_system"] += 1
        continue
    if obj.type == "CURVE" and (obj.data is None or not obj.data.splines):
        skipped["no_splines"] += 1
        continue
    if obj.type == "MESH" and obj.data is None:
        skipped["empty"] += 1
        continue
    # ".g" objects are the atlas's floating titles ("SKELETAL SYSTEM" as 3D text), not anatomy. (".j" objects are
    # empty group markers such as "Facial bones.j": kept, as named points for lookups.)
    if obj.name.strip().endswith(".g"):
        skipped["titles"] = skipped.get("titles", 0) + 1
        continue
    node, base, side, qual = parse_name(obj.name)
    licence = licence_for(base)
    entries.append({
        "src": obj,
        "slug": slug,
        "node": node,
        "base": base,
        "side": side,
        "qual": qual,
        "licence": licence,
        "groups": groups_for(obj, slug),
        "marker": obj.type == "MESH" and len(obj.data.polygons) == 0,
    })

log("export candidates:", len(entries), "skipped:", skipped)
grouped = sum(1 for entry in entries if entry["groups"])
log("structures with topic groups: %d, without: %d" % (grouped, len(entries) - grouped))

# Non-commercial list for the report.
nc_list = sorted({e["node"] for e in entries if e["licence"] != CC_BY_SA})
log("non-commercial structures (%d):" % len(nc_list))
for name in nc_list:
    log("  nc:", name)

# --------------------------------------------------------------------------
# Build one flat export collection: evaluated geometry in world space
# --------------------------------------------------------------------------

EXPORT_COLL = bpy.data.collections.new("ANATOMY_EXPORT")
scene.collection.children.link(EXPORT_COLL)

# Free the original names first: new objects take the exact node names.
for o in bpy.data.objects:
    o.name = "SRC:" + o.name

dg = bpy.context.evaluated_depsgraph_get()

simplified = {}
DEFAULT_COLOUR = (0.75, 0.73, 0.70)


def simplified_material(src):
    key = src.name if src else None
    if key in simplified:
        return simplified[key]
    colour = material_colour(src) if src else DEFAULT_COLOUR
    mat = bpy.data.materials.new("anat-" + (key or "default"))
    mat.use_nodes = True
    nt = mat.node_tree
    for node in list(nt.nodes):
        if node.type != "OUTPUT_MATERIAL":
            nt.nodes.remove(node)
    out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Base Color"].default_value = (colour[0], colour[1], colour[2], 1.0)
    bsdf.inputs["Roughness"].default_value = 0.85
    bsdf.inputs["Metallic"].default_value = 0.0
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    mat.diffuse_color = (colour[0], colour[1], colour[2], 1.0)
    simplified[key] = mat
    return mat


failures = []
used_names = {}

for i, entry in enumerate(entries):
    src = entry["src"]
    world = src.matrix_world.copy()
    det = world.to_3x3().determinant()

    # Unique node name within its (system, licence) file.
    group = (entry["slug"], "nc" if entry["licence"] != CC_BY_SA else "cc")
    names = used_names.setdefault(group, set())
    name = entry["node"]
    if name in names:
        k = 2
        while "%s_%d" % (entry["node"], k) in names:
            k += 1
        name = "%s_%d" % (entry["node"], k)
        entry["renamed_from"] = entry["node"]
    names.add(name)
    entry["node"] = name

    if entry["marker"]:
        obj = bpy.data.objects.new(name, None)
        obj.empty_display_type = "PLAIN_AXES"
        obj.empty_display_size = 0.005
        EXPORT_COLL.objects.link(obj)
        obj.matrix_world = world
        entry["obj"] = obj
        entry["tris"] = 0
        continue

    try:
        ev = src.evaluated_get(dg)
        if src.type == "MESH" and not src.modifiers:
            me = src.data.copy()
        else:
            me = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=False, depsgraph=dg)
        if me is None or len(me.polygons) == 0:
            raise RuntimeError("no faces after evaluation")
        me.transform(world)
        if det < 0:
            me.flip_normals()
        for slot_i, src_mat in enumerate(me.materials):
            me.materials[slot_i] = simplified_material(src_mat)
        if len(me.materials) == 0:
            me.materials.append(simplified_material(None))
        obj = bpy.data.objects.new(name, me)
        EXPORT_COLL.objects.link(obj)
    except Exception as exc:  # pragma: no cover - reported, not hidden
        failures.append({"node": entry["node"], "error": str(exc)})
        log("FAILED:", entry["node"], exc)
        continue

    entry["obj"] = obj
    entry["tris"] = tri_count(me)
    if (i + 1) % 500 == 0:
        log("prepared %d/%d" % (i + 1, len(entries)))

entries = [e for e in entries if "obj" in e]
log("prepared objects:", len(entries), "failures:", len(failures), "elapsed %.1fs" % (time.time() - T0))

# The atlas itself is no longer needed.
for o in [o for o in bpy.data.objects if o.name.startswith("SRC:")]:
    bpy.data.objects.remove(o, do_unlink=True)

# --------------------------------------------------------------------------
# Groups: one glTF per (system, licence)
# --------------------------------------------------------------------------

groups = {}
for entry in entries:
    key = (entry["slug"], "nc" if entry["licence"] != CC_BY_SA else "cc")
    groups.setdefault(key, []).append(entry)

for key in sorted(groups):
    tris = sum(e["tris"] for e in groups[key])
    log("group %s/%s: %d nodes, %d triangles" % (key[0], key[1], len(groups[key]), tris))

# --------------------------------------------------------------------------
# Export
# --------------------------------------------------------------------------

EXPORT_OPTS = {
    "export_format": "GLB",
    "use_selection": True,
    "export_apply": True,
    "export_yup": True,
    "export_draco_mesh_compression_enable": True,
    "export_draco_mesh_compression_level": 6,
    "export_draco_position_quantization": 14,
    "export_draco_normal_quantization": 10,
    "export_draco_texcoord_quantization": 12,
    "export_materials": "EXPORT",
    "export_image_format": "NONE",
    "export_extras": False,
    "export_cameras": False,
    "export_lights": False,
    "export_animations": False,
    "export_skins": False,
    "export_morph": False,
    "export_attributes": False,
    "export_vertex_color": "NONE",
    "export_texcoords": False,
    "export_normals": True,
    "export_tangents": False,
    "export_hierarchy_flatten_objs": False,
}

_op_props = set(bpy.ops.export_scene.gltf.get_rna_type().properties.keys())
_dropped = sorted(set(EXPORT_OPTS) - _op_props)
EXPORT_OPTS = {k: v for k, v in EXPORT_OPTS.items() if k in _op_props}
if _dropped:
    log("exporter options not available in this Blender build:", _dropped)


def extras_for(entry):
    match = ta2.get(ta2_key(entry["base"]))
    return {
        "en": entry["base"],
        "side": entry["side"],
        "la": match["la"] if match else None,
        "ta": match["ta"] if match else None,
        "system": SYSTEM_NAMES[entry["slug"]],
        "groups": entry["groups"],
    }


def inject_extras(path, meta):
    """Write node extras into the GLB JSON chunk (extras must allow null)."""
    with open(path, "rb") as fh:
        blob = fh.read()
    magic, version, total = struct.unpack_from("<III", blob, 0)
    if magic != 0x46546C67:
        raise RuntimeError("not a GLB: %s" % path)
    chunks = []
    off = 12
    while off < total:
        clen, ctype = struct.unpack_from("<II", blob, off)
        off += 8
        chunks.append((ctype, blob[off:off + clen]))
        off += clen
    doc = json.loads(chunks[0][1].decode("utf-8"))
    found = 0
    for node in doc.get("nodes", []):
        meta_entry = meta.get(node.get("name"))
        if meta_entry is not None:
            node["extras"] = meta_entry
            found += 1
    node_count = len(doc.get("nodes", []))
    accessors = doc.get("accessors", [])
    tris = 0
    for mesh in doc.get("meshes", []):
        for prim in mesh.get("primitives", []):
            if prim.get("mode", 4) != 4:
                continue
            if "indices" in prim:
                tris += accessors[prim["indices"]]["count"] // 3
            else:
                tris += accessors[prim["attributes"]["POSITION"]]["count"] // 3
    js = json.dumps(doc, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    js += b" " * ((4 - len(js) % 4) % 4)
    out_chunks = [(0x4E4F534A, js)]
    for ctype, data in chunks[1:]:
        pad = (4 - len(data) % 4) % 4
        out_chunks.append((ctype, data + b"\0" * pad))
    body = b"".join(struct.pack("<II", len(d), t) + d for t, d in out_chunks)
    header = struct.pack("<III", 0x46546C67, 2, 12 + len(body))
    with open(path, "wb") as fh:
        fh.write(header + body)
    return found, node_count, tris


def export_group(key, path, ratio=1.0):
    members = groups.get(key)
    if not members:
        return 0, 0, 0
    objs = [e["obj"] for e in members]
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]

    modifiers = []
    if ratio < 1.0:
        for entry in members:
            if entry["tris"] >= MIN_DECIMATE_TRIS:
                mod = entry["obj"].modifiers.new("light", "DECIMATE")
                mod.ratio = ratio
                mod.use_collapse_triangulate = True
                modifiers.append((entry["obj"], mod))

    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, **EXPORT_OPTS)

    # Exact triangle count of what was written (modifiers are still attached).
    if modifiers:
        light_dg = bpy.context.evaluated_depsgraph_get()
        tris_after = 0
        for entry in members:
            try:
                light_dg.update()
                ev = entry["obj"].evaluated_get(light_dg)
                me = ev.to_mesh()
                tris_after += tri_count(me)
                ev.to_mesh_clear()
            except Exception:
                tris_after += entry["tris"]
    else:
        tris_after = sum(e["tris"] for e in members)

    for obj, mod in modifiers:
        obj.modifiers.remove(mod)

    meta = {e["node"]: extras_for(e) for e in members}
    found, _node_count, _written_tris = inject_extras(path, meta)
    if found != len(meta):
        log("WARNING: %s has %d nodes with extras, expected %d" % (path, found, len(meta)))
    return len(members), tris_after, os.path.getsize(path)


def target_path(key, light):
    slug, kind = key
    if light:
        if kind == "nc":
            return os.path.join(BUNDLE_DIR, "nc", "light", slug + ".glb")
        return os.path.join(BUNDLE_DIR, "light", slug + ".glb")
    # The original detail ships too (34 MB): the viewer uses it for close-ups, the light set for overviews.
    if kind == "nc":
        return os.path.join(BUNDLE_DIR, "nc", "full", slug + ".glb")
    return os.path.join(BUNDLE_DIR, "full", slug + ".glb")


# --- full detail -----------------------------------------------------------

full_sizes = {}
for key in sorted(groups):
    path = target_path(key, light=False)
    nodes, tris, size = export_group(key, path, ratio=1.0)
    full_sizes[key] = size
    log("full %s/%s: %d nodes, %d tris, %.1f MB (%.1fs)" % (
        key[0], key[1], nodes, tris, size / 1e6, time.time() - T0))

# --- light detail ----------------------------------------------------------

slug_full = {}
for (slug, kind), size in full_sizes.items():
    slug_full[slug] = slug_full.get(slug, 0) + size

ratios = {}
for slug, size in slug_full.items():
    if not size:
        continue
    budget = LIGHT_TARGET * size / max(1, sum(slug_full.values()))
    ratios[slug] = max(0.02, min(1.0, (budget / size) ** 0.9))

log("light ratios (estimate):", {k: round(v, 3) for k, v in ratios.items()})

light_report = {}
# Each pass re-exports the whole light set (about an hour for the full atlas on a laptop), and the compressed size
# follows the triangle count closely, so two passes land within a few per cent of the target.
LIGHT_PASSES = 2
for iteration in range(1, LIGHT_PASSES + 1):
    total = 0
    light_report = {}
    for key in sorted(groups):
        path = target_path(key, light=True)
        ratio = ratios.get(key[0], 1.0)
        nodes, tris, size = export_group(key, path, ratio=ratio)
        light_report[key] = {"nodes": nodes, "tris": tris, "size": size, "ratio": ratio}
        total += size
    log("light pass %d: total %.1f MB (%.1fs)" % (iteration, total / 1e6, time.time() - T0))
    if total <= LIGHT_BUDGET and total >= LIGHT_TARGET * 0.75:
        break
    if iteration == LIGHT_PASSES:
        break
    factor = (LIGHT_TARGET / total) ** 0.7 if total else 1.0
    factor = max(0.6, min(1.8, factor))
    if abs(total - LIGHT_TARGET) / LIGHT_TARGET < 0.08:
        break
    for slug in ratios:
        ratios[slug] = max(0.02, min(1.0, ratios[slug] * factor))
    log("light ratios:", {k: round(v, 3) for k, v in ratios.items()})

# --------------------------------------------------------------------------
# index.json + LICENSE.md
# --------------------------------------------------------------------------

structures = []
for entry in entries:
    kind = "nc" if entry["licence"] != CC_BY_SA else "cc"
    light_rel = ("nc/light/%s.glb" % entry["slug"]) if kind == "nc" else ("light/%s.glb" % entry["slug"])
    full_rel = ("nc/full/%s.glb" % entry["slug"]) if kind == "nc" else ("full/%s.glb" % entry["slug"])
    match = ta2.get(ta2_key(entry["base"]))
    structures.append({
        "node": entry["node"],
        "file": light_rel,
        "full": full_rel,
        "system": SYSTEM_NAMES[entry["slug"]],
        "en": entry["base"],
        "la": match["la"] if match else None,
        "side": entry["side"],
        "ta": match["ta"] if match else None,
        "licence": entry["licence"],
        "groups": entry["groups"],
    })

structures.sort(key=lambda s: (list(SYSTEM_NAMES.values()).index(s["system"]), s["node"]))
index = {"credit": CREDIT, "structures": structures}
os.makedirs(BUNDLE_DIR, exist_ok=True)
with open(os.path.join(BUNDLE_DIR, "index.json"), "w", encoding="utf-8") as fh:
    json.dump(index, fh, ensure_ascii=False, indent=1)

LICENSE = """# Anatomy library licence

The models in this directory are derived from **Z-Anatomy - The libre 3D atlas
of anatomy - CC-BY-SA 4.0** and **BodyParts3D - The Database Center for Life
Science - CC-BY-SA 2.1 Japan**. They are distributed under the
[Creative Commons Attribution-ShareAlike 4.0 International License][cc-by-sa].

## Attribution

As requested by the atlas:

- "BodyParts3D - The Database Center for Life Science - CC-BY-SA 2.1 Japan"
- "Z-Anatomy - The libre 3D atlas of anatomy - CC-BY-SA 4.0"

The following models included in Z-Anatomy are **non-commercial**. They are kept
in their own files (`nc/light/…`) under their own licences, and are bundled only
because OpenClicky is free and runs locally; remove them before any paid or
commercial use:

- "Anatomy of the Inner Ear - by University of Dundee School of Medicine - CC-BY-NC-SA 4.0"
  ([CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/))
- "Kidney - by Lissie Cowley - CC-BY-NC 4.0"
  ([CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/))

## Source

- https://www.z-anatomy.com/
- https://dbarchive.biosciencedbc.jp/en/bodyparts3d/download.html

[cc-by-sa]: http://creativecommons.org/licenses/by-sa/4.0/
"""
with open(os.path.join(BUNDLE_DIR, "LICENSE.md"), "w", encoding="utf-8") as fh:
    fh.write(LICENSE)

# --------------------------------------------------------------------------
# summary.json + console report
# --------------------------------------------------------------------------

ta_matches = sum(1 for entry in entries if ta2.get(ta2_key(entry["base"])))
distinct_bases = len({ta2_key(e["base"]) for e in entries})
renamed = [e for e in entries if "renamed_from" in e]

summary = {
    "generated": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    "credit": CREDIT,
    "structures": len(entries),
    "ta2_matched": ta_matches,
    "ta2_distinct_base_names": distinct_bases,
    "non_commercial": nc_list,
    "topic_groups": {
        "with_groups": sum(1 for entry in entries if entry["groups"]),
        "without_groups": sum(1 for entry in entries if not entry["groups"]),
    },
    "renamed_nodes": [(e["renamed_from"], e["node"]) for e in renamed],
    "failures": failures,
    "systems": {},
    "sizes": {},
}
for slug in SYSTEM_NAMES:
    members = [e for e in entries if e["slug"] == slug]
    if not members:
        continue
    light = [v for (s, _k), v in light_report.items() if s == slug]
    full = [v for (s, _k), v in full_sizes.items() if s == slug]
    summary["systems"][slug] = {
        "name": SYSTEM_NAMES[slug],
        "nodes": len(members),
        "tris": sum(e["tris"] for e in members),
        "light_tris": sum(v["tris"] for v in light),
        "light_bytes": sum(v["size"] for v in light),
        "full_bytes": sum(full),
        "light_ratio": round(ratios.get(slug, 1.0), 3),
    }
for (slug, kind), v in sorted(light_report.items()):
    summary["sizes"]["light:%s:%s" % (slug, kind)] = v["size"]
for (slug, kind), v in sorted(full_sizes.items()):
    summary["sizes"]["full:%s:%s" % (slug, kind)] = v

with open(os.path.join(OUT_DIR, "summary.json"), "w", encoding="utf-8") as fh:
    json.dump(summary, fh, ensure_ascii=False, indent=1)

log("=" * 72)
log("structure %-20s %7s %10s %10s %9s %9s" % ("system", "nodes", "tris", "light tris", "light MB", "full MB"))
for slug, row in summary["systems"].items():
    log("%-26s %7d %10d %10d %9.1f %9.1f" % (
        slug, row["nodes"], row["tris"], row["light_tris"],
        row["light_bytes"] / 1e6, row["full_bytes"] / 1e6))
log("light total: %.1f MB (budget %.1f MB)" % (
    sum(v["size"] for v in light_report.values()) / 1e6, LIGHT_BUDGET / 1e6))
log("index entries: %d, TA2 matched: %d (%.1f%%), renamed nodes: %d" % (
    len(structures), ta_matches, 100.0 * ta_matches / max(1, len(entries)), len(renamed)))
log("topic groups: %d structures with a group path, %d without" % (
    sum(1 for e in entries if e["groups"]), sum(1 for e in entries if not e["groups"])))
log("failures:", len(failures))
log("total time: %.1fs" % (time.time() - T0))
