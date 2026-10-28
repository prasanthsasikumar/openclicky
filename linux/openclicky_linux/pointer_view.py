"""The pointer: a small orange-red triangle beside the mouse (the Mac's overlay cursor colour), that
flies along an arc to what an answer is about, with a caption bubble typed out beside it.

Two popup windows, so neither ever takes the focus: the triangle (an orange window cut to the
triangle's outline with the SHAPE extension, so it needs no compositor and no cairo bridge, and with
an empty input shape, so clicks go straight through it) and the caption. Follows the mouse at 30 Hz,
a little below and to the right so the real cursor stays clear. Whether it shows while following is
the presence setting (`presence.py`: always, while the mouse moves, after a shake, only while
answering); flying, pointing or captioning, it always shows.

On Wayland the mouse's position is never told to another app, so the pointer cannot follow it: it
rests out of sight at the top centre of the main monitor (where the Mac's pointer flies out of the
notch), says "thinking…" there, flies out to what the answer is about, and flies home after. Once
its answer has been read, moving the mouse sends it home, out of the way of the click.

Clicks pass through both windows on Wayland too, with one catch (measured on GNOME 42 with a test
mouse): an XWayland window that moves after it is mapped keeps blocking clicks, its empty input
shape left behind, and setting the shape again does not help. So the caption is sized and placed
for its whole sentence before it shows (the untyped rest is drawn in the bubble's own colour: it
never grows or moves while typing), and on Wayland a window that moved is unmapped and mapped again
where it landed (`_settle`), which lets clicks through again.
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
# Wayland: where the pointer rests, below the top of the main monitor's work area (under the top bar).
# Below the island's busy strip (island.py), which hangs from the top centre too: their captions
# overlapped when both sat at the top edge.
HOME_TOP_MARGIN = 52
# Wayland: once an answer is typed out, this long before a mouse movement sends the pointer home.
STEP_ASIDE_AFTER_SECONDS = 1.0

CAPTION_COLOUR = "#f0452b"
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


def _contains(area, x: int, y: int) -> bool:
    return area.x <= x < area.x + area.width and area.y <= y < area.y + area.height


class PointerView:
    def __init__(self, presence: str = "always", on_caption_finished: Callable[[], None] = lambda: None,
                 rest_delay_seconds: float = 3.0, knows_the_mouse: bool = True) -> None:
        # False on Wayland: the pointer rests at its home and shows only while answering.
        self.knows_the_mouse = knows_the_mouse
        self.tracker = PointerPresenceTracker(presence if knows_the_mouse else "whenAnswering", rest_delay_seconds=rest_delay_seconds)
        # When the caption finished typing (None while typing or hidden): a movement after that
        # sends the pointer home on Wayland.
        self._caption_typed_at: float | None = None
        self.on_caption_finished = on_caption_finished
        self.mode = "following"  # "following" | "flying" | "pointing"
        self.reduce_motion = False
        # Docked (the island's "dock"): the pointer waits at the top of the screen, by the island,
        # until an answer needs it, as it always does on Wayland.
        self.docked = False
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
        Gdk.Screen.get_default().connect("monitors-changed", lambda _screen: self.displays_changed())

    # MARK: where it is

    @staticmethod
    def mouse_position() -> tuple[int, int]:
        _screen, mouse_x, mouse_y = Gdk.Display.get_default().get_default_seat().get_pointer().get_position()[0:3]
        return mouse_x, mouse_y

    @staticmethod
    def home_position() -> tuple[float, float]:
        """Wayland's resting place: the top centre of the main monitor's work area."""
        display = Gdk.Display.get_default()
        monitor = display.get_primary_monitor() or display.get_monitor(0)
        area = monitor.get_workarea()
        return float(area.x + (area.width - POINTER_WIDTH) // 2), float(area.y + HOME_TOP_MARGIN)

    def displays_changed(self) -> None:
        """A monitor came or went (the Mac rebuilds its overlays): a pointer left pointing where no
        monitor is now comes back, and on Wayland the resting pointer moves to the new home."""
        display = Gdk.Display.get_default()
        point_x, point_y = round(self.position[0]), round(self.position[1])
        on_a_monitor = any(_contains(display.get_monitor(index).get_geometry(), point_x, point_y)
                           for index in range(display.get_n_monitors()))
        if self.mode == "following":
            self.position = self._follow_target()
            self._move_triangle()
        elif not on_a_monitor:
            self.dismiss_caption()
            self.return_to_mouse()

    def _follow_target(self) -> tuple[float, float]:
        if not self.knows_the_mouse or self.docked:
            return self.home_position()
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
        if self.knows_the_mouse:
            self.tracker.presence = presence
        self._apply_presence()

    def mouse_moved(self) -> None:
        """Wayland: the mouse moved somewhere. An answer read for a moment is put away (its caption
        dismissed, the pointer sent home), so neither is in the way of the click that follows."""
        if self.knows_the_mouse or self._caption_typed_at is None or not self.caption.get_visible():
            return
        if self._caption_hold_seconds is None or time.monotonic() - self._caption_typed_at < STEP_ASIDE_AFTER_SECONDS:
            return
        self.dismiss_caption()
        self.on_caption_finished()

    def raise_above_other_windows(self) -> None:
        for window in (self.triangle, self.caption):
            if window.get_visible() and window.get_window() is not None:
                window.get_window().raise_()
        self._ticks_since_raise = 0

    def _follow_tick(self) -> bool:
        self._ticks_since_raise += 1
        if self._ticks_since_raise >= RAISE_EVERY_FOLLOW_TICKS:
            self.raise_above_other_windows()
        if not self.knows_the_mouse:
            # Nothing to follow: the pointer rests at home, out of sight unless it is answering.
            self._apply_presence()
            return True
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
        # Reduce motion (Settings → General): the flight takes no time, so the pointer is simply there.
        seconds = 0.01 if self.reduce_motion else flight_seconds(start, destination)
        self._flight = (start, destination, time.monotonic(), seconds, then)
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
            self._settle()
            return False
        return True

    # MARK: the caption

    def _caption_markup(self, typed: int) -> str:
        """The typed part, then the rest in the bubble's own colour: there, but not seen yet."""
        shown, hidden = self._caption_text[:typed], self._caption_text[typed:]
        return GLib.markup_escape_text(shown) + (f'<span foreground="{CAPTION_COLOUR}">{GLib.markup_escape_text(hidden)}</span>' if hidden else "")

    def _settle(self) -> None:
        """Wayland: windows that moved while mapped are mapped again where they are, so clicks pass
        through them (see the module's note). Not seen: no frame is drawn in between."""
        if self.knows_the_mouse:
            return
        for window in (self.triangle, self.caption):
            if window.get_visible():
                window.hide()
                window.show_all()

    def show_caption(self, text: str, hold_seconds: float | None, continue_typing: bool = False) -> None:
        """Types `text` out beside the pointer; holds it `hold_seconds` once typed (None: until replaced).
        `continue_typing`: `text` carries on the caption showing (a streamed answer that grew), so the
        letters already typed stay and typing goes on from there."""
        if continue_typing and self.caption.get_visible() and text.startswith(self._caption_text):
            typed = self._caption_typed
        else:
            typed = 0
        if self.reduce_motion:
            # Shown whole, not typed out (the last "letter" is typed by the first tick).
            typed = max(typed, len(text) - 1)
        self._cancel_caption_timers()
        self._caption_text = text
        self._caption_typed = typed
        self._caption_typed_at = None
        # The bubble is sized and placed for the whole caption (up to 42 characters a line) before
        # it shows, so it never grows, wraps differently or moves while it types.
        if not self.knows_the_mouse and self.caption.get_visible():
            self.caption.hide()
        self.caption_label.set_width_chars(min(len(text), 42))
        self.caption_label.set_markup(self._caption_markup(typed))
        self.caption.resize(1, 1)
        self._place_caption(self.caption.get_preferred_size()[1].width)
        self._caption_hold_seconds = hold_seconds
        self.caption.show_all()
        self._apply_presence()
        self.raise_above_other_windows()
        self._caption_timer = GLib.timeout_add(CAPTION_TYPING_MILLISECONDS, self._type_next_character)

    def _type_next_character(self) -> bool:
        self._caption_typed += 1
        self.caption_label.set_markup(self._caption_markup(self._caption_typed))
        if self._caption_typed < len(self._caption_text):
            return True
        self._caption_timer = None
        self._caption_typed_at = time.monotonic()
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
        self._caption_typed_at = None
        self.caption.hide()
        self._apply_presence()

    def _cancel_caption_timers(self) -> None:
        for timer in (self._caption_timer, self._caption_dismiss_timer):
            if timer is not None:
                GLib.source_remove(timer)
        self._caption_timer = None
        self._caption_dismiss_timer = None

    def _place_caption(self, caption_width: int | None = None) -> None:
        if caption_width is None:
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
