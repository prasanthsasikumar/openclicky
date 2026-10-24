"""Wayland: the keyboard and the mouse, read from the kernel's input devices (`/dev/input/event*`).

Wayland does not let one app watch the keys typed into another (X11's RECORD has no equivalent), so
on Wayland the keys are read where every compositor reads them: the evdev devices. Listen-only, like
the Mac app's event tap and the X11 watcher: the devices are never grabbed, every key keeps working
in every app. Reading them needs the one-time permission `inputaccess.py` sets up (the Mac's
Accessibility and Input Monitoring, in Linux's terms).

`InputDeviceListener` has the X11 `DictationKeyListener`'s callbacks, so the gestures, the take and
the guide are fed the same way on both, plus `on_pointer_moved` (the mouse moved: Wayland never says
where it is, only that it moved). Pure Python: the event structs and the ioctls are read with
`struct` and `fcntl`, nothing to compile. Keyboards plugged in later are picked up within a couple
of seconds; our own virtual keyboard (`uinput.py`, the paste) is left out, like the X11 watcher
ignores its own XTEST keystrokes.

A tap on a touchpad (or a touchscreen) is a click too, but the device never says so: tap-to-click
is made up by libinput inside the desktop, and the device only reports a finger touching and
lifting. So a short touch that barely moved (`TapDetector`, libinput's rule in short) counts as the
click a guided step waits for; found live, when a guide stalled on a tapped touchpad.
"""

from __future__ import annotations

import errno
import fcntl
import glob
import os
import select
import struct
import threading
import time
from dataclasses import dataclass
from typing import Callable, Iterable

from . import applog

# <linux/input-event-codes.h>
EV_SYN, EV_KEY, EV_REL, EV_ABS = 0x00, 0x01, 0x02, 0x03
KEY_ESC, KEY_TAB, KEY_ENTER, KEY_KPENTER = 1, 15, 28, 96
KEY_LEFTCTRL, KEY_LEFTSHIFT, KEY_LEFTALT, KEY_RIGHTSHIFT = 29, 42, 56, 54
KEY_RIGHTCTRL, KEY_RIGHTALT, KEY_LEFTMETA, KEY_RIGHTMETA, KEY_COMPOSE = 97, 100, 125, 126, 127
KEY_A, KEY_Z = 30, 44
KEY_V, KEY_INSERT = 47, 110
BTN_MOUSE, BTN_LEFT, BTN_TASK = 0x110, 0x110, 0x117
BTN_TOUCH = 0x14A
# Button codes that are not a key or a mouse button: a touchpad's fingers (BTN_TOOL_FINGER and its
# double and triple taps), pens, joysticks and gamepads. Taken for key presses, every touch of a
# touchpad looked like typing: a guide kept waiting for it to stop, and a take held with ctrl was
# called a shortcut (found live on a ThinkPad).
NOT_KEYS = (range(0x100, 0x110), range(0x118, 0x160), range(0x2C0, 0x300))
BTN_TOOL_FINGER = 0x145
ABS_X, ABS_Y = 0x00, 0x01
KEY_MAX = 0x2FF
# A touch shorter than this that moved less than this share of the pad is a tap (libinput: 180 ms
# and about 1.3 mm; a little more lenient here, since a missed tap stalls a guide).
TAP_MAX_SECONDS = 0.25
TAP_MAX_TRAVEL = 0.03

# The X keysym names the settings use (`linuxDictationKey`), as evdev key codes: one name, one
# physical key, whatever the layout.
KEY_CODES_BY_NAME = {
    "Control_L": KEY_LEFTCTRL, "Control_R": KEY_RIGHTCTRL, "Alt_L": KEY_LEFTALT, "Alt_R": KEY_RIGHTALT,
    "ISO_Level3_Shift": KEY_RIGHTALT, "Super_L": KEY_LEFTMETA, "Super_R": KEY_RIGHTMETA, "Menu": KEY_COMPOSE,
    "Shift_L": KEY_LEFTSHIFT, "Shift_R": KEY_RIGHTSHIFT, "Escape": KEY_ESC,
}
STEP_KEY_CODES = frozenset({KEY_ENTER, KEY_KPENTER, KEY_TAB})

# struct input_event: a timeval (two native longs), then type, code (u16) and value (s32).
INPUT_EVENT = struct.Struct("llHHi")
# Our own virtual keyboard's name (`uinput.py`): its keystrokes are the paste, never the person's.
OWN_DEVICE_NAME = "OpenClicky virtual keyboard"
RESCAN_SECONDS = 2.0


def _ioc_read(number: int, size: int) -> int:
    # _IOC(_IOC_READ, 'E', number, size)
    return (2 << 30) | (size << 16) | (ord("E") << 8) | number


def _bits(device_descriptor: int, event_type: int, highest: int) -> set[int]:
    """The codes a device can send for `event_type` (0: which event types it sends at all)."""
    length = highest // 8 + 1
    buffer = bytearray(length)
    fcntl.ioctl(device_descriptor, _ioc_read(0x20 + event_type, length), buffer)
    return {index for index in range(highest + 1) if buffer[index // 8] >> (index % 8) & 1}


def device_name(device_descriptor: int) -> str:
    buffer = bytearray(256)
    try:
        fcntl.ioctl(device_descriptor, _ioc_read(0x06, len(buffer)), buffer)
    except OSError:
        return ""
    return buffer.split(b"\0", 1)[0].decode(errors="replace")


@dataclass(frozen=True)
class DeviceKind:
    keyboard: bool
    pointer: bool
    # Touched rather than pressed (a touchpad, a touchscreen): its taps are found by `TapDetector`.
    touch: bool = False


def kind_of(event_types: set[int], key_codes: set[int]) -> DeviceKind:
    """A keyboard has letters (a power button or a headset's keys do not count); a pointer (mouse,
    touchpad, trackpoint) has a left button and moves."""
    keyboard = EV_KEY in event_types and KEY_LEFTCTRL in key_codes and KEY_A in key_codes and KEY_Z in key_codes
    pointer = EV_KEY in event_types and (BTN_LEFT in key_codes or BTN_TOUCH in key_codes) and bool({EV_REL, EV_ABS} & event_types)
    touch = pointer and EV_ABS in event_types and BTN_TOUCH in key_codes
    return DeviceKind(keyboard=keyboard, pointer=pointer, touch=touch)


def axis_span(device_descriptor: int, code: int) -> int:
    """How many units an absolute axis spans (EVIOCGABS: value, minimum, maximum, …); 0 unknown."""
    buffer = bytearray(24)
    try:
        fcntl.ioctl(device_descriptor, _ioc_read(0x40 + code, len(buffer)), buffer)
    except OSError:
        return 0
    _value, minimum, maximum = struct.unpack_from("iii", buffer)
    return max(0, maximum - minimum)


class TapDetector:
    """One touch device's taps: a touch that lifted within `TAP_MAX_SECONDS` having moved less than
    `TAP_MAX_TRAVEL` of the pad, with no button pressed meanwhile (a clickpad's press is a click of
    its own, seen as BTN_LEFT)."""

    def __init__(self, span_x: int, span_y: int) -> None:
        self.span_x, self.span_y = span_x or 1000, span_y or 1000
        self._touched_at: float | None = None
        self._first: list[int | None] = [None, None]
        self._travel = [0, 0]
        self._pressed = False

    def feed(self, at_seconds: float, event_type: int, code: int, value: int) -> bool:
        """True when this event ends a tap."""
        if event_type == EV_KEY and code == BTN_TOUCH:
            if value == 1:
                self._touched_at, self._first, self._travel, self._pressed = at_seconds, [None, None], [0, 0], False
                return False
            touched_at, self._touched_at = self._touched_at, None
            if touched_at is None or self._pressed:
                return False
            return (at_seconds - touched_at <= TAP_MAX_SECONDS and self._travel[0] <= self.span_x * TAP_MAX_TRAVEL
                    and self._travel[1] <= self.span_y * TAP_MAX_TRAVEL)
        if self._touched_at is None:
            return False
        if event_type == EV_KEY and BTN_MOUSE <= code <= BTN_TASK and value == 1:
            self._pressed = True
        elif event_type == EV_ABS and code in (ABS_X, ABS_Y):
            axis = code - ABS_X
            if self._first[axis] is None:
                self._first[axis] = value
            else:
                self._travel[axis] = max(self._travel[axis], abs(value - self._first[axis]))
        return False


def probe_device(path: str) -> tuple[int, str, DeviceKind] | None:
    """Opens a device and says what it is; None (and closed) when it is neither a keyboard nor a
    pointer, or is our own virtual keyboard. Raises PermissionError when it cannot be read."""
    device_descriptor = os.open(path, os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        name = device_name(device_descriptor)
        event_types = _bits(device_descriptor, 0, 0x1F)
        key_codes = _bits(device_descriptor, EV_KEY, KEY_MAX) if EV_KEY in event_types else set()
        kind = kind_of(event_types, key_codes)
        if name == OWN_DEVICE_NAME or not (kind.keyboard or kind.pointer):
            os.close(device_descriptor)
            return None
        return device_descriptor, name, kind
    except OSError:
        os.close(device_descriptor)
        raise


@dataclass(frozen=True)
class InputAccess:
    readable: int  # keyboards and pointers that could be opened
    refused: int  # devices that exist but could not be read (no permission yet)


def check_access(paths: Iterable[str] | None = None) -> InputAccess:
    """How many input devices this user can read now, and how many refuse: refused with none
    readable means the one-time permission has not been given (`inputaccess.py`)."""
    readable = refused = 0
    for path in sorted(paths if paths is not None else glob.glob("/dev/input/event*")):
        try:
            probed = probe_device(path)
        except PermissionError:
            refused += 1
            continue
        except OSError:
            continue
        if probed is not None:
            os.close(probed[0])
            readable += 1
    return InputAccess(readable=readable, refused=refused)


def decode_events(data: bytes) -> list[tuple[float, int, int, int]]:
    """Raw bytes read from a device → (seconds, type, code, value), whole events only."""
    size = INPUT_EVENT.size
    events = []
    for offset in range(0, len(data) - len(data) % size, size):
        seconds, microseconds, event_type, code, value = INPUT_EVENT.unpack_from(data, offset)
        events.append((seconds + microseconds / 1_000_000, event_type, code, value))
    return events


class InputDeviceListener:
    """Calls the X11 watcher's callbacks from its own thread, for every keyboard and pointer:
    `on_dictation_key(is_down, at_seconds)`, `on_composer_key(is_down, at_seconds)`,
    `on_ask_key(is_down)`, `on_other_key_down(is_escape)` (a click while a key is held counts as
    another key, as on X11), `on_step_input(kind)` ("click", "step key", "escape", "other key") and
    `on_pointer_moved()` (at most every `MOVE_INTERVAL_SECONDS`)."""

    MOVE_INTERVAL_SECONDS = 0.1

    def __init__(self, dictation_key_name: str, on_dictation_key: Callable[[bool, float], None],
                 on_other_key_down: Callable[[bool], None], display_name: str | None = None,
                 composer_key_name: str | None = "Control_L",
                 on_composer_key: Callable[[bool, float], None] = lambda _is_down, _at_seconds: None,
                 on_step_input: Callable[[str], None] = lambda _kind: None,
                 ask_key_name: str | None = "Alt_L",
                 on_ask_key: Callable[[bool], None] = lambda _is_down: None,
                 on_pointer_moved: Callable[[], None] = lambda: None,
                 device_paths: Callable[[], list[str]] | None = None,
                 probe: Callable[[str], "tuple[int, str, DeviceKind] | None"] = probe_device) -> None:
        del display_name  # the X11 watcher's argument; devices are not on a display
        if dictation_key_name not in KEY_CODES_BY_NAME:
            raise ValueError(f"unknown key name {dictation_key_name!r} (one of {', '.join(sorted(KEY_CODES_BY_NAME))})")
        self.dictation_code = KEY_CODES_BY_NAME[dictation_key_name]
        composer_code = KEY_CODES_BY_NAME.get(composer_key_name or "")
        self.composer_code = composer_code if composer_code != self.dictation_code else None
        ask_code = KEY_CODES_BY_NAME.get(ask_key_name or "")
        self.ask_code = ask_code if ask_code != self.dictation_code else None
        self._on_dictation_key = on_dictation_key
        self._on_composer_key = on_composer_key
        self._on_ask_key = on_ask_key
        self._on_other_key_down = on_other_key_down
        self._on_step_input = on_step_input
        self._on_pointer_moved = on_pointer_moved
        self._device_paths = device_paths or (lambda: sorted(glob.glob("/dev/input/event*")))
        # How a path is opened and recognised (the tests hand in a pipe).
        self._probe = probe
        self._devices: dict[str, int] = {}  # path → open descriptor
        self._taps: dict[str, TapDetector] = {}  # touch devices' paths → their tap detectors
        self._ignore_until = 0.0
        self._last_move_reported = 0.0
        self._stopping = threading.Event()
        self._wake_read, self._wake_write = os.pipe()
        self._thread: threading.Thread | None = None
        self.device_names: list[str] = []

    def ignore_keys_for(self, seconds: float) -> None:
        self._ignore_until = time.monotonic() + seconds

    def start(self) -> None:
        self._rescan()
        if not self._devices:
            access = check_access(self._device_paths()) if self._probe is probe_device else InputAccess(0, 0)
            if access.refused:
                raise RuntimeError("openclicky may not read the keyboard yet: run `openclicky-linux setup` once (it asks for your password)")
            raise RuntimeError("no keyboard was found under /dev/input")
        self._thread = threading.Thread(target=self._run, name="openclicky-input", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stopping.set()
        os.write(self._wake_write, b"x")
        if self._thread:
            self._thread.join(timeout=1.0)
        for device_descriptor in self._devices.values():
            os.close(device_descriptor)
        self._devices.clear()
        os.close(self._wake_read)
        os.close(self._wake_write)

    def _rescan(self) -> None:
        present = set(self._device_paths())
        for path in list(self._devices):
            if path not in present:
                os.close(self._devices.pop(path))
                self._taps.pop(path, None)
        for path in sorted(present - set(self._devices)):
            try:
                probed = self._probe(path)
            except OSError:
                continue
            if probed is not None:
                self._devices[path] = probed[0]
                if probed[2].touch:
                    self._taps[path] = TapDetector(axis_span(probed[0], ABS_X), axis_span(probed[0], ABS_Y))
                applog.append(f"input: reading {'a keyboard' if probed[2].keyboard else 'a pointer'} ({probed[1]})")
        self.device_names = sorted(self._devices)

    def _run(self) -> None:
        next_rescan = time.monotonic() + RESCAN_SECONDS
        while not self._stopping.is_set():
            descriptors = {descriptor: path for path, descriptor in self._devices.items()}
            try:
                ready, _, _ = select.select([self._wake_read, *descriptors], [], [], max(0.05, next_rescan - time.monotonic()))
            except (OSError, ValueError):
                ready = []
            for descriptor in ready:
                if descriptor == self._wake_read:
                    continue
                try:
                    data = os.read(descriptor, INPUT_EVENT.size * 64)
                except OSError as failure:
                    if failure.errno in (errno.EAGAIN, errno.EINTR):
                        continue
                    # Unplugged (ENODEV): forgotten now, found again if it comes back.
                    path = descriptors[descriptor]
                    os.close(self._devices.pop(path))
                    self._taps.pop(path, None)
                    continue
                try:
                    self.handle_events(decode_events(data), device=descriptors[descriptor])
                except Exception as failure:  # noqa: BLE001 - one bad event must not end the watcher
                    applog.append(f"input: skipped an event ({type(failure).__name__}: {failure})")
            if time.monotonic() >= next_rescan:
                self._rescan()
                next_rescan = time.monotonic() + RESCAN_SECONDS

    def handle_events(self, events: list[tuple[float, int, int, int]], device: str | None = None) -> None:
        """The decoded events of one read from `device`, in order (the tests call this directly)."""
        taps = self._taps.get(device) if device is not None else None
        for at_seconds, event_type, code, value in events:
            if taps is not None and taps.feed(at_seconds, event_type, code, value) and time.monotonic() >= self._ignore_until:
                # As a pressed button would: a shortcut while ctrl is held, and a guided step's click.
                self._on_other_key_down(False)
                self._on_step_input("click")
            if event_type in (EV_REL, EV_ABS):
                now = time.monotonic()
                if now - self._last_move_reported >= self.MOVE_INTERVAL_SECONDS:
                    self._last_move_reported = now
                    self._on_pointer_moved()
                continue
            # value 2 is the key's auto-repeat: not a new press.
            if event_type != EV_KEY or value == 2:
                continue
            if time.monotonic() < self._ignore_until:
                continue
            is_down = value == 1
            if BTN_MOUSE <= code <= BTN_TASK:
                # As on X11: a click while ctrl is held is ctrl+click, not a tap; a left button
                # release is what a guided step waits for.
                if is_down:
                    self._on_other_key_down(False)
                elif code == BTN_LEFT:
                    self._on_step_input("click")
                continue
            if any(code in codes for codes in NOT_KEYS):
                continue
            if code == self.dictation_code:
                self._on_dictation_key(is_down, at_seconds)
            elif code == self.composer_code:
                self._on_composer_key(is_down, at_seconds)
            elif code == self.ask_code:
                self._on_ask_key(is_down)
            elif is_down:
                is_escape = code == KEY_ESC
                self._on_other_key_down(is_escape)
                self._on_step_input("escape" if is_escape else "step key" if code in STEP_KEY_CODES else "other key")
