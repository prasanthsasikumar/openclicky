---
name: Terminal
description: Teaching notes for Terminal and iTerm2 — windows, tabs, panes, profiles, shell basics, and what to point at.
apps: [com.apple.Terminal, com.googlecode.iterm2]
surfaces: [talk]
---

## Layout
The window is one text area; the prompt is the last line, where the cursor sits. Tabs run along the top of the window when more than one is open. In Terminal the menu bar has Shell, Edit, View, Window; in iTerm2 it has Shell, Edit, View, Session, Scripts, Profiles, Toolbelt, Window. iTerm2 can split the window into panes separated by thin divider lines, and its toolbelt is an optional right-hand column. Settings live under the app menu > Settings (command-comma).

## Common tasks
- New tab: command-T; new window: command-N.
- Split panes (iTerm2): command-D vertical, shift-command-D horizontal; move between panes with option-command-arrows.
- Clear the screen: command-K, or type clear.
- Find in output: command-F.
- Copy and paste: command-C / command-V (control-C interrupts the running program instead).
- Change font or colors: Settings > Profiles, pick the profile, then Text or Colors.
- Open the current folder in Finder: type open . and press return.
- Scroll back through history: mouse wheel, or up-arrow for previous commands.

## Pointing hints
- "Where do I type?" — point at the prompt on the last line of the window.
- "Which tab is running it?" — point at the tab strip along the top of the window.
- "What went wrong?" — point at the last error line above the prompt; read it conversationally, do not spell paths.
- "How do I stop this?" — point at the window and say press control-C.
- "Where are the settings?" — point at the app menu at the top-left of the menu bar; Settings is inside it.

## Gotchas
- control-C interrupts, control-D sends end-of-file (closes the shell if the line is empty), control-Z suspends; these differ from the command shortcuts.
- The default shell is zsh; a percent sign prompt means zsh, a dollar sign usually means bash.
- Terminal asks for Full Disk Access or folder permissions the first time a command touches Desktop, Documents or Downloads; the prompt is a system dialog, not part of the app.
- A blank window with no prompt usually means a command is still running or waiting for input.
