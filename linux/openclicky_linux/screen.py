"""The screenshots a question carries: one per monitor, the one with the mouse first, each scaled
down to what the vision models actually look at (see `picture_scale`), as PNG.
Each keeps its scale and its monitor's place on the X screen, so a point the model gives in a
picture's pixels (and its :screenN) maps back to the desktop. One monitor gives one picture of the
whole screen, as before; two side by side no longer make one wide picture whose text is too small
to read.

On X11 the screen is read directly; on Wayland the desktop hands the pictures over (a screen cast,
`screencast.py`, or `portal.py`) and they are cut into the monitors here.
"""

from __future__ import annotations

import gi

gi.require_version("Gdk", "3.0")
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import Gdk, GdkPixbuf  # noqa: E402

from .ask import MAXIMUM_SCREENS, Screenshot  # noqa: E402
from .pointing import MonitorArea, monitors_mouse_first  # noqa: E402

# The Mac app's size (CompanionScreenCaptureUtility: 1280 on the longest side). A bigger picture is
# not seen in more detail: Claude shrinks anything over about 1.15 megapixels itself and then
# answers in the smaller picture's pixels, so every point came back pulled toward the top-left
# (on a 1680x1050 screen sent at 1568 px, the minimize button was missed by ~150 px).
MAX_SCREENSHOT_SIDE = 1280
MAX_SCREENSHOT_PIXELS = 1_150_000


def picture_scale(width: int, height: int) -> float:
    """Picture pixels per screen pixel: at most 1280 on the longest side and 1.15 megapixels, never
    enlarged."""
    return min(1.0, MAX_SCREENSHOT_SIDE / max(width, height), (MAX_SCREENSHOT_PIXELS / (width * height)) ** 0.5)


def screen_size() -> tuple[int, int]:
    root = Gdk.get_default_root_window()
    return root.get_width(), root.get_height()


def monitor_areas() -> list[MonitorArea]:
    display = Gdk.Display.get_default()
    areas = []
    for index in range(display.get_n_monitors()):
        geometry = display.get_monitor(index).get_geometry()
        areas.append(MonitorArea(geometry.x, geometry.y, geometry.width, geometry.height))
    return areas or [MonitorArea(0, 0, *screen_size())]


def _screenshot_from_pixbuf(pixbuf, area: MonitorArea) -> Screenshot:
    # On a HiDPI screen (GDK_SCALE=2, or a Wayland desktop scaled up) the picture has more pixels
    # than the area asked for, so the downscale and the scale kept are measured from the picture
    # itself, not assumed from the area.
    shrink = picture_scale(pixbuf.get_width(), pixbuf.get_height())
    if shrink < 1.0:
        pixbuf = pixbuf.scale_simple(round(pixbuf.get_width() * shrink), round(pixbuf.get_height() * shrink), GdkPixbuf.InterpType.BILINEAR)
    # Picture pixels per screen pixel, the way a point the model gives is mapped back.
    scale = pixbuf.get_width() / area.width
    succeeded, png_bytes = pixbuf.save_to_bufferv("png", [], [])
    if not succeeded:
        raise RuntimeError("the screenshot could not be encoded")
    return Screenshot(png_bytes=bytes(png_bytes), width=pixbuf.get_width(), height=pixbuf.get_height(), scale=scale,
                      origin_x=area.x, origin_y=area.y)


def _capture_area(root, area: MonitorArea) -> Screenshot:
    pixbuf = Gdk.pixbuf_get_from_window(root, area.x, area.y, area.width, area.height)
    if pixbuf is None:
        raise RuntimeError("the screen could not be read")
    return _screenshot_from_pixbuf(pixbuf, area)


def crop_rectangle(area: MonitorArea, picture_width: int, picture_height: int, desktop_width: int, desktop_height: int) -> tuple[int, int, int, int]:
    """A monitor's part of a picture of the whole desktop (x, y, width, height in the picture's
    pixels): the picture may have more pixels than the desktop has (a scaled-up Wayland desktop)."""
    ratio_x = picture_width / desktop_width
    ratio_y = picture_height / desktop_height
    left = max(0, min(picture_width - 1, round(area.x * ratio_x)))
    top = max(0, min(picture_height - 1, round(area.y * ratio_y)))
    right = max(left + 1, min(picture_width, round((area.x + area.width) * ratio_x)))
    bottom = max(top + 1, min(picture_height, round((area.y + area.height) * ratio_y)))
    return left, top, right - left, bottom - top


def screens_from_desktop_picture(png_bytes: bytes, monitors: list[MonitorArea], desktop_size: tuple[int, int]) -> list[Screenshot]:
    """One picture per monitor, cut from a picture of the whole desktop, in the order given."""
    loader = GdkPixbuf.PixbufLoader.new_with_type("png")
    loader.write(png_bytes)
    loader.close()
    desktop = loader.get_pixbuf()
    if desktop is None:
        raise RuntimeError("the screenshot could not be read")
    screens = []
    for area in monitors:
        left, top, width, height = crop_rectangle(area, desktop.get_width(), desktop.get_height(), *desktop_size)
        screens.append(_screenshot_from_pixbuf(desktop.new_subpixbuf(left, top, width, height), area))
    return screens


def primary_monitor_first() -> list[MonitorArea]:
    """Wayland never says where the mouse is: the primary monitor (the top bar's) goes first."""
    display = Gdk.Display.get_default()
    primary = display.get_primary_monitor() or display.get_monitor(0)
    geometry = primary.get_geometry() if primary else None
    if geometry is None:
        return monitor_areas()
    return monitors_mouse_first(monitor_areas(), geometry.x + geometry.width / 2, geometry.y + geometry.height / 2)


def screens_from_frames(frames, primary: MonitorArea | None) -> list[Screenshot]:
    """One picture per screen cast frame (`screencast.MonitorFrame`), the main monitor's first."""
    from gi.repository import GLib

    ordered = sorted(frames, key=lambda frame: (primary is None or (frame.x, frame.y) != (primary.x, primary.y), frame.x, frame.y))
    screens = []
    for frame in ordered[:MAXIMUM_SCREENS]:
        pixbuf = GdkPixbuf.Pixbuf.new_from_bytes(GLib.Bytes.new(frame.pixels), GdkPixbuf.Colorspace.RGB, False, 8,
                                                 frame.width, frame.height, frame.stride)
        screens.append(_screenshot_from_pixbuf(pixbuf, MonitorArea(frame.x, frame.y, frame.logical_width, frame.logical_height)))
    return screens


def capture_screens_wayland():
    """Wayland: the desktop's own pictures, cut into one per monitor: grim on wlroots desktops, else a
    screen cast (`screencast.py`: asked once, then silent), else the screenshot portal (which GNOME
    asks about every time). The monitors are read here, on the main thread; the returned function
    takes the pictures and is run off it (it waits on the desktop, and GdkPixbuf needs no display)."""
    from . import portal, screencast

    monitors = primary_monitor_first()[:MAXIMUM_SCREENS]
    desktop_size = screen_size()
    if not portal.uses_grim() and screencast.is_available():
        return lambda: screens_from_frames(screencast.capture_monitors(), monitors[0] if monitors else None)
    return lambda: screens_from_desktop_picture(portal.take_screenshot(), monitors, desktop_size)


def capture_screens() -> list[Screenshot]:
    root = Gdk.get_default_root_window()
    _screen, mouse_x, mouse_y = Gdk.Display.get_default().get_default_seat().get_pointer().get_position()[0:3]
    ordered = monitors_mouse_first(monitor_areas(), mouse_x, mouse_y)[:MAXIMUM_SCREENS]
    return [_capture_area(root, area) for area in ordered]


def capture_screen() -> Screenshot:
    """The whole X screen as one picture."""
    return _capture_area(Gdk.get_default_root_window(), MonitorArea(0, 0, *screen_size()))
