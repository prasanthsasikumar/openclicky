# OpenClicky → ASSETS 2027: routes, decision, review, method, outline

Written 2026-09-16. Five parallel literature sweeps (four completed, one lost to a rate limit),
plus direct Crossref / arXiv / ACL / ISCA verification. Every citation below carries a DOI or URL.
Items that could not be verified are quarantined in §7 — **do not cite those without checking**.

---

## 0. The decision, up front

**Target:** ASSETS 2027 full paper (~April 2027 deadline), with an IUI 2027 demo on 10 Nov 2026 as a
forcing function.

**Thesis:** *Agentic voice control of a computer has been studied for blind users only in English.
When a multilingual blind user delegates a task in their own language, the accent/code-switch
recognition penalty and the loss of visual verification compound — and nobody has measured what
that costs.*

**Contribution type:** empirical, mixed-methods, with a secondary technical contribution.

This replaces two earlier framings that the literature has already closed. See §2.

---

## 1. What changed during the dig

Two claims made earlier in planning turned out to be **false** and were discarded:

1. *"Computer-use agents have never been evaluated with blind users."* Wrong. At least six studies
   between mid-2025 and Sept 2026 do exactly this. Publishing that framing would invite a desk
   reject.
2. *"The companion/guidance framing is unoccupied."* Wrong. AskEase (CHI 2026) and Morae (UIST 2025)
   both sit in it.

What survived verification is narrower and stronger: **the language axis is empty.** Querying arXiv
for blind × agent × multilingual returns exactly one paper, whose own language handling is
English-only.

---

## 2. Routes considered

Ten candidate framings, assessed against the verified literature.

| # | Route | Verdict | Why |
|---|---|---|---|
| R1 | Grounding-accuracy benchmark, no participants | **Secondary** | A11y-CUA already characterises the gap; OSWorld-class benchmarks exist. Keep as a technical section, not the paper. |
| R2 | Verification & repair of agent **actions** by BLV users | **Core mechanism** | Prior work covers verification of *descriptions*, never of irreversible *actions*. See §3.3. |
| R3 | Confirmation granularity — when should an agent ask? | **Reject as primary** | Morae (UIST'25) and Zhou et al. (CHI'26) occupy it. Usable as a condition, not a contribution. |
| R4 | Guidance vs. automation ("companion") | **Framing only** | AskEase (CHI'26) and Kodandaram et al.'s "beyond-automation needs" already establish it. Cite as motivation. |
| R5 | **Multilingual / code-switched agentic control for BLV users** | **PRIMARY** | Verified empty. See §3.4. |
| R6 | macOS vs. Windows accessibility substrate | **Secondary** | Everything published is Windows/UIA. Real but thin alone. |
| R7 | Low digital literacy as the access barrier | **Defer — separate paper** | Out of ASSETS scope; see §6.4. |
| R8 | Longitudinal deployment | **Defer** | Kodandaram et al. did 3 weeks with n=8. Expensive to beat; not where novelty lies. |
| R9 | Agent and screen reader share the AX substrate | **Discussion + technical** | Screen2AX: only ~33% of macOS apps expose full accessibility. Inaccessible apps break *both*. |
| R10 | Formative needs-finding interviews | **Reject** | Made redundant by Singapore access and by Kodandaram's interview themes. |

**Chosen combination: R5 primary, R2 as the measured mechanism, R4 as framing, R6 + R9 secondary.**

---

## 3. Literature review

### 3.1 Computer-use agents are evaluated without users

The GUI-agent field evaluates by programmatic task success on scripted environments. OSWorld
(NeurIPS 2024, arXiv:2404.07972), WebArena (arXiv:2307.13854), Mind2Web (NeurIPS 2023),
AndroidWorld (arXiv:2405.14573), VisualWebArena (arXiv:2401.13649) and Windows Agent Arena contain
no disabled participants, no assistive-technology condition, and no non-visual success criterion.
A systematic review of **336 GUI-agent papers** (Jan 2018 – Apr 2026, arXiv:2608.09278) finds
evaluation "remains centered on task success," with "recovery, human escalation, safety enforcement,
and auditability" underdeveloped.

### 3.2 The accessibility work that does exist

- **A11y-CUA** — Gubbi Mohanbabu, Natalie, Kim, Guo & Pavel. CHI 2026. doi:10.1145/3772318.3791896.
  16 participants (8 BLV, 8 sighted), 60 tasks, 40.4 h. A frontier CUA solving **78.3%** of tasks by
  default falls to **41.7%** keyboard-only and **28.3%** at 150% magnification. The empirical smoking
  gun: standard benchmarks measure none of this.
- **Are We There Yet?** — Kodandaram, Padma Reddy, Bi, Zhou, Ramakrishnan & Ashok. **EMNLP 2026 Main**.
  arXiv:2609.00524. Three-week diary study, **n=8 blind users**, 1,258 commands, 12 apps. Best model
  (GPT-5) 52.5% success. Failure taxonomy: grounding, hidden-path discovery, prior-knowledge
  overreliance, constraint binding, termination recognition.
- **Morae** — Peng, Li, Bigham & Pavel. UIST 2025. doi:10.1145/3746059.3747797. Proactively pauses UI
  agents for user choices; 10 BLV participants, head-to-head against OpenAI Operator and TaxyAI.
- **AskEase** — Chen, Lu, Wang, Qiu, Chen & Yang. CHI 2026 (cond. accepted). arXiv:2601.18092.
  Within-subjects, 12 screen reader users; guidance rather than execution.
- **Savant** — Kodandaram, Uckun, Bi, Ramakrishnan & Ashok. ASSETS 2024.
  doi:10.1145/3663548.3675605. Natural-language control of arbitrary app interfaces, n=11 blind
  participants, ~3× usability gain. **The direct predecessor. English only; language not discussed.**
- **Bespoke Visual Assistance** — Seehorn et al. ASSETS 2026. arXiv:2607.21760. BLV people *directing*
  agents to build tools.
- **Position: Assistive Agents Need Accessibility Alignment** — Hu et al. ICML 2026. arXiv:2605.13579.
  Argues agents assume "sighted interaction, low-cost verification, and tolerable trial-and-error."

### 3.3 Verification: the literature studies descriptions, not actions

Every major empirical study of AI for BLV users from 2017 to 2026 studies an AI that **tells** the
user something; the user then decides what to do.

- MacLeod, Bennett, Morris & Cutrell. CHI 2017. doi:10.1145/3025453.3025814. Blind users place high
  trust in automatic captions and *rationalise away* incongruities. The over-trust anchor.
- Alharbi, Lor, Herskovitz, Schoenebeck & Brewer. **Misfitting With AI.** ASSETS 2024.
  doi:10.1145/3663548.3675659. How blind people verify and contest AI errors: ask a sighted person,
  re-photograph, apply O&M skills.
- Chen, Iyer & Pavel. **Surfacing Variations.** ASSETS 2025. doi:10.1145/3663547.3746393. Showing
  multiple sampled descriptions raised identification of unreliable claims **4.9×**.
- Perera, Ananthanarayan, Goncu & Marriott. CHI 2026. doi:10.1145/3772318.3790988. 12 blind users;
  five verification strategies in spreadsheets; verification was "effortful, time-consuming, or
  infeasible."
- Gonzalez, Collins, Azenkot & Bennett. CHI 2024. doi:10.1145/3613904.3642211. Diary study, n=16;
  trust 2.43/4. Successor: Gonzalez Penuela et al., CHI 2026 (Honorable Mention),
  doi:10.1145/3772318.3793266, n=20.

**The structural point.** Every one of those verification strategies presupposes a *persisting,
inspectable world state* the user can re-interrogate at leisure. An agent that has already clicked
"Confirm purchase" or sent the email produces an irreversible fact, not a claim to be checked.
**No published work studies how blind users verify actions already taken.**

Supporting theory: a screen reader is direct manipulation in Shneiderman's precise sense (1983,
doi:10.1109/MC.1983.1654471) — the object of interest is continuously perceivable, actions are
incremental and reversible. Delegation is Maes's bargain (Shneiderman & Maes 1997,
doi:10.1145/267505.267514): it closes the gulf of execution and reopens the gulf of evaluation.
In Parasuraman, Sheridan & Wickens's terms (2000, doi:10.1109/3468.844354) the agent raises
automation at the *action implementation* stage while leaving the human accountable — Bainbridge's
irony (1983, doi:10.1016/0005-1098(83)90046-8). Lee & See (2004, doi:10.1518/hfes.46.1.50_30392)
give the normative target: trust calibrated to observable competence. For a sighted user
verification is a glance — cheap, peripheral, parallel. For a screen reader user, verification runs
through *the same serial audio channel the agent occupies*, so it is a second full traversal of the
task. Vasconcelos et al. (2023, doi:10.1145/3579605) show that this cost determines whether people
verify at all.

### 3.4 The language gap

Four literatures exist and none of them meet.

**(a) BLV voice-interface HCI is monolingual-English by construction.** Abdolrahmani, Kuber &
Branham (ASSETS 2018, doi:10.1145/3234695.3236344); Pradhan, Mehta & Findlater (CHI 2018,
doi:10.1145/3173574.3174033); Abdolrahmani et al. (TACCESS 2019, doi:10.1145/3368426). Language is
never a variable. Seymour et al. (CUI 2023, doi:10.1145/3571884.3603760) quantify this narrowness
in the CUI literature itself.

**(b) Multilingual / code-switching CUI research contains no disabled users.** Cihan et al. (CUI
2022, doi:10.1145/3543829.3544511); Choi, Lee & Lee (CHI 2023, doi:10.1145/3544548.3581445); Bawa et
al. (CSCW 2020, doi:10.1145/3392846); Karusala et al. (CHI 2018, doi:10.1145/3173574.3174147).

> **The linchpin.** Wu et al. (MobileHCI 2020, doi:10.1145/3379503.3403563) find that L2 speakers
> prefer smartphones to smart speakers **because visual feedback lets them diagnose recognition
> breakdowns.** That is exactly the repair channel a blind user does not have. The mechanism is
> named in the literature; the compounded case has never been tested.

Harm evidence exists but only for sighted users: Wenzel et al. (CHI 2023,
doi:10.1145/3544548.3581357) show ASR failure rates causally lower self-esteem for Black
participants; Wenzel & Kaufman (CHI 2024 Best Paper, doi:10.1145/3613904.3642900) taxonomise
downstream harms and repair.

**(c) The multilingual screen-reader problem is documented as a rendering bug, not an interaction
problem.** Bhuiyan, Varvello, Zaki & Staicu (**IMC 2025**, doi:10.1145/3730567.3764505,
arXiv:2508.18328) measure 120,000 sites across 12 non-Latin scripts: screen readers "misrender or
mispronounce non-English text." Noh, Sulaiman & Mohamed Noor (i-USEr 2016,
doi:10.1109/iuser.2016.7857926) show Bahasa Melayu users served by non-BM voices whose enunciation
"confuses and often does not help." The CIS-India / Hans Foundation eSpeak project brief states
outright that Hindi, Kannada and Tamil are "supported at a basic level." Podsiadło & Chahar
(Interspeech 2016, doi:10.21437/interspeech.2016-1376) establish that BLV users depend on synthetic
voice quality more than any other group. **All of this is about output. None addresses input.**

**(d) ASR penalties are quantified but never connected to this population.** Koenecke et al. (PNAS
2020, doi:10.1073/pnas.1915768117); Meyer et al. (Artie Bias Corpus, LREC 2020) find US English
transcribed more accurately than Indian English; Svarah (Interspeech 2023,
doi:10.21437/interspeech.2023-2588) benchmarks Indian-accented English; SEAME (doi:10.21437/
interspeech.2010-563) provides the Singapore Mandarin-English code-switching corpus at ~82%
intra-sentential switching. No benchmark measures agentic *task* success across accents — only WER.

**(e) ICTD voice work reached BLV users but stopped at the feature phone.** Rajput, Agarwal, Kumar &
Nanavati (ASSETS 2008, doi:10.1145/1414471.1414542) built a Spoken Web layer explicitly for VI users
in developing countries; Baang (CHI 2018, doi:10.1145/3173574.3174217) reports **10,721 users, 69%
blind**. These are IVR content platforms — menu-driven, single-turn, pre-agentic. None controls a GUI.

### 3.5 Singapore

The population is documented as overwhelmingly multilingual. Census 2020 (Singapore DoS): home
language English 48.3%, Mandarin 29.9%, Malay 9.2%, Tamil 2.5%; **74.3% literate in two or more
languages**; among English-dominant home speakers, **86.8% report a second language**.

The assistive-technology infrastructure is described as if monolingual. The Disabled People's
Association's national report *Assistive Technology – A Road to Inclusion* (2018) discusses screen
readers throughout and mentions language exactly once, incidentally.

**No published source records what language blind Singaporeans actually compute in.** Crossref,
arXiv, SAVH, iC2, DPA, SG Enable and IMDA were all checked. That absence makes baseline descriptive
data a contribution in its own right.

---

## 4. Study design

### 4.1 Research questions

- **RQ1.** When a blind multilingual user issues agent commands in their preferred language or
  code-switches, how does task success change relative to English?
- **RQ2.** When the agent acts on the wrong target, do users detect it — and how long does detection
  take? What repair strategies do they use?
- **RQ3.** How does detection and repair differ between a *guided* mode (agent narrates and moves
  accessibility focus; user executes) and a *delegated* mode (agent executes, then reports)?
- **RQ4.** What language would users choose, given a system that genuinely permits either?

### 4.2 Design

Mixed-methods, **within-subjects**, in person in Singapore.

- **Factor A — Language:** English vs. participant's preferred language (code-switching permitted
  and not corrected).
- **Factor B — Mode:** Guided vs. Delegated.
- Counterbalanced (Latin square). Baseline block: participant's own screen reader workflow on
  matched tasks.

### 4.3 Participants

**n = 16** BLV adults. Justification from field norms: Savant n=11, Morae n=10, AskEase n=12,
A11y-CUA n=16 (8 BLV), Perera n=12, Kodandaram diary n=8, Gonzalez n=16/20. Sixteen sits at the
upper end of the published range and supports within-subjects modelling.

Screening: daily screen reader use (NVDA/JAWS/VoiceOver); self-reported fluency in at least one
language other than English; uses a computer for work or study.

Recruitment: SAVH (employment arm), iC2 PrepHouse, SIT / Guide Dogs Singapore.

### 4.4 Tasks

8–10 realistic desktop tasks spanning the verbs OpenClicky actually supports — `open_app`,
`open_url`, `create_folder`, `reveal_in_finder`, `set_volume`, `media_control`, plus agent-lane file
and application work. Mix of **reversible** (open an app) and **consequential** (send, delete,
purchase, change a setting) so that irreversibility is a real variable, not a hypothetical.

Ambiguity is designed in: repeated captions such as "Edit" on every row of a list — the exact case
OpenClicky's `AccessibleElementLocator` exists to disambiguate, and where the model's 30–100 px
grounding error bites.

### 4.5 The novel measure: seeded misgrounding

In a predetermined subset of trials the agent acts on a **plausible but wrong** target. Record:

- **Detection rate** — did the participant notice at all?
- **Detection latency** — actions elapsed between the error and its discovery.
- **Repair strategy** — coded against Schegloff, Jefferson & Sacks's self/other-initiated,
  self/other-repair taxonomy (1977), extended with the strategies Alharbi et al. observed.
- **Attribution** — did the participant blame the system or themselves? Sakib et al.
  (arXiv:2604.00187) report BLV self-blame for AI failure; whether that survives *action* errors is
  untested and is a publishable finding either way.

**Ethics.** Seeded errors require: explicit consent language stating the system makes mistakes and
that some are introduced deliberately; no consequential action that cannot be reversed by the
researcher; full debrief; pre-registration of the seeding schedule. Run this past the IRB explicitly
— it is the part most likely to attract questions.

### 4.6 Measures

| Measure | Instrument | Note |
|---|---|---|
| Task success | Binary + partial credit, two coders | Report inter-rater agreement (Krippendorff's α) |
| Time on task | Logged | |
| Detection rate / latency | Logged + video | Primary novel measure |
| Workload | NASA-TLX | Administer **verbally**; no visual scales |
| Trust | Jian et al. trust-in-automation scale | Verbal administration; see §7 |
| ASR accuracy | WER per language condition | Objective, links to Svarah/SEAME literature |
| Language choice | Free-choice block + interview | Addresses RQ4 |
| Qualitative | Semi-structured post-task interview | Reflexive thematic analysis |

### 4.7 Analysis

Mixed-effects models (participant as random effect) for success, time, and detection latency.
Reflexive thematic analysis for interviews, two coders, codebook reported.

### 4.8 Threats to validity

- **Response bias.** Dell et al. (CHI 2012, doi:10.1145/2207676.2208589) — participants inflate
  ratings toward the researcher who built the system. Use a facilitator who is not an author; state
  clearly that criticism is wanted.
- **Language authenticity.** Sessions in the participant's language need a native-speaking
  facilitator. Do not run a Tamil session through English.
- **Platform familiarity.** Recruit for the OS the participant already uses; do not hand a Windows
  NVDA user a Mac. This is the single biggest confound available.
- **Novelty effects.** Single-session design cannot address these; state it.

---

## 5. Paper outline

**Working title:** *"In My Own Words": Multilingual Voice Delegation and the Verification Problem
for Blind Computer Users*

1. **Introduction** — agents act where assistants described; verification assumptions break; language
   has never been a variable. Contributions: (i) first multilingual study of agentic computer
   control with BLV users; (ii) the first measurement of detection and repair of *actions already
   taken*; (iii) baseline descriptive data on language practice among BLV Singaporeans; (iv) design
   implications for confirmation and repair in a non-visual, multilingual channel.
2. **Related work** — §3.1–3.5 condensed: CUA evaluation without users; agents for BLV users;
   verification of descriptions; multilingual voice interaction; ICTD voice work.
3. **System** — OpenClicky: three lanes (ask / guide / act), AX-tree grounding with OCR fallback,
   cross-platform. Brief; this is not a systems paper.
4. **Method** — §4.
5. **Findings** — organised by RQ. Expect: a language penalty that is larger in the delegated mode
   than the guided mode; detection latency dominated by irreversibility; repair strategies that
   fall back on sighted help.
6. **Discussion** — verification as a serial-channel cost; where to reintroduce inspection; the
   shared-substrate argument (agent inherits the screen reader's inaccessibility; Screen2AX's 33%);
   what multilingual agentic access would require.
7. **Limitations** — single site, single session, n=16, seeded errors are not natural errors.
8. **Conclusion.**

---

## 6. Plan and open questions

### 6.1 Timeline

| When | What |
|---|---|
| Sept–Oct 2026 | Ethics application (incl. seeded-error protocol); confirm SAVH / SIT partner; pilot n=2 |
| **10 Nov 2026** | **IUI 2027 demo deadline** — 4 pp. + ≤5 min video, system only, **zero empirical content** |
| Nov–Dec 2026 | Refine protocol from pilot; recruit |
| Jan–Feb 2027 | 16 sessions, in person, multilingual |
| Feb–Mar 2027 | Analysis |
| **April 2027** | **ASSETS 2027 submission** |

### 6.2 Blocking questions

1. **Are the reachable Singapore BLV participants genuinely non-English in practice?** If the
   recruitable population computes in English, RQ1 and RQ4 collapse and the paper must lead with
   RQ2/RQ3. **This must be answered before the protocol is fixed.** A screening survey through SAVH
   would settle it and is itself publishable baseline data (§3.5).
2. **Which platform is participant-ready by January — Mac, Windows, or both?**

### 6.3 Owed engineering

- Measure the fast local-action lane. `scripts/measure-actions.sh` exists; the p95-under-2s
  acceptance number has never been run. Do not repeat "about two seconds" until it has.
- Language-condition support: transcription already accepts an ISO hint (`--language`), and the
  realtime instruction is "Speak English unless the user speaks another language" — emergent, not
  engineered. Decide whether the study tests off-the-shelf multilingual capability (defensible and
  cheaper) or an engineered path.

### 6.4 The deferred second paper

Low digital literacy as an access barrier belongs at ICTD / COMPASS / CHI, not ASSETS — ASSETS is
disability-scoped and literacy work appears there only intersected with disability. Framing low
literacy *as* a disability will draw pushback from disability studies reviewers. The defensible
framing is the social model: the visual-spatial GUI contract disables different people differently.
That argument, spanning both populations, is a CHI 2028 synthesis once both studies exist.

---

## 7. Citation hygiene

Two errors caught during verification, both common in the wild:

- **"Everyone has an accent"** is **Markl & Lai, Interspeech 2023** (doi:10.21437/interspeech.2023-1847),
  *not* Vashistha. His publication list contains no accent/ASR-bias paper.
- **Sangeet Swara** is **CHI 2015** (doi:10.1145/2702123.2702191), not ASSETS. The ASSETS 2015 paper
  by that group is the separate *Social Media Platforms for Low-Income Blind People in India*.
- **Podsiadło & Chahar** (Interspeech 2016) — co-author is Chahar, frequently miscited.

**Verify before citing:** the Jian et al. trust scale reference (methodology sweep failed to a rate
limit — the instrument is standard but pull the exact citation); Washington Post "The Accent Gap"
(403, statistics unread); Xurxe Toivo García's UX Collective article (403); IMDA National Speech
Corpus per-language coverage; the JOIV Malaysian e-book paper; ScreenAudit (seen second-hand only).

**Methodology norms in §4.3 and §4.6 are inferred from sample sizes visible in the published papers
cited above, not from a completed methodology sweep.** Re-run that search before finalising the
protocol.
