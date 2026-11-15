# Anatomy library conversion

Turns the Z-Anatomy atlas (one Blender file, ~5,450 structures) into the glTF files the drawing stage bundles under
`windows/src/OpenClicky.Windows/Assets/DrawStage/library/anatomy/`.

## Inputs

- Blender 5.x (run headless).
- The atlas: `Z-Anatomy.zip` from https://github.com/Z-Anatomy/Models-of-human-anatomy (branch `master`), unzipped:
  `Z-Anatomy/Startup.blend`. Never saved back.
- `TA2.csv` from the same repository (English → Latin names and TA2 ids).

## Run

```bash
blender -b Z-Anatomy/Startup.blend --python scripts/library/anatomy/convert.py -- <workDir> <TA2.csv>
```

About 15 minutes on an 8-core laptop (Ryzen 7 4700U, 16 GB). `ANATOMY_ONLY=regions,joints` converts a subset while
working on the script.

## Output (bundled)

- `full/<system>.glb` — the atlas's original detail (≈35 MB in all). The viewer decodes only the structures a
  drawing asks for, so this one copy serves overviews and close-ups alike.
- `light/<system>.glb` — only with `ANATOMY_LIGHT=1`: a decimated copy (≈25 MB) for very weak graphics. Off by
  default, to keep the app small; `file` in index.json then points at the full file too.
- `nc/full/…` — the inner ear and the kidney, which Z-Anatomy took from non-commercial models (see
  `LICENSE.md`). Bundled because OpenClicky is free and local only; remove them before any paid use.
- `index.json` — one entry per structure: `node`, `file` (light), `full`, `system`, `en`, `la`, `side`, `ta`, `licence`,
  `groups` (the topic collection path inside the system, e.g. `["Vessels", "Heart"]`; empty when the atlas files the
  object only under its system).
- `LICENSE.md` — CC BY-SA 4.0 notice and the attributions the atlas asks for.

Systems: skeleton, muscle-insertions, joints, muscles, cardiovascular, lymphoid, nervous (with sense organs),
visceral, regions. Every structure is its own named node (the atlas's English Terminologia Anatomica name, `.l` / `.r`
for left and right) with extras `{en, side, la, ta, system, groups}`; 97.8 % match a TA2 Latin name. Curves (most vessels and
nerves) become tubes; Draco-compressed.

## What the script decides

- Each object once, in the first numbered system it belongs to; "Bonus collection", reference lines and cross-section
  planes are left out.
- `.g` objects are the atlas's floating titles ("SKELETAL SYSTEM" as 3D text): skipped. `.j` objects are empty group
  markers ("Facial bones.j"): kept as named points.
- A curve that shapes another curve (its bevel profile or taper, e.g. `Oesophagus-profile`) is never given a tube of
  its own, or the curve it shapes evaluates to nothing.
- Unbevelled curves get a thin round tube so they show.
