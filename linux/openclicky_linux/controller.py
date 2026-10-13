"""One take at a time, the Mac app's `DictationTakeController` in small: idle → listening →
finishing. Press opens the microphone and remembers the window in front; release stops it, the
audio is transcribed off the main thread, formatted, and pasted into that window — only that
window: if another one is in front by then, the words go to the clipboard instead. Every
continuation carries the take's id, so a cancelled take never pastes into the next.

The desktop is reached only through `DictationHost`, so the tests run takes with fakes.
"""

from __future__ import annotations

import itertools
from dataclasses import dataclass
from enum import Enum
from typing import Any, Callable, Protocol

from . import applog, formatter
from .gestures import AskJoined, DictationDoubleTapped, DictationPressed, DictationReleased, EscapePressed, ShortcutEvent
from .recorder import pcm_seconds, wav_from_pcm
from .space import DictationSpace

ASKING_HINT = "ask your question, then let go"
# Shorter than this the take was a slip of the finger, not speech.
MINIMUM_SPEECH_SECONDS = 0.3


class TakeState(Enum):
    IDLE = "idle"
    LISTENING = "listening"
    FINISHING = "finishing"


@dataclass(frozen=True)
class FocusedWindow:
    window_id: int
    app_class: str | None = None
    is_terminal: bool = False


class Recording(Protocol):
    def stop(self) -> bytes: ...
    def cancel(self) -> None: ...


@dataclass
class DictationHost:
    start_recording: Callable[[], Recording]
    transcribe: Callable[[bytes], str]
    focused_window: Callable[[], FocusedWindow | None]
    # True when the paste keystroke was sent; False leaves the words on the clipboard.
    paste: Callable[[str, FocusedWindow | None], bool]
    copy_to_clipboard: Callable[[str], None]
    load_space: Callable[[], DictationSpace]
    # Runs `work` off the main thread, then `done(result_or_exception)` back on it.
    run_in_background: Callable[[Callable[[], Any], Callable[[Any], None]], None]
    # Calls `action` on the main thread after `seconds`.
    schedule: Callable[[float, Callable[[], None]], None]
    # A take started: the engine may get ready now (the offline one loads its model while the person speaks).
    prepare_transcription: Callable[[], None] = lambda: None
    # A take held with left alt was a spoken question: it goes to the pointer (the ask lane).
    ask_question: Callable[[str], None] = lambda _question: None


class OrbModel:
    """What the orb shows: a phase ("idle", "listening", "working", "done", "failed") and one line."""

    def __init__(self) -> None:
        self.phase = "idle"
        self.message: str | None = None
        self.listeners: list[Callable[["OrbModel"], None]] = []

    def show(self, phase: str, message: str | None = None) -> None:
        self.phase = phase
        self.message = message
        for listener in list(self.listeners):
            listener(self)


class DictationTakeController:
    def __init__(self, host: DictationHost, orb: OrbModel) -> None:
        self.host = host
        self.orb = orb
        self.state = TakeState.IDLE
        # This take's words are a question for the pointer (left alt was held), not text to type.
        self.take_asks_the_pointer = False
        self._take_ids = itertools.count(1)
        self.take_id = 0
        self._take_is_toggle = False
        self._recording: Recording | None = None
        self._window_at_press: FocusedWindow | None = None
        # Every take's outcome, newest last: the tests and the log read it.
        self.finished_takes: list[tuple[str, str]] = []

    @property
    def is_take_in_progress(self) -> bool:
        return self.state is not TakeState.IDLE

    def handle(self, event: ShortcutEvent) -> None:
        if isinstance(event, DictationPressed):
            if self.state is TakeState.IDLE:
                self._begin_take(asks_the_pointer=event.ask)
            elif self.state is TakeState.LISTENING and self._take_is_toggle:
                # A second press while a tapped take listens: this press ends it.
                self._stop_take()
        elif isinstance(event, DictationReleased):
            if self.state is not TakeState.LISTENING or self._take_is_toggle:
                return
            if event.key_was_pressed_during_hold:
                # The key was held for a shortcut (right ctrl + C), not to talk.
                self.cancel_take("cancelled, that was a shortcut")
            elif event.was_tap:
                self._take_is_toggle = True
                self.orb.show("listening", "say it, then tap the key again")
            else:
                self._stop_take()
        elif isinstance(event, AskJoined):
            # Left alt joined a take that listens: its words become a question for the pointer.
            if self.state is TakeState.LISTENING and not self.take_asks_the_pointer:
                self.take_asks_the_pointer = True
                self.orb.show("listening", ASKING_HINT)
        elif isinstance(event, DictationDoubleTapped):
            if self.is_take_in_progress:
                self.cancel_take("cancelled")
        elif isinstance(event, EscapePressed):
            if self.is_take_in_progress:
                self.cancel_take("cancelled")

    def _begin_take(self, asks_the_pointer: bool = False) -> None:
        self.take_asks_the_pointer = asks_the_pointer
        self.take_id = next(self._take_ids)
        self._take_is_toggle = False
        self._window_at_press = self.host.focused_window()
        try:
            self._recording = self.host.start_recording()
        except Exception as failure:  # noqa: BLE001 - any failure to open the microphone ends the take the same way
            self.state = TakeState.LISTENING
            self._fail(str(failure) or "couldn't open the microphone", self.take_id)
            return
        self.state = TakeState.LISTENING
        self.host.prepare_transcription()
        applog.append(f"take {self.take_id} started in {self._window_at_press.app_class if self._window_at_press else 'an unknown window'}")
        self.orb.show("listening", ASKING_HINT if asks_the_pointer else "release to transcribe")

    def _stop_take(self) -> None:
        if self.state is not TakeState.LISTENING or self._recording is None:
            return
        this_take_id = self.take_id
        recording = self._recording
        self._recording = None
        self.state = TakeState.FINISHING
        self.orb.show("working", "moving your words")
        transcribe = self.host.transcribe

        def hear_the_take() -> str:
            pcm = recording.stop()
            if pcm_seconds(pcm) < MINIMUM_SPEECH_SECONDS:
                return ""
            return transcribe(wav_from_pcm(pcm))

        self.host.run_in_background(hear_the_take, lambda outcome: self._finish_take(outcome, this_take_id))

    def cancel_take(self, reason: str) -> None:
        if not self.is_take_in_progress:
            return
        if self._recording is not None:
            self._recording.cancel()
            self._recording = None
        self._end_take()
        self._show_then_rest("failed", reason, after_seconds=1.4)

    def _finish_take(self, outcome: Any, this_take_id: int) -> None:
        # Cancelled, or another take began, while the engine was at work: these words are dropped.
        if self.take_id != this_take_id or self.state is not TakeState.FINISHING:
            return
        if isinstance(outcome, BaseException):
            self._fail(str(outcome) or "couldn't transcribe that", this_take_id)
            return
        text = formatter.format_locally(outcome or "", self.host.load_space())
        if text and self.take_asks_the_pointer:
            # A spoken question: the pointer answers it (and guides, step by step); nothing is typed.
            self.finished_takes.append((text, "asked the pointer"))
            applog.append(f"take {this_take_id} done: a {len(text)}-character question for the pointer")
            self._end_take()
            self.orb.show("idle")
            self.host.ask_question(text)
            return
        if not text:
            self._fail("didn't catch that", this_take_id)
            return
        try:
            message = self._deliver(text)
        except Exception as failure:  # noqa: BLE001 - an X error mid-paste must not leave the take stuck
            self.host.copy_to_clipboard(text)
            message = "couldn't paste. copied, press ctrl+v"
            applog.append(f"take {this_take_id}: paste failed ({type(failure).__name__}); left on the clipboard")
        # Defensive: delivering must not run the main loop (the paste reads the clipboard
        # asynchronously), but if it ever did, an esc or a new take handled meanwhile owns the state.
        if self.take_id != this_take_id or self.state is not TakeState.FINISHING:
            return
        self.finished_takes.append((text, message))
        applog.append(f"take {this_take_id} done: {len(text)} chars, {message}")
        self._end_take()
        self._show_then_rest("done", message, after_seconds=1.8)

    def _deliver(self, text: str) -> str:
        """Pasted into the window that was in front at press, and only there: when either window
        cannot be told (no window manager answer, an X error), the words go to the clipboard."""
        window_now = self.host.focused_window()
        window_at_press = self._window_at_press
        if window_at_press is None or window_now is None:
            self.host.copy_to_clipboard(text)
            return "couldn't tell which window. copied, press ctrl+v"
        if window_now.window_id != window_at_press.window_id:
            self.host.copy_to_clipboard(text)
            return "the window changed. copied, press ctrl+v"
        if self.host.paste(text, window_now):
            return "moved to text box"
        self.host.copy_to_clipboard(text)
        return "copied, press ctrl+v"

    def _fail(self, message: str, failed_take_id: int) -> None:
        if self.take_id != failed_take_id or not self.is_take_in_progress:
            return
        if self._recording is not None:
            self._recording.cancel()
            self._recording = None
        self.finished_takes.append(("", message))
        applog.append(f"take {failed_take_id} failed: {message}")
        self._end_take()
        self._show_then_rest("failed", message, after_seconds=2.5)

    def _end_take(self) -> None:
        self.state = TakeState.IDLE
        self._take_is_toggle = False

    def _show_then_rest(self, phase: str, message: str, after_seconds: float) -> None:
        self.orb.show(phase, message)
        shown_for_take_id = self.take_id

        def rest() -> None:
            if self.take_id == shown_for_take_id and self.state is TakeState.IDLE:
                self.orb.show("idle")

        self.host.schedule(after_seconds, rest)

    def report_late_paste_problem(self, message: str) -> None:
        """The keystroke for a take that already ended could not be sent where it belonged (the paste
        finishes after the take, see `ClipboardPaster`): the words are on the clipboard; say so."""
        applog.append(f"take {self.take_id}: {message}")
        if self.state is TakeState.IDLE:
            self._show_then_rest("failed", message, after_seconds=4.0)
