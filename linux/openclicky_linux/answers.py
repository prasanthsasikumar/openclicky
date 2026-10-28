"""Who answers a question, and how its answer streams back: the Mac's `AnswerProvider.swift`,
`ProviderRoute.answerSourcesInOrder`, `OpenAIChatAnswerProvider`, `GeminiAnswerProvider`,
`ClaudeCodeAnswerProvider`, `CodexAnswerProvider`, `PlanToolProtocol` and `AnswerSourceCheck`, ported.

- **Sources**, in the order "Automatic" tries them: own keys (Anthropic, OpenRouter, OpenAI,
  Gemini, xAI, Mistral, Groq), then the plans switched on (Claude Code on a Claude plan, Codex on a
  ChatGPT plan), then the free account. Settings → Answers come from picks one; a pick that cannot
  answer (its key removed) falls back to Automatic. shell.json keys are the Mac's: `answerSource`,
  `answerModels`, `geminiApiKey` …, `useClaudeCodePlan`, `useCodexPlan`.
- **Streaming**: every source but Codex streams its words as they are written (Anthropic's and
  OpenAI's server-sent events, Gemini's `streamGenerateContent?alt=sse`, Claude Code's stream-json),
  so the pointer can fly and the voice can start before the answer is finished.
- **Tools** (the quick actions) go to each vendor in its own shape; the plan CLIs run without
  tools, so they are listed in the prompt and asked for as `[ACTION: name {json}]` lines.
- **Test**: one tiny question (what colour is this red square?) to a single source.

Own keys go straight to their vendor; only the account goes to the OpenClicky backend.
"""

from __future__ import annotations

import base64
import json
import os
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

from . import applog

REQUEST_TIMEOUT_SECONDS = 60
PLAN_TIMEOUT_SECONDS = 120
ANTHROPIC_VERSION = "2023-06-01"
ANTHROPIC_MESSAGES_URL = "https://api.anthropic.com/v1/messages"
CLAUDE_MODELS = (("claude-sonnet-4-6", "Sonnet"), ("claude-opus-4-6", "Opus"))
DEFAULT_CLAUDE_MODEL = CLAUDE_MODELS[0][0]


@dataclass(frozen=True)
class Source:
    id: str  # the Mac's raw value, written to shell.json's `answerSource` and `answerModels`
    display_name: str
    short_name: str
    key_field: str | None = None  # the LinuxSettings field holding its key
    default_model: str | None = None
    chat_completions_url: str | None = None


SOURCES = (
    Source("anthropic", "Claude (Anthropic key)", "Claude", "anthropic_api_key"),
    Source("openRouter", "OpenRouter", "OpenRouter", "openrouter_api_key", "anthropic/claude-sonnet-4.6",
           "https://openrouter.ai/api/v1/chat/completions"),
    Source("openAI", "OpenAI", "OpenAI", "openai_api_key", "gpt-4.1", "https://api.openai.com/v1/chat/completions"),
    Source("gemini", "Google Gemini", "Gemini", "gemini_api_key", "gemini-3.8-flash"),
    Source("xai", "xAI (Grok)", "Grok", "xai_api_key", "grok-4", "https://api.x.ai/v1/chat/completions"),
    Source("mistral", "Mistral", "Mistral", "mistral_api_key", "mistral-medium-latest", "https://api.mistral.ai/v1/chat/completions"),
    Source("groq", "Groq", "Groq", "groq_api_key", "meta-llama/llama-4-scout-17b-16e-instruct",
           "https://api.groq.com/openai/v1/chat/completions"),
    Source("claudeCodePlan", "Claude Code (your Claude plan)", "Claude Code"),
    Source("codexPlan", "Codex (your ChatGPT plan)", "Codex"),
    Source("openClickyAccount", "OpenClicky free credits", "free credits"),
)
SOURCES_BY_ID = {source.id: source for source in SOURCES}
NEEDS_A_WAY_TO_ANSWER = ("i can answer that once you add your email for a free account, in settings, account. a key from openai, "
                         "anthropic, openrouter, gemini and others works too.")


class AnswerError(Exception):
    """Carries the sentence the caption shows; `account_refused` when the account's token was refused."""

    def __init__(self, message: str, account_refused: bool = False) -> None:
        super().__init__(message)
        self.account_refused = account_refused


@dataclass(frozen=True)
class Image:
    data: bytes
    label: str
    media_type: str = "image/png"


@dataclass(frozen=True)
class ToolCall:
    name: str
    arguments: dict


@dataclass
class AnswerRequest:
    images: list[Image]
    system_prompt: str
    history: list[tuple[str, str]]
    user_prompt: str
    tools: list[dict] = field(default_factory=list)  # Anthropic's shape: name, description, input_schema
    max_tokens: int = 400


# MARK: which source answers


def can_answer(source: Source, settings) -> bool:
    if source.id == "claudeCodePlan":
        return settings.use_claude_code_plan and plan_cli("claude") is not None
    if source.id == "codexPlan":
        return settings.use_codex_plan and plan_cli("codex") is not None
    if source.id == "openClickyAccount":
        return bool(settings.account_token)
    return bool(getattr(settings, source.key_field or "", None))


def sources_in_order(settings) -> list[Source]:
    return [source for source in SOURCES if can_answer(source, settings)]


def answering_source(settings) -> Source | None:
    """The person's pick when it can answer, else the first that can (Automatic)."""
    picked = SOURCES_BY_ID.get(settings.answer_source or "")
    if picked is not None and can_answer(picked, settings):
        return picked
    available = sources_in_order(settings)
    return available[0] if available else None


def model_for(source: Source, settings) -> str | None:
    if source.id in ("anthropic", "openClickyAccount"):
        return settings.claude_model or DEFAULT_CLAUDE_MODEL
    # The Mac keys `answerModels` by the source's id ("openRouter"); earlier Linux files said "openrouter".
    overrides = settings.answer_models or {}
    return overrides.get(source.id) or overrides.get(source.id.lower()) or source.default_model


# MARK: requests


def _anthropic_content(request: AnswerRequest) -> list[dict]:
    content: list[dict] = []
    for image in request.images:
        content.append({"type": "image", "source": {"type": "base64", "media_type": image.media_type,
                                                    "data": base64.b64encode(image.data).decode("ascii")}})
        content.append({"type": "text", "text": image.label})
    if content:
        # The last label and the question share one block, as before.
        last_label = content.pop()["text"]
        content.append({"type": "text", "text": f"{last_label}\n\n{request.user_prompt}"})
    else:
        content.append({"type": "text", "text": request.user_prompt})
    return content


def anthropic_body(request: AnswerRequest, model: str, stream: bool = True) -> dict:
    messages = []
    for earlier_question, earlier_answer in request.history:
        messages += [{"role": "user", "content": earlier_question}, {"role": "assistant", "content": earlier_answer}]
    messages.append({"role": "user", "content": _anthropic_content(request)})
    body = {"model": model, "max_tokens": request.max_tokens, "system": request.system_prompt, "messages": messages}
    if stream:
        body["stream"] = True
    if request.tools:
        body["tools"] = request.tools
    return body


def openai_body(request: AnswerRequest, model: str, stream: bool = True) -> dict:
    messages: list[dict] = [{"role": "system", "content": request.system_prompt}]
    for earlier_question, earlier_answer in request.history:
        messages += [{"role": "user", "content": earlier_question}, {"role": "assistant", "content": earlier_answer}]
    content: list[dict] = []
    for image in request.images:
        content.append({"type": "image_url", "image_url": {"url": f"data:{image.media_type};base64," + base64.b64encode(image.data).decode("ascii")}})
        content.append({"type": "text", "text": image.label})
    if content:
        last_label = content.pop()["text"]
        content.append({"type": "text", "text": f"{last_label}\n\n{request.user_prompt}"})
    else:
        content.append({"type": "text", "text": request.user_prompt})
    messages.append({"role": "user", "content": content})
    body: dict = {"model": model, "max_tokens": request.max_tokens, "messages": messages}
    if stream:
        body["stream"] = True
    if request.tools:
        body["tools"] = [{"type": "function", "function": {"name": tool["name"], "description": tool["description"],
                                                           "parameters": tool["input_schema"]}} for tool in request.tools]
    return body


GEMINI_REJECTED_SCHEMA_KEYWORDS = {"additionalProperties", "$schema", "$id", "$comment", "$defs", "definitions", "examples"}


def gemini_schema(schema):
    """A JSON schema without the keywords Gemini refuses."""
    if isinstance(schema, list):
        return [gemini_schema(item) for item in schema]
    if not isinstance(schema, dict):
        return schema
    accepted = {}
    for keyword, value in schema.items():
        if keyword in GEMINI_REJECTED_SCHEMA_KEYWORDS:
            continue
        if keyword == "properties" and isinstance(value, dict):
            accepted[keyword] = {name: gemini_schema(argument) for name, argument in value.items()}
        else:
            accepted[keyword] = gemini_schema(value)
    return accepted


def gemini_body(request: AnswerRequest, model: str) -> dict:
    contents = []
    for earlier_question, earlier_answer in request.history:
        contents.append({"role": "user", "parts": [{"text": earlier_question}]})
        contents.append({"role": "model", "parts": [{"text": earlier_answer}]})
    parts: list[dict] = []
    for image in request.images:
        parts.append({"inlineData": {"mimeType": image.media_type, "data": base64.b64encode(image.data).decode("ascii")}})
        parts.append({"text": image.label})
    parts.append({"text": request.user_prompt})
    contents.append({"role": "user", "parts": parts})
    generation_config: dict = {"maxOutputTokens": request.max_tokens}
    if model.startswith("gemini-3"):
        generation_config["thinkingConfig"] = {"thinkingLevel": "low"}
    body: dict = {"contents": contents, "generationConfig": generation_config}
    if request.system_prompt:
        body["systemInstruction"] = {"parts": [{"text": request.system_prompt}]}
    if request.tools:
        body["tools"] = [{"functionDeclarations": [{"name": tool["name"], "description": tool["description"],
                                                    "parameters": gemini_schema(tool["input_schema"])} for tool in request.tools]}]
    return body


def http_request(source: Source, request: AnswerRequest, settings, stream: bool = True) -> urllib.request.Request:
    """The vendor request for a source that answers over HTTP (every source but the plans)."""
    model = model_for(source, settings)
    if source.id == "anthropic":
        return urllib.request.Request(ANTHROPIC_MESSAGES_URL, data=json.dumps(anthropic_body(request, model, stream)).encode(), method="POST",
                                      headers={"x-api-key": settings.anthropic_api_key, "anthropic-version": ANTHROPIC_VERSION,
                                               "Content-Type": "application/json"})
    if source.id == "openClickyAccount":
        return urllib.request.Request(f"{settings.backend_url}/chat", data=json.dumps(anthropic_body(request, model, stream)).encode(),
                                      method="POST", headers={"Authorization": f"Bearer {settings.account_token}",
                                                              "anthropic-version": ANTHROPIC_VERSION, "Content-Type": "application/json"})
    if source.id == "gemini":
        url = ("https://generativelanguage.googleapis.com/v1beta/models/" + urllib.parse.quote(model, safe="")
               + (":streamGenerateContent?alt=sse" if stream else ":generateContent"))
        return urllib.request.Request(url, data=json.dumps(gemini_body(request, model)).encode(), method="POST",
                                      headers={"x-goog-api-key": settings.gemini_api_key, "Content-Type": "application/json"})
    if source.chat_completions_url:
        headers = {"Authorization": f"Bearer {getattr(settings, source.key_field)}", "Content-Type": "application/json"}
        if source.id == "openRouter":
            # OpenRouter's app attribution headers: shown on its dashboard, never needed to answer.
            headers.update({"HTTP-Referer": "https://openclicky.flowsxr.com", "X-Title": "OpenClicky for Linux"})
        return urllib.request.Request(source.chat_completions_url, data=json.dumps(openai_body(request, model, stream)).encode(),
                                      method="POST", headers=headers)
    raise ValueError(f"{source.id} does not answer over HTTP")


# MARK: reading streamed answers


def sse_data(line: str) -> str | None:
    line = line.rstrip("\r\n")
    if not line.startswith("data:"):
        return None
    payload = line[5:].strip()
    return None if payload in ("", "[DONE]") else payload


class AnthropicStream:
    """Anthropic's events: text deltas, and tool_use blocks whose input arrives as JSON pieces."""

    def __init__(self) -> None:
        self.text = ""
        self.stop_reason: str | None = None
        self._tools: dict[int, dict] = {}
        self.error: str | None = None

    def consume(self, line: str) -> str:
        payload = sse_data(line)
        if payload is None:
            return ""
        try:
            event = json.loads(payload)
        except ValueError:
            return ""
        kind = event.get("type")
        if kind == "content_block_start" and (event.get("content_block") or {}).get("type") == "tool_use":
            block = event["content_block"]
            self._tools[event.get("index", 0)] = {"name": block.get("name", ""), "json": ""}
        elif kind == "content_block_delta":
            delta = event.get("delta") or {}
            if delta.get("type") == "text_delta":
                self.text += delta.get("text", "")
                return delta.get("text", "")
            if delta.get("type") == "input_json_delta" and event.get("index", 0) in self._tools:
                self._tools[event["index"]]["json"] += delta.get("partial_json", "")
        elif kind == "message_delta":
            self.stop_reason = (event.get("delta") or {}).get("stop_reason") or self.stop_reason
        elif kind == "error":
            self.error = (event.get("error") or {}).get("message") or "the answer broke off"
        return ""

    @property
    def tool_calls(self) -> list[ToolCall]:
        calls = []
        for index in sorted(self._tools):
            tool = self._tools[index]
            try:
                arguments = json.loads(tool["json"] or "{}")
            except ValueError:
                arguments = {}
            calls.append(ToolCall(tool["name"], arguments if isinstance(arguments, dict) else {}))
        return calls


class OpenAIStream:
    """Chat completions' events: content deltas, and tool calls whose name and arguments arrive in
    pieces under an index."""

    def __init__(self) -> None:
        self.text = ""
        self._tools: dict[int, dict] = {}
        self.error: str | None = None

    def consume(self, line: str) -> str:
        payload = sse_data(line)
        if payload is None:
            return ""
        try:
            event = json.loads(payload)
        except ValueError:
            return ""
        if isinstance(event.get("error"), dict):
            self.error = event["error"].get("message") or "the answer broke off"
            return ""
        choices = event.get("choices") or []
        if not choices:
            return ""
        delta = choices[0].get("delta") or {}
        for call in delta.get("tool_calls") or []:
            tool = self._tools.setdefault(call.get("index", 0), {"name": "", "arguments": ""})
            function = call.get("function") or {}
            tool["name"] += function.get("name") or ""
            tool["arguments"] += function.get("arguments") or ""
        piece = delta.get("content")
        if isinstance(piece, str) and piece:
            self.text += piece
            return piece
        return ""

    @property
    def tool_calls(self) -> list[ToolCall]:
        calls = []
        for index in sorted(self._tools):
            tool = self._tools[index]
            try:
                arguments = json.loads(tool["arguments"] or "{}")
            except ValueError:
                arguments = {}
            if tool["name"]:
                calls.append(ToolCall(tool["name"], arguments if isinstance(arguments, dict) else {}))
        return calls


class GeminiStream:
    """Gemini's events: each a whole response whose first candidate carries text or a functionCall."""

    def __init__(self) -> None:
        self.text = ""
        self._tool_calls: list[ToolCall] = []
        self.error: str | None = None

    def consume(self, line: str) -> str:
        payload = sse_data(line)
        if payload is None:
            return ""
        try:
            event = json.loads(payload)
        except ValueError:
            return ""
        if isinstance(event.get("error"), dict):
            self.error = event["error"].get("message") or "the stream broke off"
            return ""
        block_reason = (event.get("promptFeedback") or {}).get("blockReason")
        if block_reason:
            self.error = f"Gemini would not answer this question ({block_reason})"
            return ""
        candidates = event.get("candidates") or []
        if not candidates:
            return ""
        pieces = ""
        for part in (candidates[0].get("content") or {}).get("parts") or []:
            if part.get("thought") is True:
                continue
            call = part.get("functionCall")
            if isinstance(call, dict) and call.get("name"):
                self._tool_calls.append(ToolCall(call["name"], call.get("args") if isinstance(call.get("args"), dict) else {}))
            elif isinstance(part.get("text"), str):
                pieces += part["text"]
        self.text += pieces
        return pieces

    @property
    def tool_calls(self) -> list[ToolCall]:
        return list(self._tool_calls)


def _vendor_message(body: bytes) -> str:
    try:
        error = json.loads(body.decode("utf-8", "replace"))
    except ValueError:
        return body.decode("utf-8", "replace").strip()[:160]
    if isinstance(error, dict):
        detail = error.get("error")
        if isinstance(detail, dict):
            return str(detail.get("message") or detail.get("type") or "")[:160]
        if isinstance(detail, str):
            return detail[:160]
        return str(error.get("message") or "")[:160]
    return ""


def _refusal_sentence(source: Source, status: int, body: bytes) -> AnswerError:
    vendor = "the openclicky backend" if source.id == "openClickyAccount" else source.short_name.lower()
    if status == 401:
        return AnswerError(f"{vendor} refused the key or token. check it in settings.", account_refused=source.id == "openClickyAccount")
    if status == 402 and source.id == "openClickyAccount":
        return AnswerError("your openclicky allowance is used up for now.")
    if status == 402:
        return AnswerError(f"{vendor} says the key is out of credit.")
    message = _vendor_message(body)
    return AnswerError(f"{vendor} couldn't answer ({status}{': ' + message if message else ''}).")


def stream_http(source: Source, request: AnswerRequest, settings, on_text: Callable[[str], None]) -> tuple[str, list[ToolCall]]:
    """Asks an HTTP source and hands each piece of the answer to `on_text` as it arrives."""
    vendor = "the openclicky backend" if source.id == "openClickyAccount" else source.short_name.lower()
    reader = AnthropicStream() if source.id in ("anthropic", "openClickyAccount") else GeminiStream() if source.id == "gemini" else OpenAIStream()
    try:
        with urllib.request.urlopen(http_request(source, request, settings), timeout=REQUEST_TIMEOUT_SECONDS) as response:
            for raw_line in response:
                piece = reader.consume(raw_line.decode("utf-8", "replace"))
                if piece:
                    on_text(piece)
    except urllib.error.HTTPError as refusal:
        raise _refusal_sentence(source, refusal.code, refusal.read()) from refusal
    except (urllib.error.URLError, TimeoutError, OSError) as failure:
        raise AnswerError(f"couldn't reach {vendor}.") from failure
    if reader.error:
        raise AnswerError(f"{vendor} stopped answering: {reader.error}")
    if isinstance(reader, AnthropicStream) and reader.stop_reason == "max_tokens":
        applog.append(f"answer: {source.id} cut off at max_tokens ({len(reader.text)} chars)")
    return reader.text, reader.tool_calls


# MARK: the plans (Claude Code, Codex)

ACTION_TAG_OPENING = "[ACTION"
MAXIMUM_ACTIONS = 2


def plan_cli(name: str) -> str | None:
    """The `claude` or `codex` command, on PATH or where their installers put them."""
    home = Path.home()
    extra = [home / ".local/bin", home / ".npm-global/bin", home / ".claude/local", Path("/usr/local/bin")]
    return shutil.which(name, path=os.pathsep.join([*map(str, extra), os.environ.get("PATH", "")]))


def plan_instruction_block(tools: list[dict]) -> str:
    """The quick actions, listed in the prompt for a CLI that runs without tools."""
    if not tools:
        return ""
    lines = ["Quick actions: you can do these things on the person's computer yourself.",
             "To do one, write your spoken words first, then put the action on a line of its own:",
             "[ACTION: <action name> <arguments as one JSON object>]",
             'For example: [ACTION: some_action {"argument":"value"}]',
             f"Use at most {MAXIMUM_ACTIONS} actions, only the ones listed below, with arguments that match their schema.",
             "Never explain, mention or read out the action line; say in plain words what you are doing.",
             "If no action fits, write no action line.", "Actions:"]
    for tool in tools:
        lines.append(f"- {tool['name']}: {tool['description']} Arguments: {json.dumps(tool['input_schema'], sort_keys=True, separators=(',', ':'))}")
    return "\n".join(lines)


def _scan_action_tag(text: str, opening: int) -> int:
    """Where a tag opening at `opening` ends (exclusive): its closing "]" outside the JSON, the end
    of its line, or the end of the text."""
    brace_depth = bracket_depth = 0
    in_string = escaped = False
    index = opening + len(ACTION_TAG_OPENING)
    while index < len(text):
        character = text[index]
        if character == "\n":
            return index
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
        elif character == '"' and brace_depth > 0:
            in_string = True
        elif character == "{":
            brace_depth += 1
        elif character == "}":
            brace_depth = max(0, brace_depth - 1)
        elif character == "[" and brace_depth > 0:
            bracket_depth += 1
        elif character == "]":
            if brace_depth > 0 and bracket_depth > 0:
                bracket_depth -= 1
            elif brace_depth == 0:
                return index + 1
        index += 1
    return len(text)


def parse_action_lines(answer: str, known_tools: set[str]) -> tuple[str, list[ToolCall]]:
    """The answer without its `[ACTION: …]` tags, and the actions they ask for (at most two; an
    unknown name or arguments that are not a JSON object drop that action, never the answer)."""
    text = answer
    calls: list[ToolCall] = []
    search_start = 0
    while True:
        opening = text.upper().find(ACTION_TAG_OPENING, search_start)
        if opening < 0:
            break
        end = _scan_action_tag(text, opening)
        content = text[opening + len(ACTION_TAG_OPENING):end].rstrip("]").lstrip(": \t")
        name = ""
        for character in content:
            if not (character.isalnum() or character in "_-"):
                break
            name += character
        arguments_text = content[len(name):]
        arguments: dict | None = None
        if "{" in arguments_text and "}" in arguments_text:
            try:
                decoded = json.loads(arguments_text[arguments_text.index("{"):arguments_text.rindex("}") + 1])
                arguments = decoded if isinstance(decoded, dict) else None
            except ValueError:
                arguments = None
        elif all(character.isspace() or character in ".,;:)" for character in arguments_text):
            arguments = {}
        if name in known_tools and arguments is not None and len(calls) < MAXIMUM_ACTIONS:
            calls.append(ToolCall(name, arguments))
        removal_end = end
        while removal_end < len(text) and text[removal_end] in ".,;!":
            removal_end += 1
        text = text[:opening] + text[removal_end:]
        search_start = opening
    cleaned = "\n".join(line.rstrip() for line in text.replace("```", "").split("\n"))
    while "\n\n\n" in cleaned:
        cleaned = cleaned.replace("\n\n\n", "\n\n")
    return cleaned.strip(), calls


def _history_text(history: list[tuple[str, str]]) -> str | None:
    if not history:
        return None
    lines = ["Earlier in this conversation:"]
    for user, assistant in history:
        lines += [f"User: {user}", f"You: {assistant}"]
    return "\n".join(lines)


def _plan_failure(cli: str, text: str) -> str:
    lowered = text.lower()
    if cli == "claude" and ("not logged in" in lowered or "/login" in lowered or "authentication_failed" in lowered):
        return "Claude Code is signed out. Open a terminal, run claude, and sign in with your Claude account."
    if cli == "codex" and ("login" in lowered or "unauthorized" in lowered or "401" in lowered):
        return "Codex is signed out. Open a terminal, run codex login, and sign in with your ChatGPT account."
    if "rate_limit" in lowered or "usage limit" in lowered:
        return f"Your {'Claude' if cli == 'claude' else 'ChatGPT'} plan has hit its usage limit for now."
    name = "Claude Code" if cli == "claude" else "Codex"
    return f"{name} couldn't answer: {text.strip()[:160]}" if text.strip() else f"{name} couldn't answer."


def ask_claude_code(request: AnswerRequest, model: str | None, on_text: Callable[[str], None]) -> tuple[str, list[ToolCall]]:
    """`claude -p` with stream-json in and out, no tools, no session: the Mac's arguments."""
    executable = plan_cli("claude")
    if executable is None:
        raise AnswerError("Claude Code isn't installed: npm install -g @anthropic-ai/claude-code, then sign in.")
    system_prompt = request.system_prompt + ("\n\n" + plan_instruction_block(request.tools) if request.tools else "")
    arguments = [executable, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                 "--include-partial-messages", "--tools", "", "--permission-mode", "dontAsk", "--permission-prompts", "none",
                 "--system-prompt", system_prompt, "--no-session-persistence", "--safe-mode", "--strict-mcp-config",
                 "--disable-slash-commands"]
    if model:
        arguments += ["--model", model]
    content: list[dict] = []
    history = _history_text(request.history)
    if history:
        content.append({"type": "text", "text": history})
    for image in request.images:
        content.append({"type": "text", "text": image.label})
        content.append({"type": "image", "source": {"type": "base64", "media_type": image.media_type,
                                                    "data": base64.b64encode(image.data).decode("ascii")}})
    content.append({"type": "text", "text": request.user_prompt})
    message = json.dumps({"type": "user", "message": {"role": "user", "content": content}}, sort_keys=True) + "\n"
    with tempfile.TemporaryDirectory(prefix="openclicky-plan-") as scratch:
        process = subprocess.Popen(arguments, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, cwd=scratch)
        assert process.stdin is not None and process.stdout is not None
        process.stdin.write(message)
        process.stdin.close()
        streamed, result_text, failure = "", None, None
        deadline = time.monotonic() + PLAN_TIMEOUT_SECONDS
        for line in process.stdout:
            if time.monotonic() > deadline:
                process.kill()
                raise AnswerError(f"Claude Code took longer than {PLAN_TIMEOUT_SECONDS} seconds to answer.")
            try:
                event = json.loads(line)
            except ValueError:
                continue
            if event.get("type") == "stream_event":
                delta = ((event.get("event") or {}).get("delta") or {})
                if (event.get("event") or {}).get("type") == "content_block_delta" and delta.get("type") == "text_delta":
                    streamed += delta.get("text", "")
                    if not request.tools:
                        on_text(delta.get("text", ""))
            elif event.get("type") == "result":
                if event.get("is_error") or str(event.get("subtype", "")).startswith("error"):
                    failure = event.get("result") or event.get("subtype") or "unknown error"
                else:
                    result_text = event.get("result")
        process.wait()
        error_output = process.stderr.read() if process.stderr else ""
    if failure:
        raise AnswerError(_plan_failure("claude", str(failure)))
    if process.returncode != 0 and not (result_text or streamed):
        raise AnswerError(_plan_failure("claude", error_output.strip().splitlines()[-1] if error_output.strip() else ""))
    answer = (result_text or streamed).strip()
    if not answer:
        raise AnswerError("Claude Code finished without an answer.")
    if not request.tools:
        return answer, []
    text, calls = parse_action_lines(answer, {tool["name"] for tool in request.tools})
    if text:
        on_text(text)
    return text, calls


CODEX_DISABLED_FEATURES = ("shell_tool", "unified_exec", "plugins", "apps", "multi_agent", "image_generation", "browser_use",
                           "computer_use", "skill_search", "hooks", "view_image")


def toml_basic_string(text: str) -> str:
    escaped = '"'
    for character in text:
        if character == "\\":
            escaped += "\\\\"
        elif character == '"':
            escaped += '\\"'
        elif character == "\n":
            escaped += "\\n"
        elif character == "\r":
            escaped += "\\r"
        elif character == "\t":
            escaped += "\\t"
        elif ord(character) < 0x20 or ord(character) == 0x7F:
            escaped += f"\\u{ord(character):04X}"
        else:
            escaped += character
    return escaped + '"'


def ask_codex(request: AnswerRequest, model: str | None, on_text: Callable[[str], None]) -> tuple[str, list[ToolCall]]:
    """`codex exec`, ephemeral, read-only, every agent feature off: one answer, not streamed."""
    executable = plan_cli("codex")
    if executable is None:
        raise AnswerError("Codex isn't installed: npm install -g @openai/codex, then codex login.")
    system_prompt = request.system_prompt + ("\n\n" + plan_instruction_block(request.tools) if request.tools else "")
    with tempfile.TemporaryDirectory(prefix="openclicky-plan-") as scratch:
        image_paths = []
        for index, image in enumerate(request.images, start=1):
            path = Path(scratch) / f"image-{index}.{'png' if image.media_type == 'image/png' else 'jpg'}"
            path.write_bytes(image.data)
            image_paths.append(str(path))
        output_path = Path(scratch) / "last-message.txt"
        arguments = [executable, "exec", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules",
                     "--sandbox", "read-only", "--color", "never", "--json"]
        for feature in CODEX_DISABLED_FEATURES:
            arguments += ["--disable", feature]
        arguments += ["-c", 'web_search="disabled"', "-c", 'model_reasoning_effort="low"', "-c",
                      f"developer_instructions={toml_basic_string(system_prompt)}"]
        if model:
            arguments += ["-m", model]
        arguments += ["-C", scratch]
        for path in image_paths:
            arguments += ["-i", path]
        arguments += ["-o", str(output_path), "-"]
        sections = [section for section in (_history_text(request.history),) if section]
        if request.images:
            sections.append("Attached images, in order:\n" + "\n".join(f"{index}. {image.label}" for index, image in enumerate(request.images, start=1)))
        sections.append(request.user_prompt)
        try:
            finished = subprocess.run(arguments, input="\n\n".join(sections), capture_output=True, text=True, cwd=scratch,
                                      timeout=PLAN_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired as timeout:
            raise AnswerError(f"Codex took longer than {PLAN_TIMEOUT_SECONDS} seconds to answer.") from timeout
        answer = output_path.read_text(encoding="utf-8").strip() if output_path.exists() else ""
    if not answer:
        error_lines = (finished.stderr or "").strip().splitlines()
        raise AnswerError(_plan_failure("codex", error_lines[-1] if error_lines else ""))
    text, calls = parse_action_lines(answer, {tool["name"] for tool in request.tools}) if request.tools else (answer, [])
    if text:
        on_text(text)
    return text, calls


# MARK: one entry point


def answer(source: Source, request: AnswerRequest, settings, on_text: Callable[[str], None] = lambda _piece: None) -> tuple[str, list[ToolCall]]:
    """The answer from `source` (its text and the tools it called), streamed into `on_text`."""
    if source.id == "claudeCodePlan":
        return ask_claude_code(request, (settings.answer_models or {}).get(source.id), on_text)
    if source.id == "codexPlan":
        return ask_codex(request, (settings.answer_models or {}).get(source.id), on_text)
    return stream_http(source, request, settings, on_text)


# MARK: the Test buttons


def red_square_png(side: int = 64) -> bytes:
    import struct
    import zlib

    row = b"\x00" + b"\xff\x00\x00" * side
    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", side, side, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(row * side)) + chunk(b"IEND", b""))


def check(source: Source, settings) -> str:
    """The line a Test button leaves in its row: "✓ Works. It answered in 3.8 s." or what went wrong."""
    started = time.monotonic()
    if source.id == "anthropic":
        if not settings.anthropic_api_key:
            return "Add the key first."
        request = urllib.request.Request("https://api.anthropic.com/v1/models?limit=1",
                                         headers={"x-api-key": settings.anthropic_api_key, "anthropic-version": ANTHROPIC_VERSION})
        try:
            with urllib.request.urlopen(request, timeout=20):
                pass
        except urllib.error.HTTPError as refusal:
            return "Anthropic didn’t accept this key." if refusal.code == 401 else f"Anthropic answered {refusal.code}."
        except (urllib.error.URLError, OSError, TimeoutError):
            return "Couldn't reach Anthropic."
        return f"✓ Works. It answered in {time.monotonic() - started:.1f} s."
    if source.id == "openClickyAccount":
        return "The free credits are checked by asking Clicky."
    if not can_answer(source, settings) and source.id not in ("claudeCodePlan", "codexPlan"):
        return "Add the key first."
    request = AnswerRequest(images=[Image(red_square_png(), "a test image")], system_prompt="answer in lowercase, one word.", history=[],
                            user_prompt="what color is this square?", max_tokens=20)
    try:
        text, _calls = answer(source, request, settings)
    except AnswerError as failure:
        return str(failure)[:1].upper() + str(failure)[1:]
    if "red" not in text.lower():
        return f"It answered “{text[:40]}” instead of red."
    return f"✓ Works. It answered in {time.monotonic() - started:.1f} s."
