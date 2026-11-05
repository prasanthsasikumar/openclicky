# Security policy

OpenClicky hears your microphone, sees your screen when you ask it something, can act on your computer,
and holds API keys, so security reports are welcome and taken seriously.

## Reporting a vulnerability

Please report privately, not in a public issue or discussion:
[open a private report on GitHub](https://github.com/prasanthsasikumar/openclicky/security/advisories/new).

Include what you found, the platform and version, and the steps to reproduce it. This is a small
open-source project, so replies are best effort. Please give us a reasonable chance to release a fix
before you publish details. We're happy to credit you unless you'd rather stay anonymous.

## Supported versions

Fixes go into the latest release on the Mac, Windows and Linux clients. Older versions aren't patched,
so please check that the problem still exists in the
[latest release](https://github.com/prasanthsasikumar/openclicky/releases/latest).

## In scope

- The Mac, Windows and Linux clients, the `openclicky` CLI and the backend in this repository
- The hosted backend at `api.openclicky.org`
- API keys, account tokens, screenshots, audio or dictated text exposed or sent somewhere they shouldn't go
- Ways for a web page, a file or a model's reply to make the app run actions the person didn't ask for
- Escaping the sandbox the drawing stage runs model-written programs in
- Release, update and signing problems

## Out of scope

- Bugs in the AI vendors' own services, which should go to those vendors
- Attacks that need someone who already controls your unlocked computer or your user account
- A model giving a wrong answer, unless it leads to one of the problems above
- Windows SmartScreen warnings, which appear because the Windows build isn't code-signed yet

Please don't test against other people's accounts, or run denial-of-service tests against the hosted backend.
