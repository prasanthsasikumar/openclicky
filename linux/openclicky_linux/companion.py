"""Asking, one question at a time: left ctrl tapped twice opens the ask box (or a question is asked
out loud); the question goes with a screenshot; the pointer says "thinking…", and the answer
streams in: the pointer flies as soon as the answer's `[POINT:…]` tag is complete, the caption
grows a sentence at a time, and the voice says the first sentence while the rest is still being
written. Esc dismisses the answer and the pointer comes back. A new question replaces one still
being answered (its answer is dropped).

The person stays in charge (`ask_lane.py`, the Mac's `AskLaneControl`): before the screen is sent
the pointer says "sending your screen · esc to cancel" for a moment (Settings → Privacy); an
answer marked [UNSURE] never points; a point on a password or permission prompt is not shown; the
question carries where the mouse is and a close-up around it, and says when it was just asked.

Step by step guidance (`guided.py`, the Mac's): an answer ending in [NEXT] is one step of a guide.
The controller then waits for the person to do it — `step_input` is fed every click and key while a
step waits — and asks for the next step on its own with a fresh screenshot, repeating the original
question, until an answer comes without the marker. A click well away from where the pointer
pointed says "that wasn't the spot i pointed at, let me look" and asks the model to check. Esc, a
new question, two minutes without a click or Return, or fifteen steps end the guide.

The desktop is reached only through `AskHost` and `PointerSurface`, so the tests run it with fakes.
"""

from __future__ import annotations

import itertools
import time
from dataclasses import dataclass
from typing import Any, Callable, Protocol

from . import applog, ask_lane, guided, writing
from .accessibility import selection_context_line
from .ask import AskExtras, Conversation, Screenshot, as_screen_list
from .pointing import parse_pointing, point_on_screens
from .voice import SpokenChunker

THINKING_CAPTION = "thinking…"
# The ask box, the pointer and its caption are out of sight before the screenshot is taken (the
# model would see them, and on a guided step the pointer would cover the very thing to check); a
# compositor needs a moment to draw the screen without them.
COMPOSER_HIDE_SETTLE_SECONDS = 0.15


class PointerSurface(Protocol):
    def hide_for_screenshot(self) -> None: ...
    def show_after_screenshot(self) -> None: ...
    def show_caption(self, text: str, hold_seconds: float | None, continue_typing: bool = False) -> None: ...
    def dismiss_caption(self) -> None: ...
    def fly_to(self, screen_x: int, screen_y: int) -> None: ...
    def return_to_mouse(self) -> None: ...


@dataclass
class AskHost:
    show_composer: Callable[[], None]
    hide_composer: Callable[[], None]
    # One picture per monitor, the one with the mouse first (or a single picture); or, on Wayland, a
    # function that takes them, which is run off the main thread.
    capture_screen: Callable[[], "Screenshot | list[Screenshot] | Callable[[], list[Screenshot]]"]
    # (question, pictures, conversation, extras=, on_text=) → the model's answer; raises with a
    # sentence to show. `on_text` hears each piece as it is written (from the worker thread).
    ask: Callable[..., str]
    run_in_background: Callable[[Callable[[], Any], Callable[[Any], None]], None]
    schedule: Callable[[float, Callable[[], None]], None]
    # Runs `action` on the main loop (a streamed answer's pieces arrive on a worker thread).
    on_main_loop: Callable[[Callable[[], None]], None] = lambda action: action()
    # Spoken answers (voice.py): get ready as a question starts; a streamed answer's pieces as they
    # are ready (begin, say each, end); stop on esc.
    prepare_voice: Callable[[], None] = lambda: None
    speak_begin: Callable[[], None] = lambda: None
    speak_piece: Callable[[str], None] = lambda _text: None
    speak_end: Callable[[], None] = lambda: None
    stop_speaking: Callable[[], None] = lambda: None
    # A failure's caption, for the voice to say when it is one of the recorded notices.
    say_notice: Callable[[str], None] = lambda _caption: None
    # Ends work still going for the last question (an agent run), on esc or a new question.
    cancel_work: Callable[[], None] = lambda: None
    # Settings → Privacy → Pause before sending my screen.
    pause_before_sending: Callable[[], bool] = lambda: False
    # Where the mouse is on the desktop (X11), None when it cannot be told (Wayland).
    mouse_position: Callable[[], "tuple[int, int] | None"] = lambda: None
    # The square around the pointer cut from its screenshot, as an image (None when it cannot be cut).
    cut_close_up: Callable[[Screenshot, ask_lane.PointerInCapture], "bytes | None"] = lambda _screen, _pointer: None
    # Password and permission prompts on screen now (X11; Wayland never says).
    prompt_windows: Callable[[], "list[ask_lane.OnScreenWindow]"] = lambda: []
    # The text the person is working on (`writing.TextContext`), read as a question is asked, and
    # what writes a rewrite over it ((target, new text) → the sentence said; run from the worker).
    read_text_context: Callable[[], Any] = lambda: None
    write_in_place: Callable[[Any, str], str] = lambda _target, _text: ""
    # The app in front (`skills.FrontApp`, or None), and the skills block for it (run off the main thread).
    front_app: Callable[[], Any] = lambda: None
    talk_skills: Callable[[Any], str] = lambda _front: ""


def caption_hold_seconds(text: str) -> float:
    """Long enough to read: four seconds plus a little per character, at most fifteen."""
    return min(15.0, 4.0 + len(text) * 0.05)


def shown_text(answer: str) -> str:
    """What the caption shows of an answer so far: no markers, no tag (a tag still arriving is held back)."""
    text = ask_lane.removing_unsure_markers(guided.removing_markers(answer))
    if text.lstrip().startswith("[") and "]" not in text:
        return ""
    return parse_pointing(text).text


@dataclass
class _Streaming:
    """One question's answer as it streams in."""
    text: str = ""
    pointed: bool = False  # the leading tag was handled (flown to, or found to point nowhere)
    caption_started: bool = False
    chunker: SpokenChunker | None = None
    speaking: bool = False


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
        # Where the pointer last pointed (desktop pixels), and where the click that finished a step
        # landed: the wrong-click check compares them.
        self._pointed_at: tuple[int, int] | None = None
        self._step_click: tuple[int, int] | None = None
        # The last question the person asked, and when: the same one again means the answer missed.
        self._last_question: tuple[str, float] | None = None
        self._streaming: dict[int, _Streaming] = {}
        # The app in front as the ask box opened (the box itself is in front by the time it sends).
        self._front_app: Any = None
        # Every question asked, in order (the tests and the log read it).
        self.questions_asked: list[str] = []
        # What the island's busy strip says that the states alone do not: the pause before the
        # screen is sent, and what an agent is doing.
        self.send_pause_showing = False
        self.agent_activity: str | None = None
        # The app the question (and the guide it starts) is about.
        self._guide_front_app: Any = None
        # The text the person is working on, read as the ask box opened, and for the question asked.
        self._text_context: Any = None
        self._question_text_context: Any = None

    def open_composer(self) -> None:
        # Read before the box takes the keyboard: the text box in front is the one talked about.
        self._text_context = self.host.read_text_context()
        self._front_app = self.host.front_app()
        self.host.show_composer()

    def submit(self, question: str, asked_out_loud: bool = False) -> None:
        """A question the person typed or said: anything they ask starts afresh, ending a guide under way."""
        question = question.strip()
        self.host.hide_composer()
        if not question:
            return
        self._stop_waiting_for_step()
        self.guide_goal = question
        self.guide_steps_given = 0
        # Asked out loud, the app in front is the one the person is in; typed, the one before the box.
        self._guide_front_app = self.host.front_app() if asked_out_loud else self._front_app
        self._question_text_context = self.host.read_text_context() if asked_out_loud else self._text_context
        self._text_context = None
        self._ask(question, from_the_person=True)

    def _ask(self, question: str, from_the_person: bool = False, pixels_off_target: int | None = None) -> None:
        self.questions_asked.append(question)
        # A new question ends whatever is still being said or done, and the voice gets ready (it
        # loads in a few seconds, while the answer is being thought about).
        self.host.stop_speaking()
        if self.is_answering:
            self.host.cancel_work()
        self.host.prepare_voice()
        this_question_id = next(self._question_ids)
        self.question_id = this_question_id
        self.is_answering = True
        # What goes with the question: where the mouse is now, and whether it was just asked; a
        # guide's own "what's next?" carries none of it, nor the text the first question was about.
        if not from_the_person:
            self._question_text_context = None
        mouse = self.host.mouse_position() if from_the_person else None
        repeated = False
        if from_the_person:
            now = time.monotonic()
            previous, asked_at = self._last_question or (None, now)
            repeated = ask_lane.is_repeated_question(question, previous, now - asked_at)
            self._last_question = (question, now)
            if repeated:
                applog.append("ask: the same question again; answering about what is under the pointer")
        pause = ask_lane.send_pause_seconds(self.host.pause_before_sending(), is_guided_step_continuation=not from_the_person)
        if pixels_off_target is not None:
            # The guide's own check of a wrong click: said while the screen is looked at again.
            self.pointer.show_caption(ask_lane.OFF_TARGET_CAPTION, hold_seconds=None)
        if pause:
            # A moment to call it off: esc now and nothing is captured or sent.
            self.pointer.show_caption(ask_lane.SEND_PAUSE_CAPTION, hold_seconds=None)
            self.send_pause_showing = True
            self.host.schedule(pause, lambda: self._hide_then_capture(question, this_question_id, mouse, repeated))
        else:
            self._hide_then_capture(question, this_question_id, mouse, repeated)

    def _hide_then_capture(self, question: str, this_question_id: int, mouse, repeated: bool) -> None:
        self.send_pause_showing = False
        if this_question_id != self.question_id:
            return
        self.pointer.hide_for_screenshot()
        self.host.schedule(COMPOSER_HIDE_SETTLE_SECONDS, lambda: self._capture_and_ask(question, this_question_id, mouse, repeated))

    def _capture_and_ask(self, question: str, this_question_id: int, mouse=None, repeated: bool = False) -> None:
        if this_question_id != self.question_id:
            return
        try:
            screenshot = self.host.capture_screen()
        except Exception as failure:  # noqa: BLE001 - shown to the person, like any other failure
            self.pointer.show_after_screenshot()
            self._show_failure(f"couldn't see the screen: {failure}", this_question_id)
            return
        if callable(screenshot):
            # Wayland: the desktop takes the picture, and that waits on it, so off the main thread.
            self.host.run_in_background(screenshot, lambda outcome: self._captured(question, outcome, this_question_id, mouse, repeated))
            return
        self._captured(question, screenshot, this_question_id, mouse, repeated)

    def _captured(self, question: str, screenshot: Any, this_question_id: int, mouse=None, repeated: bool = False) -> None:
        if this_question_id != self.question_id:
            return
        if isinstance(screenshot, BaseException):
            self.pointer.show_after_screenshot()
            self._show_failure(f"couldn't see the screen: {screenshot}", this_question_id)
            return
        self.pointer.show_after_screenshot()
        self.pointer.show_caption(THINKING_CAPTION, hold_seconds=None)
        conversation = self.conversation
        screens = as_screen_list(screenshot)
        pointer_in_capture = ask_lane.locate_pointer(mouse[0], mouse[1], screens) if mouse else None
        self._streaming[this_question_id] = _Streaming(chunker=SpokenChunker())

        def on_text(piece: str) -> None:
            self.host.on_main_loop(lambda: self._stream_piece(this_question_id, piece, screenshot))

        def work():
            extras = self._extras(screens, pointer_in_capture, repeated)
            return screenshot, self.host.ask(question, screenshot, conversation, extras=extras, on_text=on_text)

        self.host.run_in_background(work, lambda outcome: self._show_answer(question, outcome, this_question_id))

    def _extras(self, screens: list, pointer_in_capture, repeated: bool) -> AskExtras:
        """Where the mouse is and its close-up, "asked again", and the skills for the app in front
        (run off the main thread: the close-up is cut from the picture, the skills read from files)."""
        lines: list[str] = []
        images: list[tuple[bytes, str]] = []
        if pointer_in_capture is not None:
            lines.append(pointer_in_capture.prompt_line)
            close_up = self.host.cut_close_up(screens[pointer_in_capture.screen_number - 1], pointer_in_capture)
            if close_up:
                images.append((close_up, ask_lane.close_up_label()))
        if repeated:
            lines.append(ask_lane.REPEATED_QUESTION_LINE)
        instruction, tools, write = "", (), None
        context = self._question_text_context
        if context is not None:
            if context.target is not None:
                lines.insert(0, context.target.context_line)
                target = context.target
                instruction, tools = writing.PROMPT_INSTRUCTION, (writing.tool_definition(),)
                write = lambda new_text: self.host.write_in_place(target, new_text)  # noqa: E731
            else:
                if context.selected_text:
                    lines.insert(0, selection_context_line(context.selected_text))
                instruction = writing.NO_TARGET_PROMPT_INSTRUCTION if context.readable else writing.NO_ACCESSIBILITY_PROMPT_INSTRUCTION
        try:
            skills_block = self.host.talk_skills(self._guide_front_app)
        except OSError as failure:
            applog.append(f"skills: not read ({failure})")
            skills_block = ""
        return AskExtras(prompt_lines=tuple(lines), images=tuple(images), skills_block=skills_block, instruction=instruction, tools=tools,
                         write_in_place=write)

    # MARK: the answer, as it streams and once it is whole

    def _stream_piece(self, this_question_id: int, piece: str, screenshot: Any) -> None:
        streaming = self._streaming.get(this_question_id)
        if this_question_id != self.question_id or streaming is None:
            return
        streaming.text += piece
        if not streaming.pointed:
            self._point_if_ready(streaming, screenshot)
        text = shown_text(streaming.text)
        # The caption shows the answer a sentence at a time; until the first one, it keeps thinking.
        last_end = max((index for index, character in enumerate(text) if character in ".!?"), default=-1)
        if last_end >= 0:
            self.pointer.show_caption(text[:last_end + 1], hold_seconds=None, continue_typing=streaming.caption_started)
            streaming.caption_started = True
        for spoken in streaming.chunker.receive(streaming.text):
            if not streaming.speaking:
                self.host.speak_begin()
                streaming.speaking = True
            self.host.speak_piece(spoken)

    def _point_if_ready(self, streaming: _Streaming, screenshot: Any) -> None:
        """Flies as soon as the answer's opening says where (or that it points nowhere)."""
        text = streaming.text.lstrip()
        if not text:
            return
        if ask_lane.contains_unsure_marker(text[:20]):
            streaming.pointed = True
            self.pointer.return_to_mouse()
            return
        if text.startswith("[") and "]" not in text:
            return  # the tag is still arriving
        streaming.pointed = True
        result = parse_pointing(guided.removing_markers(text))
        self._point(result, screenshot)

    def _point(self, result, screenshot: Any) -> None:
        if not result.has_point:
            # Still pointing at the last step's target would point at the wrong thing: back to the mouse.
            self._pointed_at = None
            self.pointer.return_to_mouse()
            return
        screen_x, screen_y = point_on_screens(result.x, result.y, result.screen_number, as_screen_list(screenshot))
        # Granting a permission is the person's choice, never a step: no pointing at those prompts.
        if ask_lane.is_on_system_prompt((screen_x, screen_y), self.host.prompt_windows()):
            applog.append("point: not shown, it was on a password or permission prompt")
            self._pointed_at = None
            self.pointer.return_to_mouse()
            return
        self._pointed_at = (screen_x, screen_y)
        self.pointer.fly_to(screen_x, screen_y)

    def _show_answer(self, question: str, outcome: Any, this_question_id: int) -> None:
        if this_question_id != self.question_id:
            return
        streaming = self._streaming.pop(this_question_id, None) or _Streaming(chunker=SpokenChunker())
        if isinstance(outcome, BaseException):
            if streaming.speaking:
                self.host.speak_end()
            self._show_failure(str(outcome) or "couldn't get an answer", this_question_id)
            return
        screenshot, answer = outcome
        self.is_answering = False
        self.agent_activity = None
        is_unsure = ask_lane.contains_unsure_marker(answer)
        # An [UNSURE] answer never points (a tag sent with it is ignored) and is never a guide's step.
        is_a_guided_step = guided.contains_marker(answer) and not is_unsure
        result = parse_pointing(ask_lane.removing_unsure_markers(guided.removing_markers(answer)))
        if is_unsure:
            applog.append(f"ask: not sure what to point at{'' if not result.has_point else ' (its point tag ignored)'}")
        # Kept without the tag and the markers, as on the Mac: they would only confuse later answers.
        # Never empty: an answer that was only a tag would be an empty assistant turn, which
        # Anthropic refuses, and every question after it would fail.
        self.conversation.remember(question, result.text or f"(pointed at {result.label or 'nothing'})")
        if is_unsure:
            self._pointed_at = None
            self.pointer.return_to_mouse()
        elif not streaming.pointed:
            # Not streamed (a plan's whole answer, an agent's result), or a tag at the end.
            self._point(result, screenshot)
        text = result.text or "…"
        self.is_showing_answer = True
        self.pointer.show_caption(text, hold_seconds=caption_hold_seconds(text), continue_typing=streaming.caption_started)
        rest = streaming.chunker.finish(answer)
        if rest and not streaming.speaking:
            self.host.speak_begin()
            streaming.speaking = True
        for spoken in rest:
            self.host.speak_piece(spoken)
        if streaming.speaking:
            self.host.speak_end()
        if is_a_guided_step:
            self._wait_for_step()
        elif is_unsure:
            self._stop_waiting_for_step()
            self.guide_steps_given = 0

    @property
    def island_status(self) -> str | None:
        """The island's words for the ask lane when its state alone says too little (the Mac's
        `askIslandStatus` and a guide's step)."""
        if self.send_pause_showing:
            return ask_lane.SEND_PAUSE_CAPTION
        if self.guide_steps_given and (self.is_waiting_for_step or self.is_answering):
            return f"step {self.guide_steps_given + 1} · looking" if self.is_answering else f"step {self.guide_steps_given} · your turn"
        return None

    def _show_failure(self, message: str, this_question_id: int) -> None:
        if this_question_id != self.question_id:
            return
        self.is_answering = False
        self.agent_activity = None
        self.is_showing_answer = True
        self.pointer.show_caption(message, hold_seconds=6.0)
        self.host.say_notice(message)

    def escape(self) -> bool:
        """Esc: calls off a question about to be sent, drops one still being answered, dismisses the
        answer and ends a guide. True if it did anything."""
        if not self.is_answering and not self.is_showing_answer and not self.is_waiting_for_step:
            return False
        self.pointer.show_after_screenshot()
        self.host.stop_speaking()
        if self.is_answering:
            self.host.cancel_work()
        self._stop_waiting_for_step()
        self.guide_steps_given = 0
        self._streaming.pop(self.question_id, None)
        self.send_pause_showing = False
        self.agent_activity = None
        self.question_id = next(self._question_ids)
        self.is_answering = False
        self.is_showing_answer = False
        self.pointer.dismiss_caption()
        self.pointer.return_to_mouse()
        return True

    def show_progress(self, text: str, for_question_id: int) -> None:
        """What the agent is doing for a question still being answered, beside the pointer."""
        if for_question_id == self.question_id and self.is_answering and text.strip():
            self.agent_activity = text.strip()[:120]
            self.pointer.show_caption(text.strip()[:120], hold_seconds=None)

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

    def step_input(self, step_input: str, position: "tuple[int, int] | None" = None) -> None:
        """A click or a key (guided.CLICK, STEP_KEY, ESCAPE, OTHER_KEY) anywhere, while a step may
        wait; a click's `position` on the desktop when it is known (X11)."""
        if not self.is_waiting_for_step:
            return
        reaction = guided.reaction(step_input, ask_is_pending=self._pending_ask_id is not None)
        if reaction.kind == "end guide":
            self.escape()
        elif reaction.kind in ("ask for next step", "postpone"):
            if step_input == guided.CLICK:
                self._step_click = position
            elif step_input == guided.STEP_KEY:
                self._step_click = None
            # Another click or key restarts the wait, so a double click or a quick second click (a
            # menu, then its item) is one step.
            this_ask_id = next(self._step_wait_ids)
            self._pending_ask_id = this_ask_id
            self.host.schedule(reaction.settle_seconds, lambda: self._step_done(this_ask_id))

    def _step_done(self, ask_id: int) -> None:
        if ask_id != self._pending_ask_id or not self.is_waiting_for_step:
            return
        self._stop_waiting_for_step()
        pixels_off = ask_lane.click_verdict(self._pointed_at, self._step_click)
        self._step_click = None
        if pixels_off is not None:
            applog.append(f"guide: the click was about {pixels_off} px from where the pointer pointed")
        self._ask(guided.next_step_request(self.guide_goal, pixels_off), pixels_off_target=pixels_off)
