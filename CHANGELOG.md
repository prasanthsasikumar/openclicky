# Changelog

What each OpenClicky release changed, in the words the update dialog shows. One `## x.y.z` section
per version, newest first. The release script (`macos/OpenClicky/scripts/release.sh`) reads the
section for the version in `macos/OpenClicky/VERSION` and puts it in the Sparkle update dialog and
the GitHub release, so a publish refuses to run until this file has a section for that version.

Write for the person who sees the update prompt: what they can now do, in plain words, one bullet
per change with the gist in bold. Links, `code` and *italics* are fine; keep it to bullets and
short paragraphs.

Split a section by platform with `### All platforms`, `### Mac`, `### Windows` and `### Linux`
(leave out the ones with nothing new). The Mac update dialog shows All platforms and Mac; the GitHub
release page shows each platform under its own heading once that platform's files are attached
(`scripts/update-release-page.sh`, run by the Windows and Linux release workflows), followed by a
downloads table. Under a platform heading, don't start bullets with "Windows:" again.

## Unreleased

### Mac

- **No crash after unplugging a headset.** Dictating after a headset or dock came off could take the whole app down: the microphone engine still described the headset while the Mac's own microphone runs at a different rate. The next take now starts a fresh engine whenever the input changed.
- **A closed lid no longer means a silent take.** A MacBook's own microphone is off in hardware while the lid is closed, yet macOS still falls back to it when a headset drops. With the microphone on Automatic, OpenClicky now records from another connected microphone instead (a USB receiver, an iPhone), and when there is none it says the lid is the reason rather than "check the microphone".
- **A clear note when your free trial ends.** If you tried OpenClicky without an email, it used to ask for "the code from the email" when your free days ran out, in a robotic voice, and then go quiet. It now says your trial has ended, opens Keys & Account so you can add your email, and shows the note beside the pointer every time you ask.
- **Account notes in your voice.** Notes about your free allowance now play in the voice you picked (Kokoro, or a key's voice) instead of the Mac's built-in voice.

## 0.11.0

With thanks to [darkworks](https://github.com/darkworks), who wrote the drawing stage both apps draw on and brought the Windows app its 3D figures, own-model answers, voice control and more, and to [Tan Wei Jie (Justin)](https://github.com/weijietan97), who maintains the Windows app.

### Mac

- **Draw to explain.** Ask "how does a jet engine work?", "what's inside this?" or "why is the sky blue?" and Clicky draws on your screen while it talks: a diagram or a turning 3D model built part by part, each part drawn as its sentence is spoken. When it is done the drawing shrinks into a bubble beside the island; click it to bring it back, and ask again while it is open to build on it. Only questions a picture really explains are drawn; everything else is answered in words as before. On by default: Settings → Clicky → Draw to explain. The drawing stage itself (`draw-stage/`) is shared with the Windows app and was written by darkworks for Windows PR #39.
- **Drawings on fast voice too.** With fast voice on, a question a picture explains is drawn and narrated by the answer voice while the fast voice steps aside, then it carries on with what was drawn in mind. Start talking over a drawing to stop it; it keeps what was drawn and goes to its bubble.
- **Mirror.** A quick look at yourself before a call: the camera button in the island's toolbar opens a live mirror where now playing sits, and closes it again. Nothing is recorded or sent anywhere. macOS asks for the camera the first time.
- **The Teacher is built in.** It is always on the island with one switch, on its row and under Settings → Island → Teacher, which turns lessons on and off; nothing to install. Off until you switch it on.
- **Now playing, Shelf, Timer and Mirror are on from the start.** Coding agents, Clipboard, Notes and System stay off until you switch them on under Settings → Island (Coding agents asks for your Keychain to show Claude's plan limits). Anyone who already chose their own set keeps it.
- **A sharper island.** The open island no longer shows a faint box behind it (its shadow was being cut off at the window's edge), the agents' running bar stays inside its row, and the busy strip's words cross-fade instead of jumping.

### Windows

- **Draw to explain.** Ask "how does a jet engine work?" and Clicky draws on your screen while it talks: a diagram or a turning 3D model built part by part. Ask again to build on it, drag or resize it, and save it as a picture or a video with its voice. To ask about one part of the screen, press Ctrl+Alt+M and circle it. On by default; the switch is on the island's Teacher row and in Settings. By darkworks (PR #39).
- **More by voice and mouse.** "Press tab three times", "copy that" and "minimize this window" work by voice, and you can talk by holding a mouse side button. By darkworks (PR #39).
- **Make it yours.** Give the assistant its own name, and pick the pointer's colour and shape under Settings → Clicky. By darkworks (PR #39).
- **Clicky's settings in sections.** Settings → Clicky opens in the sidebar into Talking, Hands-free, Pointer, Drawings, Your models and More, one section at a time. By darkworks (PR #42).
- **Answers from your own models.** Ollama, LM Studio or any OpenAI-compatible server (vLLM, llama.cpp, LocalAI…) can answer: give its address under Settings → Clicky → Your models, press Turn on to list what it serves, then pick it under Answers come from. No key needed; in Automatic they come after your own keys and before the plans. By darkworks (PR #41).

## 0.10.2

### Mac

- **A brighter icon, in the colour you pick.** The icon is now a white mark on a bright red tile. Settings → General → App icon switches the Dock to Ultraviolet, Sunset, Paper or Aqua; Finder and Launchpad keep the red one.
- **Settings in six pages.** Dictation, Clicky, Island, Keys & Account, Privacy and General, each with a header card and a search box above them. What you rarely change folds into "More" at the bottom of each page.
- **The island's home.** The open island shows now playing, your calendar, the weather and CPU and memory side by side, in liquid glass, blend or black. Timer, Notes and Clipboard join the Shelf as buttons in the island's band. Hovering the island grows it sideways along the menu bar, with downwards kept as a choice.
- **OpenClicky has its own address: [openclicky.org](https://openclicky.org).** The old address still works.

### Windows

- **Settings in six pages.** Dictation, Clicky, Island, Keys & Account, Privacy and General. What you rarely change folds into "More" at the bottom of each page. The orb, Your words, How it writes and Calendars open from a row with a back button, and the light/dark choice sits at the window's top right. The Region choice is gone: Sarvam and the Indian languages show when you already use them.
- **Pick the app icon's colour.** Settings → General → App icon: Signal, Ultraviolet, Sunset, Paper or Aqua, on the window, the taskbar and the tray.
- **The island's home.** The open island shows now playing, your calendar, the weather and CPU and memory side by side, in liquid glass, blend or black. Timer, Notes and Clipboard join the Shelf as round buttons in the island's band. Hovering the island grows it sideways along the top edge, with downwards kept as a choice.

## 0.10.1

### All platforms

- **Fast voice and hands-free with a Gemini key.** A Gemini key now runs the fast voice lane and hands-free, not only an OpenAI key. With both, Settings → Voice picks which; Automatic stays on OpenAI.

### Mac

- **Move around by voice.** "Click the first link" clicks it in the tab you're looking at ("open it in a new tab" for another); cookie banners' "Accept all" and videos work too ("stop the video" clicks it), while anything that sends, buys, deletes or confirms is only pointed at. "Switch to the YouTube tab", "next tab"; "scroll down", "scroll a little", "scroll to the top"; "scroll down slowly" glides a page so you can follow it, and "keep scrolling" moves at reading pace until you say stop or press esc. "Open my Downloads folder"; "look for my tax return" finds the file and selects it in Finder; "find price on this page" searches the page with its find bar.
- **Hands-free without a key.** With fast voice off, hands-free still listens on your Mac and Clicky answers once you stop talking. "That's all, thanks", "stop listening" or "ok bye" ends it.
- **The free voice speaks first.** Answers use your account's voice before Kokoro, and if it's slow or fails, the rest of that answer switches to the voice on your Mac instead of going quiet. Kokoro no longer downloads by itself.
- **Live facts are looked up.** "What's the weather outside?" is searched and read from the results instead of "I can't check".
- **"Show just the Terminal" brings back only the Terminal** after "minimize everything", and only windows Clicky minimized come back.
- **Fix: "play the first song in my favourites" no longer resumes a paused YouTube video.** Playback that names an app, song or list goes to Clicky, and play and pause go to the music player instead of the media key.
- **Dictated questions stay questions.** The clean-up no longer answers a question you dictated; it keeps your words.

### Windows

- **Island plugins.** Switch on System, Shelf, Coding agents, Now playing or Teacher under Settings → Plugins. The open island shows what's happening (your words while dictating, an agent's progress), and a small capsule flips between components. Drop files on the island to keep them on the Shelf; Now playing works with any player that reports to Windows.
- **Teacher: Clicky draws while it explains.** Ask "teach me how a rainbow forms" and the lesson is drawn in handwriting on your screen, stroke by stroke, while the pointer flies to each label; a real picture from Wikimedia Commons comes up when a drawing can't show it. If you typed the question or your sound is low, the answer is written on screen too. Esc, the ✕ or your next question clears it.
- **Do things by voice.** "Search YouTube for lo-fi", "minimize everything" and "bring them back", "play Blinding Lights" and "stop that song", "check my email", "draft an email to Ana saying I'll be late" (Gmail or Outlook on the web; never sent). Live facts like the weather are looked up and read from the page instead of "I can't check".
- **Hands-free without a key.** With fast voice off, hands-free still listens on your PC and Clicky answers once you stop talking. Say "okay, that's all I need for now" to end it, on fast voice too.
- **You stay in control.** Esc stops anything Clicky is doing: listening, thinking, speaking, or a guide waiting for your click. Clicky pauses a moment before sending your screen ("esc to cancel"; Settings → Privacy & Security turns it off), notices when you click somewhere other than where it pointed, and says what to look for when it isn't sure instead of pointing at the wrong thing.
- **Move around by voice.** "Click the first link" clicks it and says what opened, in the tab you're looking at ("open it in a new tab" for another); cookie banners' "Accept all" and videos work too, while anything that sends, buys, deletes or confirms is only pointed at. "Switch to the YouTube tab", "next tab"; "scroll down", "scroll a little", "scroll to the top"; "scroll down slowly" glides a page so you can follow it, and "keep scrolling" moves at reading pace until you say stop or press esc. "Open my Downloads folder"; "look for my tax return" finds the file and selects it in File Explorer; "find price on this page" searches the page with its find bar.
- **"What is this?" means what's under the mouse.** A close-up around the pointer goes with each question, and the pointer finds a window's close, minimize and maximize buttons exactly.
- **"Rephrase this" rewrites in place on the fast voice lane.** Highlighted text, or the box you're typing in, is replaced with the new version; Ctrl+V puts the original back.
- **The free voice speaks first.** Answers use your account's voice before Kokoro, and if it's slow or fails, the rest of that answer switches to the voice on your PC instead of going quiet. Kokoro no longer downloads by itself.
- **Dictated questions stay questions.** The clean-up no longer answers a question you dictated; it keeps your words.
- **No more docking the pointer in the island**, as on the Mac.
- **Fix: dictation into a web page you haven't clicked into** now asks you to click a box instead of pasting into the page.
- **Fix: right Alt works on laptops with no right Ctrl** even if you skipped the tour.
- **Fix: voices and speech models are used only once fully unpacked**, so a fresh install no longer speaks or listens with a half-downloaded model.

## 0.10.0

### All platforms

- **Fix: the Kokoro voice downloads again.** Its download failed its check after the archive was re-packed upstream.

### Mac

- **Tell the Mac what to do.** With hands-free on (or holding ⌃⌥), a command is done and confirmed in a few words instead of taught: "minimize everything" (and "bring them back"), "search for rotary engines" or "search YouTube for lo-fi", "check my email", "draft an email to Ana saying I'll be late", "play Blinding Lights", "stop that song". A question ("how do I minimize this?") still teaches and points.
- **Email is never sent by voice.** "Check my email" reads the unread senders and subjects from Apple Mail, or opens your Gmail inbox and reads what's on screen; "draft an email" opens a new message with the recipient and a short body filled in, for you to read and send yourself. Settings → Voice → Doing things picks the inbox (Automatic is Apple Mail when it's set up here, else Gmail).
- **Play a song in Spotify by name.** The song is looked up on OpenClicky's Spotify app (only the words leave your Mac) and the desktop app plays it; with your own Spotify client credentials in Keys & Plans the lookup goes straight to Spotify. If the lookup can't answer, Spotify's own search opens for you to pick. "Stop that song" pauses the player itself, so it can never start something by mistake.
- **It looks before it speaks.** After an action that changes the screen (a page opened, a search, Gmail), Clicky takes a fresh look and describes what's there now.
- **Simple requests are done on your Mac.** "Open Safari", "set the volume to thirty", "pause the music", and "make this shorter" with text in front of you are understood by Apple Intelligence on Macs that have it (macOS 26): no screenshot is sent, no credits are used, and the answer comes in about a second. Everything else goes to Clicky as before. Settings → Clicky → Simple requests stay on this Mac.
- **Say goodbye to end hands-free.** "That's all I need, thanks", "stop listening" or "ok bye" now ends hands-free: Clicky says goodbye, closes the microphone and goes back to push-to-talk. A plain "thank you" keeps it listening.
- **"Rephrase this" rewrites in place in Apple Mail too.** With the cursor in an email body and nothing highlighted, "rephrase this so it sounds professional" rewrote nothing, because Mail's message body reports no text to Accessibility. Clicky now reads the box from the screenshot and, when you ask for a rewrite, selects it and pastes the new text. ⌘V puts the original back.
- **The island is solid black again when it opens.** The black melting into glass was harder to read, and its Glass level slider is gone from Settings → General.
- **No more docking the pointer in the island.** Parking it in the notch added nothing: the pointer already rests out of sight and comes back when you shake the mouse or when Clicky needs it. The option is gone from Settings, the menu bar and the island, which also gives the island's shortcuts more room. If you had it docked, it now follows "The pointer shows" in Settings → Clicky like everyone's.
- **Fix: dictation that heard nothing on some Macs.** Since 0.8.1, on some Macs every take ended in "didn't catch that" and the microphone test stayed flat, though the microphone worked everywhere else. OpenClicky now leaves the Mac's default microphone alone, as it did before.
- **Fix: the pointer finds a window's close, minimize and zoom buttons exactly.** Asked how to minimize the Terminal, it pointed at the menu bar; the three buttons are too small for a screenshot to place, so macOS is asked where they are instead.
- **Fix: "this tab" means the window in front.** With a terminal in front of Chrome, "minimize this tab" sent the pointer to a Chrome tab behind it. A browser behind the front app is now only pointed at when you name the site ("my Reddit tab").
- **Fix: the hands-free shortcut works.** Holding ⌃ and tapping fn twice did nothing on Macs with a Globe key, because macOS sends an extra Globe key event after each fn tap that cut the double tap short. The island now shows the shortcut in the right order too: hold ⌃, tap fn twice.
- **Fix: your music stays at full volume while Clicky is idle.** With the fast voice lane, other apps' sound could stay turned down until you quit OpenClicky. The microphone is now opened only when you press the shortcut.

### Windows

- **Dictation works the moment it's installed.** A small English speech model (Moonshine) now comes with the app, so the first take is heard on your PC with no download and no "turn on online speech" prompt. Parakeet still downloads after the tour and takes over; Windows speech recognition stays a choice in Settings → Dictation. If you already picked Windows speech, you stay on it. A take that starts with a long pause no longer ends in "didn't catch that" after the words showed live.
- **Works on laptops with no right Ctrl.** The tour's first step says "hold right ctrl or right alt", and right Alt (just right of the space bar) works from its very first press and stays the dictation key. Left Ctrl is left alone for copy, paste and selecting: holding it on that step points you at right Alt instead. With a key other than right Ctrl dictating, the shortcuts, cards and tour voice say "Ctrl + Win" for talking to Clicky, and either Ctrl works for it.
- **Dictate into a terminal.** Windows Terminal, PowerShell and Command Prompt count as a place to type, so holding the key there no longer says "no text box"; the words are pasted at the prompt.
- **The tour keeps one voice.** Whichever dictation key you have, the tour's lines are spoken in the same recorded voice as the rest, instead of switching to the PC's own voice for "hand me a task" and the other lines that name keys.
- **No robotic voice while Kokoro downloads.** A voice that can't speak yet (Kokoro still downloading, a key that's missing) hands over to your account's free voice rather than this PC's built-in one.
- **Hovering the island only peeks.** The pointer over the island grows it a little and shows "click to open"; a click opens the panel, as on the Mac.

## 0.9.3

- **Teacher: Clicky draws while it explains.** Ask "teach me how Pythagoras works", "how does a rotary engine work compared to a normal engine" or "explain refraction" and the lesson is drawn in handwriting in the emptiest part of your screen (ink on a white page, chalk on a dark one, a paper sheet when there's no room), stroke by stroke, with labels and arrows, while the pointer flies to each label. When a drawing can't show it well, a real picture from Wikimedia Commons comes up instead. Esc, the ✕ beside the drawing, or your next question clears it. Switch it on under Settings → Plugins → Teacher.
- **"Solve this" writes out the working.** With a problem on screen, "solve this" or "work this out step by step" puts the worked solution next to it, one step per line, the answer in red.
- **Answers you can read when you can't listen.** If you typed the question, your sound is muted or the volume is low, the answer is written on screen too.
- **Works without an OpenAI key.** Lessons are drawn by whatever you have: an OpenAI or Anthropic key, another API key, or a free OpenClicky account (about 3–5 seconds). The fast voice lane still needs your own OpenAI key.
- **"Rephrase this" rewrites in place on the fast voice lane too.** With your own OpenAI key, highlighting text (a Gmail draft, say) and asking to rephrase, shorten or polish it replaces the text instead of reading a new version out. Asking *how* to rephrase it is still answered in words. ⌘Z gets yours back.
- **Fix: dictation with no text box selected no longer loses your words.** In Chrome with nothing clicked, the page itself counted as a text box, so the words were pasted into nothing and your clipboard put back. Now Clicky asks you to click into a box, waits for it, and pastes there, or leaves the words on the clipboard for ⌘V.

## 0.9.2

- **Esc stops Clicky at any time.** While it listens, thinks, speaks or guides you, Esc (or a click on the island) stops it. A stopped answer never carries out an action, and the island can now stop the first step of a guide too. Esc during a dictation take still only cancels the take.
- **A moment before your screen is sent.** After you ask, the island shows "sending your screen · esc to cancel" for about a second; cancel and nothing is captured or sent. The next steps of a guide don't pause again. Switch it off under Settings → Privacy ("Pause before sending my screen").
- **"That wasn't the spot I pointed at."** In a step-by-step guide, a click well away from where the pointer pointed is noticed, and Clicky looks again and points you back.
- **"Not sure" instead of a confident wrong point.** When Clicky can't find what you asked about, the pointer stays put, it tells you what to look for, and the island reads "not sure · tell me what you see".
- **A new island.** Black melting into glass with notch-shaped corners; hovering peeks and a click opens the panel. Optional plugins, all off until you switch them on under Settings → Plugins: System (CPU, memory, disk and battery), Shelf (drop files on the island, drag them back out), Coding agents (what a Claude Code or Codex session is doing) and Now playing (Music and Spotify, with controls).

## 0.9.1

- **"Rephrase this" works in Gmail, Chrome and Electron apps.** Chrome, Edge, Brave, Arc and Electron apps keep their accessibility tree switched off until an assistive app wakes it, so OpenClicky found nothing to write over and only read a rewrite out. It now wakes the tree, and where a highlight still cannot be read it copies the highlight itself (your clipboard is put back) and replaces it in place. With nothing highlighted, it asks you to highlight the text instead of reading a rewrite out. Mac and Windows.
- **Parakeet on every Mac.** Offline dictation with Parakeet downloads on its own once the tour is done, on macOS 26 as well. "Clean up my words with AI" is on from a fresh install; switch it off for live typing.
- **Permissions, announced.** The first setup card says four permissions are coming and names them, with a tick under the cards as each one lands.
- **"What is this?" means the pointer.** The question now carries where the mouse is, with a close-up, so asking about a desktop item answers about that item rather than the window in front. Asking the same thing again within three minutes tells the model its first answer probably missed.
- **Windows: right Ctrl dictates, right Ctrl + Win asks.** Hold right Ctrl to dictate and let go to paste, as on the Mac ("Tap to keep listening" in Settings → Dictation brings the tap-toggle back). Win joining a held right Ctrl asks instead. The tour says the Windows keys in the app's own voice.
- **Windows fixes.** A dictation that ended with nothing pasted and a busy microphone after the first take; an error logged on every quit; online speech recognition can be switched on from the island in one click; spoken numbers in a folder name become digits.

## 0.9.0

- **OpenClicky for Windows.** A native Windows app for Windows 10 (2004 or later) and 11, x64 and ARM64, contributed by [Tan Wei Jie (Justin)](https://github.com/weijietan97). Hold **Ctrl + Win** to ask and the pointer shows you where; hold **right Ctrl** to dictate. It has the island, the tours, every settings page, skills, offline dictation (Parakeet) and voice (Kokoro). Download `OpenClicky.Windows-win-Setup.exe` (x64) or the `arm64` one from the release, or go to [openclicky.flowsxr.com/download/windows](https://openclicky.flowsxr.com/download/windows/). It is not code-signed yet, so SmartScreen may warn: choose *More info*, then *Run anyway*. After that it updates itself.
- **Skills arrive by pull request.** App skills are written as files and reviewed on GitHub. There's a "Request a skill" form for people who'd rather not write one. Drafting a skill inside the app is gone.
