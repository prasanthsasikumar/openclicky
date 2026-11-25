# Brain and jobs: design for approval

Status: **proposed, not built.** Nothing in this document is implemented. It needs a yes on the
four decisions at the end before any code is written.

## What this is for

Three things make an assistant feel instant, and OpenClicky does each only in part today:

1. The answer starts happening while it is still being written.
2. The voice says exactly what was written, and knows what it had said when it is interrupted.
3. Long work runs beside the conversation, shows a plan, can stop to ask, and hands back a report.

## Where OpenClicky stands

| Piece | Today | Gap |
|---|---|---|
| Answers executed as they arrive | Only when the drawing gate says "draw": `DrawingAnswer` feeds the stage statement by statement (`say()`, marks). An ordinary answer is prose with `[POINT]` / `[ACTION]` / `[NEXT]` tags read out of the finished or streaming text. | Two formats, two prompts, two code paths. Pointing waits for a tag; actions run after the text ends. |
| Voice says what was written | Ask lane: yes (`SpeechQueue` speaks the streamed text in pieces through the chosen voice). Fast voice: no, the Realtime model words its own reply, so a drawing narrated beside it needs a second voice. | In the fast lane what is drawn and what is said can disagree. |
| Knows what was heard on interrupt | Ask lane stops the queue; the remembered exchange keeps the whole text. Realtime: the server cancels its reply, but no `conversation.item.truncate` is sent, so the model believes it said everything. | After an interruption the model can refer to words nobody heard. |
| Warm at key-down | Realtime: the socket is kept warm for 15 minutes after a turn. Ask lane: TLS warm-up only; the transcription session opens at the press. Local rung: the model is prewarmed at the press. | The first press after a pause pays for a connection. |
| Jobs | `openclicky run --events` starts a Codex thread per job; milestones arrive as lines; one job at a time (a new one cancels the last); the result is spoken and shown as a card. | No plan, no way for a job to ask a question, no report beyond the reply text, nothing runs beside a conversation. |

## Proposal

### A. One answer format: the program

Every answer in the ask lane becomes a small program in the drawing stage's language, with the
stage's `say()` and marks, plus three statements that replace the tags:

- `point(x, y, "label")` for `[POINT:…]`
- `act("open_app", {…})` for `[ACTION:…]` (the same `MacAction` table, the same checks)
- `next()` for `[NEXT]` in a guide

A plain spoken answer is then a program of `say()` lines and nothing else; the stage window opens
only when a mark is drawn. `DrawingAnswer`'s incremental reader already exists; it gains the three
statements and loses the gate (`DrawingGate`), because every answer takes the same path.

Risks, and what limits them:
- Models that write prose instead of a program: the reader treats any text outside a statement as
  one `say()`, which is today's behaviour. The plans (`claude -p`, `codex exec`) keep the tag
  protocol until they are measured, since they already go through `PlanToolProtocol`.
- Cost: a program costs more output tokens than prose (measured on drawings: about 1.4×). The
  free account keeps prose.
- The Windows app shares the stage and its prompt. The three statements must be added there in
  the same release or its reader must ignore them; this needs Wei's agreement first.

### B. A voice that reads lines, and the truncation

- Fast voice, OpenAI: when the answer comes from the ask lane's program, each `say()` line goes
  to the Realtime session as "say exactly this", which removes the second voice for drawings.
  Whether Realtime reads verbatim reliably has to be measured before this is the default; if it
  does not, the fast lane keeps wording its own replies and only C and D below ship for it.
- On barge-in or esc, send `conversation.item.truncate` with the milliseconds actually played
  (`AnswerVoicePlayer` and the Realtime player both know them), and store the same cut in
  `rememberExchange` for the ask lane, so the next turn's history says what was heard, followed
  by "(interrupted here)".

### C. Warm at key-down

At the ⌃⌥ press, in parallel with the microphone:
- open the transcription session for the chosen engine (AssemblyAI token + socket, or the
  OpenAI upload session's connection);
- send the answer source a warm-up that reuses the system prompt, where the provider caches
  prompts (Anthropic and OpenAI do; skipped for plans and for Gemini until measured);
- keep the existing rule that the Realtime socket is only kept warm near a recent turn, because
  each session is minted and billed.

Measured before and after with the existing `scripts/measure-actions.sh` timings (press →
transcript → first token → first word). The target is not a number picked here; the measurement
decides whether each warm-up stays.

### D. Jobs that run beside the conversation

Keep the `openclicky` CLI and Codex as the runner; change what the app does around it.

- **More than one job, none blocking the voice.** `OpenClickyAgentClient` becomes a pool: each
  job is its own process and thread id; a question in the ask lane no longer cancels a job
  (`interruptCurrentAnswer(includingAgentWork:)` stops only what is being said). A cap of three.
- **A plan.** The job's instructions ask for a plan of three to five steps kept up to date; the
  CLI forwards Codex's plan updates as `--events` lines (`{"type":"plan","steps":[…]}`). The
  reply card's step list (already built, fed by milestones today) shows the real plan.
- **A question that pauses the job.** A new event `{"type":"question","text":…,"choices":[…]}`:
  the island shows it as a card with the choices and a text field, the job waits, and the answer
  is sent to the same thread (`openclicky run --thread <id> --answer`). Unanswered after ten
  minutes, the job is told to go on with its best guess and say so.
- **A report.** A job ends with `output/` (what it made) and a `report.md` whose first line is
  the result in one sentence. The job-done notice (built) shows that line; the result card shows
  the report and the files. The day's offers (`Plate/`) run through the same pool, which removes
  its private queue.

Not proposed: a second runner, a cloud runner, the program-writing `exec` tool, routines,
computer use, a browser bridge. Each is its own decision.

## Order and size

1. D (jobs) first: it is the most self-contained, and the island's reply card and notice were
   built for it. About a week, most of it in `agent/` and `OpenClickyAgentClient`.
2. B's truncation: small, both lanes. Two days with tests.
3. C: one day per engine, each kept only if the measurement shows it.
4. A last, behind a switch, after Wei agrees the stage language change. Two weeks including
   prompt work; this is the one that can make answers worse if rushed.

## Decisions needed

1. **A:** change the ask lane to programs for own-key users (plans and the free account keep
   prose), yes or no? If yes, I ask Wei about the three new statements first.
2. **B:** try verbatim lines through Realtime and keep it only if measured reliable, or leave the
   fast voice alone?
3. **D:** up to three jobs at once, and a job that keeps running when you ask something else. Is
   that what you want, given each job uses your plan or key?
4. **D:** may a job stop and ask you a question on the island, or should jobs never interrupt and
   only report?
