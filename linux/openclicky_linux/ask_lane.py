"""The person stays in charge of the ask lane, and the model is told what they point at: the Mac's
`AskLaneControl.swift`, `AskPointerContext.swift`, `GuidedStepClickCheck` (in `GuidedSteps.swift`)
and `SystemPromptGuard.swift`, ported with their numbers and sentences.

- **The pause before the screen is sent**: for a moment after a question the pointer says
  "sending your screen · esc to cancel"; esc calls it off before anything is captured. Settings →
  Privacy turns it off; a guide's own "what's next?" never waits.
- **[UNSURE]**: a reply that cannot find what to point at opens with the marker, carries no point
  (a tag sent anyway is ignored) and is never a guide's step.
- **Where the mouse is** (X11; Wayland never says): its place in the screenshot goes into the
  question, and a close-up of the area around it rides along as one more picture, so "what is
  this?" is about the thing under the pointer and not the window with the keyboard. The same
  question asked again within three minutes tells the model its last answer probably missed.
- **The click that finished a guided step** (X11): well away from where the pointer pointed, it is
  "that wasn't the spot i pointed at, let me look", and the next step checks the screen first.
- **Password and permission prompts** (X11): a point that lands on one is not shown. Granting a
  permission is the person's choice, never a step.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass

# MARK: the pause before the screen is sent

SEND_PAUSE_SECONDS = 1.2
SEND_PAUSE_CAPTION = "sending your screen · esc to cancel"


def send_pause_seconds(is_enabled: bool, is_guided_step_continuation: bool, pause_seconds: float = SEND_PAUSE_SECONDS) -> float | None:
    """How long to wait before the screenshot, or None to take it at once."""
    if not is_enabled or is_guided_step_continuation or pause_seconds <= 0:
        return None
    return pause_seconds


# MARK: [UNSURE]

UNSURE_MARKER = "[UNSURE]"
UNSURE_STATUS = "not sure · tell me what you see"
_UNSURE_PATTERN = re.compile(r"\[\s*unsure\s*\]", re.IGNORECASE)

UNSURE_PROMPT_SECTION = f"""
not sure:
if you can't find the control they need on the screen, or can't tell which one it is, don't guess with the pointer. start your reply with {UNSURE_MARKER}, send no point tag at all, and say in words what they should look for (its name, what it looks like, roughly where it usually is), or ask them to tell you what they see. leave [NEXT] off that reply.
example: "{UNSURE_MARKER} i can't spot the display settings on this screen. look for a gear icon called settings in your app menu, or tell me what you see."
"""


def contains_unsure_marker(text: str) -> bool:
    return _UNSURE_PATTERN.search(text) is not None


def removing_unsure_markers(text: str) -> str:
    return _UNSURE_PATTERN.sub("", text).strip()


# MARK: where the mouse is


@dataclass(frozen=True)
class PointerInCapture:
    """The mouse in the screenshot it is on, in that picture's pixels from the top-left."""
    x: int
    y: int
    # 1-based, as the model sees the pictures ("screen 1").
    screen_number: int

    @property
    def prompt_line(self) -> str:
        return (f"The mouse pointer is at ({self.x}, {self.y}) from the top-left of the screenshot labelled screen {self.screen_number}. "
                "When the user asks what \"this\" or \"that\" is, or what they are pointing at, they mean what is under the pointer or "
                "right next to it, not the window with keyboard focus. Name the element you are describing.")


def locate_pointer(mouse_x: float, mouse_y: float, screens: list) -> PointerInCapture | None:
    """The mouse placed in the picture (`ask.Screenshot`) of the monitor it is on; None when none is."""
    for number, screen in enumerate(screens, start=1):
        monitor_width = screen.width / screen.scale
        monitor_height = screen.height / screen.scale
        if screen.origin_x <= mouse_x < screen.origin_x + monitor_width and screen.origin_y <= mouse_y < screen.origin_y + monitor_height:
            return PointerInCapture(round((mouse_x - screen.origin_x) * screen.scale), round((mouse_y - screen.origin_y) * screen.scale), number)
    return None


CLOSE_UP_SIDE = 480


def close_up_square(pointer: PointerInCapture, image_width: int, image_height: int, side: int = CLOSE_UP_SIDE) -> tuple[int, int, int] | None:
    """(left, top, side) of the square around the pointer, pushed back inside the picture near an edge."""
    side = min(side, image_width, image_height)
    if side <= 0:
        return None
    left = min(max(pointer.x - side // 2, 0), image_width - side)
    top = min(max(pointer.y - side // 2, 0), image_height - side)
    return left, top, side


def close_up_label(side: int = CLOSE_UP_SIDE) -> str:
    return (f"Close-up: the {side}x{side} pixel area around the mouse pointer, cut from the screenshot the pointer is on (the pointer "
            "is at its centre unless it is near an edge). For looking only: never use this image's coordinates in a [POINT:x,y] tag.")


REPEAT_WITHIN_SECONDS = 180
REPEAT_MAXIMUM_DIFFERENCE = 0.25
REPEATED_QUESTION_LINE = ("The user just asked nearly the same question again. The previous answer was probably not about what they "
                          "meant: this time answer about what is under the mouse pointer, and say which element you are describing.")


def normalized_question(text: str) -> str:
    """Lowercase letters, digits and single spaces: punctuation and apostrophes make no new question."""
    kept = "".join(character if character.isalnum() or character == " " else ("" if character in "'’" else " ")
                   for character in text.lower())
    return " ".join(kept.split())


def _edit_distance(left: str, right: str) -> int:
    previous_row = list(range(len(right) + 1))
    for left_index, left_character in enumerate(left):
        current_row = [left_index + 1]
        for right_index, right_character in enumerate(right):
            current_row.append(min(previous_row[right_index + 1] + 1, current_row[right_index] + 1,
                                   previous_row[right_index] + (left_character != right_character)))
        previous_row = current_row
    return previous_row[-1]


def is_repeated_question(question: str, previous: str | None, seconds_since_previous: float) -> bool:
    if previous is None or seconds_since_previous > REPEAT_WITHIN_SECONDS:
        return False
    current, earlier = normalized_question(question), normalized_question(previous)
    if not current or not earlier:
        return False
    return _edit_distance(current, earlier) / max(len(current), len(earlier)) <= REPEAT_MAXIMUM_DIFFERENCE


# MARK: the click that finished a guided step

OFF_TARGET_DISTANCE = 120
OFF_TARGET_CAPTION = "that wasn't the spot i pointed at, let me look"


def click_verdict(pointed_at: tuple[float, float] | None, clicked_at: tuple[float, float] | None,
                  threshold: float = OFF_TARGET_DISTANCE) -> int | None:
    """None: on target, or unknown (Return or Tab finished the step, nothing pointed, Wayland). Else
    how far off, rounded to ten pixels."""
    if pointed_at is None or clicked_at is None:
        return None
    distance = math.dist(pointed_at, clicked_at)
    if distance <= threshold:
        return None
    return int(round(distance / 10)) * 10


# MARK: password and permission prompts

# Window classes (lowercased) of the programs that draw Linux's password and permission prompts.
PROMPT_WINDOW_CLASSES = frozenset({
    "gcr-prompter", "polkit-gnome-authentication-agent-1", "polkit-kde-authentication-agent-1", "polkit-mate-authentication-agent-1",
    "lxpolkit", "lxqt-policykit-agent", "xfce-polkit", "pinentry", "pinentry-gtk-2", "pinentry-gnome3", "pinentry-qt",
    "ksshaskpass", "ssh-askpass", "gnome-ssh-askpass", "x11-ssh-askpass", "kdesu", "gksu", "polkit-dumb-agent",
})
PROMPT_MARGIN = 40


@dataclass(frozen=True)
class OnScreenWindow:
    window_class: str
    x: int
    y: int
    width: int
    height: int


def is_on_system_prompt(point: tuple[float, float], windows: list[OnScreenWindow]) -> bool:
    """Whether a point is on, or within the models' error of, a password or permission prompt."""
    point_x, point_y = point
    for window in windows:
        if window.window_class.lower() not in PROMPT_WINDOW_CLASSES:
            continue
        if (window.x - PROMPT_MARGIN <= point_x <= window.x + window.width + PROMPT_MARGIN
                and window.y - PROMPT_MARGIN <= point_y <= window.y + window.height + PROMPT_MARGIN):
            return True
    return False
