# HeyClicky changelog (mirrored)

Recorded by `scripts/upstream-watch.mjs` from https://www.heyclicky.com/changelog and the Sparkle appcast. Newest first.

## v1.0.48 — Walkthroughs go the distance (Aug 25, 2026)

Download: https://github.com/farzaa/clicky-releases/releases/download/v1.0.48/HeyClicky.dmg (build 57)

This is a continuation of the last update, 1.0.47, making HeyClicky more reliable and more accurate. Almost every line below started as a report from one of you, and the numbers are from our own logs. Keep them coming <3

### new

- **Always approve for agents:** Long agent tasks kept pausing to ask for extra-usage approval. Hit "Always approve" once and they stop asking (you can turn it back off in Settings → Agent).
- **Shortcuts your way:** The dictation and hands-free shortcuts now take any held combo or a double-tap, so bindings like Control Control finally work. One double-tap starts hands-free dictation, the next one stops it :)
- The Log Out row in Settings now shows which email you're signed in with.
- **What's new, in the app:** This changelog is now linked from Settings → Support & Updates, and after an update the notch shows a little "See what's new" card ^-^

### walkthroughs

- **Up to 15 steps:** Step-by-step walkthroughs used to stop at 5 steps even when the task clearly wasn't done. The budget is now 15 ＼(^o^)／
- **No more lost context:** When a walkthrough handed you manual work and you said "continue", it could forget the goal, repeat steps, or start over. It now remembers the goal and every completed step across the pause.

### audio

- **AirPods stop clipping replies:** With Bluetooth earphones, macOS could switch audio profiles mid-reply and cut off the first seconds of the answer. The switch now waits for post-reply silence, and playback pre-rolls so a cold Bluetooth link doesn't swallow the start.
- **No more going silently deaf:** A wedged microphone used to leave voice dead until a restart, with no sign anything was wrong. HeyClicky now fails over to your Mac's built-in mic and tells you what happened in the notch.
- **Pro audio interfaces work:** Interfaces like the Behringer UMC404HD captured zero audio because of a sample-format mismatch. Fixed.
- **Muted speaker, answer kept:** A reply that would play into a muted speaker is copied to your clipboard instead, with a one-click volume fix in the notch.

### dictation

- **Sturdier insertion:** Paste now respects your keyboard layout (Dvorak and Colemak friends, it works now), the clipboard restore no longer races slow apps into pasting your old clipboard, clipboard managers stop archiving your dictated text, and no more stray trailing space after Chinese, Japanese, or Thai.
- Dictating into a terminal can never run your words as commands. Newlines are collapsed before insertion.

### agents

- **Works on managed Macs:** On company Macs with IT-managed Codex policies, every agent spawn failed with a cryptic error. HeyClicky now adapts to the allowed policy and runs.
- **A new computer-use engine:** Computer use moved to a faster native driver. Background clicks stay scoped to the target window and never move your real cursor.

### fixed

- A file dropped on the notch could silently vanish, and typing stayed dead afterward, especially on the macOS 27 beta. Drops now always attach, and typing recovers.
- HeyClicky was blind to its own windows. Ask about the Skills Library and it can now actually see it.
- The notch snapping shut instantly over an empty desktop instead of animating.
- The Agents search icon hiding under the hardware notch, and search leaving the notch pinned open.
- A cold-start race where the first voice turn could run before your speech was heard.
- HeyClicky now knows the actual current time on every turn, and repeats "let me check that for you" a lot less in long conversations (¬_¬)

## v1.0.47 — Whole-document understanding (Aug 13, 2026)

Download: https://github.com/farzaa/clicky-releases/releases/download/v1.0.47/HeyClicky.dmg (build 56)

Most of this release came from you telling us what was broken, and every number below is from our own logs. Thank you, and keep it coming <3

### new

- **Whole-document understanding:** Ask about a PDF, file, or web page and HeyClicky reads the entire thing as context, not just what's visible on screen. Our most-requested capability ＼(^o^)／

### improved

- **Works outside the US:** Requests from Asia and the Middle East were routed through a Hong Kong data center our AI providers block, so HeyClicky would listen, think, then silently give up. It now detects the block and re-routes through the US automatically. No VPN needed :)
- **Smarter answers:** Detailed and screen-related questions now route to a deeper model.
- **Lighter on your Mac:** Removed an always-running animation and moved sound playback off the main thread. Fixes high idle CPU (up to 45% on some Macs) and a freeze after double-tapping Control.
- **No more cut-off answers:** Deep answers had a hard 60-second timeout and got cut mid-thought; some need a few minutes. They now run to completion.

### dictation

- **Long recordings are safe:** Sessions over 10 minutes, including hands-free ones, were silently dropping. They now save reliably, and every finished transcript gets a local backup.
- **Interruptions don't wipe your work:** A WhatsApp call or a mic change used to discard the whole dictation. HeyClicky now keeps everything you'd already said { ^-^ }
- **Works in more apps:** Text now inserts correctly in WeChat, WeCom, Chromium browsers (Chrome, Brave, Arc), and Electron apps like Slack, VS Code, and ChatGPT, instead of falling back to the clipboard.
- **Faithful cleanup:** No more rewriting or adding words you didn't say (¬_¬)

### fixed

- Lag right after a background agent task finished.
- Account deletion failures are no longer silent, and the false "Nothing was removed" message is gone.
- Blank System Settings window, plus the Help menu now opens.
- Leftover drawings when closing text mode, and stale text in guided tours.

### security

- Sign-in credentials now live in the macOS Keychain.

## v1.0.46 — More reliable voice (Aug 1, 2026)

Download: https://github.com/farzaa/clicky-releases/releases/download/v1.0.46/HeyClicky.dmg (build 55)

The overall voice experience should be more reliable now. Keep the feedback coming :)

### improved

- **Calmer voice:** Behavior fixes from a week of using it ourselves. Web lookups now happen silently instead of being narrated, and the voice stays calmer ( ˘▽˘)
- **Faster first response:** The first voice interaction after opening the app is quicker. We warm up the session and network paths before you start speaking.

### removed

- **Proactive agents:** When we launched proactive agents, we told you: if any of this makes you uneasy, let us know. Some of you did. We listened. Proactive agents are off, and all the local activity tracking that powered them is gone. No app names, no tab titles, no accessibility data, and the old local database deletes itself on update. We still believe in the idea, and we'll bring it back when it feels magical without feeling watched.

## v1.0.45 — Faster realtime voice (Jul 31, 2026)

A big change under the hood. We rewrote the voice harness, wrote down every moment that felt slow, robotic, or wrong, and fixed them one by one. If anything feels off or you spot a regression, please send it our way. Thanks <3

### improved

- **Noticeably faster:** Quick questions get a fast model, deep or complex ones go to a frontier model. A little router decides on every question, so you don't pick anything. One user this week: "clicky is fast af lately. this feels so much better."
- **Talks more like a person:** Fewer greetings, fewer fillers ^ ω ^ Still lots to improve here, keep the feedback coming.

### new

- **Voice speed control:** Everyone listens at their own pace. Some of you asked for a faster HeyClicky voice, a few wanted it slower. Now it's yours to set in Settings → Voice, from 0.5x to 1.5x.

### privacy

- Deleting your account now erases your analytics data too, and we locked down a data-exposure hole found in a security audit.

## v1.0.44 — HeyClicky for teams (Jul 23, 2026)

A few of you have asked for a team version of HeyClicky. So we built a v0.

### new

- **One subscription for the whole team:** A mix of Pro and Max seats, and a small dashboard to manage members and billing.
- **Team-shared skills:** If someone on your team makes a skill, they can share it with just the team in one click. It stays private within your team.
- It's early, so we're setting teams up by hand right now, and we just onboarded our first pilot customer. If you want to try it with your company, email hi@heyclicky.com and we'll set you up ＼(^o^)／

## v1.0.43 — Agent retries and routing fixes (Jul 22, 2026)

A quick one, mostly fixes (¬_¬)

### new

- **Retry for failed agents:** Failed agent cards now have a retry button, so one flaky run doesn't mean starting over.

### improved

- **Works in more places:** Changed API routing so HeyClicky works in regions that were getting location-blocked, like South Korea ( ^_^)／

### fixed

- Reliability issues in always-on realtime mode.
- Some microphone crashes, including our top native crash.

## v1.0.42 — Reliability fixes across the board (Jul 20, 2026)

This should fix the reliability issues a bunch of you hit over the last two days. It was a combination of stuff piling up with the huge traffic we're getting. Most of this release came straight from your bug reports, so keep them coming! ^-^

### fixed

- **Fewer freezes:** If HeyClicky was hanging or the cursor got stuck while you were in DaVinci Resolve or other heavy apps, that should be gone now.
- **Push-to-talk:** If you pressed the hotkey (Control + Option) and your request just got dropped, or it stopped responding until you restarted the app, fixed.
- **Works on strict networks:** If you're on a corporate or filtered network and HeyClicky couldn't connect, or your paid plan wasn't showing up, that's fixed too.
- **Agents:** If you kicked off an agent task and nothing happened, fixed.
- Plus smaller stuff: the notch now stays put when you switch or reconnect displays, cleaner text-mode controls with a close button, and a tidier integrations popup.

## v1.0.41 — A new face, agents unstuck (Jul 20, 2026)

A quick one!

### new

- **A new face:** We gave HeyClicky a fresh new logo (・∀・)

### payments

- **UPI support:** For our Indian friends: you can now subscribe with UPI, priced in rupees 🇮🇳 Enjoy!

### fixed

- The agent getting stuck issue. If you were affected, please update and let us know if it works :)

## v1.0.40 — A hands-on tutorial in onboarding (Jul 17, 2026)

HeyClicky is a hard product to explain. There's no window and no buttons to click around, it lives in your voice and your keyboard shortcut, and a lot of people never discovered half of what it can do.

### new

- **Guided tutorial:** New users now do things instead of reading about them: say a first hello out loud, watch HeyClicky draw on the screen, circle something and ask about it, draft an email reply by voice, and try dictation in a real text box. Takes about two minutes. You've already been through onboarding so you won't see it, but if you ever show HeyClicky to a friend, their first two minutes should make a lot more sense now (⌒‿⌒)
- **Hand-drawn annotations:** When HeyClicky draws on your screen, the shapes now look hand-drawn, Excalidraw-style, instead of machine-perfect.

## v1.0.39 — Dictation polish, and a privacy step (Jul 15, 2026)

A day-two batch for dictation, plus a privacy change we want on the record.

### improved

- **No more em dashes:** Dictation cleanup no longer inserts em dashes into your text. We don't use them, so neither should it (¬_¬)
- **Updates come to front:** The update window now comes to the front when you hit Check for Updates, instead of hiding behind your windows.
- **Lighter on your battery:** The skill-panel orbit animation pauses while it's offscreen.

### privacy

- We removed proactive activity tracking and all accessibility-data collection, and the afternoon proactive slot went with it. Morning suggestions now wait until at least 6am your time. (The full proactive shutdown came in v1.0.46.)

## v1.0.38 — Dictation tuning (Jul 14, 2026)

A quick one.

### improved

- Dictation now runs on two models, one for speech-to-text and one for cleanup, with timing measured per utterance. Faster and more accurate.

## v1.0.37 — Dictation, and screen-aware dictation (Jul 14, 2026)

We've been using it internally for a few days and it's really good. As fast as Wispr Flow, if not faster, and it formats your text nicely. There's a full demo below if you don't wanna read :)

### new

- **Super fast speech-to-text:** Dictate into any app on your Mac. Gmail, Claude, Slack, it works everywhere. Press and hold Fn + Control, speak, and the text streams in at around 450ms. Double tap Fn + Control for hands-free mode, and change either anytime in Settings → Shortcuts.
- **It learns your words:** Grammar and spelling get cleaned up for you. And if a word comes out wrong, just edit it within about 20 seconds and it's added to your dictionary automatically (also editable in Settings → Dictionary).
- **Multi-language:** It auto-detects your language, with 50+ supported. If detection is a bit off, set a default in Settings → Dictation.
- Unlimited dictation is included on Pro and Max. On free, there's a generous limit, so try it out :)

### screen-aware

- **Ramble, and let HeyClicky write it:** Many times when you're replying to an email or writing a follow-up prompt to Claude, you don't even really know what you wanna say. Now you can just ramble. Open the email, press Control + Option, and say "HeyClicky, type a reply to this person in the text box here". It takes a screenshot, understands what you're replying to, and inserts a draft written in your voice, based on HeyClicky's memories of you. You can even activate a skill (our Y-Combinator skill, or Write Like Farza lol) and it writes like the skill.

### fixed

- Dictation falling back to the clipboard in Electron apps like Discord.
- Clicks at the start and end of the notch UI chimes.

## v1.0.34-36 — The road to dictation (Jul 13, 2026)

Three quick builds in one day getting dictation ready for launch, plus some goodies that snuck in ^-^

### new

- **Hold-to-dictate:** Promoted out of developer preview and on by default, with shortcuts, hands-free mode, and cancel. Streaming plus a faster engine made it about 3.4x faster.
- **Cursor customization:** A dedicated Settings page for customizing your cursor.

### improved

- **Skills 2.0:** Capability-aware skill creation and better routing, a My Skills filter in the library, and creators get emailed when their skill is approved.
- Text insertion now prefers typing directly into the field over the clipboard.
- A redesigned DMG install window.

### fixed

- Account deletion failing with a server error.
- The top macOS 26 crash.

## v1.0.33 — Spatial context, skills, proactive agents, memory (Jul 6, 2026)

Four launches in one go, all in production now. If you have any questions on how this stuff works, just ask!

### spatial context

- **Point, circle, scribble:** When you talk to HeyClicky, you'll notice a little trail of paint it leaves behind. Draw on your screen to point at or circle the specific thing you want the AI to focus on. Especially useful when you have a lot going on, like complex editing software or a complicated diagram. It's very intuitive, try it out!

### skills

- **A skills library:** Skills literally give AI superpowers, but installing them from random GitHub links is annoying. So we built a library: hover your notch, click add skill, and activate any of about 100 community-built skills in one click. When you talk to HeyClicky or run an agent, the skill is active. Want your own? Hit "Create a skill" and type what it should do.

### proactive agents

- **Agents that suggest themselves:** 99% of people don't even know the extent of an agent's power, so how would you know what to ask for? Every morning and afternoon, HeyClicky recommends two personalized agents at the top right of your screen. All you gotta do is click approve. We found ourselves getting magical recommendations for agents we had no idea we even needed.
- (A note from the future: we later turned proactive agents off over privacy unease. See v1.0.46 above.)

### memory

- **It remembers:** The more you use HeyClicky, the more it remembers. Your name, your language, how you like your CSVs named, everything. Under the hood it maintains two files: PROFILE.md for your habits and general preferences, and VOLATILE.md for the project you're working on right now. Both get injected into the voice model and the agent.

### also

- Library mode: typed replies stream as text and get read aloud.
- The realtime voice model upgraded to gpt-realtime-2.1.
- Fixed billing blips that paywalled and double-charged paid users. Sorry about that one.
- Fixed guided walkthroughs hanging on typed sessions, and a duplicate spoken acknowledgement when handing off to the deeper model.

## v1.0.32 — Delete your account, if you must (Jul 4, 2026)

Small but important.

### new

- **Permanent account deletion:** You can now permanently delete your account from Settings. Yours to keep, yours to leave.

### improved

- Walkthrough drawings are no longer capped at 3 shapes on screen.
- (Also hiding in this build behind flags: the skills library and proactive agents, warming up for their launch.)

## v1.0.31 — Claude Fable 5 by default (Jul 1, 2026)

Hi hi! A smarter default model, and a batch of fixes.

### improved

- **Claude Fable 5 is now the default:** We found it's really good at understanding your screen. Replies might be a tiny bit slower, but way more accurate. Let us know if you notice a difference :)
- **Always On is headphones-only for now:** Without headphones it was pretty broken, so we restricted it. Always On is still experimental, expect some bugs if you use it.
- The cursor color picker moved to its own Settings page.

### fixed

- **Agents for new users:** The Codex we bundle inside HeyClicky broke, so agents were failing, especially if you'd just signed up. Really sorry if you hit this. It's fixed now.
- **Highlights and drawings:** A highlighted area now disappears after 2 seconds. If HeyClicky drew something like a polygon to explain a concept, it stays while it's speaking, and once it stops, everything clears. Nothing stays on your screen.
- **Clearer agent approval prompt:** When an agent runs long, we ask you to approve using more messages from your existing plan. Some of you thought it meant we were charging your credit card. We're not! Just clarified the wording, let us know if it's still confusing. Ty!!

## v1.0.30 — A quick fix (Jun 29, 2026)

One that mattered.

### fixed

- A crash when purchasing Pro, plus a batch of our top reliability fixes.

## v1.0.29 — Yearly billing (Jun 28, 2026)

Save 20% by going yearly, and a stack of fixes.

### new

- **Yearly plans:** 20% off with a monthly/yearly toggle right on the paywall :)
- A little haptic when you hover the agent HUD.

### fixed

- Notch jank when submitting text, and a macOS 26 crash from panel auto-resizing.
- The cursor landing on the wrong monitor after rearranging displays.
- Text drafts that were claimed but never inserted (with a clipboard fallback just in case).
- Mic capture now recovers when your audio device disappears mid-conversation.

## v1.0.28 — HeyClicky learned 89 apps (Jun 23, 2026)

Guidance got a lot smarter.

### improved

- **App skills:** HeyClicky now carries teaching skills for 89 apps, with browser-site matching, so guidance inside your tools is much sharper.
- **Quieter learning:** The skill-learning UI calmed down: one cursor bubble per app instead of a notch card every time.
- Morning greetings now show any time between 7am and noon. Good morning ☀(^_^)☀

## v1.0.27 — Notch only (Jun 20, 2026)

A small one.

### new

- Feature Request and Community links in Settings. Come say hi! ( ^_^)／

### improved

- The menu-bar icon is gone. The notch is now HeyClicky's only home.

### fixed

- Cursor lag in DaVinci Resolve caused by blocking accessibility queries.

## v1.0.26 — Draw on your screen, realtime computer use (Jun 19, 2026)

We have been shipping a lot. If you follow us on Twitter or Instagram you may have already seen some of these, but here's all of it in one place.

### new

- **HeyClicky draws on your screen:** One of the biggest things people use HeyClicky for is learning. FL Studio, Cursor, Claude Code, anything. Now it can draw directly on your screen and walk you through step by step, made much smarter by skill files injected based on the program you're in. It even detects when you click where it told you to, and moves you to the next step, like a real teacher (⌒‿⌒)

### realtime

- **Control your computer with your voice:** We shipped the world's first realtime computer-use system. It's very early, but very cool. "Play Lucid Dreams by Juice WRLD on Spotify", "Open up my Stripe dashboard for me", "Check my Google Calendar, what do I got today?" Try always-on mode by pressing Control three times (requires headphones).

### improved

- **A way more powerful agent:** Way better at using the integrations you give it. The power users of our agent are people using it to run their business: "Look at my Supabase and tell me the top 5 users I should talk to", "Make a Mac app to help me keep track of my todos", "Research new customers in my niche and put them in Notion CRM for me". Honest note: it's not good at controlling your computer yet, but very good when you authenticate with your tools directly.
- **6x faster, and 70+ languages:** Replies are now near instant. If you ask something complex about your screen, it hands off to a more powerful model and thinks longer, so you get speed while keeping quality. And since we saw users from 100+ countries, HeyClicky now speaks 70+ languages: Spanish, Portuguese, French, Japanese, Russian, Chinese, German, Korean, Hindi, and many more. Try talking to it in your own language!

## v1.0.25 — Draw for HeyClicky (Jun 18, 2026)

The start of something we'd later name spatial context.

### new

- **Screen highlight:** Press Ctrl + Option and draw on your screen. Annotations are now always-on, so you can circle the thing you're asking about. (This grew up and publicly launched as spatial context in v1.0.33.)

### improved

- **An eager agent:** The agent now acts on write actions immediately and only asks for confirmation on deletes, emails, and money.

### fixed

- The double-Control input box not opening when Caps Lock is on.
- High CPU from agent HUD chips re-rendering every frame.

## v1.0.24 — No more frozen keyboards (Jun 14, 2026)

A reliability release.

### fixed

- **Keyboard freeze:** Fixed a system-wide keyboard freeze, and a missing screen permission now routes you to the notch instead of failing silently.
- The always-on echo loop. Proper echo cancellation means you can barge in over your speakers now.
- HeyClicky quitting when another app triggered "Hide Others".
- Keyboard focus not being released after text mode.

### new

- **Tidier agents:** The floating agent HUD folds into the screen corner as an accordion, and text responses get an inline follow-up composer.

### improved

- **An honest answer:** Ask "what model are you?" and HeyClicky now tells you the truth about the models in its voice loop ^ ω ^

## v1.0.23 — Drag files into the notch (Jun 8, 2026)

File drag-and-drop, and a pile of quality-of-life.

### new

- **Drag files in:** Drag your files into the notch and start chatting about them. Available now { ^-^ }
- The double-tap text box now opens instantly, and text responses get a copy button and selectable text.

### improved

- Realtime voice now follows Bluetooth device switches instead of dying on route flips.
- Links now open directly (YouTube, Maps, searches) instead of just being read aloud.
- In-app bug reports now include logs, so we can actually fix your thing.

### fixed

- Compat mode can no longer get stuck hidden.

## v1.0.22 — Interrupt me anytime (Jun 1, 2026)

Voice got more polite about being talked over.

### improved

- **Barge in:** In always-on mode you can now interrupt HeyClicky mid-sentence and it will actually hear you.
- **Any song on Spotify:** Spotify playback now searches any track, album, or artist by name. (Until now it was deliberately restricted to AC/DC. Long story ¯\_(ツ)_/¯)

### fixed

- The notch stuck on "Thinking…" after a cancelled voice turn.

## v1.0.21 — Clicky is now HeyClicky (May 30, 2026)

The big rename, and a big release to go with it.

### new

- **HeyClicky:** We renamed Clicky to HeyClicky everywhere, app, process, and all. Same buddy, fuller name.
- **Realtime voice:** A new realtime voice surface, now the default for voice and typed input. Plus a voice picker with spoken previews, so you can hear each voice before choosing.
- **Draw-on-screen guidance:** The first version of guided walkthroughs: target rings, annotations, and narration, on by default. This grew into the step-by-step teacher in v1.0.26.
- **Google Calendar:** The realtime voice can read your events and create new ones.
- A Spotify playback tool. Deliberately restricted to AC/DC for now, as a joke. We'll open it up soon (⌒‿⌒)

### improved

- The notch is now the only home, the legacy menu-bar panel is gone, and agent history is searchable.
- The default mic is your built-in mic, and the first spoken word no longer crackles on a cold launch.

### removed

- HeyClicky Notes. It never quite found its groove.

## v1.0.20 — A sleep fix (May 19, 2026)

A tiny one.

### fixed

- A crash in the chime engine when your Mac went to sleep and woke back up ( ˘▽˘)

## v1.0.19 — Small tunings (May 16, 2026)

A small one.

### improved

- Voice now speaks the full reply instead of a shortened version.
- Integration suggestion popups are capped at 3 per day, and the notch waits a beat longer before expanding on hover.

## v1.0.16-18 — Notch follow-ups (May 14, 2026)

Three quick builds tightening the new notch.

### new

- A Show in Dock toggle, and microphone selection in Settings.

### improved

- Reliable agent follow-ups, and voice replies are no longer truncated.
- The cursor is undocked by default again (docked felt too radical as a default).
- Integration suggestions got an overhaul: the full connector catalog, native Mac apps recognized, and way fewer false positives.

### fixed

- Rejected agent handoffs no longer bill your message quota :)

## v1.0.15 — A home in the notch (May 13, 2026)

New update is out. I wanna tell you what we did!!

### new

- **The notch:** HeyClicky now has a home in your Mac's notch. This is a bit of a radical design and we're still testing it. We're not married to it, but it's feeling pretty good so far :)

### connections

- **HeyClicky connects to your stuff:** In all honesty, we have no interest in shoving AI agents down everyone's throats, the whole industry is doing that already lol. But we really wanted HeyClicky to connect to more stuff. The agent now talks to Notion, Gmail, Linear, and a whole lot more: "Research new TikTokers for my product and put them in my Notion table", "Take this bug report, make a Linear ticket, and then send it to Joshua in Slack". It's endless. Try it out in settings :)

### more

- **New voices:** We handpicked a ton of new voices. Believe it or not, quite a few kids are using HeyClicky, so there's a bunch of kid-friendly voices now too. Pick one in settings.
- **Dock HeyClicky:** Click dock and HeyClicky won't follow your cursor around. He'll take refuge in your notch instead. Enjoy!
- **A new brain:** HeyClicky replies much faster, has context of your agents, and will hopefully not randomly spawn agents anymore. (If you're curious: we built our own lightweight agent harness in house.)

## v1.0.12 — HeyClicky can click (May 4, 2026)

This is a fat release. We usually hate long-ass update posts, but there's genuinely cool stuff here. Skim through!

### new

- **HeyClicky can now click:** Thanks to our friends at Cua, HeyClicky can use your computer. It controls your browser and more: "Research really good SSDs, then go to Amazon and add one to my cart", "Go to OpenAI and generate me an API key from the dashboard". Honestly: this feature is interesting, but early. Sometimes it breaks. We wanted to get it out to see what you do with it.
- **HeyClicky Notes:** Tell HeyClicky what you wanna save and it writes and maintains a personal notes wiki for you. Like a Wikipedia, but for your thoughts { ^-^ } Bug reports, interesting articles, ideas from iMessage, visual inspo. Hit the bookmark icon next to your settings button.
- **HeyClicky types for you, anywhere:** Kinda like WisprFlow, but HeyClicky types for you. Say "HeyClicky, type text in this box for me..." and it types wherever your cursor is. Great in email.

### improved

- **No more saying "HeyClicky Agent":** Small change, but surprisingly useful. HeyClicky now just understands your intent and spawns an agent. Talk to it, give it a task, and it will either guide you on how to do the thing, or just do the thing for you.
- **A cancel window:** Agents now wait 5 seconds before starting so you can cancel a mis-spawn, and the agent speaks its follow-up questions aloud.

## v1.0.11 — Google Workspace, and shortcuts your way (Apr 30, 2026)

The groundwork before the fat release.

### new

- **Google Suite is in:** The agent now talks to your Gmail, Google Drive, Sheets, and Calendar, and does your tasks in the background. "Did I miss any important emails in the last 2 days? Been busy", "Take my bank statement and make a Google Sheet for my budget". Connect it in settings under Google Workspace. (By default, HeyClicky can't send emails for you.)
- **Custom shortcuts:** Push-to-talk and text-mode shortcuts are now customizable, including double-tap-modifier triggers.

### improved

- A fast intent classifier decides when a request should go to the agent, using your last few turns.
- The agent can type directly into visible on-screen fields, and drives its own tinted overlay cursor. It is forbidden from moving your real pointer.

## v1.0.10 — HeyClicky agents (Apr 27, 2026)

The simplest interface in the world to talk to AI and spawn agents. Built for consumers, zero setup.

### new

- **Spawn agents with your voice:** HeyClicky can spawn other HeyClickys that do work for you via AI agents. It builds Mac apps. It does research to help you find IG micro-influencers. It interacts with native Apple Notes, Calendar, and Reminders. If you've never spawned an AI agent before, congrats, you can now easily make one with just your voice ＼(^o^)／

## v1.0 — Introducing HeyClicky (Apr 6, 2026)

Where it all started. An AI teacher that lives as a buddy next to your cursor. It can see your screen, talk to you, and even point at stuff, kinda like having a real teacher next to you.

### new

- **Version 1.0:** Press a shortcut and talk to your computer. It sees your screen and answers out loud. We'd been using it for days to learn DaVinci Resolve, 10/10. Everything above grew from here (^-^)ノ
