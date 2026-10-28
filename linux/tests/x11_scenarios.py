"""Scenarios run against a private Xvfb display by `test_x11.py`, one per child process (GTK binds
to a display once per process). Each prints one JSON line with what it saw. Keys are pressed with
XTEST, exactly as a keyboard would: through the X server, seen by the RECORD listener.
"""

from __future__ import annotations

import json
import sys
import time

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("GdkX11", "3.0")
from gi.repository import Gdk, GdkX11, Gtk  # noqa: E402,F401
from Xlib import X, XK, display as xdisplay  # noqa: E402
from Xlib.ext import xtest  # noqa: E402

from openclicky_linux.__main__ import wire_up  # noqa: E402
from openclicky_linux.recorder import SAMPLE_RATE  # noqa: E402
from openclicky_linux.x11 import DictationKeyListener, focused_window  # noqa: E402

keyboard = xdisplay.Display()


def pump(seconds: float, until=lambda: False) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline and not until():
        while Gtk.events_pending():
            Gtk.main_iteration_do(False)
        time.sleep(0.01)


def key(keysym_name: str, is_down: bool) -> None:
    keycode = keyboard.keysym_to_keycode(XK.string_to_keysym(keysym_name))
    xtest.fake_input(keyboard, X.KeyPress if is_down else X.KeyRelease, keycode)
    keyboard.sync()


def text_box_window(window_class: str | None = None):
    window = Gtk.Window(title="scenario")
    if window_class:
        window.set_wmclass(window_class, window_class)
    entry = Gtk.Entry()
    window.add(entry)
    window.show_all()
    pump(0.3)
    # No window manager on Xvfb: the focus is given by hand, as a click would.
    xid = window.get_window().get_xid()
    keyboard.set_input_focus(xid, X.RevertToParent, X.CurrentTime)
    keyboard.sync()
    entry.grab_focus()
    pump(0.3)
    return window, entry


class StandInRecording:
    def stop(self) -> bytes:
        return b"\x01\x00" * SAMPLE_RATE

    def cancel(self) -> None:
        pass


def scenario_key_listener() -> dict:
    seen: list = []
    listener = DictationKeyListener("Control_R", on_dictation_key=lambda is_down, _at: seen.append(["dictation", is_down]),
                                    on_other_key_down=lambda is_escape: seen.append(["other", is_escape]))
    listener.start()
    time.sleep(0.3)
    for name, is_down in [("Control_R", True), ("a", True), ("a", False), ("Control_R", False), ("Escape", True), ("Escape", False)]:
        key(name, is_down)
        time.sleep(0.05)
    time.sleep(0.3)
    listener.ignore_keys_for(0.5)
    key("Control_R", True)
    key("Control_R", False)
    time.sleep(0.3)
    listener.stop()
    return {"seen": seen}


def scenario_focused_window() -> dict:
    _window, _entry = text_box_window("xterm")
    found = focused_window(keyboard)
    return {"app_class": found and found.app_class, "is_terminal": found and found.is_terminal}


def _dictate(hold_seconds: float, keys_during_hold: tuple[str, ...] = ()) -> dict:
    _window, entry = text_box_window()
    clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
    clipboard.set_text("what was on the clipboard before", -1)
    pump(0.1)
    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "um, hello from open clicky")
    controller, listener = app.dictation, app.listener
    orb_history: list = []
    controller.orb.listeners.append(lambda orb: orb_history.append([orb.phase, orb.message]))
    listener.start()
    pump(0.3)
    key("Control_R", True)
    pump(0.1)
    for name in keys_during_hold:
        key(name, True)
        key(name, False)
    pump(hold_seconds)
    key("Control_R", False)
    pump(5, until=lambda: bool(entry.get_text()) or any(phase == "failed" for phase, _ in orb_history))
    pump(1.0)  # past the clipboard restore
    listener.stop()
    return {"text_box": entry.get_text(), "clipboard": clipboard.wait_for_text(),
            "orb_history": orb_history}


def scenario_dictate_into_text_box() -> dict:
    return _dictate(hold_seconds=0.6)


def scenario_key_used_as_shortcut() -> dict:
    return _dictate(hold_seconds=0.6, keys_during_hold=("c",))



# MARK: the pointer, the ask box, Settings

import os  # noqa: E402

from Xlib.ext import xtest as _xtest  # noqa: E402,F811

SCREENSHOT_DIRECTORY = os.environ.get("OPENCLICKY_SCREENSHOT_DIR")


def screenshot(name: str) -> None:
    if not SCREENSHOT_DIRECTORY:
        return
    pump(0.2)
    root = Gdk.get_default_root_window()
    Gdk.pixbuf_get_from_window(root, 0, 0, root.get_width(), root.get_height()).savev(
        os.path.join(SCREENSHOT_DIRECTORY, f"{name}.png"), "png", [], [])


def move_mouse(x: int, y: int) -> None:
    _xtest.fake_input(keyboard, X.MotionNotify, x=x, y=y)
    keyboard.sync()


def tap(keysym_name: str) -> None:
    key(keysym_name, True)
    time.sleep(0.03)
    key(keysym_name, False)


def type_text(text: str) -> None:
    for character in text:
        keysym_name = {" ": "space", "?": "question"}.get(character, character)
        keysym = XK.string_to_keysym(keysym_name)
        # A character on the shifted level of its key ("?" on "/") is typed with shift held.
        needs_shift = keyboard.keycode_to_keysym(keyboard.keysym_to_keycode(keysym), 0) != keysym
        if needs_shift:
            key("Shift_L", True)
        tap(keysym_name)
        if needs_shift:
            key("Shift_L", False)
        time.sleep(0.01)


def app_window_with_a_save_button():
    window = Gtk.Window(title="Notes")
    window.set_default_size(900, 500)
    layout = Gtk.Fixed()
    save = Gtk.Button(label="Save")
    layout.put(save, 640, 380)
    layout.put(Gtk.Label(label="An app the question is about"), 40, 40)
    window.add(layout)
    window.move(150, 120)
    window.show_all()
    pump(0.5)
    allocation = save.get_allocation()
    origin_x, origin_y = window.get_window().get_origin()[1:3]
    return window, (origin_x + allocation.x + allocation.width // 2, origin_y + allocation.y + allocation.height // 2)


def scenario_pointer_follows_the_mouse() -> dict:
    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "")
    positions = []
    for x, y in ((300, 300), (700, 420)):
        move_mouse(x, y)
        pump(0.3)
        positions.append([round(value) for value in app.pointer.position] + [list(app.pointer.triangle.get_window().get_position())])
    screenshot("pointer-following")
    from Xlib.ext import shape as x_shape
    triangle = keyboard.create_resource_object("window", app.pointer.triangle.get_window().get_xid())
    return {"positions": positions, "visible": app.pointer.triangle.get_visible(),
            "outline_rows": len(triangle.shape_get_rectangles(x_shape.SK.Bounding).rectangles),
            "clickable_rectangles": len(triangle.shape_get_rectangles(x_shape.SK.Input).rectangles)}


def scenario_ask_and_point() -> dict:
    window, save_center = app_window_with_a_save_button()
    asked = []

    def stand_in_model(question, screenshot_taken, conversation, **_streaming):
        # One picture per monitor; the private display has one.
        from openclicky_linux.ask import as_screen_list
        pictures = as_screen_list(screenshot_taken)
        asked.append([question, len(pictures), pictures[0].width, pictures[0].height, len(pictures[0].png_bytes) > 1000])
        time.sleep(0.4)
        return f"[POINT:{save_center[0]},{save_center[1]}:save button] that save button down on the right keeps your note."

    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "", ask=stand_in_model)
    app.listener.start()
    move_mouse(260, 260)
    pump(0.4)
    tap("Control_L")
    pump(0.1)
    tap("Control_L")
    pump(2, until=lambda: app.composer.is_open and app.composer.entry.has_focus())
    composer_opened = app.composer.is_open
    composer_has_keyboard = app.composer.entry.has_focus()
    type_text("where do i save this?")
    pump(0.3)
    screenshot("ask-box")
    typed = app.composer.entry.get_text()
    tap("Return")
    # First a moment to call it off (Settings → Privacy, on by default), then out of sight.
    pump(0.3, until=lambda: app.pointer.caption_label.get_text() != "")
    paused_with = app.pointer.caption_label.get_text()
    pump(2, until=lambda: not app.pointer.triangle.get_visible())
    hidden_for_the_screenshot = not app.pointer.triangle.get_visible()
    pump(2, until=lambda: app.pointer.caption_label.get_text() not in ("", paused_with))
    thinking = app.pointer.caption_label.get_text()
    pump(6, until=lambda: app.pointer.mode == "pointing" and "keeps your note" in app.pointer.caption_label.get_text())
    pump(0.3)
    screenshot("answer-and-point")
    pointed_at = [round(value) for value in app.pointer.position]
    caption = app.pointer.caption_label.get_text()
    composer_closed = not app.composer.is_open
    tap("Escape")
    pump(3, until=lambda: app.pointer.mode == "following")
    pump(0.2)
    app.listener.stop()
    return {"hidden_for_the_screenshot": hidden_for_the_screenshot, "paused_with": paused_with,
            "composer_opened": composer_opened, "composer_has_keyboard": composer_has_keyboard, "typed": typed,
            "composer_closed": composer_closed, "thinking": thinking, "asked": asked, "save_center": list(save_center),
            "pointed_at": pointed_at, "caption": caption, "after_escape": [app.pointer.mode, app.pointer.caption.get_visible()]}


def scenario_settings_window() -> dict:
    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "")
    settings = app.settings_window
    settings.show()
    pump(0.5)
    shortcuts_shown = list(settings.shortcut_lines)
    routes_before = settings.routes_label.get_text()
    settings.fields["anthropic_api_key"].set_text("sk-ant-test")
    settings._fill_languages("en-IN")  # kept even where the region does not offer it
    settings.fields["dictation_key"].set_active_id("Alt_R")
    settings.fields["pointer_presence"].set_active_id("whenAnswering")
    pump(0.2)
    routes_after = settings.routes_label.get_text()
    shortcuts_after_picking_right_alt = list(settings.shortcut_lines)
    screenshot("settings")
    settings.save_button.clicked()
    pump(0.3)
    shell_json = os.path.join(os.environ["OPENCLICKY_HOME"], "shell.json")
    with open(shell_json, encoding="utf-8") as saved_file:
        saved = json.load(saved_file)
    return {"shortcuts_shown": shortcuts_shown, "shortcuts_after_picking_right_alt": shortcuts_after_picking_right_alt,
            "routes_before": routes_before, "routes_after": routes_after, "saved": saved,
            "mode": oct(os.stat(shell_json).st_mode & 0o777), "status": settings.status_label.get_text(),
            "pointer_presence_now": app.pointer.presence, "pointer_visible_while_following": app.pointer.triangle.get_visible()}


def scenario_shake_to_appear() -> dict:
    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "")
    app.pointer.set_presence("onShake")
    app.pointer.tracker.rest_delay_seconds = 1.0
    seen = {}
    move_mouse(400, 300)
    pump(0.4)
    seen["at_start"] = app.pointer.triangle.get_visible()
    for x in range(400, 900, 25):  # an ordinary move across the screen
        move_mouse(x, 300)
        pump(0.034)
    seen["after_a_plain_move"] = app.pointer.triangle.get_visible()
    x = 620
    for swing in range(6):  # a shake: quick 160 px swings, side to side, four steps each
        target = 780 if swing % 2 == 0 else 620
        for step in range(1, 5):
            move_mouse(round(x + (target - x) * step / 4), 300)
            pump(0.034)
        x = target
    seen["after_a_shake"] = app.pointer.triangle.get_visible()
    screenshot("shake-to-appear")
    pump(1.6)
    seen["after_resting"] = app.pointer.triangle.get_visible()
    app.pointer.set_presence("whileMoving")
    seen["while_moving_after_rest"] = app.pointer.triangle.get_visible()
    move_mouse(500, 400)
    pump(0.2)
    seen["while_moving_after_a_nudge"] = app.pointer.triangle.get_visible()
    return seen


# MARK: live: a real model through the person's own key (only with OPENCLICKY_LIVE=1)

def editor_window():
    """A small editor-like app: a toolbar of labelled buttons and a text area, at known places."""
    window = Gtk.Window(title="Notes — shopping list")
    window.set_default_size(980, 600)
    layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
    toolbar = Gtk.Box(spacing=12)
    buttons = {}
    for label in ("New", "Open", "Save", "Print", "Settings"):
        button = Gtk.Button(label=label)
        toolbar.pack_start(button, False, False, 0)
        buttons[label] = button
    layout.pack_start(toolbar, False, False, 0)
    text = Gtk.TextView()
    text.get_buffer().set_text("milk\neggs\nbread\n")
    layout.pack_start(text, True, True, 0)
    footer = Gtk.Box()
    delete = Gtk.Button(label="Delete note")
    footer.pack_end(delete, False, False, 0)
    buttons["Delete note"] = delete
    layout.pack_start(footer, False, False, 0)
    window.add(layout)
    window.move(120, 90)
    window.show_all()
    pump(0.6)
    origin_x, origin_y = window.get_window().get_origin()[1:3]
    boxes = {}
    for label, button in buttons.items():
        allocation = button.get_allocation()
        boxes[label] = [origin_x + allocation.x, origin_y + allocation.y, allocation.width, allocation.height]
    return window, boxes


def scenario_live_questions() -> dict:
    window, boxes = editor_window()
    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "")
    app.listener.start()
    move_mouse(300, 400)
    pump(0.3)
    results = []
    for index, question in enumerate(json.loads(os.environ["OPENCLICKY_LIVE_QUESTIONS"])):
        started = time.monotonic()
        app.asking.submit(question)
        pump(60, until=lambda: not app.asking.is_answering and app.pointer.mode != "flying"
             and app.pointer.caption_label.get_text() not in ("", "thinking…"[:len(app.pointer.caption_label.get_text())]))
        pump(1.5)  # the caption finishes typing
        results.append({"question": question, "seconds": round(time.monotonic() - started, 1),
                        "answer": app.asking.conversation.exchanges[-1][1] if app.asking.conversation.exchanges else None,
                        "caption": app.pointer.caption_label.get_text(), "mode": app.pointer.mode,
                        "pointer": [round(value) for value in app.pointer.position]})
        screenshot(f"live-{index + 1}")
        app.asking.escape()
        pump(1.5, until=lambda: app.pointer.mode == "following")
    app.listener.stop()
    return {"boxes": boxes, "results": results}


# MARK: step by step guidance

def click_at(x: int, y: int) -> None:
    move_mouse(x, y)
    pump(0.15)
    _xtest.fake_input(keyboard, X.ButtonPress, 1)
    _xtest.fake_input(keyboard, X.ButtonRelease, 1)
    keyboard.sync()


def center_of(widget) -> list[int]:
    allocation = widget.get_allocation()
    origin_x, origin_y = widget.get_window().get_origin()[1:3]
    return [origin_x + allocation.x + allocation.width // 2, origin_y + allocation.y + allocation.height // 2]


def app_with_a_settings_dialog():
    """An app whose Settings button opens a dialog with a Dark mode checkbox and a Close button."""
    window = Gtk.Window(title="Photos")
    window.set_default_size(900, 520)
    layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
    toolbar = Gtk.Box(spacing=12)
    for label in ("Import", "Share"):
        toolbar.pack_start(Gtk.Button(label=label), False, False, 0)
    settings_button = Gtk.Button(label="Settings")
    toolbar.pack_end(settings_button, False, False, 0)
    layout.pack_start(toolbar, False, False, 0)
    layout.pack_start(Gtk.Label(label="12 photos"), True, True, 0)
    window.add(layout)
    window.move(120, 100)
    window.show_all()

    dialog = Gtk.Window(title="Photos settings")
    dialog.set_default_size(360, 200)
    dialog_layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
    dialog_layout.set_border_width(16)
    dialog_layout.pack_start(Gtk.CheckButton(label="Show file names"), False, False, 0)
    dark_mode = Gtk.CheckButton(label="Dark mode")
    dialog_layout.pack_start(dark_mode, False, False, 0)
    close = Gtk.Button(label="Close")
    dialog_layout.pack_end(close, False, False, 0)
    dialog.add(dialog_layout)
    dialog.move(500, 260)
    settings_button.connect("clicked", lambda _button: (dialog.show_all(), dialog.present()))
    close.connect("clicked", lambda _button: dialog.hide())
    pump(0.6)
    return window, settings_button, dialog, dark_mode


def scenario_guided_steps() -> dict:
    window, settings_button, dialog, dark_mode = app_with_a_settings_dialog()
    live = os.environ.get("OPENCLICKY_LIVE") == "1"
    questions = []

    def stand_in_model(question, screenshot_taken, conversation, **_streaming):
        questions.append(question)
        time.sleep(0.3)
        if len(questions) == 1:
            x, y = center_of(settings_button)
            return f"[POINT:{x},{y}:settings] open settings first. [NEXT]"
        if len(questions) == 2:
            x, y = center_of(dark_mode)
            return f"[POINT:{x - 30},{y}:dark mode] now tick dark mode. [NEXT]"
        return "[POINT:none] that's it, dark mode is on."

    if live:
        from openclicky_linux.ask import ask as ask_model
        from openclicky_linux.config import load_settings

        def model(question, screenshot_taken, conversation, **streaming):
            questions.append(question)
            return ask_model(question, screenshot_taken, conversation, load_settings(), **streaming)
    else:
        model = stand_in_model

    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "", ask=model)
    app.listener.start()
    move_mouse(300, 400)
    pump(0.3)
    steps = []
    app.asking.submit("how do i turn on dark mode?")
    for answers_so_far in range(1, 7):
        # Wait for this step's answer (the next question only goes out once the click has settled).
        pump(60, until=lambda: len(app.asking.conversation.exchanges) >= answers_so_far
             and not app.asking.is_answering and app.pointer.mode != "flying")
        pump(1.5)  # the caption finishes typing
        caption = app.pointer.caption_label.get_text()
        steps.append({"caption": caption, "mode": app.pointer.mode, "pointer": [round(value) for value in app.pointer.position],
                      "waiting": app.asking.is_waiting_for_step})
        screenshot(f"guide-step-{len(steps)}")
        # The pointer and its caption stay above every window, the dialog a step opened included.
        stacking = [child.id for child in keyboard.screen().root.query_tree().children]
        pointer_xid = app.pointer.triangle.get_window().get_xid()
        others = [dialog.get_window().get_xid()] if dialog.get_window() and dialog.get_visible() else []
        steps[-1]["pointer_on_top"] = all(stacking.index(pointer_xid) > stacking.index(other)
                                          for other in others if other in stacking)
        # Do the step the pointer shows (the last one too): click exactly where its tip is.
        if steps[-1]["mode"] == "pointing":
            click_at(*steps[-1]["pointer"])
            pump(0.5)
        if not app.asking.is_waiting_for_step:
            break
    app.listener.stop()
    return {"steps": steps, "questions": questions, "dark_mode_on": dark_mode.get_active(),
            "dialog_open": dialog.get_visible(), "settings_center": center_of(settings_button)}


def scenario_ctrl_click() -> dict:
    """Right ctrl + click (a link in a new tab) and left ctrl + click twice (multi-select) are
    shortcuts with the mouse: neither may start a take or open the ask box."""
    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "")
    orb_history: list = []
    app.dictation.orb.listeners.append(lambda orb: orb_history.append([orb.phase, orb.message]))
    app.listener.start()
    move_mouse(400, 300)
    pump(0.3)
    key("Control_R", True)
    pump(0.05)
    _xtest.fake_input(keyboard, X.ButtonPress, 1)
    _xtest.fake_input(keyboard, X.ButtonRelease, 1)
    keyboard.sync()
    pump(0.05)
    key("Control_R", False)
    pump(0.5)
    right_ctrl_state = app.dictation.state.value
    for _ in range(2):
        key("Control_L", True)
        pump(0.03)
        _xtest.fake_input(keyboard, X.ButtonPress, 1)
        _xtest.fake_input(keyboard, X.ButtonRelease, 1)
        keyboard.sync()
        pump(0.03)
        key("Control_L", False)
        pump(0.1)
    pump(0.5)
    app.listener.stop()
    return {"take_state_after_right_ctrl_click": right_ctrl_state, "orb_history": orb_history,
            "ask_box_open": app.composer.is_open}


def scenario_hidpi_screenshot() -> dict:
    """Run with GDK_SCALE=2: the picture has twice the pixels of the screen GTK reports."""
    from openclicky_linux.pointing import point_on_screens
    from openclicky_linux.screen import capture_screens, screen_size
    pictures = capture_screens()
    return {"screen": list(screen_size()), "picture": [pictures[0].width, pictures[0].height], "scale": pictures[0].scale,
            "middle_of_the_picture_on_screen": list(point_on_screens(pictures[0].width / 2, pictures[0].height / 2, None, pictures))}


def scenario_paste_problems() -> dict:
    """The paste's checks right before the keystroke: a keystroke that cannot be sent, and a window
    that changed since the take ended. Either way the words must stay on the clipboard (no restore
    over them) and the problem must be reported."""
    from openclicky_linux import desktop
    from openclicky_linux.controller import FocusedWindow

    clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
    outcomes = {}
    for case in ("keystroke fails", "window changed"):
        clipboard.set_text("the person's own clipboard", -1)
        pump(0.2)
        problems = []
        paster = desktop.ClipboardPaster(keyboard, on_paste_problem=problems.append)
        if case == "keystroke fails":
            desktop.send_paste_keystroke = lambda *_arguments, **_keywords: False
            desktop.focused_window = lambda _display: FocusedWindow(window_id=7)
        else:
            desktop.send_paste_keystroke = lambda *_arguments, **_keywords: True
            desktop.focused_window = lambda _display: FocusedWindow(window_id=8)  # not the take's window
        paster.paste(f"dictated words ({case})", FocusedWindow(window_id=7))
        pump(1.2)  # past the 0.6 s restore
        outcomes[case] = {"problems": problems, "clipboard": clipboard.wait_for_text()}
    return outcomes


def scenario_escape_closing_the_ask_box_keeps_the_guide() -> dict:
    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "",
                  ask=lambda question, pictures, conversation, **_streaming: "[POINT:200,200:menu] open the menu first. [NEXT]")
    app.listener.start()
    pump(0.3)
    app.asking.submit("how do i do it?")
    pump(5, until=lambda: app.asking.is_waiting_for_step)
    waiting_before = app.asking.is_waiting_for_step
    app.asking.open_composer()
    pump(2, until=lambda: app.composer.is_open and app.composer.entry.has_focus())
    tap("Escape")  # closes the ask box, nothing else
    pump(0.8)
    after_first_escape = {"ask_box_open": app.composer.is_open, "guide_waiting": app.asking.is_waiting_for_step}
    tap("Escape")  # now it is for the guide
    pump(0.8)
    app.listener.stop()
    return {"waiting_before": waiting_before, "after_first_escape": after_first_escape,
            "guide_waiting_after_second_escape": app.asking.is_waiting_for_step}


# A stand-in microphone: a 440 Hz tone (someone talking) or silence, as raw 16 kHz PCM on stdout,
# streamed until it is stopped, the way parecord does.
_TONE = ("import math, sys, time\n"
         "chunk = b''.join(int(9000 * math.sin(2 * math.pi * 440 * n / 16000)).to_bytes(2, 'little', signed=True) for n in range(1600))\n"
         "while True:\n    sys.stdout.buffer.write(chunk); sys.stdout.buffer.flush(); time.sleep(0.1)\n")
_SILENCE = "import sys, time\nwhile True:\n    sys.stdout.buffer.write(bytes(3200)); sys.stdout.buffer.flush(); time.sleep(0.1)\n"


def scenario_microphone_settings() -> dict:
    from openclicky_linux import recorder, windows
    from openclicky_linux.recorder import Microphone

    windows.list_microphones = lambda: [Microphone("alsa_input.usb-yeti", "Yeti Stereo Microphone"),
                                        Microphone("alsa_input.builtin", "Built-in Audio")]
    windows.default_microphone_name = lambda: "alsa_input.builtin"
    commands = {"alsa_input.usb-yeti": _TONE, "alsa_input.builtin": _SILENCE}
    started_with = []

    def stand_in_recording(microphone=None):
        started_with.append(microphone)
        return recorder.MicrophoneRecording(command=(sys.executable, "-c", commands[microphone or "alsa_input.builtin"]))
    windows.MicrophoneRecording = stand_in_recording

    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "")
    settings = app.settings_window
    settings.show()
    pump(0.5)
    combo = settings.fields["microphone"]
    menu = [row[0] for row in combo.get_model()]
    results = {}
    for choice in ("alsa_input.usb-yeti", "default"):
        combo.set_active_id(choice)
        settings.test_microphone(seconds=1.2)
        loudest_bar = 0.0
        deadline = time.monotonic() + 4
        while settings._microphone_test is not None and time.monotonic() < deadline:
            pump(0.05)
            loudest_bar = max(loudest_bar, settings.microphone_level.get_value())
        results[choice] = {"verdict": settings.microphone_test_result.get_text(), "meter_moved": loudest_bar > 0.3}
        if choice == "alsa_input.usb-yeti":
            screenshot("microphone-test")
    combo.set_active_id("alsa_input.usb-yeti")
    settings.save_button.clicked()
    pump(0.3)
    with open(os.path.join(os.environ["OPENCLICKY_HOME"], "shell.json"), encoding="utf-8") as saved_file:
        saved = json.load(saved_file)
    # A test still running when the window closes is stopped (the microphone is never left open).
    settings.test_microphone(seconds=30)
    running = settings._microphone_test
    settings.window.hide()
    pump(0.5)
    return {"menu": menu, "results": results, "started_with": started_with, "saved_microphone": saved.get("linuxMicrophone"),
            "recorder_stopped_on_close": running is not None and running._process.poll() is not None}


def _resampled_to_16_khz(pcm: bytes, rate: int) -> bytes:
    """Linear resampling, enough for a test: what a microphone at 16 kHz would have delivered."""
    import array
    source = array.array("h", pcm)
    length = int(len(source) * 16000 / rate)
    out = array.array("h", bytes(2 * length))
    for index in range(length):
        position = index * rate / 16000
        left = int(position)
        right = min(left + 1, len(source) - 1)
        fraction = position - left
        out[index] = int(source[left] * (1 - fraction) + source[right] * fraction)
    return out.tobytes()


def scenario_offline_dictation() -> dict:
    """The whole take with the real offline engine: right ctrl held while a stand-in microphone plays
    the model's English sample, released, and the words typed into a text box."""
    import wave as wave_module

    from openclicky_linux import offline
    from openclicky_linux.config import load_settings
    from openclicky_linux.transcriber import transcribe

    with wave_module.open(str(offline.model_folder() / "test_wavs" / "en.wav")) as wav_file:
        speech = _resampled_to_16_khz(wav_file.readframes(wav_file.getnframes()), wav_file.getframerate())

    class SpeakingRecording:
        def stop(self) -> bytes:
            return speech

        def cancel(self) -> None:
            pass

    engine = offline.OfflineEngine()
    _window, entry = text_box_window()
    app = wire_up("Control_R", start_recording=SpeakingRecording,
                  transcribe=lambda wav_bytes: transcribe(wav_bytes, load_settings(), engine),
                  prepare_transcription=engine.prepare)
    orb_history: list = []
    app.dictation.orb.listeners.append(lambda orb: orb_history.append([orb.phase, orb.message]))
    app.listener.start()
    pump(0.3)
    key("Control_R", True)
    pump(0.6)
    preloaded = engine.is_running  # the model started loading as the key went down
    key("Control_R", False)
    started = time.monotonic()
    pump(30, until=lambda: bool(entry.get_text()) or any(phase == "failed" for phase, _ in orb_history))
    seconds = round(time.monotonic() - started, 1)
    pump(0.8)
    app.listener.stop()
    engine.unload()
    return {"text_box": entry.get_text(), "orb_history": orb_history, "preloaded": preloaded, "seconds_after_release": seconds}


def scenario_offline_settings() -> dict:
    """Settings → Dictation → Offline engine: Download (a small stand-in archive from a local
    server, so nothing large is fetched), the progress, Downloaded, then Remove."""
    import functools
    import hashlib
    import http.server
    import io
    import tarfile
    import tempfile
    import threading

    from openclicky_linux import offline

    served = tempfile.mkdtemp()
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:bz2") as archive:
        for name in offline.PARAKEET.files:
            data = os.urandom(600_000)  # random, so it does not compress: ~2.4 MB to download, so the progress moves
            info = tarfile.TarInfo(f"{offline.PARAKEET.folder_name}/{name}")
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
    archive_bytes = buffer.getvalue()
    with open(os.path.join(served, "model.tar.bz2"), "wb") as archive_file:
        archive_file.write(archive_bytes)
    class SlowHandler(http.server.SimpleHTTPRequestHandler):
        """Sends the archive in small pieces with pauses, as a real download arrives: on a fast
        machine the whole stand-in came in before the test could see "Downloading…" (CI did)."""

        def copyfile(self, source, outputfile):
            while piece := source.read(128 * 1024):
                outputfile.write(piece)
                time.sleep(0.05)

        def log_message(self, *_arguments):
            pass

    handler = functools.partial(SlowHandler, directory=served)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    asset = offline.ModelAsset(url=f"http://127.0.0.1:{server.server_address[1]}/model.tar.bz2",
                               sha256=hashlib.sha256(archive_bytes).hexdigest(), size_bytes=len(archive_bytes),
                               folder_name=offline.PARAKEET.folder_name, files=offline.PARAKEET.files)

    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "")
    settings = app.settings_window
    settings.show()
    pump(0.5)
    states = [[settings.offline_status.get_text(), settings.offline_button.get_label()]]
    routes_before = settings.routes_label.get_text()
    settings.start_offline_download(asset)
    # Watched on every turn of the loop rather than sampled once: a busy CI runner can finish the
    # stand-in download before a fixed pause ends, and the test only needs to see the progress once.
    seen_downloading: list[list[str]] = []

    def finished() -> bool:
        if not seen_downloading and settings.offline_status.get_text().startswith("Downloading"):
            seen_downloading.append(["downloading", settings.offline_button.get_label()])
        return settings.offline_button.get_label() == "Remove"

    pump(20, until=finished)
    states.append(seen_downloading[0] if seen_downloading else [settings.offline_status.get_text(), settings.offline_button.get_label()])
    states.append([settings.offline_status.get_text().split(" (")[0], settings.offline_button.get_label()])
    routes_after = settings.routes_label.get_text()
    screenshot("offline-settings")
    settings.offline_button.clicked()  # Remove
    pump(0.3)
    states.append([settings.offline_status.get_text().split(".")[0], settings.offline_button.get_label()])
    server.shutdown()
    return {"states": states, "routes_before": routes_before, "routes_after": routes_after,
            "installed_after_remove": offline.is_installed()}


def scenario_left_ctrl() -> dict:
    """The default keys: left ctrl held dictates, ctrl+C is a shortcut, two taps open the ask box,
    and left ctrl + left alt asks the pointer out loud."""
    window, save_center = app_window_with_a_save_button()
    entry = Gtk.Entry()
    window.get_child().put(entry, 40, 120)
    window.show_all()
    pump(0.3)
    keyboard.set_input_focus(window.get_window().get_xid(), X.RevertToParent, X.CurrentTime)
    keyboard.sync()
    entry.grab_focus()
    pump(0.3)
    transcripts = iter(["um, buy more lemons", "where do i save this"])
    questions = []

    def stand_in_model(question, pictures, conversation, **_streaming):
        questions.append(question)
        return f"[POINT:{save_center[0]},{save_center[1]}:save button] that save button keeps it."

    app = wire_up("Control_L", start_recording=StandInRecording, transcribe=lambda _wav: next(transcripts), ask=stand_in_model)
    orb_history: list = []
    app.dictation.orb.listeners.append(lambda orb: orb_history.append([orb.phase, orb.message]))
    app.listener.start()
    move_mouse(300, 400)
    pump(0.3)
    seen = {}

    # 1. ctrl+C: a shortcut; the microphone never opens.
    key("Control_L", True)
    pump(0.05)
    tap("c")
    pump(0.5)
    key("Control_L", False)
    pump(0.4)
    seen["ctrl_c_listened"] = any(phase == "listening" for phase, _ in orb_history)

    # 2. Held on its own: dictation into the text box.
    key("Control_L", True)
    pump(0.7)
    seen["listening_while_held"] = app.dictation.state.value
    key("Control_L", False)
    pump(5, until=lambda: bool(entry.get_text()))
    seen["text_box"] = entry.get_text()
    pump(0.6)

    # 3. Two quick taps: the ask box.
    tap("Control_L")
    pump(0.1)
    tap("Control_L")
    pump(2, until=lambda: app.composer.is_open)
    seen["ask_box_opened"] = app.composer.is_open
    app.composer.hide()
    pump(0.6)

    # 4. Held with left alt: a spoken question; the pointer answers and points, nothing is typed.
    key("Alt_L", True)
    key("Control_L", True)
    pump(0.7)
    seen["asking_hint"] = app.dictation.orb.message
    key("Control_L", False)
    key("Alt_L", False)
    pump(8, until=lambda: app.pointer.mode == "pointing" and "keeps it" in app.pointer.caption_label.get_text())
    pump(0.3)
    seen["questions"] = questions
    seen["pointer_at"] = [round(value) for value in app.pointer.position]
    seen["save_center"] = list(save_center)
    seen["pointer_mode"] = app.pointer.mode
    seen["caption"] = app.pointer.caption_label.get_text()
    seen["pointer_on_save"] = seen["pointer_at"] == list(save_center)
    seen["text_box_after_question"] = entry.get_text()
    app.listener.stop()
    return seen


def scenario_live_minimize() -> dict:
    """On a display with a window manager: where the pointer lands for "how do i minimize this
    window?" (the title bar's small minimize button, near its right end)."""
    from openclicky_linux.ask import ask as ask_model
    from openclicky_linux.config import load_settings

    window = Gtk.Window(title="Notes — shopping list")
    window.set_default_size(900, 560)
    window.add(Gtk.TextView())
    window.move(500, 200)
    window.show_all()
    pump(1.5)
    frame = window.get_window().get_frame_extents()
    app = wire_up("Control_L", start_recording=StandInRecording, transcribe=lambda _wav: "",
                  ask=lambda question, pictures, conversation, **streaming: ask_model(question, pictures, conversation, load_settings(), **streaming))
    pump(0.5)
    app.asking.submit("how do i minimize this window?")
    pump(60, until=lambda: len(app.asking.conversation.exchanges) >= 1 and app.pointer.mode != "flying")
    pump(0.5)
    screenshot("live-minimize")
    return {"frame": [frame.x, frame.y, frame.width, frame.height], "pointer": [round(value) for value in app.pointer.position],
            "mode": app.pointer.mode}



# MARK: Wayland mode on Xvfb: the keys come from an input device, the paste is shift+insert

import struct as _struct  # noqa: E402

from openclicky_linux.evdev import EV_KEY, EV_SYN, KEY_LEFTCTRL, KEY_RIGHTCTRL, DeviceKind  # noqa: E402


class ShiftInsertKeyboard:
    """Stands in for the uinput virtual keyboard: the same keystroke, shift+insert, through XTEST
    (the X server here is the compositor the keystroke would reach)."""

    def __init__(self) -> None:
        self.pastes = 0

    def paste(self) -> None:
        self.pastes += 1
        key("Shift_L", True)
        key("Insert", True)
        key("Insert", False)
        key("Shift_L", False)


def _device_event(code: int, value: int) -> bytes:
    now = time.time()
    return (_struct.pack("llHHi", int(now), int(now % 1 * 1_000_000), EV_KEY, code, value)
            + _struct.pack("llHHi", int(now), int(now % 1 * 1_000_000), EV_SYN, 0, 0))


class StandInCompositor:
    """What mutter, KWin and wlroots do when an XWayland client takes the clipboard: ask it for
    TARGETS at once, to offer the selection to Wayland apps. Watches with XFixes, on its own thread."""

    def __init__(self, take_up: bool = True, selections: tuple[str, ...] = ("CLIPBOARD", "PRIMARY")) -> None:
        import threading

        from Xlib.ext import xfixes

        self.display = xdisplay.Display()
        self.display.xfixes_query_version()
        self.window = self.display.screen().root.create_window(0, 0, 1, 1, 0, X.CopyFromParent)
        self.requests = 0
        self.take_up = take_up
        for name in selections:
            self.display.xfixes_select_selection_input(self.window, self.display.intern_atom(name),
                                                       xfixes.XFixesSetSelectionOwnerNotifyMask)
        self.display.flush()
        threading.Thread(target=self._run, daemon=True).start()

    def _run(self) -> None:
        targets = self.display.intern_atom("TARGETS")
        while True:
            event = self.display.next_event()
            if self.take_up and getattr(event, "owner", None) and event.owner.id not in (0, self.window.id):
                self.requests += 1
                self.window.convert_selection(event.selection, targets, targets, X.CurrentTime)
                self.display.flush()


def _dictate_on_wayland(dictation_key: str, dictation_code: int, compositor_takes_up: bool = True,
                        compositor_selections: tuple[str, ...] = ("CLIPBOARD", "PRIMARY")) -> dict:
    compositor = StandInCompositor(take_up=compositor_takes_up, selections=compositor_selections)
    _window, entry = text_box_window()
    clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
    primary = Gtk.Clipboard.get(Gdk.SELECTION_PRIMARY)
    clipboard.set_text("what was on the clipboard before", -1)
    primary.set_text("what was selected before", -1)
    pump(0.1)
    read_end, write_end = os.pipe()
    os.set_blocking(read_end, False)
    virtual_keyboard = ShiftInsertKeyboard()
    app = wire_up(dictation_key, start_recording=StandInRecording, transcribe=lambda _wav: "um, hello from open clicky",
                  wayland=True, virtual_keyboard=virtual_keyboard, input_device_paths=lambda: ["stand-in keyboard"])
    app.listener._probe = lambda path: (read_end, "stand-in keyboard", DeviceKind(keyboard=True, pointer=False))
    orb_history: list = []
    app.dictation.orb.listeners.append(lambda orb: orb_history.append([orb.phase, orb.message]))
    app.listener.start()
    pump(0.3)
    os.write(write_end, _device_event(dictation_code, 1))
    pump(0.9)
    os.write(write_end, _device_event(dictation_code, 0))
    pump(5, until=lambda: bool(entry.get_text()) or any(phase == "failed" for phase, _ in orb_history))
    pump(1.0)  # past the clipboard restore
    app.listener.stop()
    return {"text_box": entry.get_text(), "clipboard": clipboard.wait_for_text(), "primary": primary.wait_for_text(),
            "pastes": virtual_keyboard.pastes, "orb_history": orb_history, "compositor_requests": compositor.requests,
            "pointer_knows_the_mouse": app.pointer.knows_the_mouse, "pointer_visible": app.pointer.triangle.get_visible()}


def scenario_wayland_dictate_with_right_ctrl() -> dict:
    return _dictate_on_wayland("Control_R", KEY_RIGHTCTRL)


def scenario_wayland_dictate_with_left_ctrl() -> dict:
    # The default key, shared with the ask box: it listens once held alone for 0.3 s.
    return _dictate_on_wayland("Control_L", KEY_LEFTCTRL)


def scenario_wayland_compositor_without_a_primary_selection() -> dict:
    return _dictate_on_wayland("Control_R", KEY_RIGHTCTRL, compositor_selections=("CLIPBOARD",))


def scenario_wayland_compositor_never_takes_up_the_words() -> dict:
    return _dictate_on_wayland("Control_R", KEY_RIGHTCTRL, compositor_takes_up=False)


# MARK: the first-run setup and the Words window


def scenario_setup_window_walkthrough() -> dict:
    from openclicky_linux import autostart, preferences
    from openclicky_linux.onboarding import SetupWindow

    finished: list = []
    window = SetupWindow(wayland=False, on_closed=finished.append)
    window.show()
    pump(0.4)
    seen = []
    for _ in range(12):
        page = window.page
        seen.append([page.page_id, page.title.get_text(), window.primary_button.get_label(),
                     window.secondary_button.get_label() if window.secondary_button.get_visible() else None])
        screenshot(f"setup-{page.page_id}")
        if page.page_id == "answers":
            window.account_form.email_entry.set_text("not an email")
            window.account_form.start()
            pump(0.1)
            seen.append(["answers status", page.status.get_text()])
            window.key_entry.set_text("sk-ant-test-key")
            window._use_key()
            seen.append(["answers status", page.status.get_text()])
        if page.page_id == "offline":
            window.secondary_button.clicked()  # skip the 840 MB
        else:
            window.primary_button.clicked()
        pump(0.3)
        if finished:
            break
    return {"seen": seen, "finished": finished, "setup_done": preferences.get(preferences.SETUP_DONE),
            "autostart": autostart.is_enabled(), "launcher": autostart.launcher_path().exists(), "icon": autostart.icon_path().exists()}


def scenario_words_window() -> dict:
    from openclicky_linux.history import TakeRecord, TakeStore
    from openclicky_linux.space import load_space
    from openclicky_linux.words_window import WordsWindow

    store = TakeStore()
    store.insert(TakeRecord(raw_text="um hello from the test", formatted_text="Hello from the test", app_name="gedit"))
    store.insert(TakeRecord(raw_text="see you at five", formatted_text="See you at five", app_name="Slack"))
    store.insert(TakeRecord(raw_text="", formatted_text="", app_name="gedit", status="failed", failure_reason="couldn't reach openai",
                            created_at=1.0))
    window = WordsWindow(store)
    window.show()
    pump(0.4)
    shown = [row[3] for row in window.history_rows]
    window.search.set_text("hello")
    pump(0.5)
    searched = [row[3] for row in window.history_rows]
    window.search.set_text("")
    window.filter.set_active_id("failed")
    pump(0.3)
    failed = [row[3] for row in window.history_rows]
    window.filter.set_active_id("all")
    pump(0.5)
    screenshot("words-history")
    window.notebook.set_current_page(1)
    window.dictionary_editor.key_entry.set_text("aditya shatriya, aditya")
    window.dictionary_editor.value_entry.set_text("Aaditya Kshatriya")
    window.dictionary_editor.add()
    window.notebook.set_current_page(2)
    window.shortcuts_editor.key_entry.set_text("my sign off")
    window.shortcuts_editor.value_entry.set_text("Best,\\nSam")
    window.shortcuts_editor.add()
    pump(0.3)
    screenshot("words-shortcuts")
    space = load_space()
    return {"shown": shown, "searched": searched, "failed": failed, "stats": window.stats_label.get_text(),
            "dictionary": [[term.written, list(term.heard_as)] for term in space.dictionary],
            "shortcuts": [[shortcut.trigger, shortcut.replacement] for shortcut in space.shortcuts]}


def scenario_live_typing() -> dict:
    """Live typing's keystrokes into a text box: the layout's own keys, and characters it lacks."""
    from openclicky_linux.x11 import LiveKeyboard

    window = Gtk.Window(title="typing target")
    entry = Gtk.Entry()
    window.add(entry)
    window.show_all()
    window.present()
    pump(1.0, until=lambda: entry.has_focus())
    keyboard = LiveKeyboard()
    typed = keyboard.type_text("Héllo → wörld") and keyboard.type_text(", OK? é")
    pump(1.5)
    return {"typed": typed, "text": entry.get_text(), "given_back": not keyboard._borrowed}


if __name__ == "__main__":
    print(json.dumps(globals()[f"scenario_{sys.argv[1]}"]()))
