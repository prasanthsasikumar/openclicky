# The OpenClicky Discord

Live since 2026-10-11: https://discord.gg/zGGERnM5eq (never expires), also https://openclicky.org/discord.

Prasanth creates the server (Discord → + → Create My Own → For a club or community) and pastes the
text below. Then: Server Settings → Enable Community (gives #rules and an announcement channel type).

**Name:** OpenClicky · **Icon:** `macos/OpenClicky/OpenClicky/Assets.xcassets/AppIcon.appiconset/512-mac.png`

## Channels

INFO
- #rules (read-only)
- #announcements (announcement channel; release notes go here, followable by other servers)

COMMUNITY
- #general
- #show-and-tell (screen recordings of what it pointed at, skills you made)
- #feature-requests (forum channel; tags: pointing, dictation, agent, languages, windows)

SUPPORT
- #help (forum channel; tags: install, permissions, keys-and-account, dictation, pointing; post template below)
- #bugs (forum channel; asks for the version and `~/Library/Logs/OpenClicky/app.log` lines, never keys)

BUILDING
- #contributors (for people working on the code; links CONTRIBUTING.md)

## Roles

- **Maintainer** (Prasanth): manage messages, manage threads.
- **Contributor**: given by hand to anyone with a merged pull request; can post in #contributors.
- **@everyone**: read all, post everywhere except #rules and #announcements.

## Welcome message (Server Settings → Onboarding → Welcome)

> Welcome to OpenClicky. It is the Mac helper that points: hold ⌃⌥, ask out loud, and a pointer flies
> to the answer on your screen. Stuck? Post in #help with your macOS version and what you asked.
> Made something with it? #show-and-tell. Download: https://openclicky.org/download

## Rules

1. Be kind. People here range from first-time Mac users to people writing Swift.
2. Never post API keys, account tokens or `~/.openclicky/shell.json`. Redact them in logs.
3. Screenshots of your screen can show more than you mean to: crop to what matters.
4. Bugs with steps to reproduce go in #bugs; questions in #help.
5. English or any language you like; say which so others can help.

## #help post template

> **What I asked or did:**
> **What happened:**
> **What I expected:**
> **macOS version / OpenClicky version (menu bar → about):**

## Invite

Server Settings → Invites → Create: never expires, no use limit. Put it in the website's
`netlify.toml` (repo `prasanthsasikumar/openclicky-site`):

    [[redirects]]
      from = "/discord"
      to = "https://discord.gg/<code>"
      status = 302

and add the "Join the Discord" buttons back to the hero and the nav/footer (`href="/discord"`); update
the test "no Discord link while the invite does not exist" in `tests/page.spec.mjs` to expect the links
instead. Analytics already reports clicks on `/discord` as `discord_clicked`.
