---
name: openclicky-google-workspace
description: Use OpenClicky's Composio-backed Google Workspace integration for Gmail read/search, Gmail drafts and approved sends, Calendar events, Drive, Docs, Sheets, unread mail, files in Google Drive, and day-planning tasks.
---

# OpenClicky Google Workspace

Google Workspace is powered by OpenClicky's Composio integrations, but it is not one monolithic connector in this release. The user connects Gmail, Google Sheets, Google Calendar, Google Drive, and Google Docs separately in Settings -> Integrations.

## Use When
- The user asks about Gmail, unread mail, email threads, Gmail filters/settings, Calendar, Drive, Docs, Sheets, or planning their day.
- The task involves Google Workspace files, folders, spreadsheets, documents, or messages.
- Another workflow needs Google Workspace data as an input.

## Do Not Use When
- The task is generic web research about Google or search results; use `openclicky-research-report`.
- The task is Google Cloud/GCP, Google Ads, Analytics, Maps, Photos, or another non-Workspace Google product; use normal developer, web, CLI, or browser routes.
- The user wants visible browser clicking in Gmail/Docs/Sheets; use `cua-driver` only for the GUI portion when Computer Use is exposed and the user explicitly needs visible UI.

## Primary Path
1. Use the `composio` MCP server when it is attached to the runtime.
2. Treat Google Workspace as Gmail, Calendar, Drive, Docs, and Sheets only. The separate Composio toolkit slugs are `gmail`, `googlesheets`, `googlecalendar`, `googledrive`, and `googledocs`; do not ask the user to connect "Google Workspace" as one broad integration.
3. If Composio reports missing, revoked, expired, or unauthorized auth, stop and tell the user to connect or reconnect the specific Google integration in OpenClicky Settings -> Integrations.
4. Do not run OAuth, browser sign-in, `gog`, or raw Google token flows from inside the agent.
5. For document/spreadsheet artifacts, combine Composio reads/writes with internal `doc`, `spreadsheet`, and `openclicky-artifacts`.
6. For email drafting or send requests, route through `openclicky-email-assistant` after reading the thread/context.

## Google Workspace Write Recipes
- Follow the global Composio write contract for exact schema keys, schema lookup when ambiguous, and read-back verification. The notes below are Google-specific recipes, not the full Composio policy.

## Google Docs Writes
- For `GOOGLEDOCS_CREATE_DOCUMENT_MARKDOWN`, put the body in `markdown_text`; do not use `markdown` for the create call.
- A successful create response can still mean a title-only document. If the body was not confirmed, treat it as not written yet.
- After any Google Doc write, verify with `GOOGLEDOCS_GET_DOCUMENT_PLAINTEXT` or another read-back tool and confirm the body is non-empty.
- Never report a Google Doc write as done until the intended body text is confirmed present.

## Google Sheets Writes
- For `GOOGLESHEETS_VALUES_UPDATE`, use `spreadsheet_id`, `range`, `value_input_option`, and a rectangular two-dimensional `values` array.
- For batch value writes, use the exact schema returned for that batch tool; do not assume `value_input_option` and `valueInputOption` are interchangeable.
- When replacing content that may be shorter than old sheet contents, clear the old range first or write to a fresh sheet/tab.
- After any Sheets write, verify with a values read-back and confirm the expected headers, row count, column count, and at least one representative cell.

## Fallbacks
- If the `composio` MCP server is not attached, explain which specific Google app needs to be connected in OpenClicky Settings -> Integrations. Offer voice/in-app guidance through that setup, but do not offer to connect it yourself and do not use Computer Use to operate OpenClicky's own Settings/Integrations flow. Offer visible Gmail/Docs/Sheets/Drive browser control only for the original Google app task when the user explicitly wants that route or approves the fallback.
- If a specific Google Workspace tool is unavailable, say which capability is missing and do not silently switch to browser automation. Visible browser control is a separate, less clean route; use it only for explicit UI requests or after the user accepts that fallback.
- If a Composio response is too broad, paginated, or large, narrow the query, request fields, paginate, filter, or use the tool's export/search path before considering visible UI.
- Remote Tasks and cloud-background Google jobs are not shipped in this release; do not route recurring Google Workspace requests to a scheduled sandbox or local helper.

## Safety
- Reads are okay after auth.
- Editing documents/sheets, creating or changing calendar events, adding rows, creating comments, changing sharing, and other Drive/Docs/Sheets writes the user asked for execute directly — the request is the approval, so do not draft-and-wait. Require explicit confirmation (account, target, action) ONLY for deleting or archiving files/data, overwriting content the user did not ask you to replace, and spending money.
- Gmail sending requires explicit approval of the exact recipients, subject, body, and attachments before sending.
- Never ask the user to paste Google OAuth secrets, cookies, API keys, refresh tokens, or access tokens.

## Artifacts
- When exporting or downloading Google files, report the exact local path.
- For Sheets/CSVs, use `spreadsheet` for local analysis and export checks.
- Use `openclicky-artifacts` to open/reveal local exports.

## Verification
- For writes, re-read or list the changed item when possible.
- For exports/downloads, verify the local file exists.
