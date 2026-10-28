"""Asking about the screen: the question and the screenshots go to a model, which answers in a
sentence or two and may start with a `[POINT:…]` tag the pointer flies to.

Who answers is `answers.py`'s choice (the Mac's order: own keys, then the plans switched on, then
the free account, unless Settings picks one); own keys never go to the backend. The answer
streams: `on_text` hears it as it is written, so the pointer can fly and the voice start before it
is finished. A question can carry extras (`AskExtras`): lines that go with it but are not kept in
the conversation (where the mouse is, "asked again"), and more pictures (the close-up around the
mouse).

`ask_with_tools` also offers tools (the quick actions in `actions.py`, as the Mac's ask lane does
wherever the agent lane does not run) and returns the calls the model made with its answer.
"""

from __future__ import annotations

import time
import urllib.request
from dataclasses import dataclass, field
from typing import Callable

from . import answers, applog, guided
from .ask_lane import UNSURE_PROMPT_SECTION
from .config import LinuxSettings

ANTHROPIC_MODEL = answers.DEFAULT_CLAUDE_MODEL
OPENAI_MODEL = answers.SOURCES_BY_ID["openAI"].default_model
MAX_ANSWER_TOKENS = 400
# Exchanges kept for follow-ups ("and where is that?"), as on the Mac.
HISTORY_EXCHANGES = 10

# The Mac app's ask prompt, for a typed question and an answer that is read, not heard.
SYSTEM_PROMPT = """you're openclicky, a friendly companion living on the user's linux desktop. the user just typed a question to you and you can see their screen. your answer is shown in a small caption next to your pointer, so keep it short. this is an ongoing conversation, you remember everything they've said before.

rules:
- keep it short: one or two short sentences, about thirty words at most.
- if they ask you to explain more or go deeper, say more, but still keep it under about eighty words.
- all lowercase, casual, warm. no emojis.
- no lists, bullet points, markdown or em dashes. plain sentences.
- if the user's question relates to what's on their screen, reference specific things you see.
- if the screenshot doesn't seem relevant to their question, just answer the question directly.
- you can help with anything: coding, writing, general knowledge, brainstorming.
- never say "simply" or "just".
- don't end with questions like "want me to explain more?", and don't add extra tips they didn't ask for.

element pointing:
you have a small orange triangle pointer that can fly to and point at things on screen. use it whenever pointing would genuinely help: if they're asking how to do something, looking for a menu, trying to find a button, or need help navigating an app, point at the relevant element. err on the side of pointing. don't point for general knowledge questions or at something obvious they're already looking at.

when you point, START your response with a coordinate tag. the screenshot is labeled with its pixel size; use it as the coordinate space, origin (0,0) at the top-left, x rightward, y downward.

format: [POINT:x,y:label] where x,y are integer pixel coordinates in the screenshot and label is a short 1-3 word description of the element (like "search bar" or "save button"). if you receive more than one screen image, the one labeled "primary focus" is where the mouse is: prioritize it. if the element is on a DIFFERENT screen, append :screenN where N is the screen number from the image label, like [POINT:400,300:terminal:screen2]; without it the pointer goes to the wrong place.

if pointing wouldn't help, start with [POINT:none].

examples:
- user asks how to open a new tab in firefox: "[POINT:1180,40:new tab] that plus sign at the end of your tabs opens a new one."
- user asks what html is: "[POINT:none] html stands for hypertext markup language, it's basically the skeleton of every web page."
"""

SYSTEM_PROMPT += guided.PROMPT_SECTION
SYSTEM_PROMPT += UNSURE_PROMPT_SECTION
SYSTEM_PROMPT += "\nkeep your first sentence short: it is spoken while the rest of your answer is still being written.\n"

# The Mac's sentence for no way to answer at all: the free account first, then keys.
NEEDS_A_WAY_TO_ANSWER = answers.NEEDS_A_WAY_TO_ANSWER
AskError = answers.AnswerError
ToolCall = answers.ToolCall


@dataclass(frozen=True)
class Screenshot:
    """One monitor's picture. A question carries one per monitor, the one with the mouse first."""
    png_bytes: bytes
    width: int
    height: int
    # Screenshot pixels per screen pixel (below 1 when the capture was downscaled).
    scale: float
    # Where this monitor's top-left corner is on the X screen (0, 0 for the first or only one).
    origin_x: int = 0
    origin_y: int = 0


@dataclass(frozen=True)
class AskExtras:
    """What goes with one question and is not kept in the conversation: lines after the question,
    pictures after the screens (each with its label), and the skills that apply (`skills.py`), after
    the instructions."""
    prompt_lines: tuple[str, ...] = ()
    images: tuple[tuple[bytes, str], ...] = ()
    skills_block: str = ""
    # Writing in place (`writing.py`): its line for the instructions, its tool, and what runs it
    # (the new text → the sentence said about it).
    instruction: str = ""
    tools: tuple[dict, ...] = ()
    write_in_place: Callable[[str], str] | None = None


# An account's answers take at most two pictures (the Mac's `AccountCapabilities.maxAskScreens`); own keys up to four.
MAXIMUM_SCREENS_ON_AN_ACCOUNT = 2
MAXIMUM_SCREENS = 4


def as_screen_list(screens: "Screenshot | list[Screenshot]") -> list[Screenshot]:
    return [screens] if isinstance(screens, Screenshot) else list(screens)


@dataclass(frozen=True)
class AskRequest:
    kind: str  # "anthropic" | "openrouter" | "openai" | "backend" | another source's id, lowercased
    request: urllib.request.Request


@dataclass
class Conversation:
    exchanges: list[tuple[str, str]] = field(default_factory=list)

    def remember(self, question: str, answer: str) -> None:
        self.exchanges.append((question, answer))
        del self.exchanges[:-HISTORY_EXCHANGES]


def _with_language(settings: LinuxSettings, extra_instructions: str = "") -> str:
    prompt = SYSTEM_PROMPT + (f"\n\n{extra_instructions}" if extra_instructions else "")
    if settings.language_code and settings.bare_language_code != "en":
        return prompt + f"\nalways answer in the language with code {settings.language_code}, whatever language the question is in."
    return prompt


def answer_request(question: str, screenshot: "Screenshot | list[Screenshot]", conversation: Conversation, settings: LinuxSettings,
                   tools: list[dict] | None = None, extra_instructions: str = "", extras: AskExtras | None = None,
                   maximum_screens: int = MAXIMUM_SCREENS) -> answers.AnswerRequest:
    """The question, its pictures (each labelled "screen 1 (primary focus, 1280x800 pixels)"), the
    earlier exchanges and the prompt, as every source is asked."""
    extras = extras or AskExtras()
    images = []
    for index, screen in enumerate(as_screen_list(screenshot)[:maximum_screens]):
        focus = "primary focus, " if index == 0 else ""
        images.append(answers.Image(screen.png_bytes, f"screen {index + 1} ({focus}{screen.width}x{screen.height} pixels)"))
    images += [answers.Image(data, label, "image/png" if data[:4] == b"\x89PNG" else "image/jpeg") for data, label in extras.images]
    # The Mac's `askUserPrompt`: each line of context in brackets after the question.
    user_prompt = question + ("\n\n" + "\n".join(f"[{line}]" for line in extras.prompt_lines) if extras.prompt_lines else "")
    instructions = "\n\n".join(part for part in (extra_instructions, extras.instruction, extras.skills_block.strip()) if part)
    return answers.AnswerRequest(images=images, system_prompt=_with_language(settings, instructions),
                                 history=list(conversation.exchanges), user_prompt=user_prompt, tools=list(tools or []),
                                 max_tokens=MAX_ANSWER_TOKENS)


def _kind(source: answers.Source) -> str:
    return {"openClickyAccount": "backend", "openRouter": "openrouter", "openAI": "openai"}.get(source.id, source.id.lower())


def _source_or_refusal(settings: LinuxSettings) -> answers.Source:
    source = answers.answering_source(settings)
    if source is None:
        raise AskError(NEEDS_A_WAY_TO_ANSWER)
    return source


def request_for(question: str, screenshot: "Screenshot | list[Screenshot]", conversation: Conversation, settings: LinuxSettings,
                tools: list[dict] | None = None, extra_instructions: str = "", extras: AskExtras | None = None) -> AskRequest:
    """The HTTP request the answering source gets (for the plans, which run a CLI, there is none)."""
    source = _source_or_refusal(settings)
    maximum = MAXIMUM_SCREENS_ON_AN_ACCOUNT if source.id == "openClickyAccount" else MAXIMUM_SCREENS
    built = answer_request(question, screenshot, conversation, settings, tools, extra_instructions, extras, maximum)
    return AskRequest(_kind(source), answers.http_request(source, built, settings))


def ask_with_tools(question: str, screenshot: "Screenshot | list[Screenshot]", conversation: Conversation, settings: LinuxSettings,
                   tools: list[dict] | None = None, extra_instructions: str = "", extras: AskExtras | None = None,
                   on_text: Callable[[str], None] = lambda _piece: None) -> tuple[str, list[ToolCall]]:
    """The answer and the tools it called (at most two, as on the Mac), streamed into `on_text`."""
    source = _source_or_refusal(settings)
    maximum = MAXIMUM_SCREENS_ON_AN_ACCOUNT if source.id == "openClickyAccount" else MAXIMUM_SCREENS
    built = answer_request(question, screenshot, conversation, settings, tools, extra_instructions, extras, maximum)
    started = time.monotonic()
    try:
        answer, calls = answers.answer(source, built, settings, on_text)
    except AskError as failure:
        applog.append(f"ask: {source.id} failed after {time.monotonic() - started:.1f} s: {failure}")
        raise
    if not answer.strip() and not calls:
        raise AskError(f"{source.short_name.lower()} answered with no text.")
    applog.append(f"ask: {_kind(source)} answered in {time.monotonic() - started:.1f} s, {len(as_screen_list(screenshot))} screen(s)"
                  f"{', a guided step' if guided.contains_marker(answer) else ''}{f', {len(calls)} tool call(s)' if calls else ''}")
    return answer.strip(), calls[:2]


def ask(question: str, screenshot: "Screenshot | list[Screenshot]", conversation: Conversation, settings: LinuxSettings,
        extras: AskExtras | None = None, on_text: Callable[[str], None] = lambda _piece: None) -> str:
    return ask_with_tools(question, screenshot, conversation, settings, extras=extras, on_text=on_text)[0]
