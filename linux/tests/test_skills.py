"""Skills in the talk prompt: the repository's app skills, the matcher, the budgets and the library."""

import json

from openclicky_linux import skills
from openclicky_linux.ask import AskExtras, Conversation, Screenshot, answer_request
from openclicky_linux.config import LinuxSettings
from openclicky_linux.skills import FrontApp, SkillLibrary, match_app_skill, parse_skill, prompt_block


def skill(skill_id, apps=(), sites=(), body="notes", surfaces="[talk]"):
    markdown = (f"---\nname: {skill_id.title()}\ndescription: about {skill_id}\napps: [{', '.join(apps)}]\n"
                f"sites: [{', '.join(sites)}]\nsurfaces: {surfaces}\n---\n\n{body}\n")
    return parse_skill(markdown, skill_id)


def test_the_repositorys_app_skills_are_read_and_templates_left_out():
    loaded = {found.id: found for found in skills.load_app_skills()}
    assert {"google-chrome", "vscode", "slack", "gmail"} <= set(loaded)
    assert "_template" not in loaded
    assert loaded["slack"].apps == ("com.tinyspeck.slackmacGap",) and loaded["slack"].sites == ("app.slack.com",)
    assert loaded["slack"].is_for_talk and loaded["slack"].body.startswith("## Layout")


def test_a_skill_needs_a_name_and_a_description():
    assert parse_skill("---\nname: X\n---\nbody", "x") is None
    assert parse_skill("no frontmatter", "x") is None
    parsed = parse_skill("﻿---\nname: 'Quoted'\ndescription: \"d\"\n---\nbody", "x")
    assert parsed.name == "Quoted" and parsed.surfaces == frozenset({"talk", "agent"})


def test_the_window_class_finds_the_app_skill_for_the_same_app_on_linux():
    available = [skill("google-chrome", apps=["com.google.Chrome"]), skill("vscode", apps=["com.microsoft.VSCode"]),
                 skill("gmail", sites=["mail.google.com"]), skill("google", sites=["google.com"])]
    assert match_app_skill(FrontApp("Code", "main.py - openclicky"), available).id == "vscode"
    assert match_app_skill(FrontApp("Google-chrome", "New Tab - Google Chrome"), available).id == "google-chrome"
    # A site in a browser's title wins over the browser's own skill; the most specific site wins.
    assert match_app_skill(FrontApp("Google-chrome", "Inbox - mail.google.com - Google Chrome"), available).id == "gmail"
    assert match_app_skill(FrontApp("firefox", None, url_host="docs.google.com"), available).id == "google"
    # Only a browser's title is searched: a terminal titled with a site keeps no site skill.
    assert match_app_skill(FrontApp("gnome-terminal-server", "ssh mail.google.com"), available) is None
    assert match_app_skill(FrontApp(None, None), available) is None


def test_the_prompt_block_keeps_whole_active_skills_within_budget_and_truncates_the_app_skill():
    active = [skill("tone", body="be brief"), skill("huge", body="x" * 7000), skill("agent-only", surfaces="[agent]")]
    block = prompt_block(active, skill("vscode", body="y" * 5000), FrontApp("Code"))
    assert "## Skill: Tone\nbe brief" in block
    assert "Huge" not in block and "Agent-Only" not in block
    app_part = block.split("## The app in front: Vscode (Code)\n")[1]
    assert len(app_part) == skills.APP_SKILL_BUDGET and app_part.endswith("…")


def test_the_library_is_switched_on_as_the_command_line_tool_does(tmp_path):
    library = SkillLibrary(tmp_path)
    (tmp_path / "library" / "tone").mkdir(parents=True)
    (tmp_path / "library" / "tone" / "SKILL.md").write_text("---\nname: Tone\ndescription: d\nsurfaces: [talk]\n---\nbe brief")
    library.set_active("tone", True)
    assert json.loads((tmp_path / "activations.json").read_text())["active"] == ["tone"]
    assert (tmp_path / "active" / "tone").is_symlink()
    assert [found.id for found in library.active_talk_skills()] == ["tone"]
    library.set_active("tone", False)
    assert library.active_ids() == [] and not (tmp_path / "active" / "tone").exists()


def test_the_skills_go_after_the_instructions():
    built = answer_request("hi", Screenshot(b"png", 10, 10, 1.0), Conversation(), LinuxSettings(anthropic_api_key="k"),
                           extras=AskExtras(skills_block="## The app in front: Slack\nnotes"))
    assert built.system_prompt.rstrip().endswith("## The app in front: Slack\nnotes")
