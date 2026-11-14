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

- `full/<system>.glb` — the atlas's original detail (≈34 MB in all), for close-ups.
- `light/<system>.glb` — decimated to fit ≈25 MB in all, for overviews.
- `nc/full/…`, `nc/light/…` — the inner ear and the kidney, which Z-Anatomy took from non-commercial models (see
  `LICENSE.md`). Bundled because OpenClicky is free and local only; remove them before any paid use.
- `index.json` — one entry per structure: `node`, `file` (light), `full`, `system`, `en`, `la`, `side`, `ta`, `licence`.
- `LICENSE.md` — CC BY-SA 4.0 notice and the attributions the atlas asks for.

Systems: skeleton, muscle-insertions, joints, muscles, cardiovascular, lymphoid, nervous (with sense organs),
visceral, regions. Every structure is its own named node (the atlas's English Terminologia Anatomica name, `.l` / `.r`
for left and right) with extras `{en, side, la, ta, system}`; 97.8 % match a TA2 Latin name. Curves (most vessels and
nerves) become tubes; Draco-compressed.

## What the script decides

- Each object once, in the first numbered system it belongs to; "Bonus collection", reference lines and cross-section
  planes are left out.
- `.g` objects are the atlas's floating titles ("SKELETAL SYSTEM" as 3D text): skipped. `.j` objects are empty group
  markers ("Facial bones.j"): kept as named points.
- A curve that shapes another curve (its bevel profile or taper, e.g. `Oesophagus-profile`) is never given a tube of
  its own, or the curve it shapes evaluates to nothing.
- Unbevelled curves get a thin round tube so they show.
