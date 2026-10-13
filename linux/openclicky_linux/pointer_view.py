"""The pointer: a small orange-red triangle beside the mouse (the Mac's overlay cursor colour), that
flies along an arc to what an answer is about, with a caption bubble typed out beside it.

Two popup windows, so neither ever takes the focus: the triangle (an orange window cut to the
triangle's outline with the SHAPE extension, so it needs no compositor and no cairo bridge, and with
an empty input shape, so clicks go straight through it) and the caption. Follows the mouse at 30 Hz,
a little below and to the right so the real cursor stays clear. Whether it shows while following is
the presence setting (`presence.py`: always, while the mouse moves, after a shake, only while
answering); flying, pointing or captioning, it always shows.
"""

from __future__ import annotations

import time
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("GdkX11", "3.0")
from gi.repository import Gdk, GdkX11, GLib, Gtk  # noqa: E402,F401

from .pointing import flight_position, flight_seconds  # noqa: E402
from .presence import PointerPresenceTracker  # noqa: E402
from .x11 import polygon_rows, shape_window  # noqa: E402

POINTER_WIDTH = 20
POINTER_HEIGHT = 24
# Where the pointer sits relative to the mouse while following it.
FOLLOW_OFFSET = (14, 16)
CAPTION_MAX_WIDTH = 300
CAPTION_TYPING_MILLISECONDS = 30
FOLLOW_INTERVAL_MILLISECONDS = 33
FLIGHT_FRAME_MILLISECONDS = 16
# A window opened or raised after the pointer (a dialog a guided step opened) would cover it: while
# it shows, the pointer and its caption raise themselves about twice a second, and at once whenever
# they fly or a caption appears.
RAISE_EVERY_FOLLOW_TICKS = 15

_CAPTION_CSS = b"""
.openclicky-pointer { background-color: #f0452b; }
.openclicky-caption { background-color: #f0452b; border-radius: 6px; padding: 5px 8px; }
.openclicky-caption label { color: #ffffff; font-size: 11pt; font-weight: 500; }
"""


# Tip at the top-left (0, 0), like an arrow cursor: the tip is the point it points at.
POINTER_OUTLINE = [(0.0, 0.0), (POINTER_WIDTH, POINTER_HEIGHT * 0.64), (POINTER_WIDTH * 0.44, POINTER_HEIGHT * 0.6),
                   (POINTER_WIDTH * 0.12, POINTER_HEIGHT)]


def _make_click_through(window: Gtk.Window, outline_rows=None) -> None:
    # GTK creates the window on its own connection to the X server: it must have reached the server
    # before another connection can shape it (otherwise BadWindow, and the pointer stays a square).
    display = Gdk.Display.get_default()
    display.sync()
    shape_window(window.get_window().get_xid(), outline_rows=outline_rows, click_through=True, display_name=display.get_name())


class PointerView:
    def __init__(self, presence: str = "always", on_caption_finished: Callable[[], None] = lambda: None,
                 rest_delay_seconds: float = 3.0) -> None:
        self.tracker = PointerPresenceTracker(presence, rest_delay_seconds=rest_delay_seconds)
        self.on_caption_finished = on_caption_finished
        self.mode = "following"  # "following" | "flying" | "pointing"
        self.position = (0.0, 0.0)
        self._flight: tuple[tuple[float, float], tuple[float, float], float, float, str] | None = None
        self._caption_text = ""
        self._caption_typed = 0
        self._caption_timer: int | None = None
        self._caption_dismiss_timer: int | None = None

        self.triangle = Gtk.Window(type=Gtk.WindowType.POPUP)
        self.triangle.set_default_size(POINTER_WIDTH, POINTER_HEIGHT)
        self.triangle.set_size_request(POINTER_WIDTH, POINTER_HEIGHT)
        self.triangle.set_accept_focus(False)
        self.triangle.set_keep_above(True)
        self.triangle.get_style_context().add_class("openclicky-pointer")
        # Shaped again every time it is shown: some window managers reset a shape on map.
        self.triangle.connect("map-event", lambda window, _event: _make_click_through(
            window, outline_rows=polygon_rows(POINTER_OUTLINE, POINTER_WIDTH, POINTER_HEIGHT)))

        provider = Gtk.CssProvider()
        provider.load_from_data(_CAPTION_CSS)
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        if not Gdk.Screen.get_default().is_composited():
            # Round corners need a compositor; without one they show the window's square corners.
            square = Gtk.CssProvider()
            square.load_from_data(b".openclicky-caption { border-radius: 0; }")
            Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), square, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION + 1)
        self.caption = Gtk.Window(type=Gtk.WindowType.POPUP)
        self.caption.set_accept_focus(False)
        self.caption.set_keep_above(True)
        box = Gtk.Box()
        box.get_style_context().add_class("openclicky-caption")
        self.caption_label = Gtk.Label()
        self.caption_label.set_line_wrap(True)
        self.caption_label.set_max_width_chars(42)
        self.caption_label.set_xalign(0)
        box.add(self.caption_label)
        self.caption.add(box)
        self.caption.connect("map-event", lambda window, _event: _make_click_through(window))

        self.position = self._follow_target()
        self._move_triangle()
        self._apply_presence()
        self._ticks_since_raise = 0
        GLib.timeout_add(FOLLOW_INTERVAL_MILLISECONDS, self._follow_tick)

    # MARK: where it is

    @staticmethod
    def mouse_position() -> tuple[int, int]:
        _screen, mouse_x, mouse_y = Gdk.Display.get_default().get_default_seat().get_pointer().get_position()[0:3]
        return mouse_x, mouse_y

    def _follow_target(self) -> tuple[float, float]:
        mouse_x, mouse_y = self.mouse_position()
        return float(mouse_x + FOLLOW_OFFSET[0]), float(mouse_y + FOLLOW_OFFSET[1])

    def _move_triangle(self) -> None:
        self.triangle.move(round(self.position[0]), round(self.position[1]))
        if self.caption.get_visible():
            self._place_caption()

    @property
    def presence(self) -> str:
        return self.tracker.presence

    def hide_for_screenshot(self) -> None:
        """Out of sight while a question's screenshot is taken; `show_after_screenshot` brings it back."""
        self._hidden_for_screenshot = True
        self.dismiss_caption()
        self.triangle.hide()

    def show_after_screenshot(self) -> None:
        self._hidden_for_screenshot = False
        self._apply_presence()

    def _apply_presence(self) -> None:
        if getattr(self, "_hidden_for_screenshot", False):
            return
        visible = self.tracker.is_awake or self.mode != "following" or self.caption.get_visible()
        if visible and not self.triangle.get_visible():
            # Placed where it belongs before it shows, so it never appears where it was last hidden.
            if self.mode == "following":
                self.position = self._follow_target()
            self._move_triangle()
            self.triangle.show_all()
        elif not visible and self.triangle.get_visible():
            self.triangle.hide()

    def set_presence(self, presence: str) -> None:
        self.tracker.presence = presence
        self._apply_presence()

    def raise_above_other_windows(self) -> None:
        for window in (self.triangle, self.caption):
            if window.get_visible() and window.get_window() is not None:
                window.get_window().raise_()
        self._ticks_since_raise = 0

    def _follow_tick(self) -> bool:
        self._ticks_since_raise += 1
        if self._ticks_since_raise >= RAISE_EVERY_FOLLOW_TICKS:
            self.raise_above_other_windows()
        mouse_x, mouse_y = self.mouse_position()
        self.tracker.update(mouse_x, mouse_y, time.monotonic())
        if self.mode == "following":
            target = (float(mouse_x + FOLLOW_OFFSET[0]), float(mouse_y + FOLLOW_OFFSET[1]))
            if target != self.position:
                self.position = target
                self._move_triangle()
        self._apply_presence()
        return True

    # MARK: flying

    def fly_to(self, screen_x: int, screen_y: int) -> None:
        self._start_flight((float(screen_x), float(screen_y)), then="pointing")

    def return_to_mouse(self) -> None:
        if self.mode == "following":
            return
        self._start_flight(self._follow_target(), then="following")

    def _start_flight(self, destination: tuple[float, float], then: str) -> None:
        start = self.position
        self.mode = "flying"
        self._apply_presence()
        self.raise_above_other_windows()
        self._flight = (start, destination, time.monotonic(), flight_seconds(start, destination), then)
        GLib.timeout_add(FLIGHT_FRAME_MILLISECONDS, self._flight_tick)

    def _flight_tick(self) -> bool:
        if self._flight is None:
            return False
        start, destination, started_at, duration, then = self._flight
        if then == "following":
            # The mouse may move during the flight home: aim at where it is now.
            destination = self._follow_target()
            self._flight = (start, destination, started_at, duration, then)
        progress = (time.monotonic() - started_at) / duration
        self.position = flight_position(start, destination, progress)
        self._move_triangle()
        if progress >= 1:
            self.position = destination
            self._move_triangle()
            self._flight = None
            self.mode = then
            if then == "following":
                # Back beside the mouse: shown for now, then it rests away as the presence says.
                self.tracker.wake(time.monotonic())
            self._apply_presence()
            return False
        return True

    # MARK: the caption

    def show_caption(self, text: str, hold_seconds: float | None) -> None:
        """Types `text` out beside the pointer; holds it `hold_seconds` once typed (None: until replaced)."""
        self._cancel_caption_timers()
        self._caption_text = text
        self._caption_typed = 0
        # The bubble is as wide as the whole caption will need (up to 42 characters) from the
        # first letter, so it neither grows while typing nor wraps after every word.
        self.caption_label.set_width_chars(min(len(text), 42))
        self.caption_label.set_text("")
        self._caption_hold_seconds = hold_seconds
        self.caption.show_all()
        self._apply_presence()
        self.raise_above_other_windows()
        self._caption_timer = GLib.timeout_add(CAPTION_TYPING_MILLISECONDS, self._type_next_character)

    def _type_next_character(self) -> bool:
        self._caption_typed += 1
        self.caption_label.set_text(self._caption_text[: self._caption_typed])
        self.caption.resize(1, 1)
        self._place_caption()
        if self._caption_typed < len(self._caption_text):
            return True
        self._caption_timer = None
        if self._caption_hold_seconds is not None:
            self._caption_dismiss_timer = GLib.timeout_add(int(self._caption_hold_seconds * 1000), self._caption_ran_out)
        return False

    def _caption_ran_out(self) -> bool:
        self._caption_dismiss_timer = None
        self.dismiss_caption()
        self.on_caption_finished()
        return False

    def dismiss_caption(self) -> None:
        self._cancel_caption_timers()
        self.caption.hide()
        self._apply_presence()

    def _cancel_caption_timers(self) -> None:
        for timer in (self._caption_timer, self._caption_dismiss_timer):
            if timer is not None:
                GLib.source_remove(timer)
        self._caption_timer = None
        self._caption_dismiss_timer = None

    def _place_caption(self) -> None:
        caption_width, _caption_height = self.caption.get_size()
        pointer_x, pointer_y = self.position
        # The edge that matters is the pointer's own monitor's, not the far side of the desktop.
        monitor = Gdk.Display.get_default().get_monitor_at_point(round(pointer_x), round(pointer_y)).get_geometry()
        monitor_right_edge = monitor.x + monitor.width
        # To the right of the pointer, or to its left when it would run off the monitor.
        if pointer_x + POINTER_WIDTH + 6 + caption_width <= monitor_right_edge - 12:
            caption_x = pointer_x + POINTER_WIDTH + 6
        else:
            caption_x = pointer_x - 6 - caption_width
        self.caption.move(round(caption_x), round(pointer_y + POINTER_HEIGHT * 0.5))
