"""When the pointer is on screen while it follows the mouse: the Mac app's `PointerPresence.swift`,
ported with its numbers.

- "always": beside the mouse all the time
- "whileMoving": fades out once the mouse has rested a few seconds; any movement brings it back
- "onShake": out of sight until the mouse is shaken (the way macOS finds a lost cursor), then it
  follows until the mouse rests
- "whenAnswering" (Linux only): out of sight until an answer shows

A pointer that is flying, pointing or showing a caption is always shown, whatever the presence.
Pure: fed with mouse samples and timestamps, so it is tested without a mouse.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

PRESENCES = ("always", "whileMoving", "onShake", "whenAnswering")
PRESENCE_LABELS = {
    "always": "always, beside the mouse",
    "whileMoving": "while the mouse moves",
    "onShake": "after a shake",
    "whenAnswering": "only while answering",
}


@dataclass
class _Axis:
    last: float | None = None
    direction: int = 0
    swing_start: float = 0.0
    reversals: list[float] = field(default_factory=list)

    def feed(self, value: float, at_seconds: float, minimum_swing: float, window: float, reversals_needed: int) -> bool:
        last = self.last
        self.last = value
        if last is None:
            self.swing_start = value
            return False
        delta = value - last
        if abs(delta) < 1:
            return False
        new_direction = 1 if delta > 0 else -1
        if new_direction != self.direction:
            if self.direction != 0 and abs(last - self.swing_start) >= minimum_swing:
                self.reversals.append(at_seconds)
            self.swing_start = last
            self.direction = new_direction
        self.reversals = [reversal for reversal in self.reversals if at_seconds - reversal <= window]
        if len(self.reversals) >= reversals_needed:
            self.reversals.clear()
            return True
        return False


class MouseShakeDetector:
    """Several quick reversals of direction, each after a swing long enough that a wobble of the
    hand does not count. Three reversals of 25 pt woke the Mac's pointer during ordinary mousing,
    so it takes four, each after a swing of at least 60."""

    def __init__(self, window_seconds: float = 0.8, minimum_swing: float = 60, reversals_needed: int = 4) -> None:
        self.window_seconds = window_seconds
        self.minimum_swing = minimum_swing
        self.reversals_needed = reversals_needed
        self._horizontal = _Axis()
        self._vertical = _Axis()

    def feed(self, x: float, y: float, at_seconds: float) -> bool:
        """True on the sample that completes a shake."""
        arguments = (at_seconds, self.minimum_swing, self.window_seconds, self.reversals_needed)
        shook_sideways = self._horizontal.feed(x, *arguments)
        shook_up_and_down = self._vertical.feed(y, *arguments)
        return shook_sideways or shook_up_and_down


class PointerPresenceTracker:
    """Decides, sample by sample, whether the following pointer should be on screen."""

    def __init__(self, presence: str = "always", rest_delay_seconds: float = 3.0) -> None:
        self.rest_delay_seconds = rest_delay_seconds
        self._presence = "always"
        self.is_awake = True
        self.shook_on_last_update = False
        self._last_moved_at: float | None = None
        self._last_point: tuple[float, float] | None = None
        self._shake = MouseShakeDetector()
        self.presence = presence

    @property
    def presence(self) -> str:
        return self._presence

    @presence.setter
    def presence(self, presence: str) -> None:
        self._presence = presence if presence in PRESENCES else "always"
        if self._presence in ("always", "whileMoving"):
            self.is_awake = True
        elif self._presence in ("onShake", "whenAnswering"):
            self.is_awake = False

    def update(self, x: float, y: float, at_seconds: float) -> bool:
        """Feed the mouse position; returns whether the pointer should be on screen."""
        if self._last_point is None:
            moved = False
            self._last_moved_at = at_seconds
        else:
            moved = math.dist((x, y), self._last_point) >= 1
        self._last_point = (x, y)
        if moved:
            self._last_moved_at = at_seconds
        shaken = self._shake.feed(x, y, at_seconds)
        self.shook_on_last_update = self._presence == "onShake" and shaken
        rested_for = at_seconds - (self._last_moved_at if self._last_moved_at is not None else at_seconds)

        if self._presence == "always":
            self.is_awake = True
        elif self._presence == "whileMoving":
            self.is_awake = moved or rested_for < self.rest_delay_seconds
        elif self._presence == "onShake":
            if shaken:
                self.is_awake = True
                self._last_moved_at = at_seconds
                rested_for = 0.0
            if self.is_awake and not moved and rested_for >= self.rest_delay_seconds:
                self.is_awake = False
        else:  # whenAnswering: only an answer shows it, never the mouse
            self.is_awake = False
        return self.is_awake

    def wake(self, at_seconds: float) -> None:
        """Show the pointer now (it just flew back to the mouse); it rests away again as usual."""
        self.is_awake = self._presence != "whenAnswering"
        self._last_moved_at = at_seconds
