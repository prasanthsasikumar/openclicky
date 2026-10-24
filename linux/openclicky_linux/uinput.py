"""Wayland: the paste keystroke, through a virtual keyboard (`/dev/uinput`).

On X11 the paste is an XTEST keystroke; on Wayland an app may not type into another, so the
keystroke comes from a keyboard of our own that the kernel adds (what ydotool does). Every
compositor (GNOME, KDE, Sway, Hyprland) takes it as a real keyboard. Creating it needs the same
one-time permission as reading the keys (`inputaccess.py`).

The keystroke is **shift+insert**, not ctrl+v: on Wayland the app in front cannot be told, and
shift+insert pastes in GTK, Qt, Firefox, Chromium and Electron apps and in terminals (VTE, Konsole,
xterm, kitty) alike, while ctrl+v is a literal ^V in a terminal. Terminals paste the primary
selection on shift+insert, so the paste puts the words in both (`desktop.ClipboardPaster`).
Insert is the same key in every layout, which v is not (Dvorak).

The device is made once, at start, and kept: a compositor takes a moment to pick up a new keyboard.
"""

from __future__ import annotations

import fcntl
import os
import struct
import time

from .evdev import EV_KEY, EV_SYN, KEY_INSERT, KEY_LEFTCTRL, KEY_LEFTSHIFT, KEY_V, OWN_DEVICE_NAME

UINPUT_PATH = "/dev/uinput"
SYN_REPORT = 0
BUS_VIRTUAL = 0x06


def _ioc(direction: int, number: int, size: int) -> int:
    return (direction << 30) | (size << 16) | (ord("U") << 8) | number


UI_DEV_CREATE = _ioc(0, 1, 0)
UI_DEV_DESTROY = _ioc(0, 2, 0)
# struct uinput_setup: struct input_id (four u16), char name[80], u32 ff_effects_max.
UINPUT_SETUP = struct.Struct("HHHH80sI")
UI_DEV_SETUP = _ioc(1, 3, UINPUT_SETUP.size)
UI_SET_EVBIT = _ioc(1, 100, 4)
UI_SET_KEYBIT = _ioc(1, 101, 4)
INPUT_EVENT = struct.Struct("llHHi")

# Every key the virtual keyboard can press.
KEYS = (KEY_LEFTCTRL, KEY_LEFTSHIFT, KEY_V, KEY_INSERT)
# Between one key and the next: a compositor reads a modifier and the key in the same frame
# otherwise, and some apps then see the key without it.
KEY_GAP_SECONDS = 0.012


def keystroke_events(keys: tuple[int, ...]) -> list[tuple[int, int, int]]:
    """(type, code, value) for pressing `keys` in order and releasing them in reverse, each step
    followed by a SYN_REPORT so it is a frame of its own."""
    events = []
    for code in keys:
        events += [(EV_KEY, code, 1), (EV_SYN, SYN_REPORT, 0)]
    for code in reversed(keys):
        events += [(EV_KEY, code, 0), (EV_SYN, SYN_REPORT, 0)]
    return events


class VirtualKeyboard:
    """The kernel keyboard the paste is typed on. Raises PermissionError without the one-time
    permission, OSError when the kernel has no uinput."""

    def __init__(self, path: str = UINPUT_PATH, key_gap_seconds: float = KEY_GAP_SECONDS) -> None:
        self._descriptor = os.open(path, os.O_WRONLY | os.O_NONBLOCK | os.O_CLOEXEC)
        self._key_gap_seconds = key_gap_seconds
        self._is_device = path == UINPUT_PATH
        if self._is_device:
            try:
                fcntl.ioctl(self._descriptor, UI_SET_EVBIT, EV_KEY)
                for code in KEYS:
                    fcntl.ioctl(self._descriptor, UI_SET_KEYBIT, code)
                fcntl.ioctl(self._descriptor, UI_DEV_SETUP, UINPUT_SETUP.pack(BUS_VIRTUAL, 0x4F43, 0x0001, 1, OWN_DEVICE_NAME.encode(), 0))
                fcntl.ioctl(self._descriptor, UI_DEV_CREATE)
            except OSError:
                os.close(self._descriptor)
                raise

    def press(self, keys: tuple[int, ...]) -> None:
        for event_type, code, value in keystroke_events(keys):
            now = time.time()
            os.write(self._descriptor, INPUT_EVENT.pack(int(now), int(now % 1 * 1_000_000), event_type, code, value))
            if event_type == EV_SYN:
                time.sleep(self._key_gap_seconds)

    def paste(self) -> None:
        self.press((KEY_LEFTSHIFT, KEY_INSERT))

    def close(self) -> None:
        if self._descriptor < 0:
            return
        if self._is_device:
            try:
                fcntl.ioctl(self._descriptor, UI_DEV_DESTROY)
            except OSError:
                pass
        os.close(self._descriptor)
        self._descriptor = -1
