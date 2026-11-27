# Handheld plugin hand test

What to try with the WhisPrompt handheld plugged in, in order. Written 2026-11-27 when the plugin
was built: the unit tests are green and the real listener connected to the device outside the
app, but no button has been pressed through OpenClicky, the Settings page has not been seen on a
screen, and no terminal notice has been seen. Tick each line the first time it is seen working and
note the build.

Use a build from `scripts/release.sh --no-notarize` (in the worktree, first
`ln -s <main checkout>/macos/OpenClicky/scripts/release.env scripts/release.env`, and check the
script's first line says "Developer ID Application"). A Debug build started from a shell cannot
post keystrokes, so Enter, Delete, ⌘Z and a pasted take can only be checked on the installed
build. Quit the WhisPrompt app first, except in section 6.

Read `~/Library/Logs/OpenClicky/app.log` alongside: every step logs a `handheld:` or
`external shortcut:` line.

## 1. Off means off

- [ ] Before switching anything on: Settings → Island → More shows one row, "Handheld devices",
      switched off, and no "Handheld" row. `app.log` has no `handheld:` line since launch.
- [ ] Search for "handheld" in Settings finds the switch.

## 2. Switching it on

- [ ] Switch it on. Does macOS ask for Input Monitoring (or anything else)? Write down what
      appeared. The log says `handheld: listening` and `handheld: WhisPrompt connected`.
- [ ] A "Handheld" row appears under the switch without closing Settings, and opens the page.
- [ ] The page names the device as connected and shows a picker for every press, double press and hold
      of Key A, Key B, Key C and the wheel button (or, with a model file, the picture: section 11). It also shows the mouse note and the microphone switch.
- [ ] The same page in the island's settings drop-down looks right on black.

## 3. Which key is which

- [x] *(2026-11-27, with the plugin's listener run outside the app: asked for Key B alone, the key
      WhisPrompt's picture shows top left and binds to Enter, and it arrived as `keyB`; the voice key is
      `keyA`, the only one that reports a hold. Not yet repeated through the app.)*
      Press each physical key once and watch the log (`external shortcut:` for the voice key,
      nothing typed for the others unless a text box has focus). The key that dictates must be the
      one the page calls **Key A**. If the names are wrong, say which physical key is which: only
      the names in `Devices/WhisPromptHandheld.swift` change.

## 4. The default bindings

Open TextEdit with a scratch document and click into it before each step.

- [ ] Key A, one press: a take starts (orb and island as with fn tapped). Speak, press again: the
      words land in the document.
- [ ] Key A, held: a take for as long as it is held; release pastes. **Start speaking the moment
      the key goes down.** Is the first word there? (The device reports a hold about 0.4 s after
      the press.) If the first word is lost, the hold has to start at key-down; say so.
- [ ] Key A, twice: the island's composer opens. Esc closes it.
- [ ] Key B, one press: a new line in the document. Key C, one press: one character deleted.
      Key C, twice: the deletion is undone.
- [ ] Key B, twice with no agent session anywhere: the pointer says "no terminal session to go to".

## 5. Changing bindings

- [ ] Set Key B press to "Ask Clicky (press to start, press to stop)": one press listens, the
      next sends the question. Left alone after the first press, it sends itself after a minute.
- [ ] Set Key A hold to "Ask Clicky (hold)": holding asks, releasing sends.
- [ ] Set Key C press to "A key or shortcut…", press ⌘⇧4 in the recorder: the picker shows ⇧⌘4
      and the key takes a screenshot selection. Esc in the recorder changes nothing.
- [ ] Bind a key to "Stop" and press it while Clicky is answering: the answer stops.
- [ ] "Reset all" puts the seven defaults back. Quit and reopen: changed bindings are kept.

## 6. Things that must not go wrong

- [ ] Hold Key A, and pull the receiver out while holding: the take ends (the island leaves
      "listening"), nothing stays recording. Log: `handheld: released what was held`.
- [ ] Hold Key A and switch the plugin off in Settings with the other hand: the same.
- [ ] Open the WhisPrompt app while the plugin is on: the Handheld page says each press will do
      two things. Quit it again.
- [ ] Sleep the Mac with the device plugged in, wake it: the keys still work.
- [ ] Bind a key to ⌃⌥ or esc as a recorded shortcut: pressing it does not start a question or
      stop a take through OpenClicky's own shortcuts.

## 7. Its microphone

- [ ] Switch on "Use its microphone while it is connected". Dictate with Key A: the log's
      `microphone:` line names "WhisPrompt Mic Stream" at 16000 Hz, and the words are right.
- [ ] Unplug between takes: the next take (fn) uses your own microphone again.
- [ ] Unplug during a take: the take ends, no crash (no new `.ips` in
      `~/Library/Logs/DiagnosticReports`).
- [ ] With fast voice on, ⌃⌥ and hands-free record from the handheld's microphone too.

## 8. The battery byte

- [ ] If the handheld runs on a battery: is the level shown anywhere on it or in its own app?
      The status report's twelfth byte read 100 while plugged in. Say what the level was at a time
      it was clearly not full, and the byte can be checked against it (or leave battery out).

## 10. The wheel (added 2026-11-27, builds 780–788)

The device has a fourth key, the wheel's own button (`wheelButton`), and every key reports a hold.

- [x] *(2026-11-27, build ~780: "everything worked super great")* Wheel button, one press: a stack
      of open windows; the wheel moves through it; pressing again goes to the window in front.
- [x] *(2026-11-27, build 787)* Wheel button, twice: the pointer says "tab cycling on". Wheel down
      is the next tab, up the one before, in Terminal and in Chrome. Twice again: "tab cycling off".
- [ ] During tab cycling a trackpad still scrolls the page under the pointer.
- [ ] Left alone for 30 s, tab cycling ends by itself and the wheel scrolls again.
- [ ] One press during tab cycling ends it and opens the window stack; twice with the stack open
      closes the stack and starts tab cycling. The two are never on together.
- [ ] Key C held: Delete repeats until the key is let go (and stops by itself after 10 s).

## 11. The page and the picture (added 2026-11-27)

- [x] *(2026-11-27)* With the plugin on, "Handheld" is in the Settings sidebar after Island.
- [ ] Without a model file the page lists every button's pickers.
- [x] *(2026-11-27, "this is great")* With the maker's model copied to
      `~/.openclicky/handheld/whisprompt.glb` (never in the repo or the app), the page shows the
      device floating, labels beside each button, and a click on a button shows its pickers.
- [ ] Build 783: Key B and Key C labels on the left, no header card, the device line last.

## 12. One microphone (added 2026-11-27, build 786)

- [ ] With the handheld's microphone on, Settings → Dictation → Input shows it as chosen, and
      "Speak to Test" follows a voice spoken into the handheld.
- [ ] Picking another microphone there switches the handheld's off on the Handheld page.
- [ ] Section 7's first line again on build 784 or later: before it, every take was silent
      (the 16 kHz device was tapped in the Mac microphone's 48 kHz format).

## 9. Terminal notices

Settings → Island: Coding agents on, then "Tell me when a session finishes or needs me" on.

- [ ] Sessions already finished say nothing when the switch goes on.
- [ ] Run a short Claude Code task in a terminal: within a few seconds of it finishing the island
      says "Claude Code finished" with the project, and a tone plays if Sounds is on.
- [ ] "Go to it" brings that terminal window forward (with two terminal windows open).
- [ ] Make Claude Code ask permission for a command and leave it: about 45 s later the island
      says it may be waiting for you.
- [ ] Over a normal half hour of agent work, count the "may be waiting" notices that were only a
      long command. More than a couple: that notice should go, leaving "finished" only.
- [ ] Key B twice goes to the terminal of the session that is waiting or finished last.
