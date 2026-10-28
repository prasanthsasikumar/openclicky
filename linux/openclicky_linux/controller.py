"""One take at a time, the Mac app's `DictationTakeController` in small: idle → listening →
finishing. Press opens the microphone and remembers the window in front; release stops it, the
audio is transcribed off the main thread, formatted, and pasted into that window — only that
window: if another one is in front by then, the words go to the clipboard instead. Every
continuation carries the take's id, so a cancelled take never pastes into the next.

As on the Mac, too: a take held with shift is a Hey Clicky edit (its words are an instruction for
the text selected at the press, or the last take, which a model rewrites and the paste replaces);
short tones mark a take's start, stop and end (`earcons.py`); a tapped take ends on its own after
minutes of quiet; a take an engine could not finish keeps its audio, so it can be heard again from
the history; the last take can be pasted again. A take on a streaming engine that no model will
clean up is typed as it is spoken (`live_typing.py`): settled words go in while no modifier is
held, and on release the rest is typed when it lines up with what was typed, else copied.

The desktop is reached only through `DictationHost`, so the tests run takes with fakes.
"""

from __future__ import annotations

import itertools
import time
from dataclasses import dataclass
from enum import Enum
from typing import Any, Callable, Protocol

from . import applog, formatter, live_typing
from .gestures import AskJoined, DictationDoubleTapped, DictationPressed, DictationReleased, EditJoined, EscapePressed, ShortcutEvent
from .recorder import pcm_seconds, wav_from_pcm
from .space import DictationSpace

ASKING_HINT = "ask your question, then let go"
EDITING_HINT = "say your edit, then let go"
NOTHING_TO_EDIT = "select some text first, or dictate something to edit"
# A tapped take with this long a quiet stretch ends on its own (the Mac's "inactivity timeout",
# checked every five seconds); louder than `LOUD_LEVEL` is someone talking.
QUIET_CHECK_SECONDS = 5.0
# How often a take that types live looks for a pause (the last word is then typed too).
PAUSE_CHECK_SECONDS = 0.35
LOUD_LEVEL = 0.08
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


class TakeNotFinished(Exception):
    """The engine could not finish a take (the network, the key, the credits): its audio is kept."""

    def __init__(self, failure: BaseException, wav: bytes, seconds: float) -> None:
        super().__init__(str(failure))
        self.wav = wav
        self.seconds = seconds


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
    # A Hey Clicky edit's paste, which must replace the selection (None: `paste`).
    paste_over_selection: Callable[[str, "FocusedWindow | None"], bool] | None = None
    # (heard words, window[, the text before the cursor]) → the text to paste (the app's style,
    # numbers, the model clean-up), run off the main thread; None: the local clean-up on the main loop.
    format_take: Callable[..., str] | None = None
    # Every take that ended with words, for the history: raw, text, window, seconds, outcome, mode.
    record_take: Callable[[dict], None] = lambda _take: None
    # A take the engine could not finish: reason, wav, window, seconds (kept for a retry).
    record_failed_take: Callable[[dict], None] = lambda _take: None
    # Hey Clicky: reads the text selected now (at the press) and hands it to `done` (None when
    # nothing is), without running the main loop inside the key's handling.
    read_selection: Callable[[Callable[["str | None"], None]], None] = lambda done: done(None)
    # (selection, instruction, window) → the edited text, run off the main thread; raises with a sentence.
    edit_text: Callable[[str, str, "FocusedWindow | None"], str] | None = None
    # Why an edit cannot be made now (the clean-up is off, no model), else None.
    edit_refusal: Callable[[], "str | None"] = lambda: None
    # "start", "stop", "complete", "notify", "blocked", "error" (`earcons.py`); quiet when sounds are off.
    play_earcon: Callable[[str], None] = lambda _name: None
    # Minutes of quiet that end a tapped take (0: never).
    quiet_minutes: Callable[[], float] = lambda: 0
    # Live typing: (style, space) when a take into `window` may be typed as it is spoken (no model
    # will clean it up, not a terminal or OpenClicky's own window), else None; whether a keystroke
    # may go now (no modifier held: it would merge); typing a phrase (False: it could not).
    live_typing_wanted: Callable[["FocusedWindow | None"], Any] = lambda _window: None
    can_type_now: Callable[[], bool] = lambda: False
    type_text: Callable[[str], bool] = lambda _text: False
    # Runs `action` on the main loop (a streaming engine's words arrive on its own thread).
    on_main_loop: Callable[[Callable[[], None]], None] = lambda action: action()
    # The focused text box (`accessibility.FocusedField`) when the desktop's accessibility is on,
    # else None: a take outside any text box, or in a password box, is told so.
    focused_field: Callable[[], Any] = lambda: None


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
        # This take's words are a Hey Clicky instruction for `_text_to_edit` (shift was held).
        self.take_edits = False
        self._text_to_edit: str | None = None
        # The last take's words, for "paste last take" and an edit with nothing selected.
        self.last_take_text: str | None = None
        self.last_take_at = 0.0
        self._last_loud_at = 0.0
        # This take's live typing, when it may type as it is spoken.
        self._live: live_typing.LiveTypingSession | None = None
        # Whether the Hey Clicky edit under way is of the selection (else the last take).
        self._editing_selection = False
        # The text box at the press, when accessibility says (None: it cannot be told).
        self._field_at_press: Any = None
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
                self._begin_take(asks_the_pointer=event.ask, edits=event.edit)
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
                self._last_loud_at = time.monotonic()
                self.orb.show("listening", "say it, then tap the key again")
                self.host.schedule(QUIET_CHECK_SECONDS, lambda this_take_id=self.take_id: self._watch_for_quiet(this_take_id))
            else:
                self._stop_take()
        elif isinstance(event, AskJoined):
            # Left alt joined a take that listens: its words become a question for the pointer.
            if self.state is TakeState.LISTENING and not self.take_asks_the_pointer:
                if self._live is not None and self._live.has_typed:
                    self._stop_live_typing("too late for a question")
                    self.orb.show("listening", "already typing, stopped here. let go, then ask")
                    return
                self._live = None
                self.take_asks_the_pointer = True
                self.take_edits = False
                self.orb.show("listening", ASKING_HINT)
        elif isinstance(event, EditJoined):
            # Shift joined a take that listens: its words become an edit of the selection.
            if self.state is TakeState.LISTENING and not self.take_asks_the_pointer and not self.take_edits:
                if self._live is not None and self._live.has_typed:
                    # Words already went in: what is said from now on is an instruction, never typed.
                    self._stop_live_typing("too late for an edit")
                    self.orb.show("listening", "already typing, stopped here. let go, then start an edit")
                    return
                self._live = None
                self.take_edits = True
                self._read_text_to_edit()
                refusal = self.host.edit_refusal()
                self.orb.show("listening", refusal or EDITING_HINT)
        elif isinstance(event, DictationDoubleTapped):
            if self.is_take_in_progress:
                self.cancel_take("cancelled")
        elif isinstance(event, EscapePressed):
            if self.is_take_in_progress:
                self.cancel_take("cancelled")

    def _begin_take(self, asks_the_pointer: bool = False, edits: bool = False) -> None:
        if edits and (refusal := self.host.edit_refusal()):
            # An edit is the selection sent to a model: refused before anything is recorded.
            self.host.play_earcon("blocked")
            self._show_then_rest("failed", refusal, after_seconds=2.5)
            return
        self.take_asks_the_pointer = asks_the_pointer
        self.take_edits = edits
        self.take_id = next(self._take_ids)
        self._take_is_toggle = False
        self._window_at_press = self.host.focused_window()
        self._field_at_press = self._read_focused_field()
        # Read at the press: by the release the selection may have changed.
        self._text_to_edit = None
        if edits:
            self._read_text_to_edit()
        try:
            self._recording = self.host.start_recording()
        except Exception as failure:  # noqa: BLE001 - any failure to open the microphone ends the take the same way
            self.state = TakeState.LISTENING
            self._fail(str(failure) or "couldn't open the microphone", self.take_id)
            return
        self.state = TakeState.LISTENING
        self._live = None
        field = self._field_at_press
        types_into_a_box = field is None or (field.is_editable and not field.is_password)
        if not asks_the_pointer and not edits and types_into_a_box and hasattr(self._recording, "on_live_words"):
            wanted = self.host.live_typing_wanted(self._window_at_press)
            if wanted is not None:
                self._live = live_typing.session_for(*wanted)
                this_take_id = self.take_id
                self._recording.on_live_words = lambda words: self.host.on_main_loop(lambda: self._live_words(this_take_id, words))
                self.host.schedule(PAUSE_CHECK_SECONDS, lambda: self._check_for_a_pause(this_take_id))
        self.host.prepare_transcription()
        self.host.play_earcon("start")
        applog.append(f"take {self.take_id} started in {self._window_at_press.app_class if self._window_at_press else 'an unknown window'}"
                      f"{' (an edit)' if edits else ''}")
        self.orb.show("listening", ASKING_HINT if asks_the_pointer else EDITING_HINT if edits
                      else self._no_box_hint() or "release to transcribe")

    def _read_focused_field(self) -> Any:
        try:
            return self.host.focused_field()
        except Exception as failure:  # noqa: BLE001 - an unreadable box is an unknown one
            applog.append(f"take {self.take_id}: the focused box could not be read ({type(failure).__name__})")
            return None

    def _no_box_hint(self) -> str | None:
        field = self._field_at_press
        if field is None:
            return None
        if field.is_password:
            return "a password box: your words will be copied, not typed"
        if not field.is_editable:
            return "no text box here. click into one before you let go"
        return None

    def _read_text_to_edit(self) -> None:
        """The selection, or else the last take, becomes what this take's instruction edits."""
        this_take_id = self.take_id
        self._text_to_edit = self.last_take_text
        self._editing_selection = False

        def read(selected: str | None) -> None:
            if this_take_id == self.take_id and selected and selected.strip():
                self._text_to_edit = selected
                self._editing_selection = True

        try:
            self.host.read_selection(read)
        except Exception as failure:  # noqa: BLE001 - an unreadable selection falls back to the last take
            applog.append(f"take {this_take_id}: the selection could not be read ({type(failure).__name__})")

    # MARK: live typing

    def _live_words(self, this_take_id: int, words: str) -> None:
        live = self._live
        if this_take_id != self.take_id or self.state is not TakeState.LISTENING or live is None or live.stopped_reason:
            return
        now = time.monotonic()
        live.latest_partial, live.last_partial_at = words, now
        live.waiting_words += live.committer.observe(words, now)
        self._type_waiting_words()

    def _check_for_a_pause(self, this_take_id: int) -> None:
        """With no new words for a moment, the last word is typed too (the person paused)."""
        live = self._live
        if this_take_id != self.take_id or self.state is not TakeState.LISTENING or live is None:
            return
        now = time.monotonic()
        if not live.stopped_reason and live.latest_partial and now - live.last_partial_at >= live_typing.PAUSE_SECONDS:
            live.waiting_words += live.committer.observe(live.latest_partial, now, person_paused=True)
        self._type_waiting_words()
        self.host.schedule(PAUSE_CHECK_SECONDS, lambda: self._check_for_a_pause(this_take_id))

    def _type_waiting_words(self) -> None:
        live = self._live
        if live is None or live.stopped_reason or not live.waiting_words or not self.host.can_type_now():
            return
        window_now = self.host.focused_window()
        if self._window_at_press is None or window_now is None or window_now.window_id != self._window_at_press.window_id:
            self._stop_live_typing("the window changed")
            return
        text = live_typing.text_to_type(live.waiting_words, live.style, live.space, live.typed_text)
        if live_typing.contains_control_characters(text):
            self._stop_live_typing("a line break")
            return
        if text and not self.host.type_text(text):
            self._stop_live_typing("the keystrokes could not be sent")
            return
        live.typed_text += text
        live.typed_raw_words += live.waiting_words
        live.waiting_words = []

    def _stop_live_typing(self, reason: str) -> None:
        if self._live is not None and not self._live.stopped_reason:
            self._live.stopped_reason = reason
            applog.append(f"take {self.take_id}: live typing stopped ({reason})")

    def _finish_live_typed_take(self, raw: str, seconds: float, this_take_id: int) -> None:
        """A take that typed as it was spoken: the rest is typed when it lines up with what was
        typed (never retyped, deleted or skipped), else left on the clipboard."""
        live = self._live
        self._type_waiting_words()
        final_words = live_typing.words_of(raw) or live_typing.words_of(live.latest_partial)
        alignment = live_typing.align(final_words, live.typed_raw_words)
        rest = live_typing.text_to_type(alignment.remaining(final_words), live.style, live.space, live.typed_text)
        message = "moved to text box"
        if rest:
            window_now = self.host.focused_window()
            can_type = (alignment.aligned_by_words and not live.stopped_reason and self.host.can_type_now()
                        and not live_typing.contains_control_characters(rest) and window_now is not None
                        and self._window_at_press is not None and window_now.window_id == self._window_at_press.window_id)
            if can_type and self.host.type_text(rest):
                live.typed_text += rest
            else:
                self.host.copy_to_clipboard(rest.strip())
                message = "typed most of it. the rest is copied, press ctrl+v"
        text = live.typed_text.strip()
        self.finished_takes.append((text, message))
        self.last_take_text = text
        self.last_take_at = time.time()
        applog.append(f"take {this_take_id} done: {len(text)} chars typed live, {message}")
        self.host.play_earcon("complete" if message == "moved to text box" else "notify")
        try:
            self.host.record_take({"raw": raw, "text": text, "window": self._window_at_press, "seconds": seconds, "outcome": message,
                                   "mode": "dictate"})
        except Exception as failure:  # noqa: BLE001 - the history must never cost a take its words
            applog.append(f"take {this_take_id}: not kept in the history ({type(failure).__name__})")
        self._end_take()
        self._show_then_rest("done", message, after_seconds=1.8)

    def _watch_for_quiet(self, this_take_id: int) -> None:
        """A tapped take with minutes of quiet ends on its own, as if tapped again."""
        if self.take_id != this_take_id or self.state is not TakeState.LISTENING or not self._take_is_toggle:
            return
        recording = self._recording
        level = getattr(recording, "current_level", lambda: 1.0)()
        now = time.monotonic()
        if level > LOUD_LEVEL:
            self._last_loud_at = now
        minutes = self.host.quiet_minutes()
        if minutes > 0 and now - self._last_loud_at >= minutes * 60:
            applog.append(f"take {this_take_id} ended after {minutes:g} minute(s) of quiet")
            self._stop_take()
            return
        self.host.schedule(QUIET_CHECK_SECONDS, lambda: self._watch_for_quiet(this_take_id))

    def _stop_take(self) -> None:
        if self.state is not TakeState.LISTENING or self._recording is None:
            return
        this_take_id = self.take_id
        recording = self._recording
        self._recording = None
        self.state = TakeState.FINISHING
        self.host.play_earcon("stop")
        self.orb.show("working", "finishing your edit" if self.take_edits else "moving your words")
        transcribe = self.host.transcribe
        # A spoken question is never rewritten by a model: it is formatted locally, on the main loop.
        format_take = None if self.take_asks_the_pointer else self.host.format_take
        window_at_press = self._window_at_press
        nearby_text = self._field_at_press.text_before_caret if self._field_at_press is not None else ""
        edit_text = self.host.edit_text if self.take_edits else None
        text_to_edit = self._text_to_edit
        if edit_text is not None:
            applog.append(f"take {this_take_id}: editing {'the selection' if self._editing_selection else 'the last take'}"
                          f" ({len(text_to_edit or '')} chars)")

        def hear_the_take() -> tuple[str, str | None, float]:
            pcm = recording.stop()
            seconds = pcm_seconds(pcm)
            if seconds < MINIMUM_SPEECH_SECONDS:
                return "", None, seconds
            wav = wav_from_pcm(pcm)
            # A streamed take (`streaming.py`) heard its words already; any other goes to the engine now.
            transcript_of = getattr(recording, "transcript", None)
            try:
                raw = transcript_of(wav) if transcript_of else transcribe(wav)
            except Exception as failure:  # noqa: BLE001 - every engine failure keeps the audio the same way
                raise TakeNotFinished(failure, wav, seconds) from failure
            if edit_text is not None:
                if not raw.strip():
                    return raw, None, seconds
                if not text_to_edit:
                    raise ValueError(NOTHING_TO_EDIT)
                return raw, edit_text(text_to_edit, raw.strip(), window_at_press), seconds
            formatted = None
            if format_take and raw.strip():
                formatted = format_take(raw, window_at_press, nearby_text) if nearby_text else format_take(raw, window_at_press)
            return raw, formatted, seconds

        self.host.run_in_background(hear_the_take, lambda outcome: self._finish_take(outcome, this_take_id))

    def cancel_take(self, reason: str) -> None:
        if not self.is_take_in_progress:
            return
        if self._recording is not None:
            self._recording.cancel()
            self._recording = None
        applog.append(f"take {self.take_id} {reason}")
        self._end_take()
        self._show_then_rest("failed", reason, after_seconds=1.4)

    def _finish_take(self, outcome: Any, this_take_id: int) -> None:
        # Cancelled, or another take began, while the engine was at work: these words are dropped.
        if self.take_id != this_take_id or self.state is not TakeState.FINISHING:
            return
        if isinstance(outcome, TakeNotFinished):
            try:
                self.host.record_failed_take({"reason": str(outcome), "wav": outcome.wav, "window": self._window_at_press,
                                              "seconds": outcome.seconds, "mode": "edit" if self.take_edits else "dictate"})
            except Exception as failure:  # noqa: BLE001 - keeping the audio must never hide why the take failed
                applog.append(f"take {this_take_id}: its audio was not kept ({type(failure).__name__})")
        if isinstance(outcome, BaseException):
            self._fail(str(outcome) or "couldn't transcribe that", this_take_id)
            return
        raw, formatted, seconds = outcome if isinstance(outcome, tuple) else (outcome or "", None, 0.0)
        if self._live is not None and self._live.has_typed and not self.take_edits and not self.take_asks_the_pointer:
            self._finish_live_typed_take(raw or "", seconds, this_take_id)
            return
        if self.take_edits and raw.strip() and formatted is None:
            self._fail("couldn't finish your edit", this_take_id)
            return
        text = formatted if formatted is not None else formatter.format_locally(raw or "", self.host.load_space())
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
        self.last_take_text = text
        self.last_take_at = time.time()
        applog.append(f"take {this_take_id} done: {len(text)} chars, {message}")
        self.host.play_earcon("complete" if message == "moved to text box" else "notify")
        try:
            self.host.record_take({"raw": raw, "text": text, "window": self._window_at_press, "seconds": seconds, "outcome": message,
                                   "mode": "edit" if self.take_edits else "dictate"})
        except Exception as failure:  # noqa: BLE001 - the history must never cost a take its words
            applog.append(f"take {this_take_id}: not kept in the history ({type(failure).__name__})")
        self._end_take()
        self._show_then_rest("done", message, after_seconds=1.8)

    def _deliver(self, text: str) -> str:
        """Pasted into the window that was in front at press, and only there: when either window
        cannot be told (no window manager answer, an X error), the words go to the clipboard."""
        window_now = self.host.focused_window()
        window_at_press = self._window_at_press
        field_at_press = self._field_at_press
        if field_at_press is not None and field_at_press.is_password:
            # Never typed into a password box: the person pastes it there if they meant it.
            self.host.copy_to_clipboard(text)
            return "a password box. copied, press ctrl+v"
        if field_at_press is not None and not field_at_press.is_editable:
            # Begun outside any text box: the box clicked into since takes it, in whatever window.
            field_now = self._read_focused_field()
            if field_now is not None and field_now.is_editable and not field_now.is_password and self.host.paste(text, window_now):
                return "moved to text box"
            self.host.copy_to_clipboard(text)
            return "no text box. copied, press ctrl+v"
        if window_at_press is None or window_now is None:
            self.host.copy_to_clipboard(text)
            return "couldn't tell which window. copied, press ctrl+v"
        if window_now.window_id != window_at_press.window_id:
            self.host.copy_to_clipboard(text)
            return "the window changed. copied, press ctrl+v"
        paste = self.host.paste_over_selection if self.take_edits and self.host.paste_over_selection else self.host.paste
        if paste(text, window_now):
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
        self.host.play_earcon("error")
        self._end_take()
        self._show_then_rest("failed", message, after_seconds=2.5)

    def _end_take(self) -> None:
        self.state = TakeState.IDLE
        self._take_is_toggle = False
        self._text_to_edit = None
        self._live = None
        self._field_at_press = None

    def paste_last_take(self) -> None:
        """The last take's words again, into the window in front now (Settings, the tray)."""
        if self.is_take_in_progress:
            return
        if not self.last_take_text:
            self._show_then_rest("failed", "nothing dictated yet", after_seconds=2.0)
            return
        window_now = self.host.focused_window()
        if self.host.paste(self.last_take_text, window_now):
            self._show_then_rest("done", "moved to text box", after_seconds=1.8)
        else:
            self.host.copy_to_clipboard(self.last_take_text)
            self._show_then_rest("done", "copied, press ctrl+v", after_seconds=1.8)

    def _show_then_rest(self, phase: str, message: str, after_seconds: float) -> None:
        self.orb.show(phase, message)
        shown_for_take_id = self.take_id

        def rest() -> None:
            if self.take_id == shown_for_take_id and self.state is TakeState.IDLE:
                self.orb.show("idle")

        self.host.schedule(after_seconds, rest)

    def show_notice(self, message: str) -> None:
        """A sentence about something other than a take (the speakers are muted), when no take is under way."""
        applog.append(f"notice: {message}")
        if self.state is TakeState.IDLE:
            self._show_then_rest("failed", message, after_seconds=4.0)

    def report_late_paste_problem(self, message: str) -> None:
        """The keystroke for a take that already ended could not be sent where it belonged (the paste
        finishes after the take, see `ClipboardPaster`): the words are on the clipboard; say so."""
        applog.append(f"take {self.take_id}: {message}")
        if self.state is TakeState.IDLE:
            self._show_then_rest("failed", message, after_seconds=4.0)
