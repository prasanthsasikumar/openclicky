"""The agent's result card (the Mac's `NotchHUDPanels` card, Windows' `AgentResultCardWindow`): when
an agent task ends, a card in the top-right corner holds what it said and the files it made, with
Copy, "Follow up" (the ask box, whose next question goes to the same agent thread) and a close
button. It never takes the keyboard from the app in front; it stays until it is closed.
"""

from __future__ import annotations

from pathlib import Path
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, Gio, GLib, Gtk  # noqa: E402

from .agent import AgentRunResult  # noqa: E402

MARGIN = 18
WIDTH = 360


def card_title(result: AgentRunResult) -> str:
    if result.status == "completed":
        return "the agent finished"
    return f"the agent stopped: {result.error_message or result.status}"


class ResultCard:
    def __init__(self, on_follow_up: Callable[[], None]) -> None:
        self.on_follow_up = on_follow_up
        self.window = Gtk.Window(title="OpenClicky agent")
        self.window.set_type_hint(Gdk.WindowTypeHint.UTILITY)
        self.window.set_keep_above(True)
        self.window.set_accept_focus(False)
        self.window.set_focus_on_map(False)
        self.window.set_decorated(False)
        self.window.set_resizable(False)
        self.window.set_default_size(WIDTH, -1)
        self.window.connect("delete-event", lambda *_: self.window.hide() or True)
        frame = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        frame.set_border_width(14)
        heading = Gtk.Box(spacing=8)
        self.title = Gtk.Label(xalign=0)
        heading.pack_start(self.title, True, True, 0)
        close = Gtk.Button(label="✕")
        close.set_relief(Gtk.ReliefStyle.NONE)
        close.connect("clicked", lambda _button: self.window.hide())
        heading.pack_start(close, False, False, 0)
        frame.pack_start(heading, False, False, 0)
        self.text = Gtk.Label(xalign=0)
        self.text.set_line_wrap(True)
        self.text.set_selectable(True)
        self.text.set_max_width_chars(48)
        scroller = Gtk.ScrolledWindow()
        scroller.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroller.set_max_content_height(260)
        scroller.set_propagate_natural_height(True)
        scroller.add(self.text)
        frame.pack_start(scroller, True, True, 0)
        self.files = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
        frame.pack_start(self.files, False, False, 0)
        buttons = Gtk.Box(spacing=8)
        copy = Gtk.Button(label="Copy")
        copy.connect("clicked", lambda _button: self._copy())
        follow_up = Gtk.Button(label="Follow up with the agent")
        follow_up.connect("clicked", lambda _button: (self.window.hide(), self.on_follow_up()))
        buttons.pack_start(copy, False, False, 0)
        buttons.pack_start(follow_up, False, False, 0)
        frame.pack_start(buttons, False, False, 0)
        self.window.add(frame)
        self.result = AgentRunResult()

    def show(self, result: AgentRunResult) -> None:
        self.result = result
        self.title.set_markup(f"<b>{GLib.markup_escape_text(card_title(result))}</b>")
        self.text.set_text(result.text.strip() or "done.")
        for child in self.files.get_children():
            self.files.remove(child)
        for artifact in result.artifacts[:6]:
            button = Gtk.Button(label=Path(artifact).name)
            button.set_relief(Gtk.ReliefStyle.NONE)
            button.set_tooltip_text(artifact)
            button.get_child().set_xalign(0)
            button.connect("clicked", lambda _button, artifact=artifact: _show_file(artifact))
            self.files.pack_start(button, False, False, 0)
        self.window.show_all()
        self._place()

    def _place(self) -> None:
        display = Gdk.Display.get_default()
        area = (display.get_primary_monitor() or display.get_monitor(0)).get_workarea()
        width, _height = self.window.get_size()
        self.window.move(area.x + area.width - width - MARGIN, area.y + MARGIN)

    def _copy(self) -> None:
        Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).set_text(self.result.text.strip(), -1)


def _show_file(path: str) -> None:
    """The file's folder in the file manager."""
    try:
        Gio.AppInfo.launch_default_for_uri(Path(path).expanduser().parent.as_uri(), None)
    except GLib.Error:
        pass
