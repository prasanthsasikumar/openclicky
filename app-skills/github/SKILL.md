---
name: GitHub
description: Teaching notes for github.com — repository tabs, code view, pull requests, issues, actions, settings, and what to point at.
sites: [github.com]
surfaces: [talk]
---

## Layout
The global header runs across the top: the GitHub logo and repository name at the left, the search box in the center-right, and notifications, the plus (new) menu and the profile avatar at the far right. Within a repository, the tab row sits under the header: Code, Issues, Pull requests, Actions, Projects, Wiki, Security, Insights, Settings. The Code tab shows the branch dropdown at the top-left of the file list, the green Code button at the top-right of the file list, and the About sidebar on the right. A pull request has tabs Conversation, Commits, Checks, Files changed under its title, with the merge box near the bottom of Conversation and reviewers/labels in the right sidebar.

## Common tasks
- Clone: the green Code button at the top-right of the file list, copy the URL or "Open with GitHub Desktop".
- New file or upload: the plus (Add file) dropdown next to the Code button.
- Switch branch: the branch dropdown at the top-left of the file list.
- Open a pull request: Pull requests tab, New pull request at the top-right, choose base and compare.
- Review: Files changed tab, click a line's plus to comment, then Review changes at the top-right, choose Approve or Request changes.
- Merge: scroll to the merge box at the bottom of the PR conversation, choose the merge method from the dropdown arrow.
- Create an issue: Issues tab, New issue at the top-right.
- Keyboard: press T in the Code tab to search files, period to open the web editor.

## Pointing hints
- "How do I clone this?" — point at the green Code button at the top-right of the file list.
- "Where are the pull requests?" — point at the Pull requests tab in the row under the header.
- "How do I merge?" — point at the merge box at the bottom of the conversation, above the comment field.
- "Why did CI fail?" — point at the red X in the checks section of the PR, or the Actions tab.
- "How do I change branches?" — point at the branch dropdown at the top-left of the file list.
- "Where are repo settings?" — point at the Settings tab at the far right of the tab row (only visible with admin access).

## Gotchas
- Settings and merge buttons are hidden without write or admin permission; a missing tab usually means missing access, not a UI change.
- A grey merge button means checks are pending or a review is required; hover it for the reason.
- The new "Files changed" experience groups by folder; the old view lists files flat. Positions differ, so describe from the screenshot.
- Enterprise instances live on a different host and are not matched by this skill.
