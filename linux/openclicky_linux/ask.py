"""Asking about the screen by text: the question and a screenshot go to a model, which answers in a
sentence or two and may start with a `[POINT:…]` tag the pointer flies to.

Routed in the Mac app's order (`ProviderCredentials.answerSourcesInOrder`): the person's own
Anthropic key straight to Anthropic (Messages, the Mac's default model), else an own OpenRouter key
(OpenAI-style chat completions, the Mac's OpenRouter model unless `answerModels.openrouter` names
another), else an own OpenAI key to OpenAI chat completions, else the OpenClicky account through the
backend's `POST /chat` (the same Messages body the Mac relays), else a sentence naming what is missing. Own keys never go to the backend. Answers are not streamed: they are short,
and the caption shows them whole.

`ask_with_tools` also offers tools (the quick actions in `actions.py`, as the Mac's ask lane does
wherever the agent lane does not run) and returns the calls the model made with its answer.
"""

from __future__ import annotations

import base64
import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field

from . import applog, guided
from .config import LinuxSettings

ANTHROPIC_MESSAGES_URL = "https://api.anthropic.com/v1/messages"
ANTHROPIC_VERSION = "2023-06-01"
ANTHROPIC_MODEL = "claude-sonnet-4-6"
OPENAI_CHAT_URL = "https://api.openai.com/v1/chat/completions"
OPENAI_MODEL = "gpt-4.1"
OPENROUTER_CHAT_URL = "https://openrouter.ai/api/v1/chat/completions"
OPENROUTER_MODEL = "anthropic/claude-sonnet-4.6"
MAX_ANSWER_TOKENS = 400
REQUEST_TIMEOUT_SECONDS = 60
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


class AskError(Exception):
    """Carries the sentence the caption shows; `account_refused` when the account's token was refused
    (renewing the session and asking once more may help)."""

    def __init__(self, message: str, account_refused: bool = False) -> None:
        super().__init__(message)
        self.account_refused = account_refused


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


# An account's answers take at most two pictures (the Mac's `AccountCapabilities.maxAskScreens`); own keys up to four.
MAXIMUM_SCREENS_ON_AN_ACCOUNT = 2
MAXIMUM_SCREENS = 4


def as_screen_list(screens: "Screenshot | list[Screenshot]") -> list[Screenshot]:
    return [screens] if isinstance(screens, Screenshot) else list(screens)


def _labelled_screens(screens: list[Screenshot], question: str, image_block) -> list[dict]:
    """Each picture followed by its label ("screen 1 (primary focus, 1280x800 pixels)"), the question
    last; with one screen, its label and the question share one block."""
    content: list[dict] = []
    for index, screen in enumerate(screens):
        focus = "primary focus, " if index == 0 else ""
        label = f"screen {index + 1} ({focus}{screen.width}x{screen.height} pixels)"
        content.append(image_block(screen))
        if index == len(screens) - 1:
            content.append({"type": "text", "text": f"{label}\n\n{question}"})
        else:
            content.append({"type": "text", "text": label})
    return content


@dataclass(frozen=True)
class ToolCall:
    name: str
    arguments: dict


@dataclass(frozen=True)
class AskRequest:
    kind: str  # "anthropic" | "openrouter" | "openai" | "backend"
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


def _anthropic_body(question: str, screens: list[Screenshot], conversation: Conversation, settings: LinuxSettings,
                    tools: list[dict] | None = None, extra_instructions: str = "") -> dict:
    messages = []
    for earlier_question, earlier_answer in conversation.exchanges:
        messages += [{"role": "user", "content": earlier_question}, {"role": "assistant", "content": earlier_answer}]
    messages.append({"role": "user", "content": _labelled_screens(screens, question, lambda screen: {
        "type": "image", "source": {"type": "base64", "media_type": "image/png", "data": base64.b64encode(screen.png_bytes).decode("ascii")}})})
    body = {"model": ANTHROPIC_MODEL, "max_tokens": MAX_ANSWER_TOKENS, "system": _with_language(settings, extra_instructions), "messages": messages}
    if tools:
        body["tools"] = tools
    return body


def _openai_body(question: str, screens: list[Screenshot], conversation: Conversation, settings: LinuxSettings,
                 model: str = OPENAI_MODEL, tools: list[dict] | None = None, extra_instructions: str = "") -> dict:
    messages: list[dict] = [{"role": "system", "content": _with_language(settings, extra_instructions)}]
    for earlier_question, earlier_answer in conversation.exchanges:
        messages += [{"role": "user", "content": earlier_question}, {"role": "assistant", "content": earlier_answer}]
    messages.append({"role": "user", "content": _labelled_screens(screens, question, lambda screen: {
        "type": "image_url", "image_url": {"url": "data:image/png;base64," + base64.b64encode(screen.png_bytes).decode("ascii")}})})
    body = {"model": model, "max_tokens": MAX_ANSWER_TOKENS, "messages": messages}
    if tools:
        body["tools"] = [{"type": "function", "function": {"name": tool["name"], "description": tool["description"],
                                                           "parameters": tool["input_schema"]}} for tool in tools]
    return body


def request_for(question: str, screenshot: "Screenshot | list[Screenshot]", conversation: Conversation, settings: LinuxSettings,
                tools: list[dict] | None = None, extra_instructions: str = "") -> AskRequest:
    """`screenshot` is one picture or one per monitor, the one with the mouse first; `tools` are
    Anthropic tool definitions, sent in each vendor's own shape."""
    screens = as_screen_list(screenshot)[:MAXIMUM_SCREENS]
    options = {"tools": tools, "extra_instructions": extra_instructions}
    if settings.anthropic_api_key:
        body = json.dumps(_anthropic_body(question, screens, conversation, settings, **options)).encode()
        return AskRequest("anthropic", urllib.request.Request(ANTHROPIC_MESSAGES_URL, data=body, method="POST", headers={
            "x-api-key": settings.anthropic_api_key, "anthropic-version": ANTHROPIC_VERSION, "Content-Type": "application/json"}))
    if settings.openrouter_api_key:
        model = settings.openrouter_model or OPENROUTER_MODEL
        body = json.dumps(_openai_body(question, screens, conversation, settings, model=model, **options)).encode()
        return AskRequest("openrouter", urllib.request.Request(OPENROUTER_CHAT_URL, data=body, method="POST", headers={
            "Authorization": f"Bearer {settings.openrouter_api_key}", "Content-Type": "application/json",
            # OpenRouter's app attribution headers: shown on its dashboard, never needed to answer.
            "HTTP-Referer": "https://openclicky.flowsxr.com", "X-Title": "OpenClicky for Linux"}))
    if settings.openai_api_key:
        body = json.dumps(_openai_body(question, screens, conversation, settings, **options)).encode()
        return AskRequest("openai", urllib.request.Request(OPENAI_CHAT_URL, data=body, method="POST", headers={
            "Authorization": f"Bearer {settings.openai_api_key}", "Content-Type": "application/json"}))
    if settings.account_token:
        body = json.dumps(_anthropic_body(question, screens[:MAXIMUM_SCREENS_ON_AN_ACCOUNT], conversation, settings, **options)).encode()
        return AskRequest("backend", urllib.request.Request(f"{settings.backend_url}/chat", data=body, method="POST", headers={
            "Authorization": f"Bearer {settings.account_token}", "anthropic-version": ANTHROPIC_VERSION, "Content-Type": "application/json"}))
    raise AskError("asking needs an anthropic, openrouter or openai key, or an openclicky account. add one in settings.")


def answer_text(kind: str, response: dict) -> str:
    if kind in ("openai", "openrouter"):
        choices = response.get("choices") or []
        content = choices[0].get("message", {}).get("content") if choices else None
        return content.strip() if isinstance(content, str) else ""
    blocks = response.get("content") or []
    return "".join(block.get("text", "") for block in blocks if isinstance(block, dict) and block.get("type") == "text").strip()


def tool_calls_in(kind: str, response: dict) -> list[ToolCall]:
    """The tools the model called: Anthropic's `tool_use` blocks, or OpenAI's `tool_calls`."""
    calls = []
    if kind in ("openai", "openrouter"):
        choices = response.get("choices") or []
        message = choices[0].get("message", {}) if choices and isinstance(choices[0], dict) else {}
        for call in message.get("tool_calls") or []:
            function = call.get("function") if isinstance(call, dict) else None
            if not isinstance(function, dict) or not isinstance(function.get("name"), str):
                continue
            try:
                arguments = json.loads(function.get("arguments") or "{}")
            except ValueError:
                arguments = {}
            calls.append(ToolCall(function["name"], arguments if isinstance(arguments, dict) else {}))
        return calls
    for block in response.get("content") or []:
        if isinstance(block, dict) and block.get("type") == "tool_use" and isinstance(block.get("name"), str):
            calls.append(ToolCall(block["name"], block.get("input") if isinstance(block.get("input"), dict) else {}))
    return calls


def ask_with_tools(question: str, screenshot: "Screenshot | list[Screenshot]", conversation: Conversation, settings: LinuxSettings,
                   tools: list[dict] | None = None, extra_instructions: str = "") -> tuple[str, list[ToolCall]]:
    """The answer and the tools it called (at most two, as on the Mac)."""
    routed = request_for(question, screenshot, conversation, settings, tools=tools, extra_instructions=extra_instructions)
    started = time.monotonic()
    try:
        answer, calls = _send(routed, tools_offered=bool(tools))
    except AskError as failure:
        applog.append(f"ask: {routed.kind} failed after {time.monotonic() - started:.1f} s: {failure}")
        raise
    applog.append(f"ask: {routed.kind} answered in {time.monotonic() - started:.1f} s, {len(as_screen_list(screenshot))} screen(s)"
                  f"{', a guided step' if guided.contains_marker(answer) else ''}{f', {len(calls)} tool call(s)' if calls else ''}")
    return answer, calls[:2]


def ask(question: str, screenshot: "Screenshot | list[Screenshot]", conversation: Conversation, settings: LinuxSettings) -> str:
    return ask_with_tools(question, screenshot, conversation, settings)[0]


def _send(routed: AskRequest, tools_offered: bool = False) -> tuple[str, list[ToolCall]]:
    vendor = {"anthropic": "anthropic", "openrouter": "openrouter", "openai": "openai", "backend": "the openclicky backend"}[routed.kind]
    try:
        with urllib.request.urlopen(routed.request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            answer = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as refusal:
        if refusal.code == 401:
            raise AskError(f"{vendor} refused the key or token. check it in settings.", account_refused=routed.kind == "backend") from refusal
        if refusal.code == 402 and routed.kind == "backend":
            raise AskError("your openclicky allowance is used up for now.") from refusal
        if refusal.code == 402:
            raise AskError(f"{vendor} says the key is out of credit.") from refusal
        raise AskError(f"{vendor} couldn't answer ({refusal.code}).") from refusal
    except (urllib.error.URLError, TimeoutError, OSError) as failure:
        raise AskError(f"couldn't reach {vendor}.") from failure
    except ValueError as failure:
        raise AskError(f"{vendor} sent back something that isn't json.") from failure
    answer = answer if isinstance(answer, dict) else {}
    text = answer_text(routed.kind, answer)
    calls = tool_calls_in(routed.kind, answer) if tools_offered else []
    if not text and not calls:
        raise AskError(f"{vendor} answered with no text.")
    return text, calls
