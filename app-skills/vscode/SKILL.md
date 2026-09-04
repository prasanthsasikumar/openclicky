---
name: Visual Studio Code
description: Teaching notes for VS Code — activity bar, sidebar, editor groups, panel, command palette, and what to point at.
apps: [com.microsoft.VSCode]
surfaces: [talk]
---

## Layout
The activity bar is the narrow column of icons at the far left (Explorer, Search, Source Control, Run and Debug, Extensions). Clicking one opens the sidebar next to it. Editors open as tabs across the top of the main area; the breadcrumb path sits under the tabs. The panel (Terminal, Problems, Output, Debug Console) slides up at the bottom. The status bar is the thin strip along the bottom edge with branch, errors, language mode and cursor position. The title area at the top-center holds the command center search box. The menu bar has File, Edit, Selection, View, Go, Run, Terminal, Help.

## Common tasks
- Command palette: shift-command-P; quick open a file: command-P.
- Toggle the sidebar: command-B; toggle the terminal panel: control-backtick.
- Find in files: shift-command-F, results appear in the sidebar.
- Go to symbol: shift-command-O; go to line: control-G.
- Split editor: command-backslash, or the split icon at the top-right of the editor.
- Commit: click the Source Control icon in the activity bar, type a message, press the check mark.
- Format document: shift-option-F.
- Open settings: command-comma; settings JSON via the command palette "Open User Settings (JSON)".

## Pointing hints
- "Where is the terminal?" — point at the panel along the bottom; if closed, mention control-backtick.
- "Where are my files?" — point at the Explorer icon at the top of the activity bar on the far left.
- "How do I install an extension?" — point at the Extensions icon (four squares) in the activity bar.
- "Where do I commit?" — point at the Source Control icon (branch) in the activity bar, then the message box at the top of that sidebar.
- "What are these errors?" — point at the error and warning counts at the left of the status bar, which open the Problems panel.
- "How do I change the language mode?" — point at the language name at the right of the status bar.

## Gotchas
- The activity bar can be moved or hidden (View > Appearance); the command palette always works.
- Workspace vs user settings: changes in a `.vscode/settings.json` only apply to this folder.
- Remote and dev-container sessions show a colored badge at the far left of the status bar; extensions may need installing on the remote side.
- Fork editors (Cursor, VSCodium) have different bundle ids and are not matched by this skill.
