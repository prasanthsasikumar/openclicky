# OpenClicky for Linux

- **Dictate:** hold **left ctrl** (where fn is on a Mac), speak, let go: your words are typed where
  your cursor is, written the way the Mac app writes them (fillers out, spoken numbers as digits,
  your dictionary and spoken shortcuts, sentence case, and the model clean-up in the app's style).
  A small orb at the bottom of the screen shows each step.
- **Ask:** tap **left ctrl twice** and type, or hold **left ctrl + left alt** and say it. The orange
  pointer answers in a caption (and out loud) and flies to what it is talking about; a how-to is
  guided **one step at a time**. Quick actions ("open firefox", "make a folder…") happen at once;
  bigger tasks go to the **agent** (a Codex thread through the `openclicky` CLI) when it is installed.
- **Your words:** a searchable history, the dictionary and spoken shortcuts.
- **First-run setup, Settings, a free account by email, the tray icon.**
- **X11 and Wayland** (GNOME, KDE, Sway, Hyprland).

The user guide, with pictures, is [docs/linux.md](../docs/linux.md). This page is for building,
testing and reviewing it.

## Needs

- Python 3.10+, GTK 3 bindings and a recorder, from your distribution (no cairo bridge needed);
  on Wayland, GStreamer's PipeWire source for the screen (stock on Ubuntu and Fedora desktops):
  - Debian/Ubuntu: `sudo apt install python3-venv python3-gi gir1.2-gtk-3.0 pulseaudio-utils gstreamer1.0-pipewire gir1.2-gstreamer-1.0`
  - Fedora: `sudo dnf install python3-gobject gtk3 pulseaudio-utils pipewire-gstreamer`
- On **Wayland**: XWayland (on by default), and the one-time permission the setup asks for
  (`inputaccess.py`: a udev rule, installed with `pkexec`).
- Keys or an account, set in the setup or **Settings**. They are saved in `~/.openclicky/shell.json`,
  the file the Mac app and the CLI read, and every other key in it is left alone:
  - dictation: the **offline engine** (Parakeet, downloaded in Settings), an **OpenAI key**, or the account
  - answers: an **Anthropic key**, else an **OpenRouter key**, else the OpenAI key, else the account

## Run

```bash
sh install.sh                                    # for using it: ~/.local/openclicky, then the setup
# or, for working on it:
python3 -m venv --system-site-packages .venv     # system site packages: GTK comes from the distro
.venv/bin/pip install -U pip                     # Ubuntu 22.04's pip cannot install this in place
.venv/bin/pip install -e '.[test,offline]'
.venv/bin/openclicky-linux --check               # what it found: session, permission, recorder, routes
.venv/bin/openclicky-linux                       # setup on the first run; `setup` opens it again
```

Use the distribution's Python (`/usr/bin/python3`), not a conda or pyenv one: GTK comes with it.

## Tests

```bash
.venv/bin/python -m pytest
```

The X11 tests start their own Xvfb display (install `xvfb`; without root, `apt-get download xvfb
libxfont2 xserver-common` and `dpkg -x` work too), so no keystroke reaches your desktop; they are
skipped without it. They run the real app wiring end to end with XTEST pressing keys: a take lands
in a GTK text box; left ctrl twice opens the ask box, a typed question goes out with a real
screenshot to a stand-in model, and the pointer lands on the button it named; a guide moves on only
when the test clicks where the pointer points; Settings saves into shell.json; the first-run setup
walks every page and installs the launcher; the Words window searches the history and writes the
dictionary the Mac's way. **Wayland mode** runs on Xvfb too: kernel key events come through a pipe
standing in for `/dev/input`, the paste is shift+insert, and a stand-in compositor takes up the
clipboard as mutter does (and one that never does, to prove nothing stale is ever pasted).
`OPENCLICKY_SCREENSHOT_DIR=<folder>` keeps screenshots of each scenario.

`OPENCLICKY_LIVE=1` adds the tests that spend a real key (about six requests, all about test
windows on the private display). With the offline models downloaded, the real-engine tests run too.

## How it is put together

| File | Purpose |
|---|---|
| `__main__.py` | Start: one instance (`instance.py`), the session (`session.py`), Wayland's permission and virtual keyboard, the setup on the first run, then `wire_up` joins every part on the GTK main loop. `--check`, `settings`, `setup [--remove]`. |
| `session.py` | X11 or Wayland, and which part does what in each (the table in its docstring); the windows' name and class. |
| `x11.py` | X11: the window in front (`_NET_ACTIVE_WINDOW`, `WM_CLASS`), the paste keystroke (XTEST), the listen-only key and click watcher (RECORD), window shapes (SHAPE). |
| `evdev.py` | Wayland: keys, clicks and "the mouse moved" from `/dev/input`, pure Python (struct + ioctl), listen-only, hot-plug; the X11 watcher's callbacks. |
| `uinput.py` | Wayland: OpenClicky's virtual keyboard and its shift+insert. |
| `inputaccess.py` | Wayland's one-time permission: the udev rule (`uaccess` on keyboards, mice and `/dev/uinput`), through `pkexec`; `remove`. |
| `x_selection.py` | Wayland: the clipboard and primary selection owned through XWayland, so the paste waits until the compositor has taken the words up (sent earlier, an app pasted the clipboard from before). |
| `screencast.py` | Wayland: a question's picture through the ScreenCast portal, asked once (restore token), then about 0.1 s each, every monitor its own stream (GStreamer `pipewiresrc`). |
| `portal.py` | Wayland fallbacks: `grim` on wlroots, the Screenshot portal elsewhere (GNOME asks every time; kept from leaving files behind). |
| `screen.py` | The pictures: one per monitor, the one with the mouse (Wayland: the main monitor) first, each at most 1280 px and 1.15 MP, with its scale and place on the desktop. |
| `companion.py` | Asking, one question at a time: the ask box, the picture (on Wayland taken off the main thread), the answer, the pointer's flight and caption, the agent's progress; guided steps. Reaches the desktop only through `AskHost` and `PointerSurface`. |
| `lanes.py` | Which lane a question goes down (the Mac's two-tier routing): the agent through the CLI's gate, else the ask lane, offering the quick actions where the agent does not run. |
| `ask.py` | The question + pictures to a model (own Anthropic key → Messages; OpenRouter; OpenAI; account → backend `POST /chat`), with tools; the Mac's ask prompt. |
| `actions.py` | The quick actions (the Mac's `MacActions`): open an app, a site, make or show a folder, volume, media; typed arguments only. |
| `agent.py` | The `openclicky` CLI: the gate, a Codex run with `--events`, resumed threads, cancel; the Mac's environment for it. |
| `guided.py` | Step by step guidance: the [NEXT] marker, which click or key finishes a step, the settle times. |
| `pointing.py` | The `[POINT:x,y:label:screenN]` tag, monitors mouse first, the flight's arc. |
| `pointer_view.py` | The pointer and its caption: two popup windows, click-through, sized before they show; follows the mouse on X11, rests at the top centre on Wayland and steps aside when the mouse moves. |
| `presence.py` | When the following pointer shows (X11): always, while moving, after a shake, while answering. |
| `controller.py` | One take at a time (idle → listening → finishing): the formatting and clean-up off the main thread, the paste, the history. Reaches the desktop only through `DictationHost`. |
| `gestures.py` | Hold, tap, double tap, esc and "a shortcut, not a take" from raw key downs and ups, with the Mac's timings. |
| `recorder.py` | 16 kHz mono PCM from `parecord` (30 ms fragments: at its default a short take came back empty) or `arecord`; the microphones; loudness. |
| `transcriber.py`, `offline.py`, `offline_helper.py` | The dictation engine: offline Parakeet (a helper process), own OpenAI key, or the account. |
| `formatter.py`, `spoken_numbers.py`, `polish.py` | The Mac's `TakeFormatter` (local pass and the model pass's checks), `SpokenNumbers`, and the polishers (OpenAI, Claude Haiku, OpenRouter, the backend's `/v1/polish`) under a 3 s limit. |
| `space.py` | Styles by app (Mac bundle ids and Linux window classes), the dictionary and spoken shortcuts: `~/.openclicky/dictation`. |
| `history.py` | The takes in SQLite, the Mac's `TakeStore` schema. |
| `voice.py`, `voice_helper.py` | Spoken answers in Kokoro's voice, a sentence at a time; played with `paplay`, else `pw-play` or `aplay`, a player that fails passed over (Ubuntu 22.04 has `pw-play` but no PipeWire speakers); muted speakers said once. |
| `accounts.py`, `account_form.py` | The free account by email and 6-digit code, the session's refresh, sign out; the form the setup and Settings share. |
| `onboarding.py` | The first-run setup window. |
| `windows.py`, `words_window.py`, `desktop.py` | The ask box, Settings, the tray icon; the Words window; the paste and the orb. |
| `config.py`, `preferences.py`, `applog.py`, `autostart.py`, `instance.py` | shell.json; the app's own state; the log; start at login and the applications menu; one instance. |

## For reviewers

**CI:** a `linux-client` job in `.github/workflows/test.yml` runs these tests on every pull request
(Ubuntu 24.04, GTK from apt, Xvfb), and `.github/workflows/linux-release.yml` attaches the Linux
package to a release when the maintainer runs it by hand.

**Where each piece comes from.** Most of the logic is a port of a Mac file, kept close to it on
purpose (the same names, numbers and sentences) so the two can be compared side by side and fixed
together:

| Linux | Mac counterpart | Kept the same |
|---|---|---|
| `controller.py` | `Dictation/DictationTakeController.swift` | take ids, hold/tap/double tap/esc, "a shortcut, not a take", the orb's sentences |
| `gestures.py` | `CompanionShortcutRecognizer.swift` | the 350 ms tap and 450 ms double-tap windows; left ctrl ×2 is ⌃ ×2, left ctrl held is fn, + left alt is ⌃⌥ |
| `formatter.py`, `spoken_numbers.py` | `Dictation/TakeFormatter.swift`, `SpokenNumbers.swift` | fillers, numbers (with the Mac's test cases), dictionary, shortcuts, sentence case, the clean-up prompt and its same-take checks |
| `polish.py` | `Dictation/TakePolishers.swift` | the order of polishers, the models, the 3 s limit, never for the offline engine unless allowed |
| `space.py`, `history.py` | `DictationSpace.swift`, `TakeStore.swift` | the five styles and their rules, the JSON files, the SQLite tables |
| `transcriber.py`, `ask.py`, `lanes.py` | `ProviderRoute.swift`, `ClaudeAPI.swift`, `CompanionManager` | own key straight to its vendor; the answer order; one picture per monitor; the gate, then the agent or the ask lane with quick actions |
| `actions.py` | `MacActions.swift` | the six actions, the closed set of folders, the sentences |
| `agent.py` | `OpenClickyAgentClient.swift`, `OpenClickyConfiguration.cliProcessEnvironment` | the CLI's arguments and `--events`, the environment |
| `accounts.py` | `OpenClickyAuthSession.swift`, `DeviceIdentity.swift` | `/auth/start`, `/auth/code`, `/auth/confirm`, the refresh 10 min early, the sentences, the device hash |
| `onboarding.py` | `FirstRunTour.swift`, `PermissionPrompt.swift` | the steps, one permission at a time, the wording |
| `guided.py` | `GuidedSteps.swift` | the [NEXT] marker, the settle times, 120 s idle, 15 steps |
| `offline.py`, `voice.py` | `Parakeet/`, `Kokoro/`, `Sherpa/ModelDownload.swift` | the models, URLs and SHA-256s, the helper processes |
| `presence.py` | `PointerPresence.swift` | the shake detector's numbers, the 3 s rest |

**Decisions worth a look:**

- *Wayland through the devices, with a one-time permission.* Wayland has no protocol for a global
  hold-to-talk key on GNOME 42 (GlobalShortcuts came in GNOME 48, and gives no held modifier alone),
  nor for typing into another app. Reading `/dev/input` and adding a uinput keyboard is what every
  Linux dictation tool does; the `uaccess` udev rule (as Steam's) limits it to the person at the
  seat, and needs no log-out. AT-SPI's key events were tried and do not arrive on GNOME 42 unless
  accessibility is switched on for the whole desktop.
- *XWayland for the windows.* Only X11 windows may place themselves and stay above others;
  override-redirect popups with an empty input shape do let clicks through on GNOME, as long as
  they do not move after mapping (measured: one that moves blocks clicks until it is mapped again).
- *The screen through a screen cast, not the screenshot portal:* GNOME 42 asks "share this
  screenshot?" for every picture; the screen cast asks once and then takes about 0.1 s.
- *The paste waits for the compositor.* Measured on GNOME 42: a keystroke sent right after setting
  the XWayland clipboard pasted the previous clipboard into a Wayland app. The selections are owned
  directly, and the compositor's TARGETS request is the signal; no signal, no keystroke.
- *Python + GTK 3, nothing compiled.* PyGObject comes from the distribution; the pip dependencies
  are `python-xlib` and, for the offline engine, `sherpa-onnx`.
- *`shell.json` is shared with the Mac app.* Settings writes only its own fields and keeps every
  other key, writes atomically with mode 600, and refuses to overwrite a file it cannot parse.

**Verified live** (Ubuntu 22.04, GNOME 42 on Wayland, 2026-10-23, driven with a uinput test keyboard
and mouse, a PipeWire virtual microphone fed with Kokoro speech, and screen-cast captures): the
setup (permission, screen, microphone, key, both models); dictation into a native Wayland GTK app
with the offline engine, a 1.3 s take included; esc cancels; ctrl+a is not a take; asking by typing
and by voice, the pointer on the right button; esc dismisses; a quick action; clicks through the
pointer; stepping aside; a second start opens Settings; the clipboard and primary selection put back.
On 2026-10-24, by hand with a real voice and microphone: dictation in a text editor, a browser and a
terminal; spoken answers; the Words window (history, search, edit, a dictionary word applied); a
guided how-to followed with touchpad taps. That last one needed two fixes: a touchpad never reports
tap-to-click as a button (libinput makes it up), so `evdev.TapDetector` finds taps itself; and a
touchpad's finger codes (`BTN_TOOL_FINGER`…) had been taken for key presses, which kept a guide
waiting for "typing" to stop and called a take held with ctrl a shortcut.

Then a fresh install with `install.sh` after `setup --remove` (which now also withdraws the device
access at once: udev applies a removed rule only to devices plugged in later): the whole setup,
a free account signed up and confirmed with a real email, dictation and a long guided how-to.

**Not yet verified:** KDE and wlroots desktops, more than one real monitor, a HiDPI or fractionally
scaled Wayland desktop, and the agent lane with a real Codex run.
