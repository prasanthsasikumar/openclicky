""""Can you rephrase this?" written in place (the Mac's `WriteInPlace.swift`): the text is what the
person highlighted, or else everything in the text box the cursor is in, read through the desktop's
accessibility (`accessibility.py`) when the question is asked, so the model gets the exact words.
The ask lane then offers the `write_in_place` tool, and its text replaces the old text straight away
(ctrl+z undoes it), but only if the same box still holds the same text: a reply that arrives after
the person moved on is spoken, never pasted somewhere new. The original is left on the clipboard.
Password boxes and OpenClicky's own windows are never targets.
"""

from __future__ import annotations

from dataclasses import dataclass

from .accessibility import MAXIMUM_TARGET_CHARACTERS, FocusedField

TOOL_NAME = "write_in_place"
SELECTION, WHOLE_FIELD = "selection", "wholeField"

PROMPT_INSTRUCTION = ("writing in place: when the user asks you to rephrase, rewrite, shorten, fix or translate the text quoted as "
                      "highlighted or in their text box, call write_in_place with the complete new text, and say only a few words about "
                      "what you changed (for example \"made it warmer and shorter.\"). openclicky puts it in their email and tells them "
                      "how to undo it. never read the new text out.")
NO_TARGET_PROMPT_INSTRUCTION = ("writing in place: no text to write over was found this time. if the user asks you to rephrase, rewrite, "
                                "shorten, fix or translate text that was not quoted to you as highlighted or in their text box, do not read "
                                "a new version out: tell them in one sentence to highlight the text and ask again.")
# Without the desktop's accessibility no box can be read at all: highlighting would not help.
NO_ACCESSIBILITY_PROMPT_INSTRUCTION = ("writing in place: text boxes cannot be read on this computer. if the user asks you to rephrase, "
                                       "rewrite, shorten, fix or translate text in a text box, do not read a new version out: tell them "
                                       "in one sentence that openclicky can write it in for them once \"read text boxes\" is on, in "
                                       "settings, privacy.")

REPLACED = "it's in place. press ctrl z if you want yours back."
TARGET_GONE = "you moved on before i finished, so i didn't change anything. click back into the text and ask again."
LEFT_ON_CLIPBOARD = "i couldn't put it in, so the new version is on your clipboard. press ctrl v where you want it."
EMPTY_TEXT = "i didn't get a new version to put in. please ask again."


@dataclass(frozen=True)
class WritingTarget:
    scope: str  # SELECTION | WHOLE_FIELD
    text: str
    app_name: str | None

    @property
    def context_line(self) -> str:
        place = f" in {self.app_name}" if self.app_name else ""
        if self.scope == SELECTION:
            return (f"The user has highlighted this text{place}: «{self.text}». If they ask to rephrase, rewrite, shorten, fix, polish "
                    "or translate it, call write_in_place with the complete new text.")
        return (f"The text box the user's cursor is in{place} holds this text: «{self.text}». If they ask to rephrase, rewrite, "
                "shorten, fix, polish or translate it (\"this email\", \"this\", \"what i wrote\"), call write_in_place with the complete "
                "new text.")

    def still_matches(self, field: FocusedField | None) -> bool:
        """The same box still holds the same words (and, for a highlight, still highlighted)."""
        if field is None or not field.is_editable or field.is_password:
            return False
        if self.scope == SELECTION:
            return field.selected_text == self.text
        return field.text == self.text


@dataclass(frozen=True)
class TextContext:
    """What the question can say about the text the person is working on, read as it is asked."""
    readable: bool  # the desktop's accessibility is on
    target: WritingTarget | None = None
    selected_text: str | None = None  # a highlight in a box that is not editable (a web page)


def target_in(field: FocusedField | None) -> WritingTarget | None:
    """The highlighted text, or the whole box; None for a password box, a box that is not
    editable, an empty one, or more text than a rewrite is for."""
    if field is None or field.is_password or not field.is_editable:
        return None
    selected = field.selected_text
    if selected and selected.strip():
        return WritingTarget(SELECTION, selected, field.app_name) if len(selected) <= MAXIMUM_TARGET_CHARACTERS else None
    if field.text and field.text.strip() and len(field.text) <= MAXIMUM_TARGET_CHARACTERS:
        return WritingTarget(WHOLE_FIELD, field.text, field.app_name)
    return None


def tool_definition() -> dict:
    return {"name": TOOL_NAME,
            "description": ("Replace the text the user is working on (the highlighted text, or the text box their cursor is in, quoted "
                            "in the question) with your rewritten version. Use it when they ask you to rephrase, rewrite, shorten, fix, "
                            "polish or translate that text. It replaces the old text straight away."),
            "input_schema": {"type": "object", "properties": {"text": {
                "type": "string", "description": ("The complete new text, exactly as it should appear. Keep their meaning, names and "
                                                  "greeting style, and the same language unless they asked for another. Plain text with "
                                                  "line breaks, no quotes around it, no em dashes.")}}, "required": ["text"]}}


def keeping_surrounding_space(original: str, new_text: str) -> str:
    """The new text with the spaces and line breaks the old text had at its ends."""
    leading = original[:len(original) - len(original.lstrip())]
    trailing = original[len(original.rstrip()):]
    return leading + new_text.strip() + trailing
