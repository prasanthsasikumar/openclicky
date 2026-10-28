"""The island's words and choices, and its plugins' readers, with fixtures (no desktop needed)."""

import json
import time

from openclicky_linux import island_plugins
from openclicky_linux.island_plugins import (Activity, AgentSession, ShelfItem, SystemSampler, SystemSnapshot, agent_sessions,
                                             parse_claude_tail, parse_codex_tail, parse_usage, peek_line, reset_text,
                                             shelf_items_from_drop, system_figures, track_from)


def island():
    import gi
    gi.require_version("Gtk", "3.0")
    from openclicky_linux import island as island_module
    return island_module


def test_the_busy_strip_says_what_is_going_on():
    module = island()
    Moment = module.Moment
    assert module.busy_text(Moment()) is None
    assert module.busy_text(Moment(dictating=True)) == "dictating"
    assert module.busy_text(Moment(dictation_working=True)) == "typing it in"
    assert module.busy_text(Moment(answering=True)) == "looking at your screen"
    assert module.busy_text(Moment(answering=True, agent_activity="running: zip")) == "working"
    assert module.busy_text(Moment(speaking=True, voice_name="Kokoro (on this computer)")) == "speaking · Kokoro (on this computer)"
    assert module.busy_text(Moment(asking_status="sending your screen · esc to cancel", answering=True)) == "sending your screen · esc to cancel"
    assert module.pointer_status("onShake", False, short=True) == "shake for the pointer"
    assert module.pointer_status("always", True) == "pointer is docked"


def test_the_moment_picks_the_component_unless_the_person_did(monkeypatch):
    module = island()
    monkeypatch.setattr(module.preferences, "get", lambda key, default=None: default)
    available = [module.SHORTCUTS, module.TRANSCRIPT, module.AGENT]
    now = time.time()
    assert module.component_for(module.Moment(), available, None, now) == module.SHORTCUTS
    assert module.component_for(module.Moment(last_take="hi", last_take_at=now - 2), available, None, now) == module.TRANSCRIPT
    assert module.component_for(module.Moment(last_take="hi", last_take_at=now - 20), available, None, now) == module.SHORTCUTS
    assert module.component_for(module.Moment(agent_activity="editing a.py"), available, None, now) == module.AGENT
    assert module.component_for(module.Moment(agent_activity="x"), available, module.SHORTCUTS, now) == module.SHORTCUTS
    assert b"rgba(16,16,18,1.00)" in module.glass_css(0.0) and b"rgba(16,16,18,0.45)" in module.glass_css(1.0)


def test_system_figures_from_proc_and_sys(tmp_path):
    (tmp_path / "proc").mkdir()
    (tmp_path / "proc" / "meminfo").write_text("MemTotal: 16777216 kB\nMemAvailable: 9437184 kB\n")
    (tmp_path / "proc" / "stat").write_text("cpu 100 0 100 800 0 0 0 0 0 0\n")
    battery = tmp_path / "power" / "BAT0"
    battery.mkdir(parents=True)
    (battery / "capacity").write_text("18\n")
    (battery / "status").write_text("Discharging\n")
    sampler = SystemSampler(tmp_path / "proc", tmp_path / "power")
    first = sampler.sample()
    (tmp_path / "proc" / "stat").write_text("cpu 150 0 150 900 0 0 0 0 0 0\n")
    second = sampler.sample()
    assert first.cpu_fraction == 0.0 and round(second.cpu_fraction, 2) == 0.5
    figures = {name: (fraction, figure) for name, fraction, figure in system_figures(second)}
    assert figures["memory"][1] == "7 / 16 GB" and figures["battery"][1] == "18%"
    assert island_plugins.is_warning("battery", 0.18) and not island_plugins.is_warning("cpu", 0.5)
    charging = system_figures(SystemSnapshot(0.1, 1, 2, 1, 2, 86, True))
    assert charging[-1][2] == "86% ⚡"


def test_claude_code_and_codex_logs_say_what_each_session_is_doing(tmp_path):
    claude = "\n".join(json.dumps(record) for record in (
        {"type": "user", "cwd": "/home/me/site"},
        {"type": "ai-title", "aiTitle": "Fix the hero"},
        {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Edit", "input": {"file_path": "/home/me/site/Hero.tsx"}}]}}))
    activity, title, cwd = parse_claude_tail(claude)
    assert activity.line == "editing Hero.tsx" and title == "Fix the hero" and cwd == "/home/me/site"
    finished = json.dumps({"type": "assistant", "message": {"stop_reason": "end_turn", "content": [{"type": "text", "text": "done"}]}})
    assert parse_claude_tail(finished)[0].line == "done"
    codex = "\n".join(json.dumps(record) for record in (
        {"type": "session_meta", "payload": {"cwd": "/home/me/api"}},
        {"type": "response_item", "payload": {"type": "function_call", "name": "shell", "arguments": json.dumps({"command": ["git", "status"]})}}))
    activity, _title, cwd = parse_codex_tail(codex)
    assert activity.line == "running: git status" and cwd == "/home/me/api"
    projects = tmp_path / "claude" / "projects" / "-home-me-site"
    projects.mkdir(parents=True)
    (projects / "a.jsonl").write_text(claude)
    sessions = agent_sessions(tmp_path / "claude" / "projects", tmp_path / "codex")
    assert [session.project for session in sessions] == ["site"]
    assert peek_line(sessions) == "claude · editing Hero.tsx"
    assert peek_line([AgentSession("x", "codex", "api", None, Activity("idle"), time.time())]) is None


def test_claude_usage_and_its_reset_time():
    usage = parse_usage(json.dumps({"five_hour": {"utilization": 42.0, "resets_at": "2026-10-26T12:00:00Z"},
                                    "seven_day": {"utilization": 10.0}}).encode())
    assert usage[0].fraction == 0.42 and usage[1].resets_at is None
    assert reset_text(1000 + 2 * 3600 + 600, now=1000) == "resets 2 h 10 m" and reset_text(1030, now=1000) == "resets soon"
    assert parse_usage(b"{}") is None


def test_now_playing_and_the_shelf(tmp_path):
    track = track_from("org.mpris.MediaPlayer2.spotify", "Playing", {"xesam:title": "Clair de lune", "xesam:artist": ["Debussy"]})
    assert track.playing and track.line == "♪ Clair de lune · Debussy"
    assert track_from("x", "Stopped", {}) is None
    [dropped] = shelf_items_from_drop(["file:///home/me/a%20b.pdf"], None)
    assert (dropped.kind, dropped.value, dropped.label) == ("file", "/home/me/a b.pdf", "a b.pdf")
    assert shelf_items_from_drop([], "https://example.com/x")[0].kind == "link"
    assert shelf_items_from_drop([], "a note")[0].kind == "note"
    shelf = island_plugins.Shelf(tmp_path / "shelf")
    shelf.add([ShelfItem("note", "first")])
    shelf.add([ShelfItem("link", "https://example.com"), ShelfItem("note", "first")])
    assert [item.value for item in shelf.items()] == ["https://example.com", "first"]
    shelf.remove(ShelfItem("note", "first"))
    assert [item.value for item in shelf.items()] == ["https://example.com"]
