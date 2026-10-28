"""Start OpenClicky when you log in: the freedesktop autostart entry every desktop reads
(Xfce, KDE, GNOME, MATE, Cinnamon), `$XDG_CONFIG_HOME/autostart/openclicky.desktop`. Settings
switches it on and off; nothing else writes there.

`install_launcher` puts OpenClicky in the applications menu too (`~/.local/share/applications`,
with the pointer as its icon), as the Mac app sits in Applications: the first-run setup does it.
"""

from __future__ import annotations

import os
import shutil
import sys
from pathlib import Path

DESKTOP_FILE_NAME = "openclicky.desktop"


def autostart_path() -> Path:
    config_home = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config")
    return config_home / "autostart" / DESKTOP_FILE_NAME


def _quoted(argument: str) -> str:
    """An Exec argument quoted the desktop-entry way: double quotes, with " ` $ and \\ escaped."""
    if argument and all(character.isalnum() or character in "/._-+" for character in argument):
        return argument
    escaped = "".join("\\" + character if character in '"`$\\' else character for character in argument)
    return f'"{escaped}"'


def launch_command() -> str:
    """How this copy of OpenClicky starts: its own `openclicky-linux` beside the Python running it
    (a virtualenv's bin), else the one on PATH, else this Python with `-m`."""
    beside_python = Path(sys.executable).with_name("openclicky-linux")
    if beside_python.exists():
        return _quoted(str(beside_python))
    on_path = shutil.which("openclicky-linux")
    if on_path:
        return _quoted(on_path)
    return f"{_quoted(sys.executable)} -m openclicky_linux"


def desktop_entry(command: str) -> str:
    return "\n".join([
        "[Desktop Entry]",
        "Type=Application",
        "Name=OpenClicky",
        "Comment=Dictation and a pointer that answers questions about your screen",
        f"Exec={command}",
        "Icon=openclicky",
        "StartupWMClass=OpenClicky",
        "Terminal=false",
        "X-GNOME-Autostart-enabled=true",
        "",
    ])


def is_enabled() -> bool:
    """The entry exists and is not switched off in it (`Hidden=true`, or GNOME's own switch)."""
    try:
        lines = [line.strip().lower() for line in autostart_path().read_text(encoding="utf-8").splitlines()]
    except OSError:
        return False
    return "hidden=true" not in lines and "x-gnome-autostart-enabled=false" not in lines


def set_enabled(enabled: bool) -> None:
    path = autostart_path()
    if enabled:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(desktop_entry(launch_command()), encoding="utf-8")
    elif path.exists():
        path.unlink()


def data_home() -> Path:
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")


def launcher_path() -> Path:
    return data_home() / "applications" / DESKTOP_FILE_NAME


def icon_path() -> Path:
    return data_home() / "icons" / "hicolor" / "256x256" / "apps" / "openclicky.png"


def launcher_entry(command: str) -> str:
    return desktop_entry(command).replace("X-GNOME-Autostart-enabled=true\n", "Categories=Utility;Accessibility;\n"
                                          "Keywords=dictation;voice;assistant;pointer;\nStartupNotify=false\n")


def install_launcher() -> None:
    """OpenClicky in the applications menu, with its icon (drawn from the pointer's own triangle)."""
    from .windows import pointer_icon

    icon = icon_path()
    icon.parent.mkdir(parents=True, exist_ok=True)
    pointer_icon(256).savev(str(icon), "png", [], [])
    path = launcher_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(launcher_entry(launch_command()), encoding="utf-8")
