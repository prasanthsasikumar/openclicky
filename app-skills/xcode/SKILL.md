---
name: Xcode
description: Teaching notes for Xcode — navigator, editor, inspectors, toolbar, running and debugging, and what to point at.
apps: [com.apple.dt.Xcode]
surfaces: [talk]
---

## Layout
The toolbar across the top has the run (play) and stop buttons at the left, the scheme and destination selector next to them, and the activity/status view in the center. The navigator is the left panel (files, search, issues, tests, debug, breakpoints, reports; icons along its top). The editor fills the middle; the jump bar with the file path runs along its top. The inspector is the right panel (file, history, attributes). The debug area with console and variables slides up from the bottom when running. The menu bar has File, Edit, View, Navigate, Editor, Product, Debug, Integrate, Window.

## Common tasks
- Build: command-B; run: command-R; stop: command-period.
- Open quickly: shift-command-O, then type a file or symbol name.
- Find in project: shift-command-F, results appear in the navigator on the left.
- Show or hide panels: command-0 navigator, option-command-0 inspector, shift-command-Y debug area.
- Commit: Integrate > Commit, option-command-C (older versions: Source Control > Commit).
- Change the run destination: click the device name in the toolbar next to the scheme.
- Clean build folder: shift-command-K.
- Manage signing: select the project in the navigator, then the target, then the Signing and Capabilities tab in the editor.

## Pointing hints
- "How do I run it?" — point at the play button at the top-left of the toolbar.
- "Where do I pick the simulator?" — point at the destination name in the toolbar right of the scheme.
- "Where are the errors?" — point at the red icon in the activity view at the center of the toolbar, or the issue navigator icon (triangle) at the top of the left panel.
- "Where is the console output?" — point at the debug area along the bottom of the window.
- "How do I change signing?" — point at the project file at the top of the navigator, then the Signing and Capabilities tab in the editor.
- "Where is the file inspector?" — point at the right-hand panel.

## Gotchas
- If the toolbar is hidden (View > Show Toolbar), the run button is gone; command-R still works.
- The Integrate menu replaced the Source Control menu in Xcode 26; describe it as "the source control menu" if the version is unclear.
- A yellow warning in the activity view is not a build failure; red is.
- Simulators must be downloaded once (Settings > Components) before they appear as destinations.
