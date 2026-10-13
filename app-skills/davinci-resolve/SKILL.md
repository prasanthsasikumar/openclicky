---
name: DaVinci Resolve
description: Teaching notes for DaVinci Resolve — the pages, where export and speed live, the common edits, what to point at.
apps: [com.blackmagic-design.DaVinciResolve, com.blackmagic-design.DaVinciResolveLite]
surfaces: [talk]
---

## Layout
- Page tabs along the very bottom of the window: Media, Cut, Edit, Fusion, Color, Fairlight, Deliver (rocket icon, far right). Most editing happens on **Edit**; exporting happens on **Deliver**.
- Edit page: Media Pool top left (the clips), the viewers in the middle, the timeline across the bottom half.
- Toolbar along the top: Media Pool, Effects and Sound Library buttons on the left; Mixer, Metadata and **Inspector** on the right. The Inspector panel (clip settings: zoom, volume, speed) opens on the right when that button is on.

## Common tasks
- Export a video: Deliver page → Render Settings (top left) → pick a preset such as YouTube → set the file name and Location (Browse) → **Add to Render Queue** (bottom of Render Settings) → **Render All** (Render Queue, top right). Quicker: File menu → Quick Export… → pick YouTube or H.264 → Export.
- Speed up or slow down a clip: right-click the clip in the timeline → Change Clip Speed… (or select it and press R) → type a speed such as 200% → Change. Or Inspector → Video → Speed Change.
- Cut a clip in two: move the playhead to the spot, then Timeline menu → Razor (⌘B); or the Blade tool (B) in the timeline toolbar and click the clip. Press A to go back to the arrow.
- Remove a piece: click it, press Delete. Right-click an empty gap → Delete Gap.
- Add a title: Effects (top left) → Toolbox → Titles → drag "Text" onto the timeline above the clip; type in Inspector → Title.
- Music volume and fades: drag the thin line across the audio clip up or down, or Inspector → Audio → Volume; drag the small white handles at the clip's top corners inward to fade.
- Import: File → Import → Media… (⌘I), or drag files from Finder into the Media Pool.
- Undo: ⌘Z. Save: ⌘S (projects live in the Project Manager, the house icon bottom right, not as a file in Finder).

## Pointing hints
- "Where is export?" → point at the Deliver tab (rocket, bottom right); on Deliver, point at Add to Render Queue, then Render All.
- "How do I speed it up?" → point at the clip in the timeline and say right-click → Change Clip Speed.
- "Where did my settings go / the right panel?" → point at Inspector, top right of the toolbar.
- "How do I cut?" → point at the playhead, then the Blade tool in the timeline toolbar.

## Gotchas
- Being on the wrong page is the usual confusion: say which page tab to click first, and point at it.
- Render All does nothing until a job is added to the Render Queue.
- An empty right side means the Inspector is switched off.
- The Blade tool stays on until A is pressed: every click cuts.
- Fusion and Color pages look alarming; to get back to editing, click Edit at the bottom.
- Change one step at a time and keep each step short: the user may be new to video editing.
