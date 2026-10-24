"""Spoken English numbers written as digits, without a model: "three hundred and twenty five" →
"325", "fifty percent" → "50%". The Mac's `SpokenNumbers.swift`, ported rule for rule: one to nine
stay words ("one of them", "two books"), ten and up and anything counted in hundreds or more become
digits, and a run that can't be one number ("two three four", a phone number read out) is left as
it was said.
"""

from __future__ import annotations

UNITS = {"zero": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9}
TEENS = {"ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14, "fifteen": 15, "sixteen": 16,
         "seventeen": 17, "eighteen": 18, "nineteen": 19}
TENS = {"twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80, "ninety": 90}
SCALES = {"thousand": 1_000, "million": 1_000_000, "billion": 1_000_000_000}


def kind_of(word: str) -> str | None:
    if word in UNITS:
        return "unit"
    if word in TEENS:
        return "teen"
    if word in TENS:
        return "tens"
    if word == "hundred":
        return "hundred"
    if word in SCALES:
        return "scale"
    return None


def can_follow(following: str, previous: str) -> bool:
    """Whether a word of kind `following` carries on the number ("twenty" "five" does; "two"
    "three" does not, it is two numbers)."""
    return following in {
        "unit": ("hundred", "scale"), "teen": ("hundred", "scale"), "tens": ("unit", "scale"),
        "hundred": ("unit", "teen", "tens", "scale"), "scale": ("unit", "teen", "tens"),
    }[previous]


def split_token(token: str) -> tuple[str, str, str]:
    """A token's leading punctuation, its letters and digits, and its trailing punctuation."""
    start, end = 0, len(token)
    while start < end and not token[start].isalnum():
        start += 1
    while end > start and not token[end - 1].isalnum():
        end -= 1
    return token[:start], token[start:end], token[end:]


def value_of(words: list[str]) -> int | None:
    total = current = 0
    for word in words:
        number = UNITS.get(word, TEENS.get(word, TENS.get(word)))
        if number is not None:
            current += number
        elif word == "hundred":
            current = max(current, 1) * 100
        elif word in SCALES:
            total += max(current, 1) * SCALES[word]
            current = 0
        else:
            return None
    return total + current


def _number_run(tokens: list[str], start: int) -> tuple[str, int] | None:
    words: list[str] = []
    previous_kind: str | None = None
    index = start
    leading = trailing = ""
    while index < len(tokens):
        lead, core, trail = split_token(tokens[index])
        pieces = [piece for piece in core.lower().split("-") if piece]
        kinds = [kind_of(piece) for piece in pieces]
        # "and" joins only inside a number: "three hundred and twenty".
        if (core.lower() == "and" and previous_kind in ("hundred", "scale") and not lead and not trail and index + 1 < len(tokens)
                and kind_of(split_token(tokens[index + 1])[1].lower()) not in (None, "hundred", "scale")):
            index += 1
            continue
        if not pieces or not core or any(kind is None for kind in kinds) or (lead and words):
            break
        fits = True
        kind_after = previous_kind
        for piece_kind in kinds:
            if kind_after is not None and not can_follow(piece_kind, kind_after):
                fits = False
                break
            kind_after = piece_kind
        if not fits:
            break
        if not words:
            leading = lead
        words.extend(pieces)
        previous_kind = kind_after
        index += 1
        # Punctuation ends the number: "twenty, thirty" is two.
        if trail:
            trailing = trail
            break
    if not words:
        return None
    value = value_of(words)
    if value is None:
        return None
    # A lone "hundred" or "thousand" ("a hundred people") keeps its words, and so does one to nine.
    starts_with_scale = kind_of(words[0]) in ("hundred", "scale")
    is_counted = any(kind_of(word) in ("hundred", "scale") for word in words)
    if starts_with_scale or not (value >= 10 or is_counted):
        return None
    written = str(value)
    # "fifty percent" → "50%".
    if not trailing and index < len(tokens):
        _lead, next_core, next_trail = split_token(tokens[index])
        if next_core.lower() == "percent":
            written += "%"
            trailing = next_trail
            index += 1
    return leading + written + trailing, index


def digitize(text: str) -> str:
    """`text` with its spoken numbers as digits."""
    tokens = text.split(" ")
    output = []
    index = 0
    while index < len(tokens):
        run = _number_run(tokens, index)
        if run:
            output.append(run[0])
            index = run[1]
        else:
            output.append(tokens[index])
            index += 1
    return " ".join(output)
