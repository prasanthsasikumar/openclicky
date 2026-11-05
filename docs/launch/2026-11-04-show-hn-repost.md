# Show HN repost — November 2026

The 2026-10-08 Show HN (https://news.ycombinator.com/item?id=50003527, account im_psk) got 1 point and
0 comments, and was never flagged or killed. HN's FAQ allows a small number of reposts when a story
"has not had significant attention in the last year or so", and the Show HN page says a major overhaul
qualifies where a feature bump does not. Since that post: Windows and Linux clients, the Teacher,
voice navigation, do-things-for-me, the local Apple Intelligence rung, draw-to-explain.

## Rules (the part that went wrong before)

- **Nobody is asked to upvote or comment.** Not friends, not the Discord, not the lab, not a group chat.
  The HN link is not sent to anyone. Other channels get the project link (openclicky.org or the repo),
  never the HN thread. Early votes from direct links trip the ring detector and the post is penalised.
- **One account: im_psk.** Never a friend's account.
- **Never delete and resubmit.** If it sinks, it sinks; deleting and reposting is buried as a duplicate.
- **Ask the moderators first** (email below). If they re-up the old post instead, do not also repost.
- **Ship before posting.** Draw-to-explain is merged but under Unreleased in CHANGELOG.md. Cut the Mac
  release first so what the post describes is in the download.
- **Be there.** Answer every comment within minutes for the first several hours. Specific, link to code
  lines, never argue. Thank critics.
- **No "Show HN" cross-posting on X/LinkedIn the same hour.** Post the project there a day later if at all,
  with the project link.

## Step 1: email the moderators — sent 2026-11-04 from prasanth@ahlab.org, awaiting reply

To: hn@ycombinator.com
Subject: Show HN repost — OpenClicky

> Hi,
>
> On 8 October I posted a Show HN for OpenClicky, an open-source voice assistant that points at things on
> your screen: https://news.ycombinator.com/item?id=50003527. It got one point and no comments.
>
> Since then it has grown a lot: Windows and Linux clients, moving around the screen by voice, doing
> small tasks, a local on-device rung on macOS 26, and a mode that draws a diagram on screen while it
> explains. Would it be all right to repost it as a new Show HN, or would you rather put the old one in
> the second-chance pool? Happy with whichever you prefer.
>
> Account: im_psk. Repo: https://github.com/prasanthsasikumar/openclicky
>
> Thanks,
> Prasanth

If they re-up the old post, skip step 3 and go straight to step 4 on the old thread.

## Step 2: release

- Finish and release the Mac build with draw-to-explain (CHANGELOG Unreleased → next version).
- Confirm the three download routes work from a clean machine: dmg from the release page, Homebrew cask,
  Windows Setup.exe.
- Check the README's demo GIF and the YouTube demo still show what the post claims. If there is time,
  record a 30 s clip of draw-to-explain and put it in the README under the tagline; HN readers click the
  repo, not the site.

## Step 3: submit

When: Tuesday to Thursday, 8:00–10:00 ET, after the moderators reply. Thursday 2026-11-05 if they
answer fast, else Tuesday 2026-11-10.

URL: https://github.com/prasanthsasikumar/openclicky (source, downloads and demo on one page).

Title (HN allows 80 characters; counts below are exact):

1. `Show HN: OpenClicky – voice assistant that points at and draws on your screen` (77) ← preferred
2. `Show HN: OpenClicky – ask your screen out loud, it points and draws the answer` (78)
3. `Show HN: OpenClicky – open-source voice helper for Mac/Windows that points at things` (84, too long)

No "open-source" in the title; the GitHub URL says it. No version number, no exclamation marks.

First comment, posted right after submitting:

> I posted this a month ago and it sank, so here it is again with a lot more in it.
>
> OpenClicky is a voice assistant that knows what's on your screen. Hold control+option on a Mac (Ctrl+Win
> on Windows) and ask "where do I change the deploy branch?": a small pointer flies out of the notch and
> lands on the setting while it answers out loud. Since the last post it also moves around for you
> ("click the first link", "switch to the YouTube tab", "scroll down slowly", "find my tax return"),
> does small chores ("minimize everything", "draft an email to Ana saying I'll be late", never sent), and
> draws: ask "how does a jet engine work?" and it builds a diagram on screen part by part as it speaks.
> Hold fn and talk, and the words are typed where your cursor is, cleaned up in the style of the app in
> front, with the recogniser running on the machine.
>
> It's MIT. Mac app in Swift, a Windows port in C#/WPF by Tan Wei Jie (with the drawing stage and the 3D
> figures by darkworks), a Linux client in Python/GTK by another contributor, all sharing one backend (Hono, runs on Node or Workers) and the same ~/.openclicky files.
> Pointing works by asking the model for coordinates and then snapping to OCR'd on-screen text, because
> the raw guess is 30–100 px off. On macOS 26, simple requests ("open Safari", "set the volume to thirty")
> are handled by Apple's on-device model with no screenshot leaving the machine.
>
> Three ways to run it: put your own key in (OpenAI, Gemini, Anthropic and others; the key goes straight
> to the vendor, our backend never sees it), make a free account with just an email, or self-host the
> backend from `backend/Dockerfile`.
>
> Rough edges I know about: the Windows build isn't code-signed yet so SmartScreen warns; the sub-second
> voice lane needs your own OpenAI or Gemini key (the free account answers in a few seconds); the agent
> lane that does real file/shell work needs Codex installed separately; and the pointer still misses on
> very small icons. Happy to answer anything about the Realtime tool-calling, the OCR snapping, the
> drawing stage, or the on-device rung.

Fix before posting: check each claim against the released version. If draw-to-explain did not ship,
remove the "and draws" from the title and the drawing paragraph.

## Step 4: during the thread

- Reply to every comment, fastest to the critical ones. Link to code lines.
- Questions that recur become README sections afterwards.
- Someone will ask why not just use Siri / Copilot / Clicky: the answer is "it's open source, it shows
  rather than tells, and your keys go to the vendor". Have the comparison ready, not defensive.
- Someone will ask about privacy: what leaves the machine, when, and the pause-before-send setting.
- Do not refresh the points. Do not post the thread anywhere.

## Afterwards

- Snapshot PostHog referrers, GitHub traffic and release downloads 24 h and 7 d later, against the
  2026-10-26 figures in the usage-stats memory.
- Record what the thread asked, in this file.
