"""The agent lane (the CLI's events, the gate, a Codex run resumed on its thread, cancelling) and the
quick actions (typed arguments only, the Mac's sentences), with a stand-in CLI and desktop."""

from __future__ import annotations

import json
import stat
import time
from pathlib import Path

import pytest

from openclicky_linux import actions, agent, lanes
from openclicky_linux.ask import Conversation, Screenshot, ToolCall
from openclicky_linux.config import LinuxSettings

# MARK: the CLI's events


def test_events_give_the_lane_the_milestones_and_the_result():
    seen: list[str] = []
    lines = [json.dumps({"type": "lane", "lane": "agent"}), json.dumps({"type": "event", "line": "editing notes.md"}),
             json.dumps({"type": "delta", "text": "made "}), json.dumps({"type": "delta", "text": "it"}),
             json.dumps({"type": "result", "threadId": "thread-7", "artifacts": ["/w/notes.md"], "status": "completed", "finalMessage": "made notes.md."})]
    result = agent.parse_events(lines, on_event=seen.append)
    assert seen == ["editing notes.md"]
    assert (result.lane, result.thread_id, result.artifacts, result.status, result.text) == ("agent", "thread-7", ["/w/notes.md"], "completed", "made notes.md.")
    assert agent.spoken_result(result) == "made notes.md. i saved 1 file."


def test_a_run_with_no_final_message_says_what_streamed_or_that_it_stopped():
    # Streamed pieces join as they came; a line that is not an event is a line of the answer.
    assert agent.parse_events([json.dumps({"type": "delta", "text": "half, "}), "not json at all"]).text == "half, not json at all\n"
    stopped = agent.parse_events([json.dumps({"type": "result", "status": "interrupted", "error": "the sandbox refused"})])
    assert agent.spoken_result(stopped) == "the agent stopped early: the sandbox refused"
    assert agent.spoken_result(agent.AgentRunResult(status="completed")) == "done."


def test_own_keys_reach_the_cli_under_openclickys_names_never_the_vendors(monkeypatch):
    settings = LinuxSettings(openai_api_key="sk-own", anthropic_api_key="sk-ant-own", account_token="account", backend_url="https://b")
    environment = agent.cli_environment(settings, raw={"workspace": "/tmp/oc-work"}, base={"OPENAI_API_KEY": "leak", "PATH": "/usr/bin"})
    assert environment["OPENCLICKY_OPENAI_KEY"] == "sk-own" and environment["OPENCLICKY_ANTHROPIC_KEY"] == "sk-ant-own"
    assert "OPENAI_API_KEY" not in environment and "ANTHROPIC_API_KEY" not in environment
    assert environment["OPENCLICKY_TOKEN"] == "account" and environment["BACKEND_URL"] == "https://b"
    assert environment["OPENCLICKY_WORKSPACE"] == "/tmp/oc-work"
    assert environment["PATH"].endswith(":/usr/bin") and "/.local/bin" in environment["PATH"]


def test_the_cli_command_comes_from_the_environment_then_shell_json():
    assert agent.cli_command(raw={}, environment={}) == ["openclicky"]
    assert agent.cli_command(raw={"cliCommand": ["node", "/r/agent/dist/cli.js"]}, environment={}) == ["node", "/r/agent/dist/cli.js"]
    assert agent.cli_command(raw={"cliCommand": ["x"]}, environment={"OPENCLICKY_SHELL_CLI": "node cli.js"}) == ["node", "cli.js"]


@pytest.fixture
def stand_in_cli(tmp_path, monkeypatch):
    """An `openclicky` that prints the events a real run prints, and remembers its arguments."""
    script = tmp_path / "openclicky"
    script.write_text("""#!/usr/bin/env python3
import json, sys, time, os
open(os.environ["ARGUMENTS_LOG"], "a").write(json.dumps(sys.argv[1:]) + "\\n")
if "--gate-only" in sys.argv:
    print(json.dumps({"type": "lane", "lane": "agent" if "folder" in sys.argv[2] else "ask"}))
    sys.exit(0)
if "slow" in sys.argv[2]:
    time.sleep(30)
print(json.dumps({"type": "event", "line": "creating the folder"}), flush=True)
print("▸ thinking", file=sys.stderr, flush=True)
print(json.dumps({"type": "result", "threadId": "thread-1", "artifacts": [], "status": "completed", "finalMessage": "made it."}))
""")
    script.chmod(script.stat().st_mode | stat.S_IEXEC)
    log = tmp_path / "arguments.jsonl"
    monkeypatch.setenv("OPENCLICKY_SHELL_CLI", str(script))
    monkeypatch.setenv("ARGUMENTS_LOG", str(log))
    monkeypatch.setenv("OPENCLICKY_WORKSPACE", str(tmp_path / "workspace"))
    monkeypatch.setenv("OPENCLICKY_HOME", str(tmp_path / "home"))
    return log


def logged_arguments(log: Path) -> list[list[str]]:
    return [json.loads(line) for line in log.read_text().splitlines()]


def test_the_gate_and_a_run_go_through_the_cli_and_the_next_task_resumes_the_thread(stand_in_cli):
    settings = LinuxSettings(anthropic_api_key="sk-ant-own")
    progress: list[str] = []
    routing = lanes.Lanes(agent=agent.AgentClient(lambda: settings), settings_reader=lambda: settings,
                          tool_installed=lambda _settings: True, agent_mode=lambda: True,
                          ask_model=lambda *arguments, **options: pytest.fail("the ask lane was used"))
    screen = Screenshot(b"\x89PNG fake", 10, 10, 1.0)
    answer = routing.answer("make a folder called hello", [screen], Conversation(), progress=progress.append)
    assert answer == "made it."
    assert progress[:2] == ["deciding…", "starting agent…"]
    assert "creating the folder" in progress and "thinking" in progress
    assert routing.last_thread_id == "thread-1"
    routing.answer("now another folder", [screen], Conversation())
    gate, run, _gate_again, second_run = logged_arguments(stand_in_cli)
    assert gate[:1] == ["do"] and "--gate-only" in gate
    assert run[:2] == ["run", "make a folder called hello"] and "--image" in run and "--thread" not in run
    # The screenshot handed to the agent is removed once the run ends.
    assert not Path(run[run.index("--image") + 1]).exists()
    assert second_run[second_run.index("--thread") + 1] == "thread-1"


def test_a_question_the_gate_keeps_goes_to_the_ask_lane_without_quick_actions(stand_in_cli):
    settings = LinuxSettings(anthropic_api_key="sk-ant-own")
    offered = []

    def ask_model(question, screenshot, conversation, settings, tools=None, extra_instructions="", **_streaming):
        offered.append(tools)
        return "[POINT:5,5:save] that one.", []
    routing = lanes.Lanes(agent=agent.AgentClient(lambda: settings), settings_reader=lambda: settings,
                          tool_installed=lambda _settings: True, agent_mode=lambda: True, ask_model=ask_model)
    assert routing.answer("what is this button?", [], Conversation()) == "[POINT:5,5:save] that one."
    assert routing.lanes_taken == ["ask"] and offered == [None]


def test_esc_ends_an_agent_run(stand_in_cli):
    settings = LinuxSettings(anthropic_api_key="sk-ant-own")
    client = agent.AgentClient(lambda: settings)
    import threading
    outcome = {}

    def run():
        try:
            client.run_agent("a slow task", None, None)
        except agent.AgentError as failure:
            outcome["error"] = str(failure)
    worker = threading.Thread(target=run)
    started = time.monotonic()
    worker.start()
    time.sleep(0.5)
    client.cancel()
    worker.join(timeout=5)
    assert not worker.is_alive() and time.monotonic() - started < 5
    assert "error" in outcome


def test_without_the_cli_the_agent_lane_never_runs_and_says_why(tmp_path, monkeypatch):
    monkeypatch.setenv("OPENCLICKY_SHELL_CLI", str(tmp_path / "no-such-openclicky"))
    settings = LinuxSettings(anthropic_api_key="sk-ant-own")
    assert agent.is_tool_installed(settings) is False
    with pytest.raises(agent.AgentError, match="isn't on this computer"):
        agent.AgentClient(lambda: settings).run_agent("anything", None, None)
    assert agent.AgentClient(lambda: settings).classify_lane("make a folder") == "ask"


# MARK: the quick actions


class StandInDesktop:
    def __init__(self, home: Path) -> None:
        self.done: list[tuple] = []
        self.applications = {"firefox": "Firefox Web Browser"}
        self.desktop = actions.Desktop(
            home=home, workspace=home / "OpenClicky", special_folder=lambda location: home / location.capitalize(),
            find_application=lambda name: self.applications.get(name.lower()), launch_application=lambda app: self.done.append(("launched", app)),
            application_name=lambda app: app, open_url=lambda url: self.done.append(("url", url)),
            reveal=lambda path: self.done.append(("revealed", path)), set_volume=lambda level: self.done.append(("volume", level)) or True,
            media=lambda action: self.done.append(("media", action)) or True)


def test_quick_actions_do_what_was_asked_and_say_so(tmp_path):
    stand_in = StandInDesktop(tmp_path)
    desktop = stand_in.desktop
    assert actions.perform("open_app", {"name": "Firefox"}, desktop).sentence == "Opened Firefox Web Browser."
    assert actions.perform("open_app", {"name": "nothing"}, desktop).sentence == "I couldn't find an app called nothing."
    assert actions.perform("create_folder", {"name": "hello", "location": "desktop"}, desktop).sentence == "Created hello on your Desktop."
    assert (tmp_path / "Desktop" / "hello").is_dir()
    assert actions.perform("create_folder", {"name": "hello", "location": "desktop"}, desktop).sentence == "hello is already on your Desktop."
    # No location: OpenClicky's own folder, never somewhere the person did not name.
    assert actions.perform("create_folder", {"name": "notes"}, desktop).sentence == "Created notes on your OpenClicky folder."
    assert (tmp_path / "OpenClicky" / "notes").is_dir()
    assert actions.perform("reveal_in_files", {"name": "hello", "location": "desktop"}, desktop).did_something
    assert actions.perform("set_volume", {"level": "140"}, desktop).sentence == "Volume set to 100 percent."
    assert actions.perform("media_control", {"action": "next"}, desktop).sentence == "Done."
    assert actions.perform("open_url", {"url": "https://example.com"}, desktop).sentence == "Opened it in your browser."
    assert stand_in.done == [("launched", "Firefox Web Browser"), ("revealed", tmp_path / "Desktop" / "hello"), ("volume", 100),
                             ("media", "next"), ("url", "https://example.com")]


@pytest.mark.parametrize("tool, arguments, sentence", [
    ("create_folder", {"name": "../escape"}, "That name has characters a folder can't have."),
    ("create_folder", {"name": ".hidden"}, "That name has characters a folder can't have."),
    ("create_folder", {"name": "x", "location": "/etc"}, "I don't know where /etc is."),
    ("open_app", {"name": "../../bin/sh"}, "That app name has characters an app name can't have."),
    ("open_url", {"url": "file:///etc/passwd"}, "That doesn't look like a web address I can open."),
    ("open_url", {"url": "javascript:alert(1)"}, "That doesn't look like a web address I can open."),
    ("media_control", {"action": "eject"}, "That didn't work: I don't know how to eject."),
    ("run_command", {"command": "rm -rf ~"}, "That needs your own key for now."),
])
def test_quick_actions_refuse_anything_outside_their_typed_arguments(tmp_path, tool, arguments, sentence):
    stand_in = StandInDesktop(tmp_path)
    outcome = actions.perform(tool, arguments, stand_in.desktop)
    assert (outcome.sentence, outcome.did_something) == (sentence, False)
    assert stand_in.done == []
    assert not any(path.name in ("escape", ".hidden") for path in tmp_path.rglob("*"))


def test_an_app_is_found_by_its_name_its_id_or_its_command():
    applications = [("Firefox Web Browser", "firefox.desktop", "firefox %u"), ("Visual Studio Code", "code.desktop", "/usr/share/code/code --unity-launch %F"),
                    ("Files", "org.gnome.Nautilus.desktop", "nautilus --new-window %U")]
    assert actions.application_matches("files", applications) == 2
    assert actions.application_matches("firefox", applications) == 0
    assert actions.application_matches("code", applications) == 1
    assert actions.application_matches("nautilus", applications) == 2
    assert actions.application_matches("visual studio", applications) == 1
    assert actions.application_matches("spotify", applications) is None


def test_the_volume_goes_through_pipewire_else_pulseaudio(monkeypatch):
    monkeypatch.setattr(actions.shutil, "which", lambda name: f"/usr/bin/{name}")
    assert actions.volume_command(35) == ["wpctl", "set-volume", "@DEFAULT_AUDIO_SINK@", "0.35"]
    monkeypatch.setattr(actions.shutil, "which", lambda name: "/usr/bin/pactl" if name == "pactl" else None)
    assert actions.volume_command(35) == ["pactl", "set-sink-volume", "@DEFAULT_SINK@", "35%"]


def test_the_ask_lane_offers_quick_actions_where_the_agent_lane_does_not_run_and_says_what_they_did(tmp_path):
    stand_in = StandInDesktop(tmp_path)
    offered = []

    def ask_model(question, screenshot, conversation, settings, tools=None, extra_instructions="", **_streaming):
        offered.append(([tool["name"] for tool in tools or []], extra_instructions))
        return "sure.[NEXT]", [ToolCall("create_folder", {"name": "trip", "location": "documents"})]
    routing = lanes.Lanes(settings_reader=lambda: LinuxSettings(account_token="account"), tool_installed=lambda _settings: False,
                          agent_mode=lambda: True, ask_model=ask_model, desktop=lambda: stand_in.desktop)
    assert routing.answer("make a folder called trip in my documents", [], Conversation()) == "sure. Created trip on your Documents folder.[NEXT]"
    names, instructions = offered[0]
    assert names == ["open_app", "open_url", "create_folder", "reveal_in_files", "set_volume", "media_control"]
    assert instructions == actions.TOOLS_INSTRUCTION


def test_what_a_quick_action_did_goes_before_the_answers_trailing_tags():
    assert lanes.with_outcomes("opening it. [POINT:4,4:dock]", ["Opened Files."]) == "opening it. Opened Files. [POINT:4,4:dock]"
    assert lanes.with_outcomes("", ["Opened Files."]) == "Opened Files."
    assert lanes.with_outcomes("no tools here.", []) == "no tools here."


def test_tool_calls_are_read_from_each_vendors_stream():
    from openclicky_linux import answers

    anthropic = answers.AnthropicStream()
    for event in ({"type": "content_block_start", "index": 1, "content_block": {"type": "tool_use", "name": "open_app"}},
                  {"type": "content_block_delta", "index": 1, "delta": {"type": "input_json_delta", "partial_json": "{\"name\": "}},
                  {"type": "content_block_delta", "index": 1, "delta": {"type": "input_json_delta", "partial_json": "\"Files\"}"}}):
        anthropic.consume("data: " + json.dumps(event))
    assert anthropic.tool_calls == [ToolCall("open_app", {"name": "Files"})]
    openai = answers.OpenAIStream()
    for chunk in ({"index": 0, "function": {"name": "set_volume", "arguments": "{\"level\""}}, {"index": 0, "function": {"arguments": ": 30}"}},
                  {"index": 1, "function": {"name": "open_url", "arguments": "not json"}}):
        openai.consume("data: " + json.dumps({"choices": [{"delta": {"tool_calls": [chunk]}}]}))
    assert openai.tool_calls == [ToolCall("set_volume", {"level": 30}), ToolCall("open_url", {})]
    gemini = answers.GeminiStream()
    gemini.consume("data: " + json.dumps({"candidates": [{"content": {"parts": [{"functionCall": {"name": "media_control", "args": {"action": "next"}}}]}}]}))
    assert gemini.tool_calls == [ToolCall("media_control", {"action": "next"})]


def test_tools_go_to_each_vendor_in_its_own_shape():
    from openclicky_linux.ask import request_for

    screen = Screenshot(b"png", 10, 10, 1.0)
    tools = actions.tool_definitions()
    anthropic = json.loads(request_for("q", screen, Conversation(), LinuxSettings(anthropic_api_key="k"), tools=tools).request.data)
    assert [tool["name"] for tool in anthropic["tools"]][:2] == ["open_app", "open_url"]
    assert actions.TOOLS_INSTRUCTION not in anthropic["system"]
    openai = json.loads(request_for("q", screen, Conversation(), LinuxSettings(openai_api_key="k"), tools=tools,
                                    extra_instructions=actions.TOOLS_INSTRUCTION).request.data)
    assert openai["tools"][0]["type"] == "function" and openai["tools"][0]["function"]["name"] == "open_app"
    assert actions.TOOLS_INSTRUCTION in openai["messages"][0]["content"]
    plain = json.loads(request_for("q", screen, Conversation(), LinuxSettings(anthropic_api_key="k")).request.data)
    assert "tools" not in plain


def test_the_rewrite_tool_writes_in_place_and_its_sentence_follows_the_answer():
    from openclicky_linux.ask import AskExtras
    from openclicky_linux.answers import ToolCall
    from openclicky_linux.config import LinuxSettings
    from openclicky_linux.lanes import Lanes
    from openclicky_linux.ask import Conversation, Screenshot
    offered = []

    def ask_model(question, screenshot, conversation, settings, tools=None, extra_instructions="", **_streaming):
        offered.append([tool["name"] for tool in tools or []])
        return "made it politer.", [ToolCall("write_in_place", {"text": "Could you send it?"})]
    written = []
    lanes = Lanes(settings_reader=lambda: LinuxSettings(anthropic_api_key="k"), ask_model=ask_model,
                  tool_installed=lambda _settings: False, agent_mode=lambda: False)
    extras = AskExtras(tools=({"name": "write_in_place"},), write_in_place=lambda text: written.append(text) or "it's in place.")
    answer = lanes.answer("rephrase this", Screenshot(b"png", 10, 10, 1.0), Conversation(), extras=extras)
    assert answer == "made it politer. it's in place." and written == ["Could you send it?"]
    assert "write_in_place" in offered[0] and "open_app" in offered[0]
