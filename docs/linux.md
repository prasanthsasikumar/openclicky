# OpenClicky on Linux

The Linux client of OpenClicky. It runs against the same backend contract and reads the same
`~/.openclicky` files as the Mac app, but is its own program: Python and GTK 3, under
[`linux/`](../linux). It works on **Wayland** (GNOME, KDE, Sway, Hyprland) and on **X11**.

What it does: **ask about your screen** by typing or out loud (the pointer answers, points and
speaks), **step by step guidance**, **dictation** typed where your cursor is (offline with Parakeet,
or with a key or an account), cleaned up **in the style of the app** you are in, a searchable
**history** of what you dictated with your **dictionary** and **spoken shortcuts**, **quick
actions** ("open firefox", "make a folder called trip") and the **agent** for bigger tasks, a
**free account** by email, a **first-run setup**, **Settings** and a **tray icon**.

## Install

One command, from a clone of the repository (or with a downloaded `.whl` as its argument):

```bash
sh linux/install.sh
```

It checks what your distribution must provide and prints the one line to install it if anything is
missing (Debian and Ubuntu: `sudo apt install python3-venv python3-gi gir1.2-gtk-3.0
pulseaudio-utils gstreamer1.0-pipewire gir1.2-gstreamer-1.0`; most desktops have all of it already),
then installs OpenClicky for you alone (`~/.local/openclicky`, the command in `~/.local/bin`), and
starts it. The setup opens:

1. **your keys** (Wayland only): Wayland does not let an app notice a key held in another app or
   type into it, so Linux asks for your password once, in the desktop's own dialog. That is the
   Mac's Accessibility permission; see [Wayland](#wayland) for exactly what it allows.
2. **your screen** (Wayland only): the desktop asks once whether OpenClicky may see your screens;
   pick every screen and choose **Share**. After that, a question's picture is taken in about a
   tenth of a second without asking again.
3. **microphone**: pick one, press **test** and say something.
4. **answers**: your email for a free account (no password; an email that already has one gets a
   6-digit code), or paste your own Anthropic, OpenAI or OpenRouter key.
5. **offline**: dictation and spoken answers on this computer (Parakeet and Kokoro, about 1 GB on
   disk, downloaded once). Skip it to use a key or the account instead.
6. **start when I log in**, and OpenClicky in your applications menu.

`openclicky-linux setup` opens it again. Starting OpenClicky while it runs opens its Settings.

## Ask about your screen

Tap **left ctrl twice**. A one-line box opens at the top of the screen:

![The ask box over a notes app, with "where do i save this?" typed in](media/linux/ask-box.png)

Press **Enter**. A picture of your screen goes with the question; the orange pointer says
"thinking…", then flies to what the answer is about and types the answer beside itself, and says
it out loud:

![The pointer on the notes app's Save button, with a caption saying the save button will do it](media/linux/ask-answer.png)

With more than one monitor, each goes as its own picture and the pointer flies to the right one. A
question that is not about the screen is answered without pointing. **Esc** dismisses an answer.
Follow-ups remember the last ten exchanges. Clicks go straight through the pointer and its caption.

**Out loud:** hold **left ctrl + left alt** (the Mac's control + option), ask, let go.

**Quick actions:** "open firefox", "open github.com", "make a folder called trip in my documents",
"show me the trip folder", "volume to 30", "pause the music" are done at once (the Mac's fast lane):
the app, the site, the folder, the volume (PipeWire or PulseAudio), the player (MPRIS). Only those,
with typed arguments: nothing a model says can turn into a command.

**The agent:** with the `openclicky` command-line tool and [Codex](https://github.com/openai/codex)
installed (`cd agent && npm ci && npm run build && npm link`, then `npm i -g @openai/codex`; see
[Run the agent](setup.md#run-the-agent)), "make a folder of today's screenshots and zip it" goes to
a Codex thread; the pointer shows what it is doing and says what it did. The next task continues
the same thread. Esc stops it. Settings → Agent switches it off.

## Step by step

Ask how to do something that takes several steps ("how do i turn on dark mode?") and you get one
step at a time: the pointer shows the first one and waits. Do it (a click, or Return or Tab after
typing) and a moment later OpenClicky looks again and shows the next step:

![A Photos settings dialog opened by the first step, with the pointer on its Dark mode checkbox and a caption saying to tick it](media/linux/guided-step.png)

Esc, a new question, two minutes without a click, or fifteen steps end the guide.

## Dictate

Hold **left ctrl** on its own, speak, let go: it is where fn is on a Mac keyboard. (Shortcuts such
as ctrl+C still work: dictation only starts once ctrl has been held alone for a third of a second.)
The orb at the bottom of the screen shows what is happening:

![A notes app with the orb at the bottom saying "listening · release to transcribe"](media/linux/dictation-listening.png)

Your words are written the way the Mac app writes them: fillers out ("um"), spoken numbers as
digits ("twenty five" → "25"), your dictionary and spoken shortcuts, and, when a key or the account
is set and **Clean up my words with AI** is on, one model pass in the **style of the app** you are
in (developer apps and terminals terse, Slack and Discord like a chat, Telegram and Signal casual
and lowercase, Thunderbird composed; your own rules from `~/.openclicky/dictation/styles.json`).
A clean-up that is slow (over 3 s) or answers instead of tidying is dropped for the local result.
Takes heard by the offline engine are cleaned up only if you allow it (Settings): its promise is
that nothing you say leaves the computer.

![The same app with "And two lemons" typed on a new line and the orb saying "moved to text box"](media/linux/dictation-done.png)

| Do | Get |
|---|---|
| Hold left ctrl, speak, let go | The words are typed where your cursor is |
| Hold left ctrl + left alt, speak, let go | A question for the pointer, nothing typed |
| Esc while it listens | Cancel |
| Left ctrl + another key | A shortcut, not dictation: nothing is typed |

Another dictation key (Settings → Hold to dictate: right ctrl, right alt, right super or the menu
key) listens at once, and can also be tapped to start and tapped again to finish.

The words are pasted and your own clipboard is put back afterwards. On X11 they go only into the
window that was in front when you pressed the key (otherwise "copied, press ctrl+v"), and terminals
get ctrl+shift+v. On Wayland the window in front cannot be told, so they go where the keyboard is
when they are ready; the paste is shift+insert, which pastes in terminals too.

## Your words

Tray icon → **Your words…**: every take with what was written, searchable, editable, copyable,
deletable (kept only on this computer, in `~/.local/share/openclicky/dictation.sqlite`, the Mac's
format; **Incognito** in Settings keeps takes out of it), and your **dictionary** ("you say aditya
shatriya → it writes Aaditya Kshatriya") and **spoken shortcuts** ("my sign off" → your sign-off),
the Mac app's own files under `~/.openclicky/dictation`.

## Settings

From the tray icon (**Settings…**), by starting OpenClicky again, or `openclicky-linux settings`:

![The Settings window: dictation key, language, show the pointer, and the keys](media/linux/settings.png)

The shortcuts for the key you picked; the microphone (with a **Test** that shows a level and says
whether it heard you); the dictation engine (Automatic: offline once downloaded, else your OpenAI
key, else the account); the clean-up, incognito; the voice; the pointer; your **account** (sign
in by email, confirm with the code, sign out); your own keys; the agent; start at login. **Save**
writes into `~/.openclicky/shell.json`, the file the Mac app and the CLI read, and leaves everything
else in it alone; the file stays readable by you only.

| You add | Answers come from | Dictation goes to (when Engine is Automatic and offline is not downloaded) |
|---|---|---|
| an Anthropic key | Anthropic (Claude Sonnet 4.6) | — |
| an OpenRouter key | OpenRouter (Claude Sonnet 4.6, or `answerModels.openrouter`) | — |
| an OpenAI key | OpenAI (GPT-4.1) | OpenAI (`gpt-4o-mini-transcribe`) |
| a free account | the OpenClicky backend (`/chat`) | the OpenClicky backend (`/agent/transcribe`) |

Your own keys go straight to their company and never to the OpenClicky backend. A key set in an
environment variable (`OPENCLICKY_OPENROUTER_KEY` and the like) wins over Settings while it is set.

## Wayland

Wayland keeps apps apart on purpose: one app may not see the keys typed into another, type into
it, read the screen, or know where the mouse is. OpenClicky gets each of those the sanctioned way:

| Needs | How on Wayland |
|---|---|
| the keys you hold, the clicks a guide waits for | the keyboard and mouse devices themselves (`/dev/input`), read-only, never grabbed |
| typing the paste | a virtual keyboard of OpenClicky's own (`/dev/uinput`), pressing shift+insert |
| the screen | a one-second screen cast through the desktop's portal (asked once; the "sharing" indicator shows while it looks) |
| its windows over yours | X11 windows through XWayland (on by default everywhere), the only kind that may place itself |

The first two need the **one-time permission**: a udev rule (`/etc/udev/rules.d/70-openclicky-input.rules`)
that gives the person sitting at this computer (never a remote login) access to keyboards, mice and
`/dev/uinput`, the way Steam's rule does for game controllers. Honestly said: while you are at the
computer, any program you run can then read the keyboard, as every program always could on X11.
OpenClicky acts only on its own keys and never stores or sends a keystroke. `openclicky-linux setup
--remove` takes the rule away.

Wayland never tells an app where the mouse is, so the pointer cannot follow it there: it rests out
of sight at the top centre of the screen (where the Mac's flies out of the notch), comes out to
answer, and goes back once you move the mouse after reading the answer.

## Privacy

- A question sends a picture of **every monitor** to whoever answers it (at most two on a free
  account): keep anything private out of sight before you ask.
- Spoken answers are made on this computer: nothing is sent to be spoken.
- Dictation with the offline engine sends nothing anywhere. With a key or an account, it sends only
  the audio recorded while you hold the key, and (with the clean-up on) its words.
- The history and your words stay on this computer.
- Nothing is recorded or sent otherwise: the key watcher only acts on OpenClicky's keys (and on
  clicks only while a guide waits), and the pointer only reads where your mouse is (X11).
- Keys and the account's session live in `~/.openclicky/shell.json`, readable by your user only.
- Your own pointer and captions are hidden while a picture is taken.

## When something does not work

| You see | Try |
|---|---|
| "openclicky may not read the keyboard yet" | `openclicky-linux setup` (Wayland's one-time permission). If it says so again after the password, log out and in once. |
| "needs xwayland" | Your Wayland desktop runs without XWayland; turn it on (it is on by default in GNOME, KDE and Sway). |
| "screen sharing was not allowed" | Ask again and choose **Share**, with every screen ticked. |
| "asking needs an anthropic, openrouter or openai key…" | Settings → Account (your email), or add a key. |
| "no microphone recorder found" | Install `pulseaudio-utils` (or `alsa-utils`). |
| "dictation needs an engine" | Settings → Dictation → Offline engine → Download, or add an OpenAI key, or sign in. |
| "didn't catch that" every time | Settings → Microphone → **Test**. "Heard nothing at all": pick another microphone or unmute it. |
| "couldn't paste. copied, press ctrl+v" | The desktop did not take the words in time: they are on the clipboard. |
| Answers are shown but not heard | "your sound is muted" in the orb: unmute in the top bar's menu. Otherwise `openclicky-linux --check` says whether the voice is downloaded and a player found; Settings → Voice → **Speak answers** switches it on. |
| The tray icon is missing | GNOME needs an AppIndicator extension (Ubuntu has one built in); start OpenClicky again to open Settings, or use the applications menu. |

`openclicky-linux --check` prints what it found: the session, the permission, the recorder, where
dictation and answers go. What happened, and how long it took, is in
`~/.local/state/openclicky/app.log` (never your words, questions, answers or keys).

## For developers

How it is built, which Mac file each part follows, and how to run the tests (they start their own
private X display, so they never touch yours): [`linux/README.md`](../linux/README.md). The pictures
on this page are made by `linux/scripts/make-doc-screenshots.sh`.
