"""The fast lane: the local actions OpenClicky performs itself instead of handing them to the agent,
the Mac's `MacActions.swift` in Linux's terms. Opening an app or making a folder is one answer with a
tool call (about two seconds) rather than a Codex run (about twelve).

Every action takes typed arguments, never a path and never a command: `location` is a closed set,
names that could reach outside it are refused, and only http(s) addresses open. A model can misname
a folder; it cannot widen what this can do. The sentences are the Mac's, with Linux's words.

| Mac | Linux |
|---|---|
| open_app (LaunchServices) | the desktop's applications (`.desktop` entries, by name, id or command) |
| open_url | the default browser (`Gio.AppInfo.launch_default_for_uri`) |
| create_folder, reveal_in_finder | the XDG folders; shown in the file manager (`org.freedesktop.FileManager1`) |
| set_volume (osascript) | `wpctl` (PipeWire) or `pactl` (PulseAudio) |
| media_control (media keys) | MPRIS over D-Bus, the player that is playing first |
"""

from __future__ import annotations

import shutil
import subprocess
import urllib.parse
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

LOCATIONS = ("desktop", "downloads", "documents", "workspace", "home")
SPOKEN_LOCATIONS = {"desktop": "your Desktop", "downloads": "your Downloads folder", "documents": "your Documents folder",
                    "workspace": "your OpenClicky folder", "home": "your home folder"}
MEDIA_ACTIONS = ("playpause", "next", "previous")

TOOLS_INSTRUCTION = ("When the user asks you to open an app or a website, make or show a folder, change the volume or control "
                     "playback, call the matching tool instead of describing the steps. For anything else you can only explain and point.")


@dataclass(frozen=True)
class Outcome:
    sentence: str
    did_something: bool


def folder_name(raw: str) -> str | None:
    """A name the model gave, or None when it could reach outside its location (a separator, a
    hidden name, longer than a filename may be)."""
    name = (raw or "").strip()
    if not name or len(name) > 255 or "/" in name or "\0" in name or name.startswith("."):
        return None
    return name


def web_url(raw: str) -> str | None:
    """An http(s) address with a host, or None: `file:` reads the disk and other schemes launch
    whatever app claimed them, neither of which is "open this page"."""
    url = (raw or "").strip()
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme.lower() not in ("http", "https") or not parsed.netloc:
        return None
    return url


def tool_definitions() -> list[dict]:
    """The actions as Anthropic tools (the ask lane offers them where the agent lane does not run)."""
    location = {"type": "string", "enum": list(LOCATIONS), "description": "Which folder. Leave it out for OpenClicky's own workspace folder."}
    return [
        {"name": "open_app", "description": "Open or switch to an app by its name, e.g. 'Firefox'. Use this instead of the agent for simply opening an app.",
         "input_schema": {"type": "object", "properties": {"name": {"type": "string", "description": "The app's name as the user said it."}}, "required": ["name"]}},
        {"name": "open_url", "description": "Open a web page in the user's browser. http and https only.",
         "input_schema": {"type": "object", "properties": {"url": {"type": "string", "description": "The full https URL."}}, "required": ["url"]}},
        {"name": "create_folder", "description": "Make a new folder. Use this instead of the agent for a single folder.",
         "input_schema": {"type": "object", "properties": {"name": {"type": "string", "description": "The folder's name, without any slashes."},
                                                          "location": location}, "required": ["name"]}},
        {"name": "reveal_in_files", "description": "Show an existing file or folder in the file manager.",
         "input_schema": {"type": "object", "properties": {"name": {"type": "string", "description": "The file or folder's name."},
                                                          "location": location}, "required": ["name"]}},
        {"name": "set_volume", "description": "Set the computer's output volume.",
         "input_schema": {"type": "object", "properties": {"level": {"type": "integer", "description": "0 to 100."}}, "required": ["level"]}},
        {"name": "media_control", "description": "Play, pause, or skip whatever is playing.",
         "input_schema": {"type": "object", "properties": {"action": {"type": "string", "enum": list(MEDIA_ACTIONS)}}, "required": ["action"]}},
    ]


def openai_tool_definitions() -> list[dict]:
    """The same tools in OpenAI's chat completions shape (OpenAI and OpenRouter keys)."""
    return [{"type": "function", "function": {"name": tool["name"], "description": tool["description"], "parameters": tool["input_schema"]}}
            for tool in tool_definitions()]


TOOL_NAMES = frozenset(tool["name"] for tool in tool_definitions())


# MARK: the desktop's side, injected so the runner is tested without launching anything


@dataclass
class Desktop:
    home: Path
    workspace: Path
    special_folder: Callable[[str], Path | None]  # "desktop" | "downloads" | "documents" → the XDG folder
    find_application: Callable[[str], Any]  # a name → something `launch_application` takes, or None
    launch_application: Callable[[Any], None]
    application_name: Callable[[Any], str]
    open_url: Callable[[str], None]
    reveal: Callable[[Path], None]
    set_volume: Callable[[int], bool]
    media: Callable[[str], bool]


def folder_for(location: str, desktop: Desktop) -> Path:
    if location == "workspace":
        return desktop.workspace
    if location == "home":
        return desktop.home
    return desktop.special_folder(location) or desktop.home / location.capitalize()


def perform(tool_name: str, arguments: dict, desktop: Desktop) -> Outcome:
    """Runs one tool call the model made. The model controls every value: nothing here trusts them
    beyond the closed sets and the checks above."""
    def text(key: str) -> str:
        value = arguments.get(key)
        return value.strip() if isinstance(value, str) else ""

    def location() -> str | None:
        raw = text("location").lower()
        if not raw:
            # Unnamed: the workspace, the one place it is always fine to write.
            return "workspace"
        return raw if raw in LOCATIONS else None

    if tool_name == "open_app":
        name = folder_name(text("name"))
        if name is None:
            return Outcome("That app name has characters an app name can't have.", False)
        application = desktop.find_application(name)
        if application is None:
            return Outcome(f"I couldn't find an app called {name}.", False)
        desktop.launch_application(application)
        return Outcome(f"Opened {desktop.application_name(application)}.", True)
    if tool_name == "open_url":
        url = web_url(text("url"))
        if url is None:
            return Outcome("That doesn't look like a web address I can open.", False)
        desktop.open_url(url)
        return Outcome("Opened it in your browser.", True)
    if tool_name in ("create_folder", "reveal_in_files", "reveal_in_finder"):
        where = location()
        if where is None:
            return Outcome(f"I don't know where {text('location')} is.", False)
        name = folder_name(text("name"))
        if name is None:
            return Outcome("That name has characters a folder can't have.", False)
        target = folder_for(where, desktop) / name
        spoken = SPOKEN_LOCATIONS[where]
        if tool_name == "create_folder":
            if target.exists():
                return Outcome(f"{name} is already on {spoken}.", True)
            try:
                target.mkdir(parents=True)
            except PermissionError:
                return Outcome(f"I'm not allowed to write to {spoken}.", False)
            except OSError as failure:
                return Outcome(f"That didn't work: {failure.strerror or failure}.", False)
            return Outcome(f"Created {name} on {spoken}.", True)
        if not target.exists():
            return Outcome(f"I couldn't find {name} on {spoken}.", False)
        desktop.reveal(target)
        return Outcome(f"Showing {name} on {spoken}.", True)
    if tool_name == "set_volume":
        raw = arguments.get("level")
        try:
            level = int(raw.strip()) if isinstance(raw, str) else int(raw)
        except (TypeError, ValueError):
            return Outcome("That didn't work: the volume has to be a number from 0 to 100.", False)
        level = min(100, max(0, level))
        if not desktop.set_volume(level):
            return Outcome("That didn't work: no sound server answered (wpctl or pactl).", False)
        return Outcome(f"Volume set to {level} percent.", True)
    if tool_name == "media_control":
        action = text("action").lower()
        if action not in MEDIA_ACTIONS:
            return Outcome(f"That didn't work: I don't know how to {action}.", False)
        if not desktop.media(action):
            return Outcome("Nothing is playing that I can control.", False)
        return Outcome("Done.", True)
    return Outcome("That needs your own key for now.", False)


# MARK: the live desktop


def _special_folder(location: str) -> Path | None:
    from gi.repository import GLib

    directory = {"desktop": GLib.UserDirectory.DIRECTORY_DESKTOP, "downloads": GLib.UserDirectory.DIRECTORY_DOWNLOAD,
                 "documents": GLib.UserDirectory.DIRECTORY_DOCUMENTS}.get(location)
    path = GLib.get_user_special_dir(directory) if directory is not None else None
    # Unset, XDG falls back to the home folder itself: never make "desktop" mean home.
    return Path(path) if path and Path(path) != Path.home() else None


def application_matches(query: str, applications: list[tuple[str, str, str]]) -> int | None:
    """Which of (display name, desktop id, command) best fits what was said: the name exactly, then
    the id or the command ("code" for VS Code), then a name that starts with it; None for none."""
    wanted = query.strip().lower().removesuffix(".desktop")
    if not wanted:
        return None
    for index, (name, _identifier, _command) in enumerate(applications):
        if name.lower() == wanted:
            return index
    for index, (_name, identifier, command) in enumerate(applications):
        identifier = identifier.lower().removesuffix(".desktop")
        if wanted in (identifier, identifier.rsplit(".", 1)[-1], Path(command.split(" ")[0] if command else "").name.lower()):
            return index
    for index, (name, _identifier, _command) in enumerate(applications):
        if name.lower().startswith(wanted) or wanted.replace(" ", "") == name.lower().replace(" ", ""):
            return index
    return None


def _find_application(name: str):
    from gi.repository import Gio

    applications = [application for application in Gio.AppInfo.get_all() if application.should_show()]
    index = application_matches(name, [(application.get_display_name() or "", application.get_id() or "",
                                        application.get_commandline() or "") for application in applications])
    return None if index is None else applications[index]


def _launch_application(application) -> None:
    application.launch([], None)


def _open_url(url: str) -> None:
    from gi.repository import Gio

    Gio.AppInfo.launch_default_for_uri(url, None)


def _reveal(path: Path) -> None:
    """Selected in the file manager (Files, Dolphin, Nemo, Thunar all answer FileManager1), else its folder opened."""
    from gi.repository import Gio, GLib

    try:
        bus = Gio.bus_get_sync(Gio.BusType.SESSION)
        bus.call_sync("org.freedesktop.FileManager1", "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1", "ShowItems",
                      GLib.Variant("(ass)", ([path.as_uri()], "")), None, Gio.DBusCallFlags.NONE, 3000, None)
    except GLib.Error:
        Gio.AppInfo.launch_default_for_uri(path.parent.as_uri(), None)


def volume_command(level: int) -> list[str] | None:
    if shutil.which("wpctl"):
        return ["wpctl", "set-volume", "@DEFAULT_AUDIO_SINK@", f"{level / 100:.2f}"]
    if shutil.which("pactl"):
        return ["pactl", "set-sink-volume", "@DEFAULT_SINK@", f"{level}%"]
    return None


def _set_volume(level: int) -> bool:
    command = volume_command(level)
    if command is None:
        return False
    try:
        return subprocess.run(command, capture_output=True, timeout=5).returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def _media(action: str) -> bool:
    """MPRIS: the player that is playing (else the first one) is told to play/pause, skip or go back."""
    from gi.repository import Gio, GLib

    method = {"playpause": "PlayPause", "next": "Next", "previous": "Previous"}[action]
    try:
        bus = Gio.bus_get_sync(Gio.BusType.SESSION)
        names = bus.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames",
                              None, None, Gio.DBusCallFlags.NONE, 2000, None).unpack()[0]
        players = [name for name in names if name.startswith("org.mpris.MediaPlayer2.")]
        if not players:
            return False

        def is_playing(player: str) -> bool:
            try:
                status = bus.call_sync(player, "/org/mpris/MediaPlayer2", "org.freedesktop.DBus.Properties", "Get",
                                       GLib.Variant("(ss)", ("org.mpris.MediaPlayer2.Player", "PlaybackStatus")),
                                       None, Gio.DBusCallFlags.NONE, 1000, None).unpack()[0]
            except GLib.Error:
                return False
            return status == "Playing"
        player = next((player for player in players if is_playing(player)), players[0])
        bus.call_sync(player, "/org/mpris/MediaPlayer2", "org.mpris.MediaPlayer2.Player", method, None, None,
                      Gio.DBusCallFlags.NONE, 2000, None)
        return True
    except GLib.Error:
        return False


def live_desktop(workspace: Path) -> Desktop:
    return Desktop(home=Path.home(), workspace=workspace, special_folder=_special_folder, find_application=_find_application,
                   launch_application=_launch_application, application_name=lambda application: application.get_display_name(),
                   open_url=_open_url, reveal=_reveal, set_volume=_set_volume, media=_media)
