"""Wayland's one-time permission: reading the keyboard and mouse, and typing the paste.

The Mac app asks for Accessibility and Input Monitoring once; on Linux the same thing is a udev
rule. It tags keyboards, mice, touchpads and `/dev/uinput` with `uaccess`, so systemd-logind gives
the person sitting at this computer (the active session on its seat, nobody logged in remotely)
read access to them and the right to add a virtual keyboard, from now on, without logging out:
the same mechanism Steam uses for game controllers. Installing it needs root once, through
`pkexec`, the desktop's own password dialog. `remove` takes it away again.

What it allows, honestly: any program running as this person while they are at the computer can
then read what is typed, as any X11 program always could. OpenClicky only acts on its own keys and
never stores or sends a keystroke (see docs/linux.md, Privacy).
"""

from __future__ import annotations

import os
import shutil
import subprocess
import time
from pathlib import Path

from . import applog
from .evdev import check_access

RULE_PATH = Path("/etc/udev/rules.d/70-openclicky-input.rules")
MODULES_PATH = Path("/etc/modules-load.d/openclicky-uinput.conf")
# 70-: before systemd's 73-seat-late.rules, which turns the uaccess tag into the session's access.
RULE = """\
# OpenClicky (Wayland): lets the person at this computer read keyboards, mice and touchpads, and add
# the virtual keyboard OpenClicky pastes with. Remove with `openclicky-linux setup --remove`.
SUBSYSTEM=="input", KERNEL=="event*", ENV{ID_INPUT_KEYBOARD}=="1", TAG+="uaccess"
SUBSYSTEM=="input", KERNEL=="event*", ENV{ID_INPUT_MOUSE}=="1", TAG+="uaccess"
SUBSYSTEM=="input", KERNEL=="event*", ENV{ID_INPUT_TOUCHPAD}=="1", TAG+="uaccess"
SUBSYSTEM=="input", KERNEL=="event*", ENV{ID_INPUT_POINTINGSTICK}=="1", TAG+="uaccess"
KERNEL=="uinput", SUBSYSTEM=="misc", TAG+="uaccess", OPTIONS+="static_node=uinput"
"""


def install_script(rule: str = RULE) -> str:
    """The root part, run by pkexec: the rule, uinput loaded now and at every boot, and udev told
    to apply it to the devices already plugged in."""
    return f"""set -e
umask 022
cat > {RULE_PATH} <<'OPENCLICKY_RULE'
{rule.rstrip()}
OPENCLICKY_RULE
echo uinput > {MODULES_PATH}
modprobe uinput 2>/dev/null || true
udevadm control --reload-rules
udevadm trigger --action=change --subsystem-match=input
udevadm trigger --action=change --subsystem-match=misc --sysname-match=uinput
udevadm settle --timeout=10 || true
"""


def remove_script() -> str:
    """The rule and the module file go, and the access they gave is taken back now: udev applies a
    removed rule only to devices plugged in later, so without this the person kept the access
    until the next reboot. Only this person's entry, and only on devices no other rule still tags
    for the seat (a game controller's, say)."""
    return f"""set -e
rm -f {RULE_PATH} {MODULES_PATH}
udevadm control --reload-rules
udevadm trigger --action=change --subsystem-match=input
udevadm trigger --action=change --subsystem-match=misc --sysname-match=uinput
udevadm settle --timeout=10 || true
person=${{PKEXEC_UID:-${{SUDO_UID:-}}}}
if [ -n "$person" ] && command -v setfacl >/dev/null 2>&1; then
    for device in /dev/uinput /dev/input/event*; do
        [ -e "$device" ] || continue
        tags=$(udevadm info --query=property --name="$device" 2>/dev/null | grep '^CURRENT_TAGS=' || \
               udevadm info --query=property --name="$device" 2>/dev/null | grep '^TAGS=' || true)
        case "$tags" in
            *:uaccess:*) ;;
            *) setfacl -x "u:$person" "$device" 2>/dev/null || true ;;
        esac
    done
fi
"""


def can_type() -> bool:
    return os.access("/dev/uinput", os.W_OK)


def can_read_keys() -> bool:
    return check_access().readable > 0


def is_granted() -> bool:
    """The keyboard can be read and the paste typed: everything Wayland needs."""
    return can_read_keys() and can_type()


def is_rule_installed() -> bool:
    return RULE_PATH.exists()


class SetupFailed(Exception):
    """Carries the sentence to show."""


def _run_as_root(script: str) -> None:
    if os.geteuid() == 0:
        command = ["sh", "-c", script]
    elif shutil.which("pkexec"):
        command = ["pkexec", "sh", "-c", script]
    elif shutil.which("sudo"):
        command = ["sudo", "sh", "-c", script]
    else:
        raise SetupFailed("neither pkexec nor sudo is installed, so the permission cannot be set up from here")
    finished = subprocess.run(command, capture_output=True, text=True)
    # pkexec: 126 is "dismissed", 127 "not authorised".
    if finished.returncode in (126, 127) and command[0] == "pkexec":
        raise SetupFailed("the password dialog was closed, so nothing changed")
    if finished.returncode != 0:
        raise SetupFailed(f"setting it up failed: {(finished.stderr or finished.stdout).strip() or finished.returncode}")


def grant(wait_seconds: float = 8.0) -> None:
    """Installs the rule (asks for the password) and waits until the devices can be read."""
    _run_as_root(install_script())
    applog.append("input access: rule installed")
    deadline = time.monotonic() + wait_seconds
    while time.monotonic() < deadline:
        if is_granted():
            return
        time.sleep(0.2)
    if not can_read_keys():
        raise SetupFailed("the rule is installed, but the keyboard still cannot be read. log out and in once, then try again")
    if not can_type():
        raise SetupFailed("the keyboard can be read, but the virtual keyboard cannot be made (is the uinput module available?)")


def revoke() -> None:
    _run_as_root(remove_script())
    applog.append("input access: rule removed")
