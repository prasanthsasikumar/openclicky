# Dictation hand test

What to try after installing a build, in order, and what each step should show. Written on
2026-10-06 when the dictation port landed; the unit tests and the Sarvam smoke run were green, but
the orb, the window and a real fn-key take had not been exercised by hand on this Mac (the screen
was locked). Tick each line the first time it is seen working and note the build.

## 1. First launch

- [x] The onboarding window opens: five chapters (welcome, permissions, key, engine, first take), each
      skippable; the progress capsules at the top right fill in. *(welcome chapter seen on build 130,
      2026-10-06; the other chapters not yet)*
- [ ] Permissions chapter: microphone, accessibility and speech recognition show "granted" or a
      grant button; the rows turn green on their own within a second of granting.
- [ ] Key chapter: fn is selected; if macOS still has fn on Emoji & Symbols, the amber card offers
      "free the fn key" and, after clicking, disappears.
- [ ] Engine chapter: "on this Mac" is selected; choosing Sarvam shows the key field; "needs your
      sarvam key" clears once a key is saved.
- [ ] First take chapter: hold fn, say a sentence, let go → the words appear in the big box.
- [ ] "come back to openclicky" closes the walkthrough and opens the main window on record.

## 2. The orb

- [x] A small dark pill with two dashes rests at the bottom centre of the main screen. *(seen on
      build 130; it rested on the external display until build 131 moved it to the primary one)*
- [ ] Hold fn: start tone, the pill widens, five bars follow your voice, live words show (Sarvam /
      Apple engines stream; the OpenClicky engine shows "listening…").
- [ ] Let go: stop tone, "moving your words", then "moved to text box" with the done tone, then idle.
- [ ] Tap fn (under 0.35 s): the take keeps listening, hint "say it, then tap fn" (tooltips on). Tap
      again: it finishes. Two quick taps: "cancelled". Esc while listening: "cancelled".
- [ ] Drag the pill somewhere else; it stays there after a relaunch. Settings → the orb → "back to
      the bottom" returns it.
- [ ] With no text box focused (click the desktop), a take opens the transcript box above the pill
      with "no text box found, copy from here" and a copy button; copy works.
- [ ] Click the pill: a take starts; click again: it finishes.
- [ ] Settings → the orb: look (pill / classic / pixel), theme (black / coral / mist), size, hide when
      not in use (the orb fades 1.6 s after a take), show the orb off → it disappears, fn still works.

## 3. Takes into apps

- [ ] Notes: the take is pasted at the cursor, the clipboard is restored afterwards, history shows
      the Notes icon, "pasted" in the inspector.
- [ ] Terminal: pasted; the developer style keeps commands as spoken.
- [ ] Slack / Messages: the messaging styles (lowercase for personal messaging when the model
      polishes).
- [ ] A password field (Safari login) or a `sudo` prompt in Terminal: "not pasting into a password field".
- [ ] Hold fn, let go, switch to another app during "moving your words": "the app changed — copy from
      here"; nothing is pasted into the second app.
- [ ] Hold fn, let go, press esc during "moving your words", press fn again at once: the second take
      works normally and the first one's words are nowhere (log: "finished after it was cancelled").
- [ ] Hold fn, press ⌃ while holding, say "make it formal", let go, with text selected in Notes: the
      selection is replaced (needs a Sarvam key or an account); without either, the orb says so.
- [ ] Tap fn + ⌃ twice quickly: hands-free toggles (with Realtime on) and no take starts, no tones.
- [ ] Offline engine, signed in: a take is cleaned up locally only (record banner says so) until
      Settings → engine → "also polish offline takes" is on.

## 4. The window

- [ ] record: greeting for the time of day, engine banner, the box, recent takes, words today, seven
      bars, "all takes →".
- [ ] history: day groups, search as you type, enter asks a question (needs a model), open → inspector
      with edit / copy / delete; failed takes show "couldn't finish" and, for network engines, "retry".
- [ ] dictionary: teach a term ("you say" aliases), then dictate it: it is written as taught.
- [ ] shortcuts: teach "my sign-off"; dictate exactly "my sign-off": the replacement is pasted.
- [ ] styles: language auto / pinned, script native / roman, five style cards; open one, edit its
      rules, add an app from the running list; "your apps" lists apps with takes and lets you move
      them between styles.
- [ ] settings: every page renders; reset buttons restore defaults; microphone "speak to test" shows
      bars; permissions rows match System Settings; engine "test the key" answers "ok …".
- [ ] Appearance light / dark / system switches the window and the orb's box.

## 5. Installation

- [ ] Open the dmg and launch OpenClicky from it: the "move to applications?" dialog appears;
      "Move to Applications" copies, relaunches from /Applications, and the dmg copy quits.
- [ ] About → "check now" (a published release with an appcast is needed; a dev build says "no
      update feed").
- [ ] General → "open at restart" appears in System Settings → General → Login Items.
- [ ] Menu bar icon (HUD settings → menu bar): left click opens the companion panel, right click the
      app menu; "open openclicky" opens the window; `open -a OpenClicky` does too.

## 6. Engines without the microphone

```bash
APP=/Applications/OpenClicky.app/Contents/MacOS/OpenClicky
$APP --openclicky-smoke-transcribe take.wav offline
OPENCLICKY_SARVAM_KEY=… $APP --openclicky-smoke-transcribe take.wav sarvam      # verified 2026-10-06, en + hi
```

The log (`~/Library/Logs/OpenClicky/app.log`) has one line per take: started (app, field,
editable), stopped (seconds held), done (chars, paste outcome, engine, degraded) or failed (why).
