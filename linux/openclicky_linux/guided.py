"""Step by step guidance: the Mac app's `GuidedSteps.swift`, ported with its numbers and sentences.

Asked how to do something that takes several steps on screen, the answer gives and points at only
the first step and ends with the [NEXT] marker. OpenClicky then waits for the person to do it (a
click, or Return or Tab after typing), and a moment later, once a menu or dialog has had time to
open, asks again on its own with a fresh screenshot; the answer is the next step, until one comes
without the marker. A wall of instructions is no use to someone who looks away from it after every
step: this way the screen they are looking at is the one being described.

Pure: the rules are tested without a mouse or keyboard.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

MARKER = "[NEXT]"
_MARKER_PATTERN = re.compile(r"\[\s*next\s*\]", re.IGNORECASE)

# A menu, sheet or dialog opened by the click needs a moment before the screenshot shows it.
SETTLE_SECONDS_AFTER_CLICK = 1.5
SETTLE_SECONDS_AFTER_STEP_KEY = 1.2
# Typing after a click (into the field it opened) waits for this much quiet, so a slow typist's
# pause in the middle of an address is not taken for the end of the step.
SETTLE_SECONDS_WHILE_TYPING = 4.0
# No click or Return for this long and the guide is over (they gave up or went elsewhere).
IDLE_SECONDS_BEFORE_GIVING_UP = 120.0
# A runaway guide (a model that always says there is more) stops here.
MAXIMUM_STEPS = 15

NEXT_STEP_REQUEST_OPENING = "(i did that step. this is my screen now. what's next?"


def contains_marker(text: str) -> bool:
    return _MARKER_PATTERN.search(text) is not None


def removing_markers(text: str) -> str:
    """The text without any marker, trimmed: what is shown and kept in the conversation."""
    return _MARKER_PATTERN.sub("", text).strip()


def next_step_request(goal: str) -> str:
    """The question OpenClicky asks for the person once they did a step. It repeats what they first
    asked, because the conversation keeps only the last ten exchanges and a long guide would
    otherwise lose its goal."""
    return f"{NEXT_STEP_REQUEST_OPENING} what i asked was: {goal})"


# What the person did while a step was waiting.
CLICK = "click"
STEP_KEY = "step key"  # Return, keypad Enter or Tab: what ends typing into a field
ESCAPE = "escape"
OTHER_KEY = "other key"


@dataclass(frozen=True)
class Reaction:
    kind: str  # "ask for next step" | "postpone" | "end guide" | "ignore"
    settle_seconds: float = 0.0


def reaction(step_input: str, ask_is_pending: bool) -> Reaction:
    if step_input == CLICK:
        return Reaction("ask for next step", SETTLE_SECONDS_AFTER_CLICK)
    if step_input == STEP_KEY:
        return Reaction("ask for next step", SETTLE_SECONDS_AFTER_STEP_KEY)
    if step_input == ESCAPE:
        return Reaction("end guide")
    # Typing on its own is not a finished step ("type the address" ends with a click, Return or
    # Tab), but typing right after a click means the click only put the cursor in a field.
    return Reaction("postpone", SETTLE_SECONDS_WHILE_TYPING) if ask_is_pending else Reaction("ignore")


PROMPT_SECTION = f"""
guided steps:
when they ask how to do something that takes more than one step on screen, give only the first step, point at it, and end your reply with {MARKER}. a step is one click, or one thing to type, nothing more. openclicky then waits for them to do it (a click, or return or tab after typing) and asks you on its own with "{NEXT_STEP_REQUEST_OPENING} …" and a fresh screenshot. look at the new screen and give the next step the same way: one step, pointed at, ending with {MARKER} while more steps remain. leave {MARKER} off the last step, and off any answer that isn't a step. if the new screen shows they clicked something else or went somewhere unexpected, gently help them back, still ending with {MARKER}. if the screen hasn't changed the way that step should change it, it didn't work yet: point at the same thing again and say to try once more. if you can't tell whether the step worked, say what they should see and what to do then, and end with {MARKER} so you keep waiting with them. never end a step with a question. if a permission, password or security prompt is on screen and it isn't what they asked about, it is not a step: never point at it or tell them which button to pick. reply as usual, the tag first pointing at the first step of their task, then a few words that the prompt is theirs to decide, then the step, ending with {MARKER}. for a step that is typing, say what to type and to press tab or click the next thing when they're done.
example: user asks how to turn on dark mode: "[POINT:612,88:settings button] open settings first. {MARKER}"
"""
