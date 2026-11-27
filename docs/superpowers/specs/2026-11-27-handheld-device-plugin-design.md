# Handheld devices as a plugin (first device: the WhisPrompt handheld)

Date: 2026-11-27. Status: design, awaiting approval. Mac only.

## Intent

A few people own a handheld push-to-talk device (the WhisPrompt handheld is the first; others
will follow). They should be able to use it to drive OpenClicky: press its main button and
dictate, press another and ask Clicky, and so on, with every button's job theirs to choose.
Like the device's own app, OpenClicky should also tell them when a coding agent in a terminal
finished or needs them.

99 % of users will never own such a device. The feature must be a plugin they never see and
that costs them nothing: no code path that runs, no permission prompt, and nothing to see but one
switch folded away under Settings → Island › More.

## Goals

1. With the plugin on and the device plugged in, its buttons run OpenClicky actions without the
   device maker's app running.
2. A bindings page: for every control and every gesture it supports, pick what it does. Reset all.
3. Offer the device's own microphone for takes while it is connected.
4. Island notices when a Claude Code or Codex session in a terminal finishes or seems to wait
   for the person, and an action that brings that terminal to the front.
5. A device is a small description plus a decoder, so the next device is a new folder under the
   plugin and nothing else.

## Non-goals

- Anything written to the device: vibration, its screen, backlight, firmware, key remapping on
  the device itself. This version only listens.
- Rebinding the wheel and the touch pad. They reach macOS as an ordinary mouse (scroll, pointer)
  and stay that way.
- The device maker's software features (preview panel, scene skills, accounts).
- Windows and Linux.
- Installing hooks into Claude Code, Codex or Cursor configuration.

## Anti-goals

- Any change in behaviour, start-up cost or permission prompt for someone who has not switched
  the plugin on, or any setting of it in their sight beyond the one folded switch.
- Copying the maker's artwork (the 3D picture of the device), strings or prompts. The device is
  named because it is supported; nothing else of theirs is in the repo.
- Fighting the maker's app for the device. If both run, the person is told; nothing is seized.

## What the device sends (measured 2026-11-27, maker's app quit)

USB vendor `0x303a`, product `0x8001`, product string "WhisPrompt". One HID interface with four
top-level collections (keyboard, mouse, vendor page `0xFF00`), plus a serial port and a USB
microphone named "WhisPrompt Mic Stream" (16 kHz). A listener opened it with `IOHIDManager`
from a terminal with no prompt and without sending anything.

Key events arrive by themselves as input report 4, 12 bytes after the report id:

```
04 02 KK GG SS 00 00 00 T0 T1 T2 T3 00
      |  |  |           '-- device clock, ms, little endian
      |  |  '-- counter, +1 per event
      |  '-- gesture: 05 down, 01 single, 02 double, 03 long-press start, 04 long-press end
      '-- key: 02, 01, 00
```

- Key `02` (taken to be "Key A", the voice key) sends `05` at once on press, then `01` or `02`
  about half a second later, or `03` after about 0.4 s of holding and `04` on release.
- Keys `01` and `00` (taken to be B and C) send only `01` and `02`: no down, no long press.
- The key letters are inferred from the order they were pressed in one recording. The builder
  confirms them with the device in hand before naming them in the UI.
- The keyboard collection stays empty: the device types nothing. Enter, delete and so on come
  from the maker's app.
- Wheel: mouse wheel values of ±5. Touch pad: relative pointer movement.
- Report 3 repeats about three times a second with status. In the kind whose third byte is `08`,
  the twelfth byte read `0x64` (100) throughout; probably battery percent. Unconfirmed, so the
  battery is shown only if the builder can confirm it (unplug, watch it fall); otherwise left out.

The probe used is not in the repo; the builder writes the decoder from this table and keeps the
recorded bytes above as test fixtures.

## Approaches

1. **The plugin listens to the device directly (chosen).** A folder under `Plugins/` matches the
   device by USB id, decodes report 4 and runs OpenClicky actions. Works without the maker's app;
   touches the main program at one entry point, the registry and the settings table.
2. **Keep the maker's app running and bind the F17–F20 keys it types.** Almost no code, but both
   apps act on every press and the owner must keep the other app open. Dropped.
3. **A separate helper app that owns the device and drives OpenClicky from outside.** The
   strongest separation, but OpenClicky has no outside control surface, so that would be built
   first. More work for the same result. Dropped.

Prasanth picked 1 on 2026-11-27.

## Shape

Everything lives in `macos/OpenClicky/OpenClicky/Plugins/Handheld/`.

### The device contract

`HandheldDevice` describes one supported device as data: an id, a display name, the USB vendor and
product ids, the name of its microphone if it has one, its controls (id, name, the gestures each
supports), its default bindings, and a decoder from an input report to an optional
`HandheldEvent(controlID, gesture, sequence)`. `HandheldDeviceCatalog.all` lists them. The
WhisPrompt handheld is the one entry, in its own file. Decoders are pure and unit-tested against
recorded bytes.

Gestures: `down`, `single`, `double`, `longStart`, `longEnd`. A control lists only the ones it
really sends.

### Listening

`HandheldListener` owns one `IOHIDManager`, matched only on the catalog's vendor and product ids,
opened without seizing, scheduled only while the plugin is on. It publishes connected or not and
the decoded events. With the plugin off, no manager exists. Duplicate sequence numbers are
dropped. The HID side sits behind a small protocol so the rest is tested with a fake.

To verify on the installed, Developer ID build (the app is not sandboxed): that opening this
device raises no Input Monitoring prompt. If it does, the plugin's switch says so before it asks.

### Actions and bindings

`HandheldAction`, what a gesture can be bound to:

| Action | What it does |
|---|---|
| Dictate (hold) | a take while held, as the dictation key held |
| Dictate (press to start, press to stop) | as the dictation key tapped |
| Ask Clicky (hold) | as ⌃⌥ held |
| Ask Clicky (press to start, press to stop) | the same press and release, one per press; released by itself after 60 s. Kept inside the plugin: it sends the two transitions the keyboard sends, so the ask lanes learn nothing new. Exists because only Key A can be held. (Designer's call.) |
| Hands-free on / off | as the hands-free gesture |
| Type a question | opens the composer |
| Stop | as esc |
| Go to the waiting terminal | see Terminal notices |
| Open the island | |
| A key or shortcut | one recorded combination, posted to the front app (Enter, delete, ⌘Z…) |
| Nothing | |

The hold actions are offered only for a control that reports `longStart` and `longEnd`. A hold
starts at `longStart` and ends at `longEnd` (the device decides what a long press is, about 0.4 s;
starting at `down` would open the mic on every single press). Builder's call if testing shows the
0.4 s costs the first word: start a take at `down` and cancel it silently when `single` or
`double` follows.

Bindings are stored per device id in UserDefaults (`openclicky.handheld.bindings.<device id>`),
as control → gesture → action. "Reset all" returns the device's defaults:

| Control | Single | Double | Long press |
|---|---|---|---|
| Key A | Dictate (press to start/stop) | Type a question | Dictate (hold) |
| Key B | Enter | Go to the waiting terminal | |
| Key C | Delete | ⌘Z | |

(These follow the maker's defaults where OpenClicky has the same thing; they are the designer's
call, not something Prasanth chose.)

### The one entry point into the main program

`CompanionManager` gains `performExternalShortcut(_ event: CompanionShortcutEvent)`. The plugin
turns actions into the events the keyboard already produces (`dictationPressed` /
`dictationReleased`, `talkPressed` / `talkReleased`, `textComposerRequested`,
`handsFreeToggleRequested`, `escapePressed`), so a device press takes exactly the keyboard's
path: same refusals, same island, same history. Everything but talk goes to the existing private
`handleCompanionShortcut`. Talk does not pass through it (it drops them, `CompanionManager.swift`
near line 1495): the keyboard's talk goes through `handleShortcutTransition(.pressed / .released)`,
and the new method routes talk there. Recorded keys are posted stamped with
`FrontAppTextInserter.liveTypingEventMarker`, which `GlobalPushToTalkShortcutMonitor` already
ignores.

What changes outside `Plugins/Handheld/`, all of it inert while the plugin is off: that method;
one line in `CompanionManager.start` that starts the plugin if its switch is on; a
`DictationSettingsPage` case for the Handheld page with its rows in `SettingsCatalog` and
`SettingsLayout`; the microphone stand-in (below); and the terminal notices, which live in the
Coding agents plugin.

### The microphone

When the device connects and has a microphone, the settings page offers "Use its microphone while
it is connected" (off by default). On, takes and questions record from it while it is present and
return to the person's own choice when it is unplugged; the stored `preferredMicrophoneUID` is
never overwritten. (First built for dictation takes and ⌃⌥ questions only; since 2026-11-27 it
applies to every lane, see "What changed while building".) The choice reaches three consumers wired together
in `CompanionManager.start` (about lines 1448–1455), and `MicrophoneDevices.keptEngineIsStale`
already guards a device that went away. Builder's call how: the smallest hook that allows it. The crash of
2026-11-07 (tap format after a device change) and the closed-lid rule are in this area; a
connect or disconnect during a take must be tried by hand.

### Settings

The plugin is not an `IslandComponent` (it has no island view, and a registered component would
show up in "Component order" for everyone). It has its own switch, `openclicky.handheld.enabled`,
off by default, as one row in Settings → Island's folded "More" group: "Handheld devices — use a
supported push-to-talk handheld with OpenClicky". Search finds it. Nothing looks for USB devices
until it is on. (Designer's call.)

Switched on, a row "Handheld" appears under it and opens its page (as "Calendars" does):

```
WhisPrompt handheld                         connected            Reset all
Key A    press  [ Dictate (start/stop) ▾]   twice [ Type a question    ▾]
         hold   [ Dictate (hold)       ▾]
Key B    press  [ Enter                ▾]   twice [ Go to the terminal ▾]
Key C    press  [ Delete               ▾]   twice [ ⌘Z                 ▾]
Wheel and touch pad work as a mouse.
[ ] Use its microphone while it is connected
The WhisPrompt app is also running, so each press will do two things.   (only when true)
```

Not connected: "No supported handheld is connected", the list of supported devices, and the
last device's bindings still editable. A plain list, no picture of the device. Every row is
placed in `SettingsLayout` like any other.

### Terminal notices

Part of the Coding agents plugin, since it is useful with or without a device: a switch under
that plugin, "Tell me when a session finishes or needs me", off by default. On, the existing
`AgentSessionWatcher` keeps its background polling going and posts through `IslandNoticeCenter`:

- "Claude Code finished · openclicky" when a live session goes idle.
- "Claude Code may be waiting for you · openclicky" when a session's last record has been an
  unanswered tool request for 10 s or more (the logs do not record a permission prompt, so this is
  a guess; the wording says "may"). Designer's call; if it proves noisy by hand, the builder ships
  "finished" only and says so.

The watcher runs only while the Coding agents plugin is on (`NotchHUD.swift`, about line 869), so
the switch needs that plugin on and says so. In the background it polls every 20 s, so a notice
can be 10–30 s late; while the switch is on the background interval drops to 5 s.

One notice per session per change, never for OpenClicky's own agent runs (today only Codex scratch
runs are filtered, `AgentSessions.swift` about line 316; Claude plan answers use the same scratch
root, `PlanCLIs.swift` about line 297, and get the same filter), and the dictation
sounds switch decides whether it makes a sound. The notice's button and the "Go to the waiting
terminal" action bring forward the newest such session's terminal: best effort, by finding a
terminal or editor window whose title names the project, else just activating the terminal app
last in front. The parser gets fixtures for both new states.

## Testing

- Unit: the decoder against the recorded bytes; bindings storage and defaults; action → event
  mapping with a fake manager; listener with a fake HID source (connect, disconnect, duplicate
  sequence); the watcher's two new transitions with fixture logs; notice wording.
- By hand, with the device: each default binding; a held take pasted into a scratch document
  (focus checked first); the plugin off → no HID manager (log line on start and stop);
  unplugging mid-take; the maker's app running at the same time; the installed build's
  permission behaviour.
- Tests never read `~/Documents`; xcodebuild uses the default DerivedData.

## Who has the device

Prasanth has the only one, on this Mac. The by-hand checks (key letters, battery byte, the
installed build's permission behaviour, whether holding from `longStart` loses the first word)
are done with him in the session, early, because they decide names and scope. The maker's app is
installed here as `ai.whisprompt.desktop`; that bundle id is how "also running" is detected.

## Left to the builder

The exact Swift shapes, where the page's rows sit in `SettingsLayout`, how the microphone
stand-in hooks in, the picker's look, log lines, and the CLAUDE.md key-files entry and CHANGELOG
line.

## What changed while building (2026-11-27)

- **"May be waiting" waits 45 s, not 10 s.** A real Claude Code log and a Codex rollout were
  read: neither records a permission prompt, so a prompt and a long-running command look the same.
  At 10 s every build or test run would have posted a notice. No new parser state was needed: a
  stall is the existing tool activity plus the log's last write time.
- **The device microphone covers every lane** (changed 2026-11-27 at the owner's request; it
  first stood in for dictation and classic ⌃⌥ questions only). A microphone that hears some
  lanes and not others was confusing, so it now stands in for the person's choice everywhere,
  fast voice and hands-free included. On fast voice it records without echo cancellation, as
  any microphone other than the Mac's default does.
- **Binding rows carry the device id** (`handheld.binding.<device>.<control>.<gesture>`): a
  page's rows are fixed until it is drawn again, so they are built for every supported device and
  shown for the one the page is about.
- **A finished session is announced once per turn**, and "Go to the waiting terminal" falls back
  to the session that finished last, then the newest session.
- **Four keys, all of which can be held.** Later recordings showed a fourth key (code 03, the
  wheel's button) and a long press on every key, not only Key A. A key combination bound to a
  hold repeats until release (every 0.08 s, at most 10 s): Key C held is a repeating Delete.
- **The wheel has two modes, one at a time.** The window switcher (`Switcher/`: a stack of open
  windows the wheel moves through) and tab cycling (`HandheldTabCycling`: an event tap that
  turns the wheel's clicks into ⌃Tab and ⌃⇧Tab, 30 s idle limit). The one just asked for wins.
- **The page is in the Settings sidebar** while the plugin is on, and shows a 3D picture of the
  device to pick buttons on (`HandheldDeviceView`, `Viewer/`). The maker's model is not ours to
  ship: it is read only from `~/.openclicky/handheld/<device id>.glb` on the person's own Mac,
  and without it the page lists the buttons.
- **A chosen microphone is tapped in its own format** (`MicrophoneDevices.tapFormat`): the
  16 kHz handheld beside the 48 kHz Mac microphone recorded silence in every lane. The
  microphone pickers show the handheld's while it stands in, and a pick there switches it off.
- **Still owed, all on `docs/handheld-hand-test.md`:** which key is A, B and C; whether a hold
  loses the first word; the battery byte; the Developer ID build's Input Monitoring behaviour;
  and every on-screen check (the page, the recorder, the notices).

