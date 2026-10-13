"""Makes the screenshots in docs/media/linux/ from staged scenes, so every picture in docs/linux.md
can be made again (and checked) rather than taken on faith.

Run through make-doc-screenshots.sh, which starts a private X display (Xvfb) with the Xfce window
manager for title bars and a compositor; nothing here touches the desktop you are using.

    linux/scripts/make-doc-screenshots.sh            # a scripted answer (no key, no network)
    linux/scripts/make-doc-screenshots.sh --live     # the answer from your own key in ~/.openclicky

With --live, the question and a screenshot of the private display (only the staged window) go to
the model your key routes to.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import time
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("GdkX11", "3.0")
from gi.repository import Gdk, GdkX11, Gtk  # noqa: E402,F401
from Xlib import X, XK, display as xdisplay  # noqa: E402
from Xlib.ext import xtest  # noqa: E402

LINUX_DIRECTORY = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(LINUX_DIRECTORY))

from openclicky_linux.__main__ import wire_up  # noqa: E402
from openclicky_linux.ask import ask as ask_model  # noqa: E402
from openclicky_linux.config import load_settings  # noqa: E402
from openclicky_linux.recorder import SAMPLE_RATE  # noqa: E402

OUTPUT_DIRECTORY = LINUX_DIRECTORY.parent / "docs" / "media" / "linux"
keyboard = xdisplay.Display()


def pump(seconds: float, until=lambda: False) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline and not until():
        while Gtk.events_pending():
            Gtk.main_iteration_do(False)
        time.sleep(0.01)


def key(keysym_name: str, is_down: bool) -> None:
    xtest.fake_input(keyboard, X.KeyPress if is_down else X.KeyRelease, keyboard.keysym_to_keycode(XK.string_to_keysym(keysym_name)))
    keyboard.sync()


def tap(keysym_name: str) -> None:
    key(keysym_name, True)
    time.sleep(0.03)
    key(keysym_name, False)


def type_text(text: str) -> None:
    for character in text:
        keysym_name = {" ": "space", "?": "question", "'": "apostrophe"}.get(character, character)
        keysym = XK.string_to_keysym(keysym_name)
        needs_shift = keyboard.keycode_to_keysym(keyboard.keysym_to_keycode(keysym), 0) != keysym
        if needs_shift:
            key("Shift_L", True)
        tap(keysym_name)
        if needs_shift:
            key("Shift_L", False)
        time.sleep(0.02)
        pump(0.01)


def move_mouse(x: int, y: int) -> None:
    xtest.fake_input(keyboard, X.MotionNotify, x=x, y=y)
    keyboard.sync()


def click(x: int, y: int) -> None:
    move_mouse(x, y)
    pump(0.1)
    xtest.fake_input(keyboard, X.ButtonPress, 1)
    xtest.fake_input(keyboard, X.ButtonRelease, 1)
    keyboard.sync()
    pump(0.2)


def paint_desktop() -> Gtk.Window:
    """A plain desktop behind the windows (a compositor does not draw the root window's colour)."""
    provider = Gtk.CssProvider()
    provider.load_from_data(b".scene-desktop { background-color: #2e3440; }")
    Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
    desktop = Gtk.Window()
    desktop.set_type_hint(Gdk.WindowTypeHint.DESKTOP)
    desktop.get_style_context().add_class("scene-desktop")
    root = Gdk.get_default_root_window()
    desktop.set_default_size(root.get_width(), root.get_height())
    desktop.move(0, 0)
    desktop.show_all()
    pump(0.5)
    return desktop


def save_crop(name: str, left: int, top: int, right: int, bottom: int) -> None:
    pump(0.3)
    root = Gdk.get_default_root_window()
    left, top = max(0, left), max(0, top)
    right, bottom = min(root.get_width(), right), min(root.get_height(), bottom)
    pixbuf = Gdk.pixbuf_get_from_window(root, left, top, right - left, bottom - top)
    path = OUTPUT_DIRECTORY / f"{name}.png"
    pixbuf.savev(str(path), "png", ["compression"], ["9"])
    print(f"wrote {path.relative_to(LINUX_DIRECTORY.parent)} ({right - left}x{bottom - top})")


def frame_of(window: Gtk.Window) -> tuple[int, int, int, int]:
    extents = window.get_window().get_frame_extents()
    return extents.x, extents.y, extents.x + extents.width, extents.y + extents.height


def notes_window() -> tuple[Gtk.Window, Gtk.TextView, dict[str, Gtk.Button]]:
    """A small notes app: a toolbar of labelled buttons, a text area, a delete button."""
    window = Gtk.Window(title="Notes — shopping list")
    window.set_icon_name("accessories-text-editor")
    window.set_default_size(760, 440)
    layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
    layout.set_border_width(8)
    toolbar = Gtk.Box(spacing=8)
    buttons = {}
    for label in ("New", "Open", "Save", "Share", "Settings"):
        button = Gtk.Button(label=label)
        toolbar.pack_start(button, False, False, 0)
        buttons[label] = button
    layout.pack_start(toolbar, False, False, 0)
    text = Gtk.TextView()
    text.set_left_margin(8)
    text.set_top_margin(8)
    text.get_buffer().set_text("milk\neggs\nbread\n")
    scroller = Gtk.ScrolledWindow()
    scroller.add(text)
    layout.pack_start(scroller, True, True, 0)
    footer = Gtk.Box()
    delete = Gtk.Button(label="Delete note")
    footer.pack_end(delete, False, False, 0)
    buttons["Delete note"] = delete
    layout.pack_start(footer, False, False, 0)
    window.add(layout)
    # Low enough that the ask box (120 px from the top) sits above it, not over its title bar.
    window.move(260, 210)
    window.show_all()
    pump(1.0)
    return window, text, buttons


def center_of(widget: Gtk.Widget) -> list[int]:
    allocation = widget.get_allocation()
    origin_x, origin_y = widget.get_window().get_origin()[1:3]
    return [origin_x + allocation.x + allocation.width // 2, origin_y + allocation.y + allocation.height // 2]


def photos_window():
    """A photos app whose Settings button opens a small dialog with a Dark mode checkbox."""
    window = Gtk.Window(title="Photos")
    window.set_icon_name("image-x-generic")
    window.set_default_size(760, 440)
    layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
    layout.set_border_width(8)
    toolbar = Gtk.Box(spacing=8)
    for label in ("Import", "Share"):
        toolbar.pack_start(Gtk.Button(label=label), False, False, 0)
    settings_button = Gtk.Button(label="Settings")
    toolbar.pack_end(settings_button, False, False, 0)
    layout.pack_start(toolbar, False, False, 0)
    layout.pack_start(Gtk.Label(label="12 photos"), True, True, 0)
    window.add(layout)
    window.move(260, 150)
    window.show_all()
    dialog = Gtk.Window(title="Photos settings")
    dialog.set_icon_name("image-x-generic")
    dialog.set_transient_for(window)
    dialog.set_default_size(320, 160)
    dialog_layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
    dialog_layout.set_border_width(16)
    dialog_layout.pack_start(Gtk.CheckButton(label="Show file names"), False, False, 0)
    dark_mode = Gtk.CheckButton(label="Dark mode")
    dialog_layout.pack_start(dark_mode, False, False, 0)
    dialog.add(dialog_layout)
    dialog.move(480, 300)
    settings_button.connect("clicked", lambda _button: (dialog.show_all(), dialog.present()))
    pump(1.0)
    return window, settings_button, dialog, dark_mode


class StandInRecording:
    def stop(self) -> bytes:
        return b"\x01\x00" * SAMPLE_RATE

    def cancel(self) -> None:
        pass


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--live", action="store_true", help="answer with your own key instead of a scripted answer")
    arguments = parser.parse_args()
    OUTPUT_DIRECTORY.mkdir(parents=True, exist_ok=True)
    paint_desktop()

    window, text, buttons = notes_window()
    save_box = buttons["Save"]

    def scripted_answer(question, screenshot, conversation):
        allocation = save_box.get_allocation()
        origin_x, origin_y = save_box.get_window().get_origin()[1:3]
        center_x = origin_x + allocation.x + allocation.width // 2
        center_y = origin_y + allocation.y + allocation.height // 2
        time.sleep(0.5)
        return f"[POINT:{center_x},{center_y}:save button] that save button in the toolbar keeps your list."

    live_settings = load_settings() if arguments.live else None
    app = wire_up("Control_R", start_recording=StandInRecording, transcribe=lambda _wav: "um, and two lemons",
                  ask=(lambda question, screenshot, conversation: ask_model(question, screenshot, conversation, live_settings))
                  if arguments.live else scripted_answer)
    app.listener.start()
    left, top, right, bottom = frame_of(window)

    # 1. The ask box: left ctrl twice, a question typed.
    move_mouse(520, 420)
    pump(0.4)
    tap("Control_L")
    pump(0.1)
    tap("Control_L")
    pump(2, until=lambda: app.composer.is_open and app.composer.entry.has_focus())
    type_text("where do i save this?")
    composer_left, composer_top, composer_right, _composer_bottom = frame_of(app.composer.window)
    save_crop("ask-box", min(left, composer_left) - 24, composer_top - 24, max(right, composer_right) + 24, top + 190)

    # 2. The answer: the pointer flies to what it names and types the answer beside itself.
    tap("Return")
    pump(60, until=lambda: not app.asking.is_answering and app.pointer.mode != "flying"
         and len(app.pointer.caption_label.get_text()) > 12)
    pump(3.0)  # the caption finishes typing
    answer = app.asking.conversation.exchanges[-1][1] if app.asking.conversation.exchanges else ""
    print(f"answer: {answer!r}  pointer at {[round(value) for value in app.pointer.position]}")
    caption_left, caption_top, caption_right, caption_bottom = frame_of(app.pointer.caption)
    save_crop("ask-answer", left - 24, top - 24, max(right, caption_right) + 24, max(bottom, caption_bottom) + 24)
    tap("Escape")
    pump(2, until=lambda: app.pointer.mode == "following")

    # 3. Dictation: hold right ctrl (a stand-in microphone hears "um, and two lemons"), let go.
    buffer = text.get_buffer()
    click(left + 120, top + 170)
    buffer.place_cursor(buffer.get_end_iter())
    pump(0.3)
    key("Control_R", True)
    pump(0.8)
    orb = app.dictation.orb
    save_crop("dictation-listening", left - 24, top - 24, right + 24, bottom + 120)
    key("Control_R", False)
    pump(5, until=lambda: orb.phase == "done")
    pump(0.6)
    save_crop("dictation-done", left - 24, top - 24, right + 24, bottom + 120)

    # 4. Step by step: "how do i turn on dark mode?" in an app whose Settings button opens a dialog.
    window.hide()
    photos, photos_settings, dialog, dark_mode = photos_window()
    if not arguments.live:
        scripted_steps = iter([
            lambda: "[POINT:{}:settings] open settings first. [NEXT]".format(",".join(map(str, center_of(photos_settings)))),
            lambda: "[POINT:{},{}:dark mode] now tick dark mode.".format(center_of(dark_mode)[0] - 34, center_of(dark_mode)[1]),
        ])
        app.asking.host.ask = lambda question, screenshot, conversation: (time.sleep(0.4), next(scripted_steps)())[1]
    move_mouse(300, 560)
    pump(0.3)
    answers_before = len(app.asking.conversation.exchanges)
    app.asking.submit("how do i turn on dark mode?")
    pump(60, until=lambda: len(app.asking.conversation.exchanges) > answers_before and app.pointer.mode != "flying")
    pump(2.0)
    click(*[round(value) for value in app.pointer.position])  # do the step where the pointer shows it
    pump(60, until=lambda: len(app.asking.conversation.exchanges) > answers_before + 1 and app.pointer.mode != "flying")
    pump(3.0)
    print(f"guide: {[answer for _question, answer in app.asking.conversation.exchanges[answers_before:]]}")
    photos_left, photos_top, photos_right, photos_bottom = frame_of(photos)
    caption_right, caption_bottom = frame_of(app.pointer.caption)[2:]
    save_crop("guided-step", photos_left - 24, photos_top - 24, max(photos_right, caption_right) + 24, max(photos_bottom, caption_bottom) + 24)
    app.asking.escape()
    dialog.hide()
    photos.hide()

    # 5. Settings, with example keys (never real ones), on its own.
    app.settings_window.show()
    pump(1.0)
    settings = app.settings_window
    settings.fields["openrouter_api_key"].set_text("sk-or-v1-example-not-a-real-key")
    settings.fields["pointer_presence"].set_active_id("onShake")
    pump(0.5)
    save_crop("settings", *[edge + offset for edge, offset in zip(frame_of(settings.window), (-24, -24, 24, 24))])
    app.listener.stop()
    print(json.dumps({"answer": answer}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
