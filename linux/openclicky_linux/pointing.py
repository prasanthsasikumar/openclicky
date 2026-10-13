"""The `[POINT:x,y:label:screenN]` tag an answer starts with, and the pointer's flight to it.

`parse_pointing` is the Mac app's `CompanionManager.parsePointingCoordinates`, ported: a leading tag
is cut off, then a trailing one is looked for once in what is left (no recursion: two tags with
nothing between them made the Mac app recurse forever), and the first tag that reads as one decides
where to point. A malformed leading tag is still never shown.

The flight is a quadratic bezier arcing above the straight line, eased in and out, as on the Mac.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass

_TAG = r"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]"
_TRAILING_TAG = re.compile(_TAG + r"\s*$")
_WHOLE_TAG = re.compile("^" + _TAG + "$")
_LEADING_TAG = re.compile(r"^\s*\[POINT:[^\]]*\]")


@dataclass(frozen=True)
class PointingResult:
    text: str
    # In the screenshot's pixels; None for [POINT:none] or no tag.
    x: float | None = None
    y: float | None = None
    label: str | None = None
    screen_number: int | None = None

    @property
    def has_point(self) -> bool:
        return self.x is not None and self.y is not None


def _read_tag(tag: str) -> PointingResult | None:
    match = _WHOLE_TAG.match(tag)
    if not match:
        return None
    if match.group(1) is None:
        return PointingResult(text="", label="none")
    return PointingResult(text="", x=float(match.group(1)), y=float(match.group(2)),
                          label=match.group(3).strip() if match.group(3) else None,
                          screen_number=int(match.group(4)) if match.group(4) else None)


def parse_pointing(answer: str) -> PointingResult:
    leading_tag = None
    after_leading = answer
    leading = _LEADING_TAG.match(answer)
    if leading:
        leading_tag = leading.group(0).strip()
        after_leading = answer[leading.end():]
    trailing_tag = None
    text = after_leading
    trailing = _TRAILING_TAG.search(after_leading)
    if trailing:
        trailing_tag = trailing.group(0).strip()
        text = after_leading[:trailing.start()]
    text = text.strip()
    for tag in (leading_tag, trailing_tag):
        if tag and (read := _read_tag(tag)):
            return PointingResult(text=text, x=read.x, y=read.y, label=read.label, screen_number=read.screen_number)
    return PointingResult(text=text if leading_tag else answer.strip())


@dataclass(frozen=True)
class MonitorArea:
    x: int
    y: int
    width: int
    height: int

    def contains(self, point_x: float, point_y: float) -> bool:
        return self.x <= point_x < self.x + self.width and self.y <= point_y < self.y + self.height


def monitors_mouse_first(monitors: list[MonitorArea], mouse_x: float, mouse_y: float) -> list[MonitorArea]:
    """The monitor with the mouse first (the "primary focus" the model is told to prioritise), then
    the others left to right, top to bottom: so screen 2, 3… keep their numbers from one question to
    the next as long as the mouse stays put."""
    with_mouse = [monitor for monitor in monitors if monitor.contains(mouse_x, mouse_y)][:1]
    others = sorted((monitor for monitor in monitors if monitor not in with_mouse), key=lambda monitor: (monitor.x, monitor.y))
    return with_mouse + others


def point_on_screens(x: float, y: float, screen_number: int | None, screens: list) -> tuple[int, int]:
    """A point in screen N's picture (1-based, the first when not named or out of range) → the X
    screen's pixels, kept on that monitor. `screens` are `ask.Screenshot`s, in the order sent."""
    index = (screen_number - 1) if screen_number and 1 <= screen_number <= len(screens) else 0
    screen = screens[index]
    monitor_width = round(screen.width / screen.scale)
    monitor_height = round(screen.height / screen.scale)
    on_monitor_x, on_monitor_y = screenshot_point_to_screen(x, y, screen.scale, monitor_width, monitor_height)
    return screen.origin_x + on_monitor_x, screen.origin_y + on_monitor_y


def screenshot_point_to_screen(x: float, y: float, screenshot_scale: float, screen_width: int, screen_height: int) -> tuple[int, int]:
    """A point in the (possibly downscaled) screenshot → the screen's own pixels, kept on screen."""
    screen_x = round(x / screenshot_scale)
    screen_y = round(y / screenshot_scale)
    return min(max(screen_x, 0), screen_width - 1), min(max(screen_y, 0), screen_height - 1)


def ease_in_out(progress: float) -> float:
    return 3 * progress ** 2 - 2 * progress ** 3


def flight_position(start: tuple[float, float], end: tuple[float, float], progress: float) -> tuple[float, float]:
    """Where the pointer is at `progress` (0…1) of a flight: an arc that bows upward by a fifth of the
    distance, so a flight reads as a deliberate move rather than a jump."""
    eased = ease_in_out(min(max(progress, 0.0), 1.0))
    distance = math.dist(start, end)
    control = ((start[0] + end[0]) / 2, min(start[1], end[1]) - distance * 0.2)
    one_minus = 1 - eased
    x = one_minus ** 2 * start[0] + 2 * one_minus * eased * control[0] + eased ** 2 * end[0]
    y = one_minus ** 2 * start[1] + 2 * one_minus * eased * control[1] + eased ** 2 * end[1]
    return x, y


def flight_seconds(start: tuple[float, float], end: tuple[float, float]) -> float:
    """Longer flights take longer, within limits: a short hop is not instant, a long one not slow."""
    return min(1.2, max(0.45, math.dist(start, end) / 1400))
