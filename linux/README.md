# OpenClicky for Linux (early)

- **Dictate:** hold **left ctrl** (where fn is on a Mac), speak, let go: your words are typed where your cursor is,
  cleaned up the way the Mac app cleans them (fillers out, your dictionary, spoken shortcuts,
  sentence case). A small orb at the bottom of the screen shows each step.
- **Ask:** tap **left ctrl twice**, type a question about what is on your screen, press Enter. The
  orange pointer beside your mouse answers in a caption and flies to what it is talking about.
  Esc dismisses the answer. Follow-ups remember the last ten exchanges. A how-to with several steps
  is guided **one step at a time**: the next step comes after you click or press Return.
- **The pointer** follows the mouse always, only while the mouse moves, only after you **shake the
  mouse**, or only while answering (Settings → Show the pointer).
- **Settings and the tray icon:** Ask…, Settings…, Quit.

Answers are spoken on this computer (Kokoro, the Mac app's offline voice). Not on Linux yet: the agent lane.

The user guide, with pictures, is [docs/linux.md](../docs/linux.md). This page is for building,
testing and reviewing it.

## Needs

- **An X11 session.** Wayland does not let an app watch a key, paste into another app or float a
  window over it; pick the "Xorg" session at login. (On Wayland it says so and exits.)
- Python 3.10+, GTK 3 bindings and a recorder, from your distribution (no cairo bridge needed):
  - Debian/Ubuntu: `sudo apt install python3-gi gir1.2-gtk-3.0 pulseaudio-utils` (or `alsa-utils`)
  - Fedora: `sudo dnf install python3-gobject gtk3 pulseaudio-utils`
- Keys or an account, set in **Settings** (tray icon, or `openclicky-linux settings`). They are
  saved in `~/.openclicky/shell.json`, the file the Mac app and the CLI read, and every other key in
  it is left alone:
  - dictation: an **OpenAI key**, or an **OpenClicky account** token
  - answers: an **Anthropic key**, else an **OpenRouter key** (Claude Sonnet 4.6 unless
    `answerModels.openrouter` in shell.json names another), else the OpenAI key, else the account

  An own key goes straight to its vendor and never to the backend. The language pins dictation and
  answers; the dictation key can be left ctrl (the default), right ctrl, right alt, right super or the menu key (a modifier
  is best, since other keys auto-repeat); the pointer can stay beside the mouse or show only while
  answering, or appear only while the mouse moves or after a shake.

## Run

```bash
cd linux
python3 -m venv --system-site-packages .venv     # system site packages: GTK comes from the distro
.venv/bin/pip install -e '.[test]'
.venv/bin/openclicky-linux --check               # what it found: key, recorder, transcription route
.venv/bin/openclicky-linux
```

| Do | Get |
|---|---|
| Hold left ctrl on its own, speak, let go | The words are pasted into the window in front |
| Hold left ctrl + left alt, speak, let go | A spoken question: the pointer answers and guides |
| Esc | Cancel |
| Left ctrl + another key (a shortcut) | Not a take; nothing is typed, and the microphone never opens |
| Tap left ctrl twice | The ask box (Enter sends, esc closes) |
| Esc while an answer shows | Dismiss it; the pointer comes back to the mouse |

The words go only into the window that was in front when you pressed the key: if another window is
in front by the time they are ready, they are put on the clipboard instead ("copied, press ctrl+v").
Terminals get ctrl+shift+v. Your clipboard text is put back 0.6 s after the paste.

Your dictionary and spoken shortcuts are read from `~/.openclicky/dictation/` (the Mac app's files;
edit them there, or by hand).

## Tests

```bash
.venv/bin/python -m pytest
```

The X11 tests start their own Xvfb display (install `xvfb`), so no keystroke reaches your desktop;
they are skipped without it. They run the real app wiring end to end with XTEST pressing keys: a
take lands in a GTK text box; left ctrl twice opens the ask box, a typed question goes out with a
real screenshot to a stand-in model, and the pointer lands on the button it named; a guide moves on
only when the test clicks where the pointer points, and the pointer stays above the dialog that
click opened; Settings saves
into shell.json; the pointer is checked to be cut to its triangle and click-through.
A shake with the real mouse wakes a hidden pointer. `OPENCLICKY_SCREENSHOT_DIR=<folder>` keeps
screenshots of each scenario.

`OPENCLICKY_LIVE=1` adds one test that spends your own key (three requests): real questions about a
test window on the private display (only its screenshot is sent), checking that the pointer lands on
the Save and Delete buttons and stays put for "what is the capital of france?".

## How it is put together

| File | Purpose |
|---|---|
| `companion.py` | Asking by text, one question at a time: the ask box, the screenshot, the answer, the pointer's flight and caption; a new question or esc drops an unanswered one. Guided steps: waits for the click or Return, then asks for the next step. Reaches the desktop only through `AskHost` and `PointerSurface`. |
| `ask.py` | The question + screenshot to a model, in the Mac's order: own Anthropic key → Messages; OpenRouter key → its chat completions; own OpenAI key → chat completions; account → backend `POST /chat`. The Mac's ask prompt, for a read (not spoken) answer. |
| `guided.py` | Step by step guidance: the [NEXT] marker, which click or key finishes a step and how long to let the screen settle, the "what's next?" request, the prompt section. |
| `pointing.py` | The `[POINT:x,y:label:screenN]` tag (the Mac's parser, ported); monitors ordered mouse first, and a point on screen N mapped back to the desktop; the arc and timing of a flight. |
| `voice.py`, `voice_helper.py` | Spoken answers: the Mac's Kokoro v1.0 (same model, SHA-256 and speakers) through sherpa-onnx, in a helper process that loads as a question is asked; the first sentence spoken at once, the rest generated while it plays; esc or a new question stops it. |
| `presence.py` | When the following pointer shows: the Mac's `PointerPresence` (always, while moving, after a shake, with its shake detector) ported with its tests, plus "only while answering". |
| `pointer_view.py` | The pointer (an orange window cut to a triangle with SHAPE, click-through) and its typed-out caption; follows the mouse, flies, comes back. |
| `windows.py` | The ask box, the Settings window, the tray icon. |
| `screen.py` | The screenshots: one per monitor, the one with the mouse first, each with its longest side ≤ 1568 px, PNG, with its scale and its place on the desktop. |
| `controller.py` | One take at a time (idle → listening → finishing), the Mac's `DictationTakeController` in small. Reaches the desktop only through `DictationHost`. |
| `gestures.py` | Hold, tap, double tap, esc and "a shortcut, not a take" from raw key downs and ups, with the Mac's timings; left ctrl, shared with the ask box, listens only once held alone for 0.3 s; left alt with it asks the pointer. |
| `x11.py` | The window in front (`_NET_ACTIVE_WINDOW`, `WM_CLASS`), the paste keystroke (XTEST), the listen-only key and click watcher (RECORD), window shapes (SHAPE). |
| `desktop.py` | GTK 3: the clipboard paste and restore, the orb (a popup window that never takes the focus). |
| `recorder.py` | 16 kHz mono PCM from `parecord` (the microphone picked in Settings) or `arecord`, wrapped as WAV; the microphones the sound server knows (`pactl`, monitors left out); loudness for the Test meter. |
| `transcriber.py` | The dictation engine: Automatic = offline when downloaded, else own OpenAI key → OpenAI, else account → `POST /agent/transcribe`, else a sentence naming what to add; or one chosen in Settings. |
| `offline.py`, `offline_helper.py` | Offline dictation: the Mac's Parakeet TDT 0.6B v3 int8 through sherpa-onnx 1.13.8 (same model, pinned SHA-256 and settings), downloaded on request, run in a helper process that starts with a take and ends after 180 s idle, as on the Mac. |
| `formatter.py` | The Mac's `TakeFormatter.formatLocally`, ported: shortcuts, fillers, dictionary, sentence case. |
| `config.py`, `space.py` | `~/.openclicky/shell.json` (read, and saved without touching other keys, mode 600, through a symlink, never over a file it cannot parse) and the dictionary/shortcut files. |
| `applog.py` | `~/.local/state/openclicky/app.log`, as the Mac's `AppLog`: what happened and how long it took, never the words, questions, answers or keys. |
| `autostart.py` | Settings → Start OpenClicky when I log in: the freedesktop autostart entry. |

Not yet: spoken answers and asking by voice, spoken numbers as digits, the model clean-up, history, the "is there a text box?"
check (AT-SPI), offline engines (Parakeet runs on Linux through sherpa-onnx), Wayland.

## For reviewers

**CI:** a `linux-client` job in `.github/workflows/test.yml` runs these tests on every pull request
(Ubuntu 24.04, GTK from apt, Xvfb), and `.github/workflows/linux-release.yml` attaches the Linux
package to a release when the maintainer runs it by hand (Actions → linux-release → Run workflow, with
the release's tag), built from that tag, next to the dmg and zip that `release.sh` uploads. A Mac release
never ships Linux on its own. It runs only on `prasanthsasikumar/openclicky`, not on forks.

**What changes outside `linux/`:** a new page, [`docs/linux.md`](../docs/linux.md), with its pictures
in `docs/media/linux/` (about 150 KB); one row in `CONTRIBUTING.md`'s table; a few lines in
`.gitignore` (the virtualenv and Python caches); the CI job and workflow above. Nothing in `macos/`, `backend/`, `agent/` or the
main README changes, and no backend route is added: the client uses `/agent/transcribe` and `/chat`
exactly as the Mac app sends them.

**Checking it in about a minute**, on any Linux with Python 3.10+, GTK 3 and Xvfb (no key, no
microphone, nothing on your screen):

```bash
cd linux
python3 -m venv --system-site-packages .venv && .venv/bin/pip install -e '.[test]'
.venv/bin/python -m pytest             # 170 tests; the X11 ones start their own Xvfb (with .[offline] and the model downloaded, real speech too)
OPENCLICKY_SCREENSHOT_DIR=/tmp/shots .venv/bin/python -m pytest tests/test_x11.py   # and look at /tmp/shots
scripts/make-doc-screenshots.sh        # remakes docs/media/linux/ (a scripted answer; --live uses your key)
```

`OPENCLICKY_LIVE=1` adds the two tests that spend a real key (about six requests, all about test
windows on the private display): pointing at the right button, and a guide from a Settings button to
a ticked checkbox, with the test clicking wherever the pointer points.

**Where each piece comes from.** Most of the logic is a port of a Mac file, kept close to it on
purpose (the same names, numbers and sentences) so the two can be compared side by side and fixed
together:

| Linux | Mac counterpart | Kept the same |
|---|---|---|
| `controller.py` | `Dictation/DictationTakeController.swift` | take ids, hold/tap/double tap/esc, "a shortcut, not a take", paste only into the window from the press, the orb's sentences (ctrl where the Mac says ⌘) |
| `gestures.py` | `CompanionShortcutRecognizer.swift` | the 350 ms tap and 450 ms double-tap windows; left ctrl twice is the Mac's ⌃ ×2, left ctrl held is fn, left ctrl + left alt is ⌃⌥ (talk) |
| `formatter.py` | `Dictation/TakeFormatter.swift` (`formatLocally`) | fillers, dictionary (longest alias first), whole-take shortcuts, sentence case |
| `transcriber.py`, `ask.py` | `ProviderRoute.swift`, `ClaudeAPI.swift`, `AnswerProvider.swift`, `CompanionManager.screensForAsk` | own key straight to its vendor, never to the backend; the answer order Anthropic → OpenRouter → OpenAI → account; the models; one picture per monitor, mouse's first, at most two on an account |
| `ask.py` (prompt) | `CompanionManager.companionVoiceResponseSystemPrompt` | its rules and the pointing section, reworded for an answer that is read rather than heard |
| `guided.py`, guide part of `companion.py` | `GuidedSteps.swift`, `CompanionManager.waitForTheGuidedStepToBeDone` (+ `GuidedStepsTests`, ported) | the [NEXT] marker, the prompt section, settle times (1.5 s after a click, 1.2 s after Return/Tab, 4 s while typing), 120 s idle, 15 steps, the "what's next?" that repeats the goal |
| `pointing.py` | `CompanionManager.parsePointingCoordinates` | the tag grammar, leading/trailing tags, no recursion on two tags |
| `offline.py`, `offline_helper.py` | `Parakeet/ParakeetDictation.swift`, `ParakeetEngine.swift`, `Sherpa/ModelDownload.swift` | the model, its URL and SHA-256, 80 mel features at 16 kHz, a NeMo transducer, at most four threads; a helper process that loads as a take starts and ends after 180 s idle |
| `voice.py`, `voice_helper.py` | `Kokoro/KokoroVoice.swift`, `KokoroEngine.swift`, `SpokenChunker.swift` | the model, its URL and SHA-256, the speakers and language hints, the lexicons; the first sentence alone, then pieces of at least 120 characters; a helper process that ends after 180 s idle |
| `presence.py` | `PointerPresence.swift` (+ its tests, ported case for case) | the shake detector's numbers, the 3 s rest |
| `config.py`, `space.py` | `OpenClickyConfiguration.swift`, `Dictation/DictationSpace.swift` | the same `shell.json` keys (`openaiApiKey`, `anthropicApiKey`, `openRouterApiKey`, `token`, `backendUrl`, `language`, `answerModels`) and dictionary files; Linux adds only `linuxDictationKey` and `linuxPointer` |

New for Linux, with no Mac counterpart: `x11.py` (RECORD key watcher, XTEST paste keystroke, SHAPE
for the pointer), `desktop.py`, `pointer_view.py`, `windows.py`, `screen.py`, `recorder.py`.

**Decisions worth a look:**

- *X11 only.* Wayland deliberately blocks watching global keys, typing into other apps and floating
  click-through windows; supporting it means desktop portals or compositor-specific protocols per
  desktop. On Wayland the app says so and exits instead of half-working.
- *Python + GTK 3, nothing compiled.* PyGObject comes from the distribution; the only pip
  dependency is `python-xlib`. No `python3-gi-cairo` either: the pointer is shaped with X's SHAPE
  extension, because without that bridge GTK drew it as a black square.
- *`shell.json` is shared with the Mac app.* Settings writes only its own fields and keeps every
  other key, writes atomically with mode 600, and refuses to overwrite a file it cannot parse.
- *The key watcher listens, it never grabs* (RECORD, like the Mac's listen-only event tap): right
  ctrl keeps working in every other app.

**Reviewed twice.** An independent pass over the whole client found, and this branch fixes
with a test each: an answer that was only a point tag leaving an empty turn that made Anthropic
refuse every later question; right ctrl + click (or left ctrl + click twice) counting as a tap, so a
take started and kept the microphone open; Settings copying environment keys into `shell.json`;
dictation pasting when the window at press or release could not be told; a nested GTK loop during
the paste letting an esc in mid-take; the pointer and caption showing in the screenshot they should
not cover; HiDPI screenshots mapping points 2x off; and the key watcher, a failed paste or a bad
dictation key failing silently or with a traceback. A second pass over those fixes found that the
now-asynchronous paste had to check the window and the keystroke itself (else a failure said
"moved" and the restore then dropped the words), that the esc check had to read the ask box's state
on the key watcher's thread, and that the log must never stop the app from starting; also fixed.

**Not yet verified:** dictation with a real microphone (the machine it was built on had none passing
audio); more than one real monitor (its logic is tested on fake layouts in `tests/test_monitors.py`,
because Xvfb here can only simulate one); and other desktops than Xfce (KDE, GNOME on Xorg, MATE).
