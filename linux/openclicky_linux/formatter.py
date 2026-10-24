"""Raw transcript → the text that is pasted: the Mac app's `TakeFormatter`, ported step for step so
a take reads the same on both.

- `format_locally`: a whole-take spoken shortcut, fillers, spoken English numbers as digits
  (`spoken_numbers.py`), the dictionary, sentence case; the style decides fillers and case.
- The model pass's pieces (`polish.py` runs it): the system prompt with the style's rules, the
  answer unwrapped, and the checks that the rewrite is still the same take (not an answer to it,
  not cut short), else the local result is pasted.
"""

from __future__ import annotations

import re

from .space import DictationSpace, DictationStyle, SpokenShortcut
from .spoken_numbers import digitize

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


def is_shortcut(raw: str, space: DictationSpace) -> bool:
    return matching_shortcut(collapse_whitespace(raw), space.shortcuts) is not None


def format_locally(raw: str, space: DictationSpace, style: DictationStyle | None = None, language_code: str | None = None) -> str:
    collapsed = collapse_whitespace(raw)
    if not collapsed:
        return ""
    shortcut = matching_shortcut(collapsed, space.shortcuts)
    if shortcut:
        return shortcut.replacement
    text = remove_fillers(collapsed) if style is None or style.remove_fillers else collapsed
    # English only: other languages say their numbers in their own words.
    bare_language = (language_code or "en").split("-")[0].split("_")[0].lower()
    if bare_language == "en":
        text = digitize(text)
    text = apply_dictionary(text, space)
    return sentence_cased(text) if style is None or style.sentence_case else text


# MARK: the model pass


def system_prompt(style: DictationStyle, space: DictationSpace, app_name: str | None = None, language_code: str | None = None) -> str:
    """The Mac's clean-up prompt, line for line."""
    lines = [
        "You clean up dictated speech into written text. Return only the cleaned text, nothing else: no preamble, no quotes, no explanation, no markdown fences.",
        "Keep the speaker's meaning, words and order. Fix punctuation, capitalisation and obvious speech-to-text errors; remove fillers, false starts "
        "and self-corrections (keep the corrected version). Do not add content, do not answer the text, do not translate unless told to.",
        "Write numbers the way the context expects (\"3 kg\", \"3 PM\", \"2026\"). Spoken formatting words become formatting: \"new line\", "
        "\"new paragraph\", \"comma\", \"full stop\".",
        f"Style ({style.name}): {style.rules}",
    ]
    if app_name:
        lines.append(f"The text is going into {app_name}.")
    if language_code:
        bare = language_code.split("-")[0].split("_")[0].lower()
        lines.append(f"The language is {language_code} ({bare}). Write it in its native script.")
    if space.dictionary:
        terms = [f'"{term.written}"' + (f" (heard as {', '.join(chr(34) + alias + chr(34) for alias in term.heard_as)})" if term.heard_as else "")
                 for term in space.dictionary]
        lines.append("Spell these exactly as written: " + "; ".join(terms) + ".")
    return "\n".join(lines)


def unwrap_model_answer(answer: str) -> str:
    """Models wrap answers in quotes or fences now and then; the take never wanted them."""
    text = answer.strip()
    if text.startswith("```"):
        text = "\n".join(text.split("\n")[1:])
        if text.endswith("```"):
            text = text[:-3]
        text = text.strip()
    if len(text) >= 2 and text.startswith('"') and text.endswith('"'):
        text = text[1:-1]
    return text


def _latin_words(text: str) -> list[str] | None:
    pieces = [piece.replace("\u2019", "'") for piece in re.split(r"[^\w'\u2019]+|_", text.lower()) if piece]
    pieces = [piece for piece in pieces if not any(character.isdigit() for character in piece)]
    if not all(piece.isascii() for piece in pieces):
        return None
    return pieces


def reuses_the_spoken_words(polished: str, raw: str) -> bool:
    """At least half the rewrite's words were spoken (numbers and non-latin scripts aside): a reply of
    the same length that answers the take ("tell me a joke" → a joke) is not a clean-up."""
    polished_words, raw_words = _latin_words(polished), _latin_words(raw)
    if polished_words is None or raw_words is None or len(polished_words) < 3:
        return True
    spoken = set(raw_words)
    return sum(word in spoken for word in polished_words) / len(polished_words) >= 0.5


def looks_like_the_same_take(polished: str, raw: str) -> bool:
    """A rewrite much longer or shorter than the words spoken is an answer, a refusal or cut off."""
    raw_length = len([character for character in raw if not character.isspace()])
    polished_length = len([character for character in polished if not character.isspace()])
    if raw_length >= 40 and polished_length < raw_length * 0.6:
        return False
    raw_words = max(1, len(raw.split()))
    polished_words = len(polished.split())
    if raw_words <= 3:
        return polished_words <= raw_words + 4
    if not 0.4 <= polished_words / raw_words <= 1.8:
        return False
    return reuses_the_spoken_words(polished, raw)
