# Skills attribution

The skills in this directory were ported from the skill bundle shipped inside
HeyClicky v1.0.48 (`ClickyBundledSkills/`, recovered on 2026-09-02; see
`../REVERSE-ENGINEERING.md` §9) and rebranded for OpenClicky by
`../scripts/port-skills.mjs`. Re-run `npm run port-skills` to regenerate them.

- `openclicky-*` skills (artifacts, build-preview, creative-studio,
  dev-setup-doctor, email-assistant, google-workspace, repo-operator,
  research-report) were `clicky-*` in the original bundle; the `name:`
  frontmatter was updated to match the new directory names.
- `cua-driver`, `doc`, `frontend-design`, `obsidian`, `pdf`, `spreadsheet`,
  and `vercel-deploy` keep their original names.
- `vercel-deploy` carries its own `LICENSE.txt` and `ATTRIBUTION.md`.
- `ModelInstructions.md` is the ported agent behavior contract
  (`../reference/clicky-model-instructions-verbatim.md`), rebranded only.
- The proprietary `powerpoint` skill is intentionally NOT included.

Skill format: Hermes-style `SKILL.md` (YAML frontmatter with `name` and
`description`, then a Markdown body). The Hermes Agent skills referenced in the
reverse-engineering notes are MIT-licensed by Nous Research
(`../reference/hermes-skill-license.txt`) but are not vendored in this cut.
