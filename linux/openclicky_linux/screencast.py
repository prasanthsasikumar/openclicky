"""Wayland (GNOME, KDE): the screenshot a question carries, taken through the ScreenCast portal.

GNOME's screenshot portal asks "share this screenshot?" for every single picture, and remembers
nothing. The screen cast portal asks once ("share your screens with OpenClicky?") and hands back a
restore token: with it, every later question's picture is taken without asking, until the person
revokes it. So each question opens a short screen cast of every monitor (the token restores it
silently), takes one frame of each through PipeWire (GStreamer's `pipewiresrc`), and closes it:
the desktop's "screen is being shared" indicator shows for that second, which is honest. The token
is kept in the preferences (`screenCastRestoreToken`); a refused or stale one asks again.

Each stream says where its monitor sits on the desktop, so a monitor's picture keeps its place and
a point the model gives maps back to it, as on X11.
"""

from __future__ import annotations

import os
import threading
import time
from dataclasses import dataclass

from . import applog, preferences

RESTORE_TOKEN = "screenCastRestoreToken"
SOURCE_MONITOR = 1
CURSOR_EMBEDDED = 2
PERSIST_UNTIL_REVOKED = 2
FRAME_TIMEOUT_SECONDS = 3.0
PORTAL_TIMEOUT_SECONDS = 120.0  # the first time, the person is choosing in a dialog


class ScreenCastRefused(Exception):
    """Carries the sentence the caption shows."""


@dataclass(frozen=True)
class MonitorFrame:
    """One monitor's picture: RGB bytes, and where the monitor sits on the desktop (logical pixels)."""
    pixels: bytes
    width: int
    height: int
    stride: int
    x: int
    y: int
    logical_width: int
    logical_height: int


def is_available() -> bool:
    """The portal (v4: restore tokens) and GStreamer's PipeWire source are both here."""
    try:
        import gi

        gi.require_version("Gst", "1.0")
        from gi.repository import Gio, GLib, Gst

        Gst.init(None)
        if Gst.ElementFactory.find("pipewiresrc") is None:
            return False
        bus = Gio.bus_get_sync(Gio.BusType.SESSION)
        version = bus.call_sync("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.DBus.Properties", "Get",
                                GLib.Variant("(ss)", ("org.freedesktop.portal.ScreenCast", "version")), None, Gio.DBusCallFlags.NONE, 2000, None)
        return version.unpack()[0] >= 4
    except Exception:  # noqa: BLE001 - anything missing means "not available", said once by the caller
        return False


class _Portal:
    """Calls on the ScreenCast portal from this thread, each request waited for on a main context of
    this thread's own (the app's main loop is never run from here)."""

    def __init__(self) -> None:
        from gi.repository import Gio, GLib

        self.Gio, self.GLib = Gio, GLib
        self.context = GLib.MainContext.new()
        self.context.push_thread_default()
        self.bus = Gio.bus_get_sync(Gio.BusType.SESSION)
        self.sender = self.bus.get_unique_name()[1:].replace(".", "_")
        self._counter = 0

    def close(self) -> None:
        self.context.pop_thread_default()

    def token(self) -> str:
        self._counter += 1
        return f"openclicky{os.getpid()}_{self._counter}_{int(time.monotonic() * 1000)}"

    def request(self, method: str, parameters, options: dict, timeout_seconds: float = PORTAL_TIMEOUT_SECONDS) -> dict:
        GLib = self.GLib
        handle_token = self.token()
        handle = f"/org/freedesktop/portal/desktop/request/{self.sender}/{handle_token}"
        answer: dict = {}

        def on_response(_connection, _sender, _path, _interface, _signal, response_parameters) -> None:
            answer["code"], answer["results"] = response_parameters.unpack()

        subscription = self.bus.signal_subscribe("org.freedesktop.portal.Desktop", "org.freedesktop.portal.Request", "Response", handle,
                                                 None, self.Gio.DBusSignalFlags.NONE, on_response)
        try:
            options = dict(options, handle_token=GLib.Variant("s", handle_token))
            self.bus.call_sync("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.ScreenCast",
                               method, parameters(options), None, self.Gio.DBusCallFlags.NONE, 10_000, None)
            deadline = time.monotonic() + timeout_seconds
            while "code" not in answer and time.monotonic() < deadline:
                self.context.iteration(False) or time.sleep(0.005)
        finally:
            self.bus.signal_unsubscribe(subscription)
        if "code" not in answer:
            raise ScreenCastRefused("the desktop did not answer the screen sharing request")
        if answer["code"] != 0:
            raise ScreenCastRefused("screen sharing was not allowed, so i can't see your screen. ask again and choose share "
                                    "(pick every screen)")
        return answer["results"]

    def open_pipewire_remote(self, session_handle: str) -> int:
        GLib, Gio = self.GLib, self.Gio
        reply, fd_list = self.bus.call_with_unix_fd_list_sync(
            "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.ScreenCast", "OpenPipeWireRemote",
            GLib.Variant("(oa{sv})", (session_handle, {})), GLib.VariantType("(h)"), Gio.DBusCallFlags.NONE, 10_000, None, None)
        return fd_list.get(reply.unpack()[0])

    def close_session(self, session_handle: str) -> None:
        try:
            self.bus.call_sync("org.freedesktop.portal.Desktop", session_handle, "org.freedesktop.portal.Session", "Close",
                               None, None, self.Gio.DBusCallFlags.NONE, 2000, None)
        except self.GLib.Error:
            pass


def _frame(pipewire_fd: int, node_id: int) -> tuple[bytes, int, int, int]:
    """One RGB frame of a PipeWire stream: (pixels, width, height, stride)."""
    import gi

    gi.require_version("Gst", "1.0")
    from gi.repository import Gst

    Gst.init(None)
    pipeline = Gst.parse_launch(
        f"pipewiresrc fd={pipewire_fd} path={node_id} do-timestamp=true always-copy=true ! videoconvert ! "
        "video/x-raw,format=RGB ! appsink name=frames max-buffers=1 drop=true sync=false")
    sink = pipeline.get_by_name("frames")
    pipeline.set_state(Gst.State.PLAYING)
    try:
        sample = sink.emit("try-pull-sample", int(FRAME_TIMEOUT_SECONDS * Gst.SECOND))
        if sample is None:
            raise ScreenCastRefused("the screen sharing stream sent no picture")
        structure = sample.get_caps().get_structure(0)
        width, height = structure.get_value("width"), structure.get_value("height")
        buffer = sample.get_buffer()
        succeeded, mapped = buffer.map(Gst.MapFlags.READ)
        if not succeeded:
            raise ScreenCastRefused("the screen sharing picture could not be read")
        try:
            pixels = bytes(mapped.data)
        finally:
            buffer.unmap(mapped)
        # Rows are padded to four bytes.
        stride = len(pixels) // height if height else width * 3
        return pixels, width, height, stride
    finally:
        pipeline.set_state(Gst.State.NULL)


_lock = threading.Lock()


def capture_monitors() -> list[MonitorFrame]:
    """Every monitor's picture, through a screen cast restored from the saved token (the first time,
    the desktop asks). Blocks while the desktop answers: run it off the main thread."""
    with _lock:
        started = time.monotonic()
        portal = _Portal()
        GLib = portal.GLib
        session_handle = None
        pipewire_fd = -1
        try:
            created = portal.request("CreateSession", lambda options: GLib.Variant("(a{sv})", (options,)),
                                     {"session_handle_token": GLib.Variant("s", portal.token())})
            session_handle = created["session_handle"]
            selection = {"types": GLib.Variant("u", SOURCE_MONITOR), "multiple": GLib.Variant("b", True),
                         "cursor_mode": GLib.Variant("u", CURSOR_EMBEDDED), "persist_mode": GLib.Variant("u", PERSIST_UNTIL_REVOKED)}
            saved_token = preferences.get(RESTORE_TOKEN)
            if isinstance(saved_token, str) and saved_token:
                selection["restore_token"] = GLib.Variant("s", saved_token)
            portal.request("SelectSources", lambda options: GLib.Variant("(oa{sv})", (session_handle, options)), selection)
            started_cast = portal.request("Start", lambda options: GLib.Variant("(osa{sv})", (session_handle, "", options)), {})
            # Every start hands back the token for next time (it may differ from the one used).
            if started_cast.get("restore_token"):
                preferences.set_values(**{RESTORE_TOKEN: started_cast["restore_token"]})
            streams = started_cast.get("streams") or []
            if not streams:
                raise ScreenCastRefused("no screen was shared, so i can't see your screen")
            pipewire_fd = portal.open_pipewire_remote(session_handle)
            frames = []
            for node_id, properties in streams:
                pixels, width, height, stride = _frame(pipewire_fd, node_id)
                x, y = properties.get("position", (0, 0))
                logical_width, logical_height = properties.get("size", (width, height))
                frames.append(MonitorFrame(pixels, width, height, stride, x, y, logical_width, logical_height))
            applog.append(f"screenshot: screen cast of {len(frames)} monitor(s) in {time.monotonic() - started:.2f} s")
            return frames
        finally:
            if pipewire_fd >= 0:
                os.close(pipewire_fd)
            if session_handle:
                portal.close_session(session_handle)
            portal.close()


def forget() -> None:
    """The next question asks the desktop again (Settings → "choose screens again")."""
    preferences.set_values(**{RESTORE_TOKEN: None})
