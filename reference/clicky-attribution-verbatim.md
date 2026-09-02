# Hermes Agent skills

The `SKILL.md` files in this directory are vendored unmodified from
[NousResearch/hermes-agent](https://github.com/nousresearch/hermes-agent),
licensed under the MIT License (see `LICENSE`). They power the
"What should Clicky be good at?" picker in the pre-sign-in onboarding.

When a user toggles a skill on, the skill ID is recorded in
`UserDefaults` under the key referenced by
`HermesSkillCatalog.userDefaultsSelectedIDsKey`. Backend wiring (the
piece that actually activates each skill against Clicky's Codex
runtime) is intentionally not included yet.

`blender.md` is vendored from
[dev-gom/claude-code-marketplace](https://github.com/dev-gom/claude-code-marketplace)
plugin `blender-toolkit/skills/SKILL.md`, MIT-licensed.

To refresh the bundle, re-run the curl loop documented in
`docs/refresh-hermes-skills.md` (TODO) — for now this is a one-time
copy committed by hand.
