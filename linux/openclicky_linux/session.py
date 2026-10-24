"""X11 or Wayland: which way each part reaches the desktop.

| | X11 | Wayland |
|---|---|---|
| the keys and clicks | RECORD (`x11.DictationKeyListener`) | the input devices (`evdev.InputDeviceListener`) |
| the paste keystroke | XTEST ctrl+v | the virtual keyboard's shift+insert (`uinput.py`) |
| the window in front | `_NET_ACTIVE_WINDOW` | not told: `UNTOLD_WINDOW` |
| the screenshot | the X root window | the desktop's (`portal.py`) |
| the pointer | follows the mouse | rests at the top centre, flies out to answer |
| the windows (pointer, orb, ask box, Settings) | X11 | X11 too, through XWayland: only X11 lets a window place itself |

Wayland needs the one-time permission (`inputaccess.py`) for the first two rows.
"""

from __future__ import annotations

import os

from .controller import FocusedWindow

# Wayland never tells another app which window is in front: every take sees this same "window", so
# the words are pasted wherever the keyboard focus is when they are ready (where the person is typing).
UNTOLD_WINDOW = FocusedWindow(window_id=-1, app_class=None, is_terminal=False)


def is_wayland_session(environment: dict[str, str] | None = None) -> bool:
    environment = os.environ if environment is None else environment
    return environment.get("XDG_SESSION_TYPE") == "wayland" or (
        bool(environment.get("WAYLAND_DISPLAY")) and not environment.get("XDG_SESSION_TYPE") == "x11")


def has_xwayland(environment: dict[str, str] | None = None) -> bool:
    environment = os.environ if environment is None else environment
    return bool(environment.get("DISPLAY"))


def use_x11_windows(environment: dict[str, str] | None = None) -> None:
    """GTK draws through X11 (XWayland on Wayland): set before GTK is first imported."""
    environment = os.environ if environment is None else environment
    environment["GDK_BACKEND"] = "x11"


# Every OpenClicky window's WM_CLASS, and the launcher's StartupWMClass: the desktop then shows them
# as OpenClicky, with its icon, instead of an unnamed "Openclicky-linux".
WINDOW_CLASS = "OpenClicky"


def name_the_app() -> None:
    """Before the first window: its name and window class."""
    import gi

    gi.require_version("Gdk", "3.0")
    from gi.repository import Gdk, GLib

    GLib.set_prgname("openclicky")
    GLib.set_application_name("OpenClicky")
    Gdk.set_program_class(WINDOW_CLASS)
