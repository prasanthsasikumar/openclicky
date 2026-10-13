import json
import math
import stat

import pytest

from openclicky_linux.ask import (
    ANTHROPIC_MODEL, OPENAI_MODEL, AskError, Conversation, Screenshot, answer_text, request_for,
)
from openclicky_linux.config import LinuxSettings, load_settings, save_settings
from openclicky_linux.gestures import DictationGestureRecognizer, TextComposerRequested
from openclicky_linux.pointing import flight_position, flight_seconds, parse_pointing, screenshot_point_to_screen

SCREENSHOT = Screenshot(png_bytes=b"\x89PNG", width=1280, height=800, scale=1.0)


# MARK: the [POINT] tag (the Mac app's parser, same cases)

def test_a_leading_tag_points_and_is_not_shown():
    result = parse_pointing("[POINT:1100,42:color inspector] open the color inspector up top.")
    assert (result.x, result.y, result.label) == (1100, 42, "color inspector")
    assert result.text == "open the color inspector up top."


def test_point_none_points_nowhere():
    result = parse_pointing("[POINT:none] html is the skeleton of a web page.")
    assert not result.has_point
    assert result.text == "html is the skeleton of a web page."


def test_a_trailing_tag_and_a_screen_number():
    result = parse_pointing("that's on your other monitor. [POINT:400,300:terminal:screen2]")
    assert (result.x, result.y, result.screen_number) == (400, 300, 2)
    assert result.text == "that's on your other monitor."


def test_two_tags_with_nothing_between_do_not_loop():
    result = parse_pointing("[POINT:10,20:a][POINT:30,40:b]")
    assert (result.x, result.y) == (10, 20)
    assert result.text == ""


def test_a_malformed_leading_tag_is_never_shown():
    result = parse_pointing("[POINT:here] click it")
    assert not result.has_point
    assert result.text == "click it"


def test_no_tag_keeps_the_whole_answer():
    assert parse_pointing("  just words  ").text == "just words"


def test_screenshot_points_map_back_to_the_screen_and_stay_on_it():
    assert screenshot_point_to_screen(784, 490, 0.5, 2560, 1600) == (1568, 980)
    assert screenshot_point_to_screen(5000, -3, 1.0, 1280, 800) == (1279, 0)


def test_a_flight_starts_and_ends_where_it_should_and_arcs_upward():
    start, end = (100.0, 500.0), (900.0, 500.0)
    assert flight_position(start, end, 0) == start
    assert flight_position(start, end, 1) == pytest.approx(end)
    assert flight_position(start, end, 0.5)[1] < 500
    assert 0.45 <= flight_seconds(start, end) <= 1.2
    assert flight_seconds(start, start) == 0.45
    assert math.isclose(flight_seconds((0, 0), (10_000, 0)), 1.2)


# MARK: asking

def test_an_anthropic_key_goes_straight_to_anthropic_with_the_screenshot():
    conversation = Conversation()
    conversation.remember("what is this?", "[POINT:none] a terminal.")
    routed = request_for("where is the menu?", SCREENSHOT, conversation, LinuxSettings(anthropic_api_key="sk-ant", openai_api_key="sk-oa", account_token="tok"))
    assert routed.kind == "anthropic"
    assert routed.request.full_url == "https://api.anthropic.com/v1/messages"
    assert routed.request.get_header("X-api-key") == "sk-ant"
    body = json.loads(routed.request.data)
    assert body["model"] == ANTHROPIC_MODEL
    assert body["messages"][0] == {"role": "user", "content": "what is this?"}
    last = body["messages"][-1]["content"]
    assert last[0]["type"] == "image" and last[0]["source"]["media_type"] == "image/png"
    assert "1280x800" in last[1]["text"] and last[1]["text"].endswith("where is the menu?")
    assert "[POINT:x,y:label]" in body["system"]


def test_an_openai_key_alone_goes_to_openai_chat():
    routed = request_for("hi", SCREENSHOT, Conversation(), LinuxSettings(openai_api_key="sk-oa", account_token="tok"))
    assert routed.kind == "openai"
    assert routed.request.get_header("Authorization") == "Bearer sk-oa"
    body = json.loads(routed.request.data)
    assert body["model"] == OPENAI_MODEL
    assert body["messages"][0]["role"] == "system"
    assert body["messages"][-1]["content"][0]["image_url"]["url"].startswith("data:image/png;base64,")


def test_an_account_goes_to_the_backends_chat_never_with_own_keys():
    routed = request_for("hi", SCREENSHOT, Conversation(), LinuxSettings(account_token="tok", backend_url="http://localhost:8787"))
    assert routed.kind == "backend"
    assert routed.request.full_url == "http://localhost:8787/chat"
    assert routed.request.get_header("Authorization") == "Bearer tok"
    assert routed.request.get_header("X-api-key") is None


def test_nothing_configured_says_what_to_add():
    with pytest.raises(AskError, match="anthropic, openrouter or openai key, or an openclicky account"):
        request_for("hi", SCREENSHOT, Conversation(), LinuxSettings())


def test_a_language_other_than_english_is_asked_for():
    routed = request_for("hi", SCREENSHOT, Conversation(), LinuxSettings(anthropic_api_key="k", language_code="hi-IN"))
    assert "hi-IN" in json.loads(routed.request.data)["system"]


def test_answers_are_read_from_each_vendors_shape():
    assert answer_text("anthropic", {"content": [{"type": "text", "text": " [POINT:none] hi "}]}) == "[POINT:none] hi"
    assert answer_text("backend", {"content": [{"type": "tool_use"}, {"type": "text", "text": "ok"}]}) == "ok"
    assert answer_text("openai", {"choices": [{"message": {"content": "ok "}}]}) == "ok"
    assert answer_text("openai", {}) == ""


def test_the_conversation_keeps_the_last_ten_exchanges():
    conversation = Conversation()
    for index in range(14):
        conversation.remember(f"q{index}", f"a{index}")
    assert len(conversation.exchanges) == 10
    assert conversation.exchanges[0] == ("q4", "a4")


# MARK: settings

def test_saving_keeps_every_other_key_and_is_private(tmp_path):
    shell_json = tmp_path / "shell.json"
    shell_json.write_text(json.dumps({"refreshToken": "keep-me", "speechVoice": "kokoro", "openaiApiKey": "old"}))
    save_settings({"openai_api_key": " sk-new ", "anthropic_api_key": "", "dictation_key": "Alt_R",
                   "pointer_presence": "whenAnswering", "backend_url": "https://api.openclicky.flowsxr.com/"}, shell_json)
    saved = json.loads(shell_json.read_text())
    assert saved == {"refreshToken": "keep-me", "speechVoice": "kokoro", "openaiApiKey": "sk-new",
                     "linuxDictationKey": "Alt_R", "linuxPointer": "whenAnswering"}
    assert stat.S_IMODE(shell_json.stat().st_mode) == 0o600
    settings = load_settings(shell_json, environment={})
    assert settings.pointer_presence == "whenAnswering"
    assert [path.name for path in tmp_path.iterdir()] == ["shell.json"]


def test_an_empty_field_removes_its_key(tmp_path):
    shell_json = tmp_path / "shell.json"
    shell_json.write_text(json.dumps({"token": "tok", "anthropicApiKey": "k"}))
    save_settings({"account_token": "  "}, shell_json)
    assert json.loads(shell_json.read_text()) == {"anthropicApiKey": "k"}


def test_a_broken_shell_json_is_never_overwritten(tmp_path):
    shell_json = tmp_path / "shell.json"
    shell_json.write_text("{not json")
    with pytest.raises(ValueError, match="not valid json"):
        save_settings({"openai_api_key": "sk"}, shell_json)
    assert shell_json.read_text() == "{not json"


# MARK: the ask box gesture

def test_left_ctrl_tapped_twice_opens_the_ask_box():
    recognizer = DictationGestureRecognizer()
    recognizer.composer_key_down(1.0)
    assert recognizer.composer_key_up(1.1) == []
    recognizer.composer_key_down(1.3)
    assert recognizer.composer_key_up(1.4) == [TextComposerRequested()]


def test_ctrl_c_twice_never_opens_the_ask_box():
    recognizer = DictationGestureRecognizer()
    for start in (1.0, 1.3):
        recognizer.composer_key_down(start)
        recognizer.other_key_down(is_escape=False)
        assert recognizer.composer_key_up(start + 0.1) == []


def test_a_held_ctrl_is_not_a_tap():
    recognizer = DictationGestureRecognizer()
    recognizer.composer_key_down(1.0)
    recognizer.composer_key_up(1.1)
    recognizer.composer_key_down(1.2)
    assert recognizer.composer_key_up(1.9) == []


def test_the_pointer_outline_as_pixel_rows():
    from openclicky_linux.pointer_view import POINTER_HEIGHT, POINTER_OUTLINE, POINTER_WIDTH
    from openclicky_linux.x11 import polygon_rows

    rows = polygon_rows(POINTER_OUTLINE, POINTER_WIDTH, POINTER_HEIGHT)
    widths = {y: width for _x, y, width, _height in rows}
    # The tip is the narrowest part, at the top-left; the triangle widens towards its right corner.
    assert rows[0][0] == 0 and widths[0] <= 2
    assert max(widths.values()) > POINTER_WIDTH * 0.6
    assert all(0 <= x and x + width <= POINTER_WIDTH and 0 <= y < POINTER_HEIGHT for x, y, width, _height in rows)
    # A square is every pixel of every row.
    assert polygon_rows([(0, 0), (4, 0), (4, 3), (0, 3)], 4, 3) == [(0, 0, 4, 1), (0, 1, 4, 1), (0, 2, 4, 1)]


def test_settings_name_the_fields_the_environment_overrides_never_their_values():
    from openclicky_linux.config import environment_overrides
    assert environment_overrides({"OPENCLICKY_OPENROUTER_KEY": "sk-or-secret", "BACKEND_URL": " "}) == ["OpenRouter key"]
    assert environment_overrides({}) == []


def test_a_shell_json_that_is_not_an_object_is_never_overwritten(tmp_path):
    shell_json = tmp_path / "shell.json"
    shell_json.write_text("[1, 2]")
    with pytest.raises(ValueError, match="not a json object"):
        save_settings({"openai_api_key": "sk"}, shell_json)
    assert shell_json.read_text() == "[1, 2]"


def test_a_linked_shell_json_stays_a_link(tmp_path):
    real = tmp_path / "dotfiles" / "shell.json"
    real.parent.mkdir()
    real.write_text(json.dumps({"token": "tok"}))
    link = tmp_path / "shell.json"
    link.symlink_to(real)
    save_settings({"language_code": "en-IN"}, link)
    assert link.is_symlink()
    assert json.loads(real.read_text()) == {"token": "tok", "language": "en-IN"}


def test_settings_say_which_keys_do_what_for_the_key_picked():
    import gi
    gi.require_version("Gtk", "3.0")
    from openclicky_linux.windows import shortcut_rows
    left = shortcut_rows("Control_L")
    assert ("Hold", ("Left Ctrl",), "Dictate: speak, then let go. Your words are typed where the cursor is.") in left
    assert ("Hold", ("Left Ctrl", "Left Alt"), "Ask out loud: the pointer answers, shows you where, and guides you step by step.") in left
    assert ("Tap twice", ("Left Ctrl",), "Ask by typing.") in left
    assert not any(how.startswith("Tap, then") for how, _keys, _what in left)  # left ctrl cannot be tapped to dictate
    right_alt = shortcut_rows("Alt_R", "onShake")
    assert right_alt[0][1] == ("Right Alt",) and right_alt[1][0] == "Tap, then tap again"
    assert ("Hold", ("Right Alt", "Left Alt"), "Ask out loud: the pointer answers, shows you where, and guides you step by step.") in right_alt
    assert ("Shake", ("the mouse",), "Bring the pointer back beside the mouse.") in right_alt
