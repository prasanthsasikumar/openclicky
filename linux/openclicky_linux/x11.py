"""X11: the window in front, the paste keystroke, and the dictation key.

- `focused_window` reads `_NET_ACTIVE_WINDOW` (any EWMH window manager) and falls back to the
  input focus when no window manager sets it, then the window's `WM_CLASS`, so terminals get
  ctrl+shift+v instead of ctrl+v.
- `send_paste_keystroke` posts the keystroke with XTEST to whatever has the focus.
- `shape_window` cuts a window to a shape and/or makes clicks pass through it, with the SHAPE
  extension itself: GTK's own route needs the GTK–cairo bridge (python3-gi-cairo), which many
  systems do not have, and without it the pointer drew as a black square.
- `DictationKeyListener` watches every key press and release through the RECORD extension:
  listen-only, like the Mac app's `CGEvent` tap, so the key keeps working in other apps and
  nothing is grabbed. It runs on its own thread with its own connections.

Wayland deliberately allows none of this to an X client: there `evdev.py`, `uinput.py` and
`portal.py` do these jobs (see `session.py`), and only `shape_window` is still used, through XWayland.
"""

from __future__ import annotations

import threading
import time
from typing import Callable

from Xlib import X, XK, display as xdisplay
from Xlib.error import XError
from Xlib.ext import record, shape, xtest
from Xlib.protocol import rq

from . import applog
from .controller import FocusedWindow

# WM_CLASS values (lowercased) of terminals, where paste is ctrl+shift+v (ctrl+v is a literal ^V).
TERMINAL_WINDOW_CLASSES = frozenset({
    "gnome-terminal", "gnome-terminal-server", "org.gnome.console", "kgx", "konsole", "xterm", "uxterm",
    "urxvt", "rxvt", "kitty", "alacritty", "tilix", "terminator", "xfce4-terminal", "mate-terminal",
    "lxterminal", "qterminal", "terminology", "wezterm", "wezterm-gui", "foot", "st", "st-256color",
    "guake", "yakuake", "tilda", "cool-retro-term", "sakura", "ptyxis",
})


def _window_class(display: xdisplay.Display, window) -> str | None:
    """WM_CLASS of the window or its nearest ancestor that has one (the input focus is often a child)."""
    current = window
    for _ in range(8):
        try:
            wm_class = current.get_wm_class()
            if wm_class:
                return wm_class[1] or wm_class[0]
            tree = current.query_tree()
        except XError:
            return None
        if not tree.parent or tree.parent == tree.root or tree.parent.id == 0:
            return None
        current = tree.parent
    return None


def focused_window(display: xdisplay.Display) -> FocusedWindow | None:
    root = display.screen().root
    window = None
    try:
        active = root.get_full_property(display.intern_atom("_NET_ACTIVE_WINDOW"), X.AnyPropertyType)
        if active and active.value and active.value[0]:
            window = display.create_resource_object("window", int(active.value[0]))
        else:
            focus = display.get_input_focus().focus
            if isinstance(focus, int) or focus is None or focus == root:
                return None
            window = focus
    except XError:
        return None
    app_class = _window_class(display, window)
    is_terminal = bool(app_class) and app_class.lower() in TERMINAL_WINDOW_CLASSES
    return FocusedWindow(window_id=window.id, app_class=app_class, is_terminal=is_terminal)


def send_paste_keystroke(display: xdisplay.Display, with_shift: bool) -> bool:
    """ctrl+v (ctrl+shift+v in a terminal) to the focused window. False without XTEST."""
    if not display.has_extension("XTEST"):
        return False
    keycodes = [display.keysym_to_keycode(XK.XK_Control_L)]
    if with_shift:
        keycodes.append(display.keysym_to_keycode(XK.XK_Shift_L))
    keycodes.append(display.keysym_to_keycode(XK.XK_v))
    if not all(keycodes):
        return False
    for keycode in keycodes:
        xtest.fake_input(display, X.KeyPress, keycode)
    for keycode in reversed(keycodes):
        xtest.fake_input(display, X.KeyRelease, keycode)
    display.sync()
    return True


def polygon_rows(points: list[tuple[float, float]], width: int, height: int) -> list[tuple[int, int, int, int]]:
    """The polygon as one rectangle (x, y, width, 1) per pixel row it covers: the shape the X
    server is given, and the pixels the tray icon is drawn with. Sampled at each row's centre."""
    rows = []
    edges = list(zip(points, points[1:] + points[:1]))
    for row in range(height):
        sample_y = row + 0.5
        crossings = sorted(
            start_x + (sample_y - start_y) * (end_x - start_x) / (end_y - start_y)
            for (start_x, start_y), (end_x, end_y) in edges
            if (start_y <= sample_y < end_y) or (end_y <= sample_y < start_y))
        for left, right in zip(crossings[0::2], crossings[1::2]):
            first_pixel = max(0, round(left))
            last_pixel = min(width, round(right))
            if last_pixel > first_pixel:
                rows.append((first_pixel, row, last_pixel - first_pixel, 1))
    return rows


_shaping_displays: dict[str | None, xdisplay.Display] = {}


def shape_window(window_id: int, outline_rows: list[tuple[int, int, int, int]] | None = None, click_through: bool = True,
                 display_name: str | None = None) -> bool:
    """Cuts the window to `outline_rows` (when given) and gives it an empty input shape (clicks go
    to whatever is under it), on `display_name` (the one GTK is using). False when the X server has
    no SHAPE extension."""
    if display_name not in _shaping_displays:
        _shaping_displays[display_name] = xdisplay.Display(display_name)
    _shaping_display = _shaping_displays[display_name]
    if not _shaping_display.has_extension("SHAPE"):
        return False
    window = _shaping_display.create_resource_object("window", window_id)
    if outline_rows is not None:
        window.shape_rectangles(shape.SO.Set, shape.SK.Bounding, X.Unsorted, 0, 0, outline_rows)
    if click_through:
        window.shape_rectangles(shape.SO.Set, shape.SK.Input, X.Unsorted, 0, 0, [])
    _shaping_display.sync()
    return True


class DictationKeyListener:
    """Calls `on_dictation_key(is_down, at_seconds)`, `on_composer_key(is_down, at_seconds)` and
    `on_other_key_down(is_escape)` from its own thread for every key press and release on the X
    server, and `on_step_input(kind)` for every left click and key press other than the dictation
    and composer keys ("click", "step key" for Return, keypad Enter and Tab, "escape", "other key"):
    what a guided step waits for."""

    def __init__(self, dictation_key_name: str, on_dictation_key: Callable[[bool, float], None],
                 on_other_key_down: Callable[[bool], None], display_name: str | None = None,
                 composer_key_name: str | None = "Control_L",
                 on_composer_key: Callable[[bool, float], None] = lambda _is_down, _at_seconds: None,
                 on_step_input: Callable[[str], None] = lambda _kind: None,
                 ask_key_name: str | None = "Alt_L",
                 on_ask_key: Callable[[bool], None] = lambda _is_down: None) -> None:
        self._control_display = xdisplay.Display(display_name)
        self._record_display = xdisplay.Display(display_name)
        if not self._record_display.has_extension("RECORD"):
            raise RuntimeError("the X server has no RECORD extension, so the dictation key cannot be watched")
        dictation_keysym = XK.string_to_keysym(dictation_key_name)
        if dictation_keysym == X.NoSymbol:
            raise ValueError(f"unknown key name {dictation_key_name!r} (an X keysym such as Control_R, Alt_R, Super_R)")
        self.dictation_keycodes = self._keycodes_for(dictation_keysym)
        if not self.dictation_keycodes:
            raise ValueError(f"no key on this keyboard produces {dictation_key_name}")
        self.escape_keycodes = self._keycodes_for(XK.XK_Escape)
        # Left alt, held with the dictation key: the take is a spoken question (the Mac's ⌃⌥).
        ask_keysym = XK.string_to_keysym(ask_key_name) if ask_key_name else X.NoSymbol
        self.ask_keycodes = (self._keycodes_for(ask_keysym) - self.dictation_keycodes) if ask_keysym != X.NoSymbol else set()
        self._on_ask_key = on_ask_key
        self.step_keycodes = set().union(*(self._keycodes_for(keysym) for keysym in (XK.XK_Return, XK.XK_KP_Enter, XK.XK_Tab)))
        self._on_step_input = on_step_input
        # The key tapped twice for the ask box; never the dictation key itself.
        composer_keysym = XK.string_to_keysym(composer_key_name) if composer_key_name else X.NoSymbol
        self.composer_keycodes = (self._keycodes_for(composer_keysym) - self.dictation_keycodes) if composer_keysym != X.NoSymbol else set()
        self._on_composer_key = on_composer_key
        self._on_dictation_key = on_dictation_key
        self._on_other_key_down = on_other_key_down
        # Our own XTEST keystrokes (the paste) are seen too; they are ignored until this time.
        self._ignore_until = 0.0
        self._context = None
        self._thread: threading.Thread | None = None

    def _keycodes_for(self, keysym: int) -> set[int]:
        return {keycode for keycode, _index in self._control_display.keysym_to_keycodes(keysym)}

    def ignore_keys_for(self, seconds: float) -> None:
        self._ignore_until = time.monotonic() + seconds

    def start(self) -> None:
        self._context = self._record_display.record_create_context(0, [record.AllClients], [{
            "core_requests": (0, 0), "core_replies": (0, 0),
            "ext_requests": (0, 0, 0, 0), "ext_replies": (0, 0, 0, 0),
            "delivered_events": (0, 0), "device_events": (X.KeyPress, X.ButtonRelease),
            "errors": (0, 0), "client_started": False, "client_died": False,
        }])
        self._thread = threading.Thread(target=self._record_display.record_enable_context,
                                        args=(self._context, self._handle_reply), daemon=True)
        self._thread.start()

    def stop(self) -> None:
        if self._context is None:
            return
        self._control_display.record_disable_context(self._context)
        self._control_display.flush()
        if self._thread:
            self._thread.join(timeout=1.0)
        self._record_display.record_free_context(self._context)
        self._context = None

    def _handle_reply(self, reply) -> None:
        # Runs on the RECORD thread: an exception here would end the thread, and every shortcut
        # would stop working with nothing said. Logged and survived instead.
        try:
            self._handle_events(reply)
        except Exception as failure:  # noqa: BLE001
            applog.append(f"key watcher: skipped an event ({type(failure).__name__}: {failure})")

    def _handle_events(self, reply) -> None:
        if reply.category != record.FromServer or reply.client_swapped or not reply.data:
            return
        data = reply.data
        while data:
            event, data = rq.EventField(None).parse_binary_value(data, self._record_display.display, None, None)
            if time.monotonic() < self._ignore_until:
                continue
            if event.type == X.ButtonPress:
                # A click while right ctrl (or left ctrl) is down makes it ctrl+click, not a tap:
                # without this, right ctrl + click (a link in a new tab) started a take that kept the
                # microphone open. The wheel (4–7) scrolls and is not a click.
                if event.detail not in (4, 5, 6, 7):
                    self._on_other_key_down(False)
                continue
            if event.type == X.ButtonRelease:
                # The left button only (1); the wheel and the other buttons are not a step.
                if event.detail == 1:
                    self._on_step_input("click")
                continue
            if event.type not in (X.KeyPress, X.KeyRelease):
                continue
            is_down = event.type == X.KeyPress
            if event.detail in self.dictation_keycodes:
                self._on_dictation_key(is_down, event.time / 1000.0)
            elif event.detail in self.composer_keycodes:
                self._on_composer_key(is_down, event.time / 1000.0)
            elif event.detail in self.ask_keycodes:
                self._on_ask_key(is_down)
            elif is_down:
                is_escape = event.detail in self.escape_keycodes
                self._on_other_key_down(is_escape)
                self._on_step_input("escape" if is_escape else "step key" if event.detail in self.step_keycodes else "other key")
