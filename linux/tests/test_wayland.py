"""Wayland's parts, without Wayland: the input devices' events (decoded from the kernel's struct and
read through a pipe standing in for a device), the virtual keyboard's keystroke (written to a file
standing in for /dev/uinput), the permission's root script, cutting the desktop's picture into
monitors, and which session is which. The whole take, typed with the virtual keyboard's
shift+insert into a GTK text box, runs in `test_x11.py` (Wayland mode on Xvfb)."""

from __future__ import annotations

import os
import struct
import time

import pytest

from openclicky_linux import evdev, inputaccess, session, uinput
from openclicky_linux.evdev import (BTN_LEFT, EV_KEY, EV_REL, EV_SYN, KEY_A, KEY_ENTER, KEY_ESC, KEY_INSERT, KEY_LEFTALT,
                                    KEY_LEFTCTRL, KEY_LEFTSHIFT, KEY_RIGHTCTRL, KEY_Z, DeviceKind, InputDeviceListener)


def event_bytes(event_type: int, code: int, value: int, at_seconds: float = 100.25) -> bytes:
    return struct.pack("llHHi", int(at_seconds), int(at_seconds % 1 * 1_000_000), event_type, code, value)


class Seen:
    def __init__(self) -> None:
        self.events: list = []

    def listener(self, dictation_key: str = "Control_R", **options) -> InputDeviceListener:
        return InputDeviceListener(
            dictation_key,
            on_dictation_key=lambda is_down, at: self.events.append(("dictation", is_down, at)),
            on_other_key_down=lambda is_escape: self.events.append(("other", is_escape)),
            on_composer_key=lambda is_down, at: self.events.append(("composer", is_down)),
            on_ask_key=lambda is_down: self.events.append(("ask", is_down)),
            on_step_input=lambda kind: self.events.append(("step", kind)),
            on_pointer_moved=lambda: self.events.append(("moved",)),
            **options)


def test_events_decode_from_the_kernels_struct_whole_events_only():
    data = event_bytes(EV_KEY, KEY_A, 1, 12.5) + event_bytes(EV_SYN, 0, 0, 12.5) + b"\x00" * 5
    assert evdev.decode_events(data) == [(12.5, EV_KEY, KEY_A, 1), (12.5, EV_SYN, 0, 0)]


def test_a_keyboard_has_letters_and_ctrl_and_a_pointer_has_a_left_button_and_moves():
    letters = {KEY_LEFTCTRL, KEY_A, KEY_Z}
    assert evdev.kind_of({EV_KEY}, letters) == DeviceKind(keyboard=True, pointer=False)
    assert evdev.kind_of({EV_KEY, EV_REL}, {BTN_LEFT}) == DeviceKind(keyboard=False, pointer=True)
    # A power button, or a headset's volume keys: neither.
    assert evdev.kind_of({EV_KEY}, {116}) == DeviceKind(keyboard=False, pointer=False)
    # A touchpad reports where the finger is, not how far it moved.
    assert evdev.kind_of({EV_KEY, evdev.EV_ABS}, {evdev.BTN_TOUCH, BTN_LEFT}).pointer


def test_the_listener_tells_the_dictation_composer_and_ask_keys_from_other_keys():
    seen = Seen()
    listener = seen.listener("Control_R")
    listener.handle_events([(1.0, EV_KEY, KEY_RIGHTCTRL, 1), (1.1, EV_KEY, KEY_A, 1), (1.2, EV_KEY, KEY_A, 0),
                            (1.3, EV_KEY, KEY_RIGHTCTRL, 2), (1.4, EV_KEY, KEY_RIGHTCTRL, 0),
                            (2.0, EV_KEY, KEY_LEFTCTRL, 1), (2.1, EV_KEY, KEY_LEFTCTRL, 0),
                            (3.0, EV_KEY, KEY_LEFTALT, 1), (3.1, EV_KEY, KEY_LEFTALT, 0),
                            (4.0, EV_KEY, KEY_ESC, 1), (5.0, EV_KEY, KEY_ENTER, 1)])
    assert seen.events == [
        ("dictation", True, 1.0), ("other", False), ("step", "other key"),
        # the auto-repeat (value 2) of the held key is no new press
        ("dictation", False, 1.4),
        ("composer", True), ("composer", False),
        ("ask", True), ("ask", False),
        ("other", True), ("step", "escape"),
        ("other", False), ("step", "step key"),
    ]


def test_left_ctrl_as_the_dictation_key_is_never_also_the_composer_key():
    seen = Seen()
    listener = seen.listener("Control_L")
    assert listener.composer_code is None
    listener.handle_events([(1.0, EV_KEY, KEY_LEFTCTRL, 1), (1.5, EV_KEY, KEY_LEFTCTRL, 0)])
    assert seen.events == [("dictation", True, 1.0), ("dictation", False, 1.5)]


def test_a_click_while_a_key_is_held_is_a_shortcut_and_its_release_is_a_guided_step():
    seen = Seen()
    listener = seen.listener()
    listener.handle_events([(1.0, EV_KEY, BTN_LEFT, 1), (1.1, EV_KEY, BTN_LEFT, 0), (1.2, EV_KEY, BTN_LEFT + 1, 0)])
    assert seen.events == [("other", False), ("step", "click")]


def test_mouse_movement_is_reported_at_most_ten_times_a_second():
    seen = Seen()
    listener = seen.listener()
    listener.handle_events([(1.0, EV_REL, 0, 3)] * 50)
    assert seen.events == [("moved",)]
    listener._last_move_reported -= 1.0
    listener.handle_events([(1.0, EV_REL, 1, 3)])
    assert seen.events == [("moved",), ("moved",)]


def test_our_own_paste_keystrokes_are_ignored_for_a_moment():
    seen = Seen()
    listener = seen.listener()
    listener.ignore_keys_for(0.5)
    listener.handle_events([(1.0, EV_KEY, KEY_LEFTSHIFT, 1), (1.0, EV_KEY, KEY_INSERT, 1)])
    assert seen.events == []


def test_an_unknown_dictation_key_is_refused_with_a_sentence():
    with pytest.raises(ValueError, match="unknown key name"):
        Seen().listener("Hyper_Q")


def test_the_listener_reads_events_from_its_devices_on_its_own_thread():
    read_end, write_end = os.pipe()
    os.set_blocking(read_end, False)
    seen = Seen()
    listener = seen.listener(device_paths=lambda: ["stand-in keyboard"],
                             probe=lambda path: (read_end, "stand-in keyboard", DeviceKind(keyboard=True, pointer=False)))
    listener.start()
    try:
        os.write(write_end, event_bytes(EV_KEY, KEY_RIGHTCTRL, 1, 7.0) + event_bytes(EV_SYN, 0, 0, 7.0)
                 + event_bytes(EV_KEY, KEY_RIGHTCTRL, 0, 7.5))
        deadline = time.monotonic() + 2
        while len(seen.events) < 2 and time.monotonic() < deadline:
            time.sleep(0.01)
    finally:
        listener.stop()
        os.close(write_end)
    assert seen.events == [("dictation", True, 7.0), ("dictation", False, 7.5)]


def test_no_device_at_all_is_refused_with_a_sentence():
    listener = Seen().listener(device_paths=lambda: [], probe=lambda path: None)
    with pytest.raises(RuntimeError, match="no keyboard"):
        listener.start()


def test_unreadable_devices_ask_for_the_one_time_permission(tmp_path, monkeypatch):
    def refuse(path):
        raise PermissionError(13, "Permission denied", path)
    monkeypatch.setattr(evdev, "probe_device", refuse)
    access = evdev.check_access([str(tmp_path / "event0"), str(tmp_path / "event1")])
    assert access == evdev.InputAccess(readable=0, refused=2)


# MARK: the virtual keyboard


def test_the_paste_is_shift_insert_pressed_in_order_and_released_in_reverse_each_its_own_frame():
    events = uinput.keystroke_events((KEY_LEFTSHIFT, KEY_INSERT))
    assert events == [(EV_KEY, KEY_LEFTSHIFT, 1), (EV_SYN, 0, 0), (EV_KEY, KEY_INSERT, 1), (EV_SYN, 0, 0),
                      (EV_KEY, KEY_INSERT, 0), (EV_SYN, 0, 0), (EV_KEY, KEY_LEFTSHIFT, 0), (EV_SYN, 0, 0)]


def test_the_virtual_keyboard_writes_kernel_input_events(tmp_path):
    stand_in = tmp_path / "uinput"
    stand_in.write_bytes(b"")
    keyboard = uinput.VirtualKeyboard(path=str(stand_in), key_gap_seconds=0)
    keyboard.paste()
    keyboard.close()
    written = [(event_type, code, value) for _at, event_type, code, value in evdev.decode_events(stand_in.read_bytes())]
    assert written == uinput.keystroke_events((KEY_LEFTSHIFT, KEY_INSERT))


def test_the_virtual_keyboard_is_named_so_the_listener_leaves_it_out():
    assert evdev.OWN_DEVICE_NAME == "OpenClicky virtual keyboard"
    assert uinput.UINPUT_SETUP.size == 92  # struct uinput_setup, as the kernel lays it out


# MARK: the one-time permission


def test_the_permission_tags_keyboards_mice_and_uinput_for_the_person_at_the_seat():
    script = inputaccess.install_script()
    assert str(inputaccess.RULE_PATH) == "/etc/udev/rules.d/70-openclicky-input.rules"
    for line in ('ENV{ID_INPUT_KEYBOARD}=="1", TAG+="uaccess"', 'ENV{ID_INPUT_MOUSE}=="1", TAG+="uaccess"',
                 'KERNEL=="uinput", SUBSYSTEM=="misc", TAG+="uaccess"'):
        assert line in script
    assert "udevadm trigger --action=change --subsystem-match=input" in script
    assert "modprobe uinput" in script
    assert "rm -f /etc/udev/rules.d/70-openclicky-input.rules" in inputaccess.remove_script()


def test_a_closed_password_dialog_changes_nothing_and_says_so(monkeypatch):
    class Finished:
        returncode = 126
        stdout = stderr = ""
    monkeypatch.setattr(inputaccess.os, "geteuid", lambda: 1000)
    monkeypatch.setattr(inputaccess.shutil, "which", lambda name: f"/usr/bin/{name}")
    monkeypatch.setattr(inputaccess.subprocess, "run", lambda *arguments, **options: Finished())
    with pytest.raises(inputaccess.SetupFailed, match="password dialog was closed"):
        inputaccess.grant()


# MARK: which session


@pytest.mark.parametrize("environment, wayland", [
    ({"XDG_SESSION_TYPE": "wayland", "WAYLAND_DISPLAY": "wayland-0", "DISPLAY": ":0"}, True),
    ({"XDG_SESSION_TYPE": "x11", "DISPLAY": ":0"}, False),
    ({"WAYLAND_DISPLAY": "wayland-1", "DISPLAY": ":1"}, True),
    ({"DISPLAY": ":0"}, False),
    # a nested X server (Xephyr) started from a Wayland session says x11
    ({"XDG_SESSION_TYPE": "x11", "WAYLAND_DISPLAY": "wayland-0", "DISPLAY": ":5"}, False),
])
def test_which_session_it_is(environment, wayland):
    assert session.is_wayland_session(environment) is wayland


def test_windows_draw_through_x11_everywhere():
    environment: dict[str, str] = {}
    session.use_x11_windows(environment)
    assert environment == {"GDK_BACKEND": "x11"}


# MARK: taps on a touchpad


def touch(at: float, positions: list[tuple[int, int]], lift_at: float, press_button: bool = False) -> list:
    events = [(at, EV_KEY, evdev.BTN_TOUCH, 1)]
    for x, y in positions:
        events += [(at, evdev.EV_ABS, evdev.ABS_X, x), (at, evdev.EV_ABS, evdev.ABS_Y, y)]
    if press_button:
        events += [(at, EV_KEY, BTN_LEFT, 1), (at, EV_KEY, BTN_LEFT, 0)]
    return events + [(lift_at, EV_KEY, evdev.BTN_TOUCH, 0)]


def test_a_quick_still_touch_is_a_tap_and_a_slide_or_a_long_touch_is_not():
    detector = evdev.TapDetector(3000, 2000)
    def taps(events):
        return [detector.feed(*event) for event in events].count(True)
    assert taps(touch(1.0, [(1500, 1000), (1510, 1004)], lift_at=1.12)) == 1
    assert taps(touch(2.0, [(1500, 1000), (1800, 1000)], lift_at=2.12)) == 0  # slid: moving the cursor
    assert taps(touch(3.0, [(1500, 1000)], lift_at=3.6)) == 0                 # rested
    assert taps(touch(4.0, [(1500, 1000)], lift_at=4.1, press_button=True)) == 0  # a clickpad press clicks itself


def test_a_tapped_touchpad_finishes_a_guided_step_like_a_click():
    # Found live: a guide stalled because tap-to-click never reaches the device as a button.
    seen = Seen()
    read_end, write_end = os.pipe()
    listener = seen.listener(device_paths=lambda: ["touchpad"],
                             probe=lambda path: (read_end, "touchpad", DeviceKind(keyboard=False, pointer=True, touch=True)))
    listener._rescan()
    listener.handle_events(touch(1.0, [(500, 500)], lift_at=1.1), device="touchpad")
    assert ("step", "click") in seen.events
    os.close(read_end)
    os.close(write_end)


def test_touchpads_and_touchscreens_are_touch_devices_and_mice_are_not():
    assert evdev.kind_of({EV_KEY, evdev.EV_ABS}, {evdev.BTN_TOUCH, evdev.BTN_TOOL_FINGER, BTN_LEFT}).touch
    assert evdev.kind_of({EV_KEY, evdev.EV_ABS}, {evdev.BTN_TOUCH}).touch
    assert not evdev.kind_of({EV_KEY, EV_REL}, {BTN_LEFT}).touch


def test_a_touchpads_finger_codes_are_never_taken_for_typing():
    seen = Seen()
    listener = seen.listener()
    # BTN_TOOL_FINGER, BTN_TOOL_DOUBLETAP, BTN_TOUCH: what any touch of a touchpad sends.
    listener.handle_events([(1.0, EV_KEY, 0x145, 1), (1.0, EV_KEY, 0x14D, 1), (1.0, EV_KEY, evdev.BTN_TOUCH, 1),
                            (1.2, EV_KEY, 0x145, 0), (1.2, EV_KEY, evdev.BTN_TOUCH, 0)])
    assert seen.events == []
