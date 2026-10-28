"""Where `point_at` really lands (fast voice, `realtime.py`): the Realtime model's pixels are 30–100 px
off, so its guess is moved onto the element (the Mac's `ScreenTextLocator` and
`ScreenElementGrounder`):

1. **the element's text** (the tool asks for it): the screenshot the model saw is read (Tesseract,
   when it is installed, at twice the size since captions are small) and the guess snaps to that
   text nearest to it, exact matches only, within 30 % of the picture's width; when a second match
   is nearly as close ("Edit" on every row), nothing is decided here;
2. **else Claude** (Haiku on an Anthropic key, the account, or the answering source's model on
   another key; never a plan, too slow mid-sentence) finds the element on the same screenshot
   (2–4 s), and its text is snapped again when it names some;
3. **else the model's own guess**.
"""

from __future__ import annotations

import csv
import dataclasses
import io
import math
import re
import shutil
import subprocess
import unicodedata
from dataclasses import dataclass

from . import answers, applog

GENERIC_WORDS = frozenset({
    "button", "btn", "menu", "icon", "link", "tab", "field", "box", "option", "item", "toggle", "checkbox", "dropdown", "bar", "panel",
    "window", "dialog", "input", "label", "text", "list", "row", "section", "control", "the", "and", "for", "with", "top", "left",
    "right", "bottom", "here", "this", "that"})
MAXIMUM_DISTANCE_FRACTION = 0.30
AMBIGUITY_FRACTION = 0.10
GROUNDING_MODEL = "claude-haiku-4-5"


@dataclass(frozen=True)
class TextLine:
    text: str
    left: float
    top: float
    width: float
    height: float


def normalize(text: str) -> str:
    folded = "".join(character for character in unicodedata.normalize("NFKD", text.lower()) if not unicodedata.combining(character))
    words = [word.strip("".join(chr(code) for code in range(33, 127) if not chr(code).isalnum())) for word in folded.split()]
    return " ".join(word for word in words if word)


def _matches(needle: str, lines: list[TextLine]) -> list[tuple[float, float]]:
    """The centre of every place `needle` appears, its characters spread evenly over the line's box."""
    found = []
    for line in lines:
        haystack = normalize(line.text)
        start = haystack.find(needle)
        while start >= 0:
            count = max(len(haystack), 1)
            found.append((line.left + line.width * (start + len(needle) / 2) / count, line.top + line.height / 2))
            start = haystack.find(needle, start + 1)
    return found


def _nearest(points: list[tuple[float, float]], guess: tuple[float, float], maximum_distance: float, ambiguity: float):
    ranked = sorted((math.dist(point, guess), point) for point in points)
    ranked = [entry for entry in ranked if entry[0] <= maximum_distance]
    if not ranked:
        return None
    if len(ranked) > 1 and ranked[1][0] - ranked[0][0] < ambiguity:
        return None
    return ranked[0][1]


def locate_text(hint: str, guess: tuple[float, float], lines: list[TextLine], picture_width: float) -> tuple[float, float] | None:
    """The guess snapped to `hint`'s text: the whole hint, else its distinctive words, longest first."""
    needle = normalize(hint)
    if len(needle) < 3 or needle in GENERIC_WORDS:
        return None
    maximum, ambiguity = picture_width * MAXIMUM_DISTANCE_FRACTION, picture_width * AMBIGUITY_FRACTION
    found = _nearest(_matches(needle, lines), guess, maximum, ambiguity)
    if found:
        return found
    words = sorted((word for word in needle.split() if len(word) >= 3 and word not in GENERIC_WORDS and word != needle), key=len, reverse=True)
    for word in words:
        found = _nearest(_matches(word, lines), guess, maximum, ambiguity)
        if found:
            return found
    return None


def read_screen_text(png: bytes, upscale: int = 2) -> list[TextLine]:
    """The screenshot's text lines with their boxes (in the picture's pixels), or none when no
    reader is installed (`tesseract`)."""
    if shutil.which("tesseract") is None:
        return []
    import gi

    gi.require_version("GdkPixbuf", "2.0")
    from gi.repository import GdkPixbuf

    loader = GdkPixbuf.PixbufLoader.new_with_type("png")
    loader.write(png)
    loader.close()
    picture = loader.get_pixbuf()
    larger = picture.scale_simple(picture.get_width() * upscale, picture.get_height() * upscale, GdkPixbuf.InterpType.BILINEAR)
    succeeded, data = larger.save_to_bufferv("png", [], [])
    try:
        finished = subprocess.run(["tesseract", "stdin", "stdout", "--psm", "11", "tsv"], input=bytes(data), capture_output=True, timeout=15)
    except (OSError, subprocess.SubprocessError):
        return []
    lines: dict[tuple, list[dict]] = {}
    for row in csv.DictReader(io.StringIO(finished.stdout.decode("utf-8", "replace")), delimiter="\t", quoting=csv.QUOTE_NONE):
        if row.get("level") != "5" or not (row.get("text") or "").strip():
            continue
        lines.setdefault((row["block_num"], row["par_num"], row["line_num"]), []).append(row)
    found = []
    for words in lines.values():
        left = min(int(word["left"]) for word in words)
        top = min(int(word["top"]) for word in words)
        right = max(int(word["left"]) + int(word["width"]) for word in words)
        bottom = max(int(word["top"]) + int(word["height"]) for word in words)
        found.append(TextLine(" ".join(word["text"] for word in words), left / upscale, top / upscale, (right - left) / upscale,
                              (bottom - top) / upscale))
    return found


def grounding_prompt(label: str, text: str, user_request: str | None, width: int, height: int) -> str:
    lines = [f"The screenshot is {width}×{height} px, origin top-left."]
    element = f"Find the center of this UI element: {label}" + (f' (it shows the text "{text}")' if text else "")
    lines.append(element + ".")
    if user_request:
        lines.append(f'The user asked: "{user_request}", pick the element that answers that.')
    lines.append('Reply with only [POINT:x,y:visible text], where visible text is exactly what the element itself shows (a link that '
                 'reads "arXiv" is arXiv, whatever it leads to), or [POINT:x,y] for an element with no text, or [POINT:none] if it is '
                 "not visible.")
    return "\n".join(lines)


_POINT = re.compile(r"\[POINT:\s*(\d+)\s*,\s*(\d+)\s*(?::([^\]]*))?\]")


def parse_located(reply: str, width: int, height: int) -> tuple[tuple[float, float], str | None] | None:
    match = _POINT.search(reply)
    if not match:
        return None
    x, y = float(match.group(1)), float(match.group(2))
    if x > width or y > height:
        return None
    visible = (match.group(3) or "").strip() or None
    return (x, y), visible


def ground(label: str, text: str, user_request: str | None, png: bytes, width: int, height: int, settings) -> tuple[tuple[float, float], str | None] | None:
    """Claude (or the answering source's model) finds the element; None when nothing may ask."""
    source = answers.answering_source(settings)
    if source is None or source.id in ("claudeCodePlan", "codexPlan"):
        applog.append(f"point_at: no grounding ({'nothing is set up to answer' if source is None else 'a plan is too slow mid-sentence'})")
        return None
    if source.id in ("anthropic", "openClickyAccount"):
        settings = dataclasses.replace(settings, claude_model=GROUNDING_MODEL)
    request = answers.AnswerRequest(
        images=[answers.Image(png, f"Screenshot of the user's screen, {width}×{height} px, origin top-left.")],
        system_prompt="You locate UI elements in screenshots and answer only with a [POINT:x,y:visible text] tag in the screenshot's pixel coordinates.",
        history=[], user_prompt=grounding_prompt(label, text, user_request, width, height), max_tokens=60)
    try:
        reply, _calls = answers.answer(source, request, settings)
    except answers.AnswerError as failure:
        applog.append(f"point_at: grounding failed ({type(failure).__name__})")
        return None
    return parse_located(reply, width, height)


def resolve(guess: tuple[float, float], label: str, text: str, user_request: str | None, png: bytes, width: int, height: int,
            settings, lines: list[TextLine] | None = None) -> tuple[tuple[float, float], str]:
    """Where to point, and how it was found ("snapped", "located by claude", "guess")."""
    lines = read_screen_text(png) if lines is None else lines
    if text:
        snapped = locate_text(text, guess, lines, width)
        if snapped:
            return snapped, "snapped"
    located = ground(label, text, user_request, png, width, height, settings)
    if located:
        point, visible = located
        if visible and lines:
            snapped = locate_text(visible, point, lines, width)
            if snapped:
                return snapped, "located by claude, snapped"
        return point, "located by claude"
    return guess, "guess"
