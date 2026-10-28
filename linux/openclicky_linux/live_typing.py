"""Live typing (the Mac's `LiveTyping.swift`): when no model will clean a take up, its words are
typed into the app in front while the person is still speaking, instead of pasted at the end. The
streaming engine (`streaming.py`) keeps revising its last words, so only settled words are typed,
and typed words are never taken back (deleting in a terminal is unsafe; terminals are never typed
into anyway). The pure pieces, as on the Mac: which words have settled, how a settled phrase is
formatted, how the final transcript lines up against what was typed; then one take's bookkeeping.

On Linux a keystroke typed while a modifier is physically held merges with it (left ctrl held, "m"
is ctrl+m), and nothing here can clear the held key's flag the way the Mac can, so phrases are
typed only while no modifier is down: a tapped take types as it is spoken, a held one types what
settled when the key comes up.
"""

from __future__ import annotations

import unicodedata
from dataclasses import dataclass, field

from . import formatter
from .space import DictationSpace, DictationStyle

VOLATILE_TAIL_WORD_COUNT = 1
REQUIRED_STABLE_SECONDS = 0.3
PAUSE_SECONDS = 0.7


def words_of(text: str) -> list[str]:
    return text.split()


def normalized_word(word: str) -> str:
    """Case and punctuation do not count: "Hello," lines up with "hello"."""
    return formatter.normalized_for_matching(word)


@dataclass(frozen=True)
class Alignment:
    remainder_start: int  # where the not-yet-typed words begin
    aligned_by_words: bool  # False: the typed words were rewritten; the rest goes to the person

    def remaining(self, words: list[str]) -> list[str]:
        return words[self.remainder_start:]


LONGEST_TRAILING_RUN = 3
MAXIMUM_DISTANCE_FROM_EXPECTED_END = 3
MAXIMUM_DISTANCE_FOR_ONE_WORD_RUN = 1


def align(final_words: list[str], committed_words: list[str]) -> Alignment:
    """Where the words not typed yet begin in `final_words` (the Mac's `TranscriptAlignment`)."""
    if not committed_words:
        return Alignment(0, True)
    final = [normalized_word(word) for word in final_words]
    committed = [normalized_word(word) for word in committed_words]
    expected_end = len(committed)
    # The usual case: the transcript starts with exactly the committed words.
    if final[:expected_end] == committed:
        return Alignment(expected_end, True)
    # The engine rewrote words on the way ("twenty five" → "25"): the committed words' last 3, then
    # 2, then 1, near where they should end.
    for run_length in range(min(LONGEST_TRAILING_RUN, expected_end), 0, -1):
        trailing = committed[-run_length:]
        allowed = MAXIMUM_DISTANCE_FOR_ONE_WORD_RUN if run_length == 1 else MAXIMUM_DISTANCE_FROM_EXPECTED_END
        ends = [end for end in range(run_length, len(final) + 1)
                if abs(end - expected_end) <= allowed and final[end - run_length:end] == trailing]
        if ends:
            return Alignment(min(ends, key=lambda end: abs(end - expected_end)), True)
    # Nothing lines up: never retype, delete or skip; everything after the common start is left.
    common = 0
    while common < min(len(final), expected_end) and final[common] == committed[common]:
        common += 1
    return Alignment(common, False)


@dataclass
class _Tracked:
    word: str
    unchanged_since: float


class StableWordCommitter:
    """Which words of a revising partial have settled: not among the last word of the partial, and
    unchanged at their place for `REQUIRED_STABLE_SECONDS`. Committed words are never retracted."""

    def __init__(self, protected_phrases: list[list[str]] = (), take_start_triggers: list[list[str]] = (),
                 volatile_tail: int = VOLATILE_TAIL_WORD_COUNT, stable_seconds: float = REQUIRED_STABLE_SECONDS) -> None:
        # Multi-word dictionary aliases (normalised), never split across two typed phrases.
        self.protected_phrases = [phrase for phrase in protected_phrases if len(phrase) > 1]
        # Spoken shortcut triggers: while everything said could still be one, nothing is typed.
        self.take_start_triggers = [trigger for trigger in take_start_triggers if trigger]
        self.volatile_tail = volatile_tail
        self.stable_seconds = stable_seconds
        self.committed_words: list[str] = []
        self._tracked: list[_Tracked] = []

    def observe(self, partial: str, now: float, person_paused: bool = False) -> list[str]:
        partial_words = words_of(partial)
        alignment = align(partial_words, self.committed_words)
        # The partial no longer lines up with what was committed: the final transcript decides.
        if not alignment.aligned_by_words:
            return []
        uncommitted = alignment.remaining(partial_words)
        self._tracked = [self._tracked[position] if position < len(self._tracked) and self._tracked[position].word == word
                         else _Tracked(word, now) for position, word in enumerate(uncommitted)]
        if not self.committed_words and self._could_still_be_a_shortcut(partial_words):
            return []
        past_the_tail = max(0, len(uncommitted) - (0 if person_paused else self.volatile_tail))
        settled = 0
        while settled < past_the_tail and now - self._tracked[settled].unchanged_since >= self.stable_seconds:
            settled += 1
        settled = self._shrink_so_no_protected_phrase_is_split(settled, uncommitted)
        if settled <= 0:
            return []
        newly = uncommitted[:settled]
        self.committed_words += newly
        self._tracked = self._tracked[settled:]
        return newly

    def _could_still_be_a_shortcut(self, partial_words: list[str]) -> bool:
        spoken = [normalized_word(word) for word in partial_words]
        return any(len(spoken) <= len(trigger) and trigger[:len(spoken)] == spoken for trigger in self.take_start_triggers)

    def _shrink_so_no_protected_phrase_is_split(self, settled: int, uncommitted: list[str]) -> int:
        while settled > 0:
            would_be = [normalized_word(word) for word in self.committed_words + uncommitted[:settled]]
            inside = any(len(would_be) >= length and would_be[-length:] == phrase[:length]
                         for phrase in self.protected_phrases for length in range(1, len(phrase)))
            if not inside:
                break
            settled -= 1
        return settled


def contains_control_characters(text: str) -> bool:
    """A line break, tab or other control character, or a line or paragraph separator: typed, a new
    line sends a message or runs a command, so such text is never typed."""
    return any(unicodedata.category(character) in ("Cc", "Zl", "Zp") for character in text)


def ends_a_sentence(typed_so_far: str) -> bool:
    trimmed = typed_so_far.rstrip(" \t")
    return not trimmed or trimmed[-1] in ".!?\n"


def needs_separating_space(typed_so_far: str, next_text: str) -> bool:
    if not typed_so_far or not next_text:
        return False
    if typed_so_far[-1].isspace() or next_text[0].isspace():
        return False
    return next_text[0] not in ",.;:!?)]}"


def text_to_type(phrase_words: list[str], style: DictationStyle, space: DictationSpace, typed_so_far: str) -> str:
    """A settled phrase with fillers, the dictionary and sentence case, and its leading space."""
    text = formatter.collapse_whitespace(" ".join(phrase_words))
    if not text:
        return ""
    if style.remove_fillers:
        text = formatter.remove_fillers(text)
    text = formatter.apply_dictionary(text, space)
    if style.sentence_case and text:
        if ends_a_sentence(typed_so_far):
            text = formatter.sentence_cased(text)
        else:
            # Mid-sentence: only what follows a sentence end inside the phrase is capitalised.
            text = formatter.sentence_cased("x " + text)[2:]
    if not text:
        return ""
    return " " + text if needs_separating_space(typed_so_far, text) else text


@dataclass
class LiveTypingSession:
    """One take's live typing, as the take controller keeps it."""
    committer: StableWordCommitter
    style: DictationStyle
    space: DictationSpace
    typed_text: str = ""
    typed_raw_words: list[str] = field(default_factory=list)
    # Committed, not typed yet (a modifier was down): typed with the next phrase.
    waiting_words: list[str] = field(default_factory=list)
    latest_partial: str = ""
    last_partial_at: float = 0.0
    stopped_reason: str | None = None

    @property
    def has_typed(self) -> bool:
        return bool(self.typed_raw_words)


def session_for(style: DictationStyle, space: DictationSpace) -> LiveTypingSession:
    protected = [[normalized_word(word) for word in alias.split()] for term in space.dictionary for alias in term.heard_as]
    triggers = [[normalized_word(word) for word in shortcut.trigger.split()] for shortcut in space.shortcuts]
    return LiveTypingSession(StableWordCommitter(protected, triggers), style, space)
