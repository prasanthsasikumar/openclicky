"""Raw transcript → the text that is pasted. The rules-only pass of the Mac app's `TakeFormatter`
(`formatLocally`), ported step for step so a take reads the same on both: a whole-take spoken
shortcut, fillers, the dictionary, sentence case. Spoken numbers as digits and the model clean-up
are not ported yet.
"""

from __future__ import annotations

import re

from .space import DictationSpace, SpokenShortcut

# Only the unmistakable ones: "ah", "er", "eh" and "mm" are words in romanised Indian languages.
FILLERS = ("erm", "hmm", "uh", "uhh", "um", "umm")

# Python's `re` has no \p{L}; "a word character that is not an underscore" is the same boundary.
_NOT_AFTER_LETTER_OR_NUMBER = r"(?<![^\W_])"
_NOT_BEFORE_LETTER_OR_NUMBER = r"(?![^\W_])"


def collapse_whitespace(text: str) -> str:
    collapsed = " ".join(part for part in re.split(r"[ \t]+", text) if part)
    collapsed = collapsed.replace(" \n", "\n").replace("\n ", "\n")
    return collapsed.strip()


def normalized_for_matching(text: str) -> str:
    """Lowercase words only: "My sign-off." and "my sign off" are the same trigger."""
    return " ".join(word for word in re.split(r"[\W_]+", text.lower()) if word)


def matching_shortcut(text: str, shortcuts: tuple[SpokenShortcut, ...]) -> SpokenShortcut | None:
    spoken = normalized_for_matching(text)
    if not spoken:
        return None
    return next((shortcut for shortcut in shortcuts if normalized_for_matching(shortcut.trigger) == spoken), None)


def remove_fillers(text: str) -> str:
    """Drops filler words wherever they stand, and the comma that often follows one ("um, so" → "so")."""
    pattern = _NOT_AFTER_LETTER_OR_NUMBER + "(" + "|".join(map(re.escape, FILLERS)) + ")" + _NOT_BEFORE_LETTER_OR_NUMBER + r",?\s*"
    stripped = re.sub(pattern, "", text, flags=re.IGNORECASE)
    return collapse_whitespace(stripped).replace(" ,", ",").replace(" .", ".")


def apply_dictionary(text: str, space: DictationSpace) -> str:
    """Each heard alias (and the written form itself, any case) becomes the written form, whole
    words only, longest alias first so "aditya kshatriya" wins over "aditya"."""
    replacements = [(alias, term.written) for term in space.dictionary for alias in (term.written, *term.heard_as) if alias.strip()]
    replacements.sort(key=lambda replacement: len(replacement[0]), reverse=True)
    result = text
    for alias, written in replacements:
        pattern = _NOT_AFTER_LETTER_OR_NUMBER + re.escape(alias) + _NOT_BEFORE_LETTER_OR_NUMBER
        result = re.sub(pattern, lambda _match, written=written: written, result, flags=re.IGNORECASE)
    return result


def sentence_cased(text: str) -> str:
    """A capital letter after each sentence end. No punctuation is added: the engines punctuate,
    and "git status" must land in a terminal as spoken."""
    characters = list(text)
    at_sentence_start = True
    for index, character in enumerate(characters):
        if at_sentence_start and character.isalpha():
            characters[index] = character.upper()
            at_sentence_start = False
        elif character.isalnum():
            at_sentence_start = False
        if character in ".!?\n":
            at_sentence_start = True
    return "".join(characters)


def format_locally(raw: str, space: DictationSpace) -> str:
    collapsed = collapse_whitespace(raw)
    if not collapsed:
        return ""
    shortcut = matching_shortcut(collapsed, space.shortcuts)
    if shortcut:
        return shortcut.replacement
    text = remove_fillers(collapsed)
    text = apply_dictionary(text, space)
    return sentence_cased(text)
