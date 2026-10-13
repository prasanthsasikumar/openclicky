"""The screenshots a question carries: one per monitor, the one with the mouse first, each scaled
down to what the vision models actually look at (see `picture_scale`), as PNG.
Each keeps its scale and its monitor's place on the X screen, so a point the model gives in a
picture's pixels (and its :screenN) maps back to the desktop. One monitor gives one picture of the
whole screen, as before; two side by side no longer make one wide picture whose text is too small
to read.
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


def _capture_area(root, area: MonitorArea) -> Screenshot:
    pixbuf = Gdk.pixbuf_get_from_window(root, area.x, area.y, area.width, area.height)
    if pixbuf is None:
        raise RuntimeError("the screen could not be read")
    # On a HiDPI screen (GDK_SCALE=2) the picture has twice the pixels of the area asked for, so the
    # downscale and the scale kept are measured from the picture itself, not assumed from the area.
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


def capture_screens() -> list[Screenshot]:
    root = Gdk.get_default_root_window()
    _screen, mouse_x, mouse_y = Gdk.Display.get_default().get_default_seat().get_pointer().get_position()[0:3]
    ordered = monitors_mouse_first(monitor_areas(), mouse_x, mouse_y)[:MAXIMUM_SCREENS]
    return [_capture_area(root, area) for area in ordered]


def capture_screen() -> Screenshot:
    """The whole X screen as one picture."""
    return _capture_area(Gdk.get_default_root_window(), MonitorArea(0, 0, *screen_size()))
