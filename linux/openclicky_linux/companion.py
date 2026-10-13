"""Asking by text, one question at a time: left ctrl tapped twice opens the ask box; Enter sends the
question with a screenshot; the pointer says "thinking…", then the answer, flying first to what the
answer is about when it starts with a `[POINT:…]` tag. Esc dismisses the answer and the pointer
comes back to the mouse. A new question replaces one still being answered (its answer is dropped).

Step by step guidance (`guided.py`, the Mac's): an answer ending in [NEXT] is one step of a guide.
The controller then waits for the person to do it — `step_input` is fed every click and key while a
step waits — and asks for the next step on its own with a fresh screenshot, repeating the original
question, until an answer comes without the marker. Esc, a new question, two minutes without a
click or Return, or fifteen steps end the guide.

The desktop is reached only through `AskHost` and `PointerSurface`, so the tests run it with fakes.
"""

from __future__ import annotations

import itertools
from dataclasses import dataclass
from typing import Any, Callable, Protocol

from . import guided
from .ask import Conversation, Screenshot, as_screen_list
from .pointing import parse_pointing, point_on_screens

THINKING_CAPTION = "thinking…"
# The ask box, the pointer and its caption are out of sight before the screenshot is taken (the
# model would see them, and on a guided step the pointer would cover the very thing to check); a
# compositor needs a moment to draw the screen without them.
COMPOSER_HIDE_SETTLE_SECONDS = 0.15


class PointerSurface(Protocol):
    def hide_for_screenshot(self) -> None: ...
    def show_after_screenshot(self) -> None: ...
    def show_caption(self, text: str, hold_seconds: float | None) -> None: ...
    def dismiss_caption(self) -> None: ...
    def fly_to(self, screen_x: int, screen_y: int) -> None: ...
    def return_to_mouse(self) -> None: ...


@dataclass
class AskHost:
    show_composer: Callable[[], None]
    hide_composer: Callable[[], None]
    # One picture per monitor, the one with the mouse first (or a single picture).
    capture_screen: Callable[[], "Screenshot | list[Screenshot]"]
    # (question, pictures, conversation) → the model's answer; raises with a sentence to show.
    ask: Callable[[str, "Screenshot | list[Screenshot]", Conversation], str]
    run_in_background: Callable[[Callable[[], Any], Callable[[Any], None]], None]
    schedule: Callable[[float, Callable[[], None]], None]
    # Spoken answers (voice.py): get ready as a question starts, speak an answer, stop on esc.
    prepare_voice: Callable[[], None] = lambda: None
    speak: Callable[[str], None] = lambda _text: None
    stop_speaking: Callable[[], None] = lambda: None


def caption_hold_seconds(text: str) -> float:
    """Long enough to read: four seconds plus a little per character, at most fifteen."""
    return min(15.0, 4.0 + len(text) * 0.05)


class AskController:
    def __init__(self, host: AskHost, pointer: PointerSurface) -> None:
        self.host = host
        self.pointer = pointer
        self.conversation = Conversation()
        self._question_ids = itertools.count(1)
        self.question_id = 0
        self.is_answering = False
        self.is_showing_answer = False
        # The guide under way: what was first asked, how many steps were given, whether one waits.
        self.guide_goal = ""
        self.guide_steps_given = 0
        self.is_waiting_for_step = False
        # Bumped to cancel a scheduled "ask for the next step" or the guide's idle timeout.
        self._step_wait_ids = itertools.count(1)
        self._pending_ask_id: int | None = None
        self._step_wait_id = 0
        # Every question asked, in order (the tests and the log read it).
        self.questions_asked: list[str] = []

    def open_composer(self) -> None:
        self.host.show_composer()

    def submit(self, question: str) -> None:
        """A question the person typed: anything they ask starts afresh, ending a guide under way."""
        question = question.strip()
        self.host.hide_composer()
        if not question:
            return
        self._stop_waiting_for_step()
        self.guide_goal = question
        self.guide_steps_given = 0
        self._ask(question)

    def _ask(self, question: str) -> None:
        self.questions_asked.append(question)
        # A new question ends whatever is still being said, and the voice gets ready (it loads in a
        # few seconds, while the answer is being thought about).
        self.host.stop_speaking()
        self.host.prepare_voice()
        this_question_id = next(self._question_ids)
        self.question_id = this_question_id
        self.is_answering = True
        self.pointer.hide_for_screenshot()
        self.host.schedule(COMPOSER_HIDE_SETTLE_SECONDS, lambda: self._capture_and_ask(question, this_question_id))

    def _capture_and_ask(self, question: str, this_question_id: int) -> None:
        if this_question_id != self.question_id:
            return
        try:
            screenshot = self.host.capture_screen()
        except Exception as failure:  # noqa: BLE001 - shown to the person, like any other failure
            self.pointer.show_after_screenshot()
            self._show_failure(f"couldn't see the screen: {failure}", this_question_id)
            return
        self.pointer.show_after_screenshot()
        self.pointer.show_caption(THINKING_CAPTION, hold_seconds=None)
        conversation = self.conversation
        self.host.run_in_background(
            lambda: (screenshot, self.host.ask(question, screenshot, conversation)),
            lambda outcome: self._show_answer(question, outcome, this_question_id))

    def _show_answer(self, question: str, outcome: Any, this_question_id: int) -> None:
        if this_question_id != self.question_id:
            return
        if isinstance(outcome, BaseException):
            self._show_failure(str(outcome) or "couldn't get an answer", this_question_id)
            return
        screenshot, answer = outcome
        self.is_answering = False
        is_a_guided_step = guided.contains_marker(answer)
        result = parse_pointing(guided.removing_markers(answer))
        # Kept without the tag and the marker, as on the Mac: they would only confuse later answers.
        # Never empty: an answer that was only a tag would be an empty assistant turn, which
        # Anthropic refuses, and every question after it would fail.
        self.conversation.remember(question, result.text or f"(pointed at {result.label or 'nothing'})")
        if result.has_point:
            screen_x, screen_y = point_on_screens(result.x, result.y, result.screen_number, as_screen_list(screenshot))
            self.pointer.fly_to(screen_x, screen_y)
        else:
            # Still pointing at the last step's target would point at the wrong thing: back to the mouse.
            self.pointer.return_to_mouse()
        text = result.text or "…"
        self.is_showing_answer = True
        self.pointer.show_caption(text, hold_seconds=caption_hold_seconds(text))
        if result.text:
            self.host.speak(result.text)
        if is_a_guided_step:
            self._wait_for_step()

    def _show_failure(self, message: str, this_question_id: int) -> None:
        if this_question_id != self.question_id:
            return
        self.is_answering = False
        self.is_showing_answer = True
        self.pointer.show_caption(message, hold_seconds=6.0)

    def escape(self) -> bool:
        """Esc: drops a question still being answered, dismisses the answer and ends a guide. True if
        it did anything."""
        if not self.is_answering and not self.is_showing_answer and not self.is_waiting_for_step:
            return False
        self.pointer.show_after_screenshot()
        self.host.stop_speaking()
        self._stop_waiting_for_step()
        self.guide_steps_given = 0
        self.question_id = next(self._question_ids)
        self.is_answering = False
        self.is_showing_answer = False
        self.pointer.dismiss_caption()
        self.pointer.return_to_mouse()
        return True

    def caption_finished(self) -> None:
        """The pointer's caption ran out its time: the pointer goes back to the mouse."""
        self.is_showing_answer = False
        self.pointer.return_to_mouse()

    # MARK: guided steps

    def _wait_for_step(self) -> None:
        self.guide_steps_given += 1
        if self.guide_steps_given >= guided.MAXIMUM_STEPS:
            return
        self._stop_waiting_for_step()
        self.is_waiting_for_step = True
        this_wait_id = self._step_wait_id
        self.host.schedule(guided.IDLE_SECONDS_BEFORE_GIVING_UP, lambda: self._give_up_on_step(this_wait_id))

    def _stop_waiting_for_step(self) -> None:
        self.is_waiting_for_step = False
        self._pending_ask_id = None
        self._step_wait_id = next(self._step_wait_ids)

    def _give_up_on_step(self, wait_id: int) -> None:
        if wait_id == self._step_wait_id and self.is_waiting_for_step:
            self._stop_waiting_for_step()
            self.guide_steps_given = 0

    def step_input(self, step_input: str) -> None:
        """A click or a key (guided.CLICK, STEP_KEY, ESCAPE, OTHER_KEY) anywhere, while a step may wait."""
        if not self.is_waiting_for_step:
            return
        reaction = guided.reaction(step_input, ask_is_pending=self._pending_ask_id is not None)
        if reaction.kind == "end guide":
            self.escape()
        elif reaction.kind in ("ask for next step", "postpone"):
            # Another click or key restarts the wait, so a double click or a quick second click (a
            # menu, then its item) is one step.
            this_ask_id = next(self._step_wait_ids)
            self._pending_ask_id = this_ask_id
            self.host.schedule(reaction.settle_seconds, lambda: self._step_done(this_ask_id))

    def _step_done(self, ask_id: int) -> None:
        if ask_id != self._pending_ask_id or not self.is_waiting_for_step:
            return
        self._stop_waiting_for_step()
        self._ask(guided.next_step_request(self.guide_goal))
