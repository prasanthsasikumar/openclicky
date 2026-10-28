"""The X11 parts against a real X server: a private Xvfb started for these tests, so no keystroke
ever reaches the desktop the tests run on. Skipped where Xvfb is not installed."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import pytest

pytestmark = pytest.mark.x11

LINUX_DIRECTORY = Path(__file__).resolve().parent.parent


@pytest.fixture(scope="module")
def private_display():
    if not shutil.which("Xvfb"):
        pytest.skip("Xvfb is not installed")
    for number in range(91, 120):
        if not Path(f"/tmp/.X11-unix/X{number}").exists() and not Path(f"/tmp/.X{number}-lock").exists():
            break
    server = subprocess.Popen(["Xvfb", f":{number}", "-screen", "0", "1280x800x24", "-nolisten", "tcp"],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.monotonic() + 10
    while not Path(f"/tmp/.X11-unix/X{number}").exists():
        if time.monotonic() > deadline or server.poll() is not None:
            server.kill()
            pytest.fail("Xvfb did not start")
        time.sleep(0.05)
    yield f":{number}"
    server.terminate()
    server.wait(timeout=5)


def run_scenario(name: str, display: str, openclicky_home: Path | None = None, extra_environment: dict | None = None) -> dict:
    environment = {key: value for key, value in os.environ.items() if key not in ("WAYLAND_DISPLAY", "XAUTHORITY")}
    environment.update(DISPLAY=display, PYTHONPATH=str(LINUX_DIRECTORY), GDK_BACKEND="x11", NO_AT_BRIDGE="1",
                       OPENCLICKY_HOME=str(openclicky_home or LINUX_DIRECTORY / "tests" / "no-such-openclicky-home"),
                       # Never the person's own autostart folder or log.
                       XDG_CONFIG_HOME=tempfile.mkdtemp(prefix="openclicky-test-config-"),
                       XDG_STATE_HOME=tempfile.mkdtemp(prefix="openclicky-test-state-"),
                       XDG_DATA_HOME=tempfile.mkdtemp(prefix="openclicky-test-data-"),
                       # The history goes to that private folder; the models downloaded on this
                       # computer are still found (the real-engine tests use them).
                       OPENCLICKY_MODELS_DIR=str(__import__("openclicky_linux.offline", fromlist=["models_directory"]).models_directory()))
    environment.update(extra_environment or {})
    if "XDG_DATA_HOME" in (extra_environment or {}):
        # A scenario with a data folder of its own downloads and removes models there, never in the
        # computer's own models folder.
        environment["OPENCLICKY_MODELS_DIR"] = str(Path(extra_environment["XDG_DATA_HOME"]) / "openclicky" / "models")
    finished = subprocess.run([sys.executable, str(LINUX_DIRECTORY / "tests" / "x11_scenarios.py"), name],
                              env=environment, capture_output=True, text=True, timeout=60)
    assert finished.returncode == 0, finished.stderr
    return json.loads(finished.stdout.strip().splitlines()[-1])


def test_the_listener_sees_the_dictation_key_other_keys_and_escape_but_not_our_own_keystrokes(private_display):
    seen = run_scenario("key_listener", private_display)["seen"]
    assert seen == [["dictation", True], ["other", False], ["dictation", False], ["other", True]]


def test_the_window_in_front_and_whether_it_is_a_terminal(private_display):
    assert run_scenario("focused_window", private_display) == {"app_class": "xterm", "is_terminal": True}


def test_holding_the_key_types_the_words_into_the_text_box_and_puts_the_clipboard_back(private_display):
    outcome = run_scenario("dictate_into_text_box", private_display)
    # No dictionary in this test's OpenClicky home: fillers out, sentence case, nothing else.
    assert outcome["text_box"] == "Hello from open clicky"
    assert outcome["clipboard"] == "what was on the clipboard before"
    assert ["done", "moved to text box"] in outcome["orb_history"]


def test_a_key_typed_while_holding_cancels_the_take(private_display):
    outcome = run_scenario("key_used_as_shortcut", private_display)
    assert outcome["text_box"] == ""
    assert ["failed", "cancelled, that was a shortcut"] in outcome["orb_history"]
    assert not any(phase == "done" for phase, _ in outcome["orb_history"])


def test_the_pointer_follows_the_mouse_a_little_below_and_to_the_right(private_display):
    outcome = run_scenario("pointer_follows_the_mouse", private_display)
    assert outcome["visible"]
    assert [position[:2] for position in outcome["positions"]] == [[314, 316], [714, 436]]
    assert [position[2] for position in outcome["positions"]] == [[314, 316], [714, 436]]
    # Cut to the triangle (one rectangle per pixel row, not the window's square) and click-through.
    assert outcome["outline_rows"] > 10
    assert outcome["clickable_rectangles"] == 0


def test_left_ctrl_twice_asks_and_the_pointer_flies_to_the_answer(private_display):
    outcome = run_scenario("ask_and_point", private_display)
    assert outcome["composer_opened"] and outcome["composer_has_keyboard"]
    assert outcome["typed"] == "where do i save this?"
    assert outcome["composer_closed"]
    # The pause before the screen is sent ("sending your screen · esc to cancel"), typed out.
    assert outcome["paused_with"].startswith("s")
    assert outcome["hidden_for_the_screenshot"]  # the model never sees the pointer
    assert outcome["thinking"].startswith("t")  # typing out "thinking…"
    # The question went with a real screenshot of the whole screen (the private display is 1280x800).
    assert outcome["asked"] == [["where do i save this?", 1, 1280, 800, True]]
    assert outcome["pointed_at"] == outcome["save_center"]
    assert outcome["caption"] == "that save button down on the right keeps your note."
    assert outcome["after_escape"] == ["following", False]


def test_settings_show_the_routes_and_save_into_shell_json(private_display, tmp_path):
    (tmp_path / "shell.json").write_text(json.dumps({"refreshToken": "keep-me"}))
    outcome = run_scenario("settings_window", private_display, openclicky_home=tmp_path)
    assert "Hold Left Ctrl + Left Alt: Ask out loud: the pointer answers, shows you where, and guides you step by step." in outcome["shortcuts_shown"]
    assert "Tap twice Left Ctrl: Ask by typing." in outcome["shortcuts_shown"]
    # The list follows the key picked, at once (the scenario picks right alt, which can also be tapped).
    assert outcome["shortcuts_after_picking_right_alt"][0].startswith("Hold Right Alt: Dictate")
    assert "Tap, then tap again Right Alt: Dictate without holding the key." in outcome["shortcuts_after_picking_right_alt"]
    assert "Answers come from nothing yet" in outcome["routes_before"]
    assert "Answers come from your Anthropic key" in outcome["routes_after"]
    assert outcome["saved"] == {"refreshToken": "keep-me", "anthropicApiKey": "sk-ant-test", "language": "en-IN",
                                "linuxDictationKey": "Alt_R", "linuxPointer": "whenAnswering"}
    assert outcome["mode"] == "0o600"
    assert outcome["status"].startswith("Saved.")
    assert outcome["pointer_presence_now"] == "whenAnswering"
    assert not outcome["pointer_visible_while_following"]


def test_shake_to_appear_with_the_real_mouse(private_display):
    assert run_scenario("shake_to_appear", private_display) == {
        "at_start": False, "after_a_plain_move": False, "after_a_shake": True, "after_resting": False,
        "while_moving_after_rest": True, "while_moving_after_a_nudge": True}


@pytest.mark.skipif(os.environ.get("OPENCLICKY_LIVE") != "1", reason="live: spends the person's own key; set OPENCLICKY_LIVE=1")
def test_live_a_real_model_points_at_the_right_button(private_display):
    """Real questions through the key in ~/.openclicky/shell.json, about a test window on the private
    display (only that display's screenshot is sent). Three requests."""
    environment_questions = ["where do i save this?", "how do i get rid of this note?", "what is the capital of france?"]
    os.environ["OPENCLICKY_LIVE_QUESTIONS"] = json.dumps(environment_questions)
    outcome = run_scenario("live_questions", private_display, openclicky_home=Path.home() / ".openclicky")
    boxes, results = outcome["boxes"], outcome["results"]

    def lands_on(pointer, box, margin=12):
        x, y, width, height = box
        return x - margin <= pointer[0] <= x + width + margin and y - margin <= pointer[1] <= y + height + margin

    assert results[0]["mode"] == "pointing" and lands_on(results[0]["pointer"], boxes["Save"]), results[0]
    assert results[1]["mode"] == "pointing" and lands_on(results[1]["pointer"], boxes["Delete note"]), results[1]
    assert results[2]["mode"] == "following" and "paris" in results[2]["answer"].lower(), results[2]


def test_a_guide_waits_for_each_click_on_the_real_screen(private_display):
    outcome = run_scenario("guided_steps", private_display)
    steps = outcome["steps"]
    assert [step["caption"] for step in steps] == [
        "open settings first.", "now tick dark mode.", "that's it, dark mode is on."]
    assert [step["waiting"] for step in steps] == [True, True, False]
    # The person's clicks did the work: the dialog opened and dark mode got ticked.
    assert outcome["dialog_open"] and outcome["dark_mode_on"]
    # Above the dialog that opened, not hidden behind it.
    assert all(step["pointer_on_top"] for step in steps)
    assert outcome["questions"][0] == "how do i turn on dark mode?"
    assert all(question.startswith("(i did that step.") and "how do i turn on dark mode?" in question
               for question in outcome["questions"][1:])


@pytest.mark.skipif(os.environ.get("OPENCLICKY_LIVE") != "1", reason="live: spends the person's own key; set OPENCLICKY_LIVE=1")
def test_live_a_real_model_guides_step_by_step(private_display):
    """A real model guides through opening a dialog and ticking a box; the test clicks wherever the
    pointer points. About three requests."""
    outcome = run_scenario("guided_steps", private_display, openclicky_home=Path.home() / ".openclicky")
    assert outcome["dark_mode_on"], outcome
    assert outcome["steps"][-1]["waiting"] is False, outcome


def test_ctrl_click_never_starts_a_take_or_opens_the_ask_box(private_display):
    outcome = run_scenario("ctrl_click", private_display)
    # Without the fix: a tap, so a take kept listening (and the microphone open) until the next tap.
    assert outcome["take_state_after_right_ctrl_click"] == "idle"
    assert ["failed", "cancelled, that was a shortcut"] in outcome["orb_history"]
    assert outcome["ask_box_open"] is False


def test_a_hidpi_screen_maps_points_back_correctly(private_display):
    outcome = run_scenario("hidpi_screenshot", private_display, extra_environment={"GDK_SCALE": "2"})
    # GTK reports a 640x400 screen; the picture has its real 1280x800 pixels: 2 picture pixels each.
    assert outcome["screen"] == [640, 400]
    assert outcome["picture"] == [1280, 800]
    assert outcome["scale"] == 2.0
    assert outcome["middle_of_the_picture_on_screen"] == [320, 200]


def test_a_paste_that_cannot_finish_keeps_the_words_and_says_so(private_display):
    outcome = run_scenario("paste_problems", private_display)
    assert outcome["keystroke fails"] == {"problems": ["couldn't paste. copied, press ctrl+v"],
                                          "clipboard": "dictated words (keystroke fails)"}
    assert outcome["window changed"] == {"problems": ["the window changed. copied, press ctrl+v"],
                                         "clipboard": "dictated words (window changed)"}


def test_the_esc_that_closes_the_ask_box_does_not_also_end_a_guide(private_display):
    outcome = run_scenario("escape_closing_the_ask_box_keeps_the_guide", private_display)
    assert outcome["waiting_before"]
    assert outcome["after_first_escape"] == {"ask_box_open": False, "guide_waiting": True}
    assert outcome["guide_waiting_after_second_escape"] is False


def test_settings_picks_and_tests_the_microphone(private_display, tmp_path):
    outcome = run_scenario("microphone_settings", private_display, openclicky_home=tmp_path)
    assert outcome["menu"] == ["System default (Built-in Audio)", "Yeti Stereo Microphone", "Built-in Audio"]
    assert outcome["results"]["alsa_input.usb-yeti"] == {"verdict": "✓ Heard you. This microphone works.", "meter_moved": True}
    assert outcome["results"]["default"]["verdict"].startswith("✕ Heard nothing at all")
    assert outcome["results"]["default"]["meter_moved"] is False
    # The test records from the microphone picked in the menu; "System default" is no device at all.
    assert outcome["started_with"][:2] == ["alsa_input.usb-yeti", None]
    assert outcome["saved_microphone"] == "alsa_input.usb-yeti"
    assert outcome["recorder_stopped_on_close"]


@pytest.mark.skipif(not __import__("openclicky_linux.offline", fromlist=["is_ready"]).is_ready(),
                    reason="the offline model is not downloaded (Settings → Dictation → Download)")
def test_a_whole_take_with_the_real_offline_engine(private_display):
    outcome = run_scenario("offline_dictation", private_display)
    # The words exactly; the punctuation the model picks varies with the audio ("you, ask" or "you. Ask").
    words = "".join(character for character in outcome["text_box"].lower() if character.isalpha() or character == " ").split()
    assert words == "ask not what your country can do for you ask what you can do for your country".split()
    assert outcome["text_box"].startswith("Ask not")
    assert ["done", "moved to text box"] in outcome["orb_history"]
    assert outcome["preloaded"]


@pytest.mark.skipif(not __import__("openclicky_linux.offline", fromlist=["is_engine_available"]).is_engine_available(),
                    reason="needs sherpa-onnx (pip install -e '.[offline]'): without it the row says so instead")
def test_settings_downloads_and_removes_the_offline_engine(private_display):
    # Its own data folder: the real model, if downloaded, is never touched.
    outcome = run_scenario("offline_settings", private_display,
                           extra_environment={"XDG_DATA_HOME": tempfile.mkdtemp(prefix="openclicky-test-data-")})
    assert outcome["states"][0][0].startswith("Not downloaded") and outcome["states"][0][1] == "Download"
    assert outcome["states"][1] == ["downloading", "Cancel"]
    assert outcome["states"][2] == ["Downloaded", "Remove"]
    assert outcome["states"][3] == ["Not downloaded", "Download"]
    assert "Dictation goes to nowhere yet" in outcome["routes_before"]
    assert "Dictation goes to this computer (offline)" in outcome["routes_after"]
    assert outcome["installed_after_remove"] is False


def test_left_ctrl_dictates_asks_by_voice_and_opens_the_ask_box(private_display):
    seen = run_scenario("left_ctrl", private_display)
    assert seen["ctrl_c_listened"] is False  # ctrl+C never opens the microphone
    assert seen["listening_while_held"] == "listening"
    assert seen["text_box"] == "Buy more lemons"
    assert seen["ask_box_opened"] is True
    assert seen["asking_hint"] == "ask your question, then let go"
    assert seen["questions"] == ["Where do i save this"]
    assert seen["pointer_on_save"] is True
    assert seen["text_box_after_question"] == "Buy more lemons"  # the question was not typed


@pytest.fixture
def display_with_a_window_manager():
    """Xvfb at 1680x1050 with xfwm4, so windows have real title bars (minimize, maximize, close)."""
    if not shutil.which("Xvfb") or not shutil.which("xfwm4"):
        pytest.skip("needs Xvfb and xfwm4")
    number = next(n for n in range(60, 80) if not Path(f"/tmp/.X11-unix/X{n}").exists() and not Path(f"/tmp/.X{n}-lock").exists())
    server = subprocess.Popen(["Xvfb", f":{number}", "-screen", "0", "1680x1050x24", "-nolisten", "tcp"],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.monotonic() + 10
    while not Path(f"/tmp/.X11-unix/X{number}").exists() and time.monotonic() < deadline:
        time.sleep(0.05)
    window_manager = subprocess.Popen(["xfwm4", "--compositor=on"], env={**os.environ, "DISPLAY": f":{number}"},
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.5)
    yield f":{number}"
    window_manager.terminate()
    server.terminate()
    server.wait(timeout=5)


@pytest.mark.skipif(os.environ.get("OPENCLICKY_LIVE") != "1", reason="live: spends the person's own key; set OPENCLICKY_LIVE=1")
def test_live_the_pointer_lands_on_the_minimize_button(display_with_a_window_manager):
    """One request. With 1568 px screenshots this landed ~150 px left of the button, above the window."""
    outcome = run_scenario("live_minimize", display_with_a_window_manager, openclicky_home=Path.home() / ".openclicky")
    x, y, width, _height = outcome["frame"]
    pointer_x, pointer_y = outcome["pointer"]
    assert outcome["mode"] == "pointing"
    assert y <= pointer_y <= y + 32, outcome  # in the title bar
    # xfwm4's minimize button: the third from the right end (shade, minimize, maximize, close).
    assert x + width - 80 <= pointer_x <= x + width - 50, outcome


@pytest.mark.parametrize("dictation_key", ["right_ctrl", "left_ctrl"])
def test_on_wayland_a_take_from_the_input_device_is_pasted_with_shift_insert_and_both_selections_put_back(private_display, dictation_key):
    outcome = run_scenario(f"wayland_dictate_with_{dictation_key}", private_display)
    assert outcome["text_box"] == "Hello from open clicky"
    assert outcome["pastes"] == 1
    # The keystroke went only once the compositor had asked for the words.
    assert outcome["compositor_requests"] >= 1
    assert outcome["clipboard"] == "what was on the clipboard before"
    assert outcome["primary"] == "what was selected before"
    assert ["done", "moved to text box"] in outcome["orb_history"]
    # Wayland never says where the mouse is: the pointer rests out of sight until it answers.
    assert outcome["pointer_knows_the_mouse"] is False
    assert outcome["pointer_visible"] is False



def test_the_first_run_setup_walks_through_every_page_and_installs_the_launcher(private_display, tmp_path):
    home = tmp_path / "openclicky-home"
    home.mkdir()
    outcome = run_scenario("setup_window_walkthrough", private_display, openclicky_home=home)
    pages = [entry[0] for entry in outcome["seen"] if not entry[0].endswith("status")]
    # X11 needs no permission pages.
    assert pages == ["hello", "microphone", "answers", "offline", "startup", "done"]
    statuses = [entry[1] for entry in outcome["seen"] if entry[0] == "answers status"]
    assert statuses == ["that doesn't look like an email yet.", "✓ saved. answers come from your anthropic key."]
    assert json.loads((home / "shell.json").read_text())["anthropicApiKey"] == "sk-ant-test-key"
    assert outcome["finished"] == [True] and outcome["setup_done"] is True
    assert outcome["autostart"] and outcome["launcher"] and outcome["icon"]


def test_the_words_window_shows_and_searches_the_history_and_saves_words_the_mac_way(private_display, tmp_path):
    home = tmp_path / "openclicky-home"
    home.mkdir()
    outcome = run_scenario("words_window", private_display, openclicky_home=home)
    assert outcome["shown"] == ["See you at five", "Hello from the test", "couldn't finish: couldn't reach openai"]
    assert outcome["searched"] == ["Hello from the test"]
    assert outcome["failed"] == ["couldn't finish: couldn't reach openai"]
    assert "takes in all, kept only on this computer" in outcome["stats"]
    assert outcome["dictionary"] == [["Aaditya Kshatriya", ["aditya shatriya", "aditya"]]]
    assert outcome["shortcuts"] == [["my sign off", "Best,\nSam"]]



def test_live_typing_types_any_character_whatever_the_layout(private_display):
    assert run_scenario("live_typing", private_display) == {"typed": True, "text": "Héllo → wörld, OK? é", "given_back": True}


def test_on_wayland_words_the_compositor_never_took_up_are_never_typed(private_display):
    # Typed anyway, the app in front would paste the clipboard from before (a key, a password).
    outcome = run_scenario("wayland_compositor_never_takes_up_the_words", private_display)
    assert outcome["pastes"] == 0 and outcome["text_box"] == ""
    assert ["failed", "couldn't paste. copied, press ctrl+v"] in outcome["orb_history"]
    assert outcome["clipboard"] == "Hello from open clicky"


def test_on_wayland_a_compositor_without_a_primary_selection_still_gets_the_words(private_display):
    outcome = run_scenario("wayland_compositor_without_a_primary_selection", private_display)
    assert outcome["pastes"] == 1 and outcome["text_box"] == "Hello from open clicky"
