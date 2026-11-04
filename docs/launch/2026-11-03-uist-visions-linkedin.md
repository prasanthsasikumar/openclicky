# LinkedIn post: OpenClicky and Michael Bernstein's UIST 2026 Visions talk

Context: Michael S. Bernstein (Stanford) gave the UIST 2026 Visions talk in Detroit on 2026-11-03,
"The Interface of Theseus: The Rise of Just-In-Time Interfaces" (UIST Adjunct '26, 2 pages,
doi 10.1145/3830397.3841887, arXiv 2609.06770). Prasanth's mentors asked for a post framing OpenClicky
as a step towards it, tagging him.

## What the talk argues (from the paper)

- Interfaces today are "strikingly static artifacts", a dinner plate that cannot change as the food does.
- Two ingredients now make bespoke software nearly free: rapid coding agents, and user models with a
  strong notion of what we need. Result: "just-in-time interfaces: rapid, discardable, on-demand
  interfaces tailored to our needs in the moment."
- The image: use it, then "tear the interface away like a used sheet of paper, and start your next
  task on a blank one."
- The objection he raises himself: adaptive interfaces failed before (Office's personalized menus were
  rolled back), and just-in-time interfaces violate consistency and standards, i.e. familiarity.
  "The ever-changing staircases of Hogwarts make for amazing spectacle, they do not make for a
  particularly useful way of getting where you're going."
- Why it may win anyway: Spolsky on Napster, usefulness beats usability. Bespoke interfaces can cross
  walled gardens, already via MCP "or by simple screenshot monitoring".
- Our role shifts to toolkit creators: "we create the levers that shape what gets created and how they
  operate, but we may never lay hands or eyes on the last-mile interfaces themselves."

## The honest bridge to OpenClicky

OpenClicky does not generate a new interface. It leaves the app exactly as it is and builds the
*help* just in time: a pointer that lands on the answer on your screen, an explanation drawn by hand
over the screen (Teach Me), guided steps, dictation in the style of the app in front, and a "do it for
me" lane. It reads the screen (his "simple screenshot monitoring") and it tears itself away when you
are done. So it is one answer to the familiarity objection: keep the static interface people already
know, make the layer on top of it just-in-time. A step, not the destination.

Tag him by typing `@Michael Bernstein` in the composer and picking the Stanford entry.

## Post A (recommended, ~190 words)

```
Sat in Michael Bernstein's Visions talk at UIST this morning. His argument: when software becomes
nearly free to build, interfaces stop being fixed artifacts and become just-in-time, built for this
moment, this person, this task, then torn away like a used sheet of paper.

He also named the hard part himself. Adaptive interfaces have failed before (Office's personalized
menus got rolled back) because people need familiarity. Hogwarts staircases are spectacular and
useless for getting anywhere.

OpenClicky is a small step into that future that keeps the floor still. The app in front of you
does not change. What gets built in the moment is the help: you ask out loud "where do I change the
production branch?", a pointer flies out of the notch and lands on the setting. Ask it to teach you
and it draws the explanation over your screen by hand. Then it is gone, and the app you know is
still there.

It works by looking at your screen, which Michael listed as one of the two ingredients. The other is
knowing what you need. That is the next thing to work on.

Open source, Mac, Windows and Linux: openclicky.org

Thank you @Michael Bernstein for the talk. #UIST2026
```

## Post B (shorter, ~110 words)

```
Michael Bernstein's UIST Visions talk today: interfaces will soon be built just in time, for the
task in front of you, and thrown away after. The catch he raised: people need familiarity, and every
adaptive interface so far has lost to that.

OpenClicky is my attempt at the middle. The app stays exactly as it is. The help is just-in-time:
ask out loud where something is and a pointer lands on it; ask it to teach you and the explanation
is drawn over your screen; done, and it is gone.

A step towards the vision, with the floor kept still. Open source: openclicky.org

Thanks @Michael Bernstein. #UIST2026
```

## Video (new take, 30 s, 1080×1350 like the 2026-10-24 post)

Reuse the pipeline in `~/Downloads/OpenClicky-promo-source/` (drive.py, rec.py, edit.py). Blank
TextEdit doc, never Notes. Captions in the same hand-drawn style as the Teach Me cut.

| s | On screen | Caption |
|---|---|---|
| 0–4 | A dense, familiar app sits still (Vercel or GitHub settings page). Nothing moves. | "The interface is fixed." |
| 4–12 | Hold ⌃⌥, ask "where do I change the production branch?". Pointer flies out of the notch, lands, voice answers. | "The help is built in the moment." |
| 12–22 | "Teach me this page." Handwriting draws the explanation over the screen. | "Drawn for this screen, this question." |
| 22–27 | Overlay fades. Same page, untouched. | "Then it is gone. The app you know is still there." |
| 27–30 | End card: OpenClicky mark, openclicky.org, "open source". | |

If there is no time for a new take, `~/Downloads/OpenClicky-LinkedIn-TeachMe.mp4` (33 s) carries the
same idea and can go up with Post A as is.

## Sources

- https://arxiv.org/abs/2609.06770
- https://uist.acm.org/2026/
