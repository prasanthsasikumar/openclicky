"""Wayland: the screenshot a question carries, from the desktop itself.

On Wayland an app cannot read the screen; it asks the desktop:

- `grim` on wlroots desktops (Sway, Hyprland, river), which hand any client the screen;
- else the screenshot portal (`org.freedesktop.portal.Screenshot`, GNOME and KDE), not interactive.
  GNOME asks once whether OpenClicky may take screenshots, and remembers the answer (first-run
  setup takes one, so that question comes then and not with the first real question).

GNOME 42's portal saves its picture as `~/Pictures/Screenshot.png`, over whatever was there. A file
of the person's own at that path is moved aside first and put back afterwards, and the portal's
picture is read and deleted: nothing is left behind.

The whole desktop comes back as one picture, in the desktop's pixels; `screen.py` cuts it into
one picture per monitor.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.parse
from pathlib import Path

from . import applog

PORTAL_TIMEOUT_SECONDS = 60.0
WLROOTS_DESKTOPS = ("sway", "hyprland", "river", "wayfire", "labwc", "niri")


class ScreenshotRefused(Exception):
    """Carries the sentence the caption shows."""


def uses_grim(environment: dict[str, str] | None = None) -> bool:
    environment = os.environ if environment is None else environment
    desktop = environment.get("XDG_CURRENT_DESKTOP", "").lower()
    return bool(shutil.which("grim")) and any(name in desktop for name in WLROOTS_DESKTOPS)


def _grim() -> bytes:
    finished = subprocess.run(["grim", "-t", "png", "-"], capture_output=True, timeout=10)
    if finished.returncode != 0 or not finished.stdout:
        raise ScreenshotRefused(f"grim could not read the screen: {finished.stderr.decode(errors='replace').strip()}")
    return finished.stdout


def _portal_screenshot_uri(timeout_seconds: float) -> str:
    """Asks the portal, waiting for its answer on a main context of this thread's own (so the app's
    main loop is never run from here). Returns the picture's file:// URI."""
    from gi.repository import Gio, GLib

    context = GLib.MainContext.new()
    context.push_thread_default()
    try:
        bus = Gio.bus_get_sync(Gio.BusType.SESSION)
        token = f"openclicky{os.getpid()}_{int(time.monotonic() * 1000)}"
        sender = bus.get_unique_name()[1:].replace(".", "_")
        handle = f"/org/freedesktop/portal/desktop/request/{sender}/{token}"
        answer: dict = {}

        def on_response(_connection, _sender, _path, _interface, _signal, parameters) -> None:
            answer["response"], answer["results"] = parameters.unpack()

        subscription = bus.signal_subscribe("org.freedesktop.portal.Desktop", "org.freedesktop.portal.Request", "Response",
                                            handle, None, Gio.DBusSignalFlags.NONE, on_response)
        try:
            bus.call_sync("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                          "org.freedesktop.portal.Screenshot", "Screenshot",
                          GLib.Variant("(sa{sv})", ("", {"handle_token": GLib.Variant("s", token),
                                                         "interactive": GLib.Variant("b", False)})),
                          None, Gio.DBusCallFlags.NONE, 10_000, None)
            deadline = time.monotonic() + timeout_seconds
            while "response" not in answer and time.monotonic() < deadline:
                context.iteration(False) or time.sleep(0.01)
        finally:
            bus.signal_unsubscribe(subscription)
    except GLib.Error as failure:
        raise ScreenshotRefused(f"the desktop has no screenshot portal ({failure.message})") from failure
    finally:
        context.pop_thread_default()
    if "response" not in answer:
        raise ScreenshotRefused("the desktop did not answer the screenshot request")
    if answer["response"] != 0:
        raise ScreenshotRefused("the desktop refused the screenshot. allow openclicky to take screenshots, then ask again "
                                "(gnome: settings, privacy, screenshots; or run `openclicky-linux setup`)")
    uri = answer["results"].get("uri", "")
    if not uri.startswith("file://"):
        raise ScreenshotRefused("the desktop's screenshot came back somewhere it cannot be read")
    return uri


def _expected_portal_paths() -> list[Path]:
    """Where GNOME's portal may write its picture: kept safe before asking."""
    pictures = None
    try:
        from gi.repository import GLib

        pictures = GLib.get_user_special_dir(GLib.UserDirectory.DIRECTORY_PICTURES)
    except (ImportError, AttributeError):
        pass
    folder = Path(pictures) if pictures else Path.home() / "Pictures"
    return [folder / "Screenshot.png"]


_portal_lock = threading.Lock()


def take_portal_screenshot(timeout_seconds: float = PORTAL_TIMEOUT_SECONDS) -> bytes:
    """The PNG bytes of the whole desktop, from the portal, leaving no file behind."""
    with _portal_lock:
        kept_aside: list[tuple[Path, Path]] = []
        for path in _expected_portal_paths():
            if path.exists():
                aside = Path(tempfile.mkstemp(prefix=".openclicky-kept-", suffix=".png", dir=path.parent)[1])
                os.replace(path, aside)
                kept_aside.append((path, aside))
        try:
            uri = _portal_screenshot_uri(timeout_seconds)
            picture_path = Path(urllib.parse.unquote(urllib.parse.urlparse(uri).path))
            png_bytes = picture_path.read_bytes()
            picture_path.unlink(missing_ok=True)
            return png_bytes
        finally:
            for path, aside in kept_aside:
                os.replace(aside, path)


def take_screenshot() -> bytes:
    """The whole desktop as PNG: grim where it works, else the portal."""
    started = time.monotonic()
    png_bytes = _grim() if uses_grim() else take_portal_screenshot()
    applog.append(f"screenshot: {'grim' if uses_grim() else 'portal'} in {time.monotonic() - started:.2f} s")
    return png_bytes
