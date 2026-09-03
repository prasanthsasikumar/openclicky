# OpenClicky for macOS

The native OpenClicky app: a menu-bar companion that lives next to your cursor, sees your screen,
listens when you hold ctrl+option, talks back, points at things, and hands real work to the
OpenClicky agent.

This app started as a fork of the original open-source Clicky companion app (MIT; the copyright
holder is named in `LICENSE`). Everything provider-facing now goes through the OpenClicky backend,
and an **Agent mode** routes "do work" requests to a Codex thread. `OPENCLICKY.md` has the full
rename map and the list of integration changes.

## Build and run

```bash
open OpenClicky.xcodeproj    # set your signing team under Signing & Capabilities, then Run
```

Requirements: macOS 14.2+, Xcode 15+, the OpenClicky backend running, and `~/.openclicky/shell.json`
with your backend URL, token, and CLI command (see the repository README, "Run the macOS app").

Permissions the app asks for on first run: Microphone, Screen Recording, Accessibility (for the
global push-to-talk shortcut), and Speech Recognition (only for the Apple transcription provider).

## Headless checks

```bash
xcodebuild -project OpenClicky.xcodeproj -scheme OpenClicky build CODE_SIGNING_ALLOWED=NO
OpenClicky.app/Contents/MacOS/OpenClicky --openclicky-smoke-run "create a file called x.txt containing 'y'"
```
