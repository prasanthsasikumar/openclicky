"""The dictation key's gestures, from raw key downs and ups: the Mac app's
`CompanionShortcutRecognizer`, with the same timings.

Left ctrl (the default) sits where fn does on a Mac keyboard, but unlike fn it is in every shortcut
(ctrl+C, ctrl+V), so it shares the key with the ask box and waits before it listens:
- held on its own for `HOLD_DELAY_SECONDS` → a take; released → it ends
- with left alt (the Mac's ⌃⌥) → the take is a spoken question for the pointer, not dictation;
  alt joining a take already listening turns it into one
- with shift (Hey Clicky, the Mac's fn + ⌃: ctrl is the dictation key itself here) → the take is
  an instruction for the selected text ("make it formal"); shift joining a take already listening
  turns it into one
- any other key before the delay (ctrl+C) → nothing at all; after it → "that was a shortcut"
- tapped twice → the ask box (the Mac's ⌃ ×2)
- tapped twice while left alt is held → hands-free on or off (fast voice; the Mac's fn + ⌃ ×2)

Another dictation key (right ctrl, right alt, right super, menu) listens at once, as fn does:

- held and released → a take that ends on release
- tapped (released within `TAP_MAX_HOLD_SECONDS`) → a take that listens until the next tap
- tapped twice quickly → cancel
- esc → cancel
- another key pressed while the dictation key is held (right ctrl + C) → that was a shortcut, the
  release says so and the take is thrown away
- the composer key (left ctrl) tapped twice → the ask box opens (the Mac's ⌃ ×2); a tap is a quick
  press and release with no other key in between, so ctrl+C never counts
Pure: fed with timestamps, so it is tested without a keyboard.
"""

from __future__ import annotations

from dataclasses import dataclass

TAP_MAX_HOLD_SECONDS = 0.35
DOUBLE_TAP_WINDOW_SECONDS = 0.45
# How long a shared key (left ctrl) is held on its own before it listens: long enough that a
# shortcut's second key nearly always comes first, short enough not to feel late.
HOLD_DELAY_SECONDS = 0.3


@dataclass(frozen=True)
class DictationPressed:
    # The take is a spoken question (left alt was down as it started), not words to type.
    ask: bool = False
    # The take is a Hey Clicky edit of the selection (shift was down as it started).
    edit: bool = False


@dataclass(frozen=True)
class HoldStarted:
    """The shared key went down: whoever feeds the recognizer calls `hold_elapsed(press_id)` after
    `HOLD_DELAY_SECONDS`, and the take starts then if the key is still held on its own."""
    press_id: int


@dataclass(frozen=True)
class AskJoined:
    """Left alt joined a take already listening: its words become a question."""


@dataclass(frozen=True)
class EditJoined:
    """Shift joined a take already listening: its words become an edit of the selection."""


@dataclass(frozen=True)
class DictationReleased:
    was_tap: bool
    key_was_pressed_during_hold: bool = False


@dataclass(frozen=True)
class DictationDoubleTapped:
    pass


@dataclass(frozen=True)
class EscapePressed:
    pass


@dataclass(frozen=True)
class TextComposerRequested:
    pass


@dataclass(frozen=True)
class HandsFreeToggleRequested:
    """The dictation key tapped twice with left alt held: always-on listening on or off."""


ShortcutEvent = (DictationPressed | DictationReleased | DictationDoubleTapped | EscapePressed | TextComposerRequested
                 | HoldStarted | AskJoined | EditJoined | HandsFreeToggleRequested)


class DictationGestureRecognizer:
    def __init__(self, shares_key_with_the_ask_box: bool = False) -> None:
        """`shares_key_with_the_ask_box`: the dictation key is also the one tapped twice for the ask
        box (left ctrl), so it waits `HOLD_DELAY_SECONDS` before a take starts."""
        self.shares_key_with_the_ask_box = shares_key_with_the_ask_box
        self.is_dictation_key_down = False
        self._pressed_at = 0.0
        self._other_key_pressed_during_hold = False
        self._last_tap_released_at: float | None = None
        self._composer = _DoubleTapDetector()
        self._take_started = False
        self._press_id = 0
        self._ask_key_down = False
        self._edit_key_down = False
        # When the last tap of the dictation key with left alt held ended (hands-free's double tap).
        self._last_ask_tap_at: float | None = None

    def _hands_free_tap(self, at_seconds: float) -> list[ShortcutEvent]:
        """A tap with left alt held; the second within the double-tap window toggles hands-free."""
        is_tap = at_seconds - self._pressed_at <= TAP_MAX_HOLD_SECONDS and not self._other_key_pressed_during_hold
        if not (self._ask_key_down and is_tap):
            self._last_ask_tap_at = None
            return []
        if self._last_ask_tap_at is not None and at_seconds - self._last_ask_tap_at <= DOUBLE_TAP_WINDOW_SECONDS:
            self._last_ask_tap_at = None
            return [DictationReleased(was_tap=True), HandsFreeToggleRequested()] if self._take_started else [HandsFreeToggleRequested()]
        self._last_ask_tap_at = at_seconds
        return []

    @property
    def any_modifier_down(self) -> bool:
        """A key that merges with a typed one is held (live typing waits): the dictation key, left
        alt, shift, or the ask box's key."""
        return self.is_dictation_key_down or self._ask_key_down or self._edit_key_down or self._composer.is_down

    def dictation_key_down(self, at_seconds: float) -> list[ShortcutEvent]:
        # Auto-repeat sends more downs while the key is held: only the first counts.
        if self.is_dictation_key_down:
            return []
        self.is_dictation_key_down = True
        self._pressed_at = at_seconds
        self._other_key_pressed_during_hold = False
        if self.shares_key_with_the_ask_box:
            self._composer.key_down(at_seconds)
            self._press_id += 1
            self._take_started = False
            return [HoldStarted(self._press_id)]
        self._take_started = True
        return [DictationPressed(ask=self._ask_key_down, edit=self._edit_key_down and not self._ask_key_down)]

    def hold_elapsed(self, press_id: int) -> list[ShortcutEvent]:
        """The shared key has been down `HOLD_DELAY_SECONDS`: a take, unless it was a shortcut."""
        if (not self.is_dictation_key_down or press_id != self._press_id or self._take_started
                or self._other_key_pressed_during_hold):
            return []
        self._take_started = True
        return [DictationPressed(ask=self._ask_key_down, edit=self._edit_key_down and not self._ask_key_down)]

    def dictation_key_up(self, at_seconds: float) -> list[ShortcutEvent]:
        if not self.is_dictation_key_down:
            return []
        self.is_dictation_key_down = False
        hands_free = self._hands_free_tap(at_seconds)
        if hands_free:
            self._take_started = False
            return hands_free
        if self.shares_key_with_the_ask_box:
            events: list[ShortcutEvent] = []
            if self._take_started:
                # A hold that listened is never also half of a double tap.
                self._composer.other_key_down()
            if self._composer.key_up(at_seconds):
                events.append(TextComposerRequested())
            if not self._take_started:
                return events  # a tap, or a shortcut: no take was started
            self._take_started = False
            return [DictationReleased(was_tap=False, key_was_pressed_during_hold=self._other_key_pressed_during_hold)] + events
        self._take_started = False
        was_tap = at_seconds - self._pressed_at <= TAP_MAX_HOLD_SECONDS and not self._other_key_pressed_during_hold
        events = [DictationReleased(was_tap=was_tap, key_was_pressed_during_hold=self._other_key_pressed_during_hold)]
        if was_tap:
            if self._last_tap_released_at is not None and at_seconds - self._last_tap_released_at <= DOUBLE_TAP_WINDOW_SECONDS:
                events.append(DictationDoubleTapped())
                self._last_tap_released_at = None
            else:
                self._last_tap_released_at = at_seconds
        else:
            self._last_tap_released_at = None
        return events

    def ask_key_down(self) -> list[ShortcutEvent]:
        """Left alt: held with the dictation key, the take is a question."""
        if self._ask_key_down:
            return []
        self._ask_key_down = True
        # Not a shortcut on its own (ctrl+alt is the ask gesture), but it does break a double tap.
        self._composer.other_key_down()
        self._last_tap_released_at = None
        return [AskJoined()] if self.is_dictation_key_down and self._take_started else []

    def ask_key_up(self) -> list[ShortcutEvent]:
        self._ask_key_down = False
        return []

    def edit_key_down(self) -> list[ShortcutEvent]:
        """Shift: held with the dictation key, the take is an edit of the selection."""
        if self._edit_key_down:
            return []
        self._edit_key_down = True
        # Not a shortcut with the dictation key, but it does break a double tap.
        self._composer.other_key_down()
        self._last_tap_released_at = None
        return [EditJoined()] if self.is_dictation_key_down and self._take_started else []

    def edit_key_up(self) -> list[ShortcutEvent]:
        self._edit_key_down = False
        return []

    def other_key_down(self, is_escape: bool) -> list[ShortcutEvent]:
        if self.is_dictation_key_down:
            self._other_key_pressed_during_hold = True
        # A key between two taps breaks the double tap.
        self._last_tap_released_at = None
        self._composer.other_key_down()
        return [EscapePressed()] if is_escape else []

    def composer_key_down(self, at_seconds: float) -> list[ShortcutEvent]:
        self._composer.key_down(at_seconds)
        return []

    def composer_key_up(self, at_seconds: float) -> list[ShortcutEvent]:
        return [TextComposerRequested()] if self._composer.key_up(at_seconds) else []


class _DoubleTapDetector:
    """Two taps (quick press and release, nothing else typed) within the double-tap window."""

    @property
    def is_down(self) -> bool:
        return self._is_down

    def __init__(self) -> None:
        self._is_down = False
        self._pressed_at = 0.0
        self._interrupted = False
        self._last_tap_released_at: float | None = None

    def key_down(self, at_seconds: float) -> None:
        if self._is_down:
            return
        self._is_down = True
        self._pressed_at = at_seconds
        self._interrupted = False

    def other_key_down(self) -> None:
        self._interrupted = True
        self._last_tap_released_at = None

    def key_up(self, at_seconds: float) -> bool:
        """True on the release that completes a double tap."""
        if not self._is_down:
            return False
        self._is_down = False
        was_tap = at_seconds - self._pressed_at <= TAP_MAX_HOLD_SECONDS and not self._interrupted
        if not was_tap:
            self._last_tap_released_at = None
            return False
        if self._last_tap_released_at is not None and at_seconds - self._last_tap_released_at <= DOUBLE_TAP_WINDOW_SECONDS:
            self._last_tap_released_at = None
            return True
        self._last_tap_released_at = at_seconds
        return False
